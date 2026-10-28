<?php
declare(strict_types=1);

// Single-file JSON API keeps cPanel deployment independent of rewrite rules.
header('Content-Type: application/json; charset=utf-8');
header('Cache-Control: no-store, no-cache, must-revalidate, max-age=0');
header('Pragma: no-cache');
header('X-Content-Type-Options: nosniff');
header('Referrer-Policy: no-referrer');
header('Content-Security-Policy: default-src \'self\'; connect-src \'self\'; style-src \'self\'; script-src \'self\'; img-src \'self\' data:; object-src \'none\'; base-uri \'none\'; frame-ancestors \'none\'; form-action \'self\'');

function reply(int $status, array $body, int $protocol = 1): never {
    http_response_code($status);
    echo json_encode(['protocol' => $protocol] + $body, JSON_UNESCAPED_SLASHES | JSON_INVALID_UTF8_SUBSTITUTE);
    exit;
}

function companion_storage_is_private(string $path, string $documentRoot): bool {
    $absolute = DIRECTORY_SEPARATOR === '\\'
        ? (preg_match('~^[A-Za-z]:[\\\\/]~', $path) === 1 || str_starts_with($path, '\\\\'))
        : str_starts_with($path, '/');
    if ($path === '' || !$absolute || str_contains($path, "\0") || preg_match('~(?:^|[\\\\/])\.\.(?:[\\\\/]|$)~', $path)) return false;
    $root = realpath($documentRoot);
    $resolved = realpath($path);
    if ($resolved === false) $resolved = realpath(dirname($path));
    if (!is_string($root) || !is_string($resolved)) return false;
    $prefix = rtrim($root, DIRECTORY_SEPARATOR) . DIRECTORY_SEPARATOR;
    $inside = DIRECTORY_SEPARATOR === '\\' ? strncasecmp($resolved . DIRECTORY_SEPARATOR, $prefix, strlen($prefix)) === 0
        : str_starts_with($resolved . DIRECTORY_SEPARATOR, $prefix);
    return $resolved !== $root && !$inside;
}

try {
    $protocol = 1;
    $method = $_SERVER['REQUEST_METHOD'] ?? '';
    if ($method !== 'POST') reply(405, ['error' => 'method_not_allowed']);
    if (!str_starts_with(strtolower((string)($_SERVER['CONTENT_TYPE'] ?? '')), 'application/json')) reply(415, ['error' => 'json_required']);
    $length = (int)($_SERVER['CONTENT_LENGTH'] ?? 0);
    if ($length > 8192) reply(413, ['error' => 'request_too_large']);
    $raw = file_get_contents('php://input', false, null, 0, 8193);
    if (!is_string($raw) || strlen($raw) > 8192) reply(413, ['error' => 'request_too_large']);
    try { $request = json_decode($raw, true, 32, JSON_THROW_ON_ERROR); }
    catch (JsonException) { reply(400, ['error' => 'invalid_json']); }
    if (!is_array($request) || !in_array($request['protocol'] ?? null, [1, 2], true) || !is_string($request['action'] ?? null)) reply(400, ['error' => 'invalid_request']);
    $protocol = $request['protocol'];

    // Parse and retain the requested protocol before configuration checks. This
    // keeps health and setup failures useful to clients while the private config
    // is absent, without revealing any private path or value.
    $configPath = getenv('WODRA_COMPANION_CONFIG') ?: dirname(__DIR__) . '/private/config.php';
    if (!is_file($configPath)) reply(503, ['error' => 'not_configured'], $protocol);
    if (!companion_storage_is_private((string)$configPath, (string)($_SERVER['DOCUMENT_ROOT'] ?? ''))) reply(503, ['error' => 'configuration_not_private'], $protocol);
    $config = require $configPath;
    if (!is_array($config) || PHP_VERSION_ID < 80200 || !extension_loaded('json') || !function_exists('random_bytes'))
        reply(503, ['error' => 'requirements_not_met'], $protocol);
    $secure = strtolower((string)($_SERVER['HTTPS'] ?? '')) === 'on' || (string)($_SERVER['SERVER_PORT'] ?? '') === '443';
    $localTest = !empty($config['allow_insecure_local']) && in_array((string)($_SERVER['REMOTE_ADDR'] ?? ''), ['127.0.0.1', '::1'], true);
    if (!$secure && !$localTest)
        reply(400, ['error' => 'https_required'], $protocol);
    $origin = $_SERVER['HTTP_ORIGIN'] ?? '';
    if ($origin !== '' && !hash_equals((string)($config['origin'] ?? ''), $origin)) reply(403, ['error' => 'origin_rejected'], $protocol);
    if ($protocol === 2) {
        $browserActions = ['remembered_claim', 'remembered_read', 'remembered_forget'];
        if (in_array($request['action'], $browserActions, true) && !hash_equals((string)($config['origin'] ?? ''), $origin))
            reply(403, ['error' => 'origin_rejected'], $protocol);
        $request['_phone_cookie'] = is_string($_COOKIE['WodraPhone'] ?? null) ? $_COOKIE['WodraPhone'] : null;
    }
    if (!companion_storage_is_private((string)($config['storage'] ?? ''), (string)($_SERVER['DOCUMENT_ROOT'] ?? ''))) reply(503, ['error' => 'storage_not_private'], $protocol);
    $store = new CompanionStore((string)($config['storage'] ?? ''));
    $response = $store->execute($request, (int)($config['invite_ttl_seconds'] ?? 180), (int)($config['pair_ttl_seconds'] ?? 28800), (string)($_SERVER['REMOTE_ADDR'] ?? 'unknown'));
    if (isset($response['_set_phone_cookie'])) {
        $cookie = $response['_set_phone_cookie']; unset($response['_set_phone_cookie']);
        setcookie('WodraPhone', $cookie, ['expires' => (int)$response['_cookie_expires'], 'path' => '/', 'secure' => $secure,
            'httponly' => true, 'samesite' => 'Strict']);
        unset($response['_cookie_expires']);
    }
    if (!empty($response['_clear_phone_cookie'])) {
        unset($response['_clear_phone_cookie']);
        setcookie('WodraPhone', '', ['expires' => 1, 'path' => '/', 'secure' => $secure, 'httponly' => true, 'samesite' => 'Strict']);
    }
    reply(200, $response, $protocol);
} catch (DomainException $e) {
    $map = ['rate_limited' => 429, 'unauthorized' => 401, 'not_found' => 404, 'expired' => 410, 'conflict' => 409];
    reply($map[$e->getMessage()] ?? 400, ['error' => $e->getMessage()], $protocol ?? 1);
} catch (Throwable $e) {
    // Do not include filesystem paths, secrets or request data in responses/logs.
    error_log('Wodra companion relay internal failure: ' . get_class($e));
    reply(503, ['error' => 'temporarily_unavailable'], $protocol ?? 1);
}

final class CompanionStore {
    private const MAX_SESSIONS = 200;
    public function __construct(private readonly string $directory) {}

    public function execute(array $r, int $inviteTtl, int $pairTtl, string $ip): array {
        if ($this->directory === '' || str_contains($this->directory, "\0")) throw new RuntimeException('storage_unavailable');
        if (!is_dir($this->directory) && !mkdir($this->directory, 0700, true) && !is_dir($this->directory)) throw new RuntimeException('storage_unavailable');
        $lock = fopen($this->directory . '/companion.lock', 'c');
        if (!$lock || !flock($lock, LOCK_EX)) throw new RuntimeException('storage_unavailable');
        @chmod($this->directory, 0700); @chmod($this->directory . '/companion.lock', 0600);
        try {
            $path = $this->directory . '/sessions.json';
            $raw = is_file($path) ? file_get_contents($path) : '{}';
            if (!is_string($raw) || strlen($raw) > 1_000_000) throw new RuntimeException('storage_unavailable');
            $state = json_decode($raw, true, 64, JSON_THROW_ON_ERROR);
            if (!is_array($state)) throw new RuntimeException('storage_unavailable');
            $now = time();
            foreach (($state['sessions'] ?? []) as $id => $s) {
                if (!is_array($s) || (int)($s['expires'] ?? 0) < $now ||
                    (($s['status'] ?? '') === 'active' && (int)($s['last_seen'] ?? 0) < $now - $pairTtl)) unset($state['sessions'][$id]);
            }
            foreach (($state['remembered_devices'] ?? []) as $id => $device) {
                if (!is_array($device) || (int)($device['expires'] ?? 0) < $now) unset($state['remembered_devices'][$id]);
            }
            foreach (($state['remembered_invites'] ?? []) as $id => $invite) {
                if (!is_array($invite) || (int)($invite['expires'] ?? 0) < $now) unset($state['remembered_invites'][$id]);
            }
            foreach (($state['rates'] ?? []) as $key => $times) {
                $recent = array_values(array_filter(is_array($times) ? $times : [], static fn($t) => is_int($t) && $t > $now - 60));
                if ($recent === []) unset($state['rates'][$key]); else $state['rates'][$key] = $recent;
            }
            if (count($state['rates'] ?? []) > 512) $state['rates'] = array_slice($state['rates'], -512, null, true);
            $this->rateLimit($state, $r['action'], $ip, $now);
            $result = $this->dispatch($state, $r, $now, $inviteTtl, $pairTtl);
            $json = json_encode($state, JSON_THROW_ON_ERROR | JSON_UNESCAPED_SLASHES);
            $temp = $path . '.' . bin2hex(random_bytes(8)) . '.tmp';
            if (file_put_contents($temp, $json, LOCK_EX) !== strlen($json)) { @unlink($temp); throw new RuntimeException('storage_unavailable'); }
            @chmod($temp, 0600);
            if (!rename($temp, $path)) { @unlink($temp); throw new RuntimeException('storage_unavailable'); }
            @chmod($path, 0600);
            return $result;
        } finally { flock($lock, LOCK_UN); fclose($lock); }
    }

    private function rateLimit(array &$s, string $action, string $ip, int $now): void {
        if (!in_array($action, ['create', 'claim', 'remembered_create', 'remembered_claim'], true)) return;
        $key = hash('sha256', $ip . '|pairing|' . $action);
        $times = array_values(array_filter($s['rates'][$key] ?? [], static fn($t) => is_int($t) && $t > $now - 60));
        if (count($times) >= (str_ends_with($action, 'claim') ? 12 : 6)) throw new DomainException('rate_limited');
        $times[] = $now; $s['rates'][$key] = $times;
    }

    private function dispatch(array &$s, array $r, int $now, int $inviteTtl, int $pairTtl): array {
        if (($r['protocol'] ?? 1) === 2) return $this->dispatchRemembered($s, $r, $now, $inviteTtl);
        $sessions = &$s['sessions']; if (!is_array($sessions ?? null)) $sessions = [];
        $action = $r['action'];
        if ($action === 'create') {
            $this->hashField($r, 'invite_hash'); $this->hashField($r, 'desktop_hash');
            if (count($sessions) >= self::MAX_SESSIONS) throw new DomainException('rate_limited');
            $id = $this->opaque(); $code = strtoupper(bin2hex(random_bytes(5)));
            while ($this->findCode($sessions, $code) !== null) $code = strtoupper(bin2hex(random_bytes(5)));
            $sessions[$id] = ['status'=>'invited','code_hash'=>hash('sha256', $code), 'invite_hash'=>$r['invite_hash'],
                'desktop_hash'=>$r['desktop_hash'], 'created'=>$now, 'expires'=>$now + max(60, min(300, $inviteTtl)), 'last_seen'=>$now, 'seq'=>0];
            return ['id'=>$id, 'code'=>$code, 'expires_at'=>$sessions[$id]['expires']];
        }
            if ($action === 'claim') {
            $code = strtoupper(preg_replace('/[^A-Z0-9]/', '', (string)($r['code'] ?? '')) ?? '');
            $this->hashField($r, 'companion_hash');
            if (strlen($code) !== 10) throw new DomainException('not_found');
            foreach ($sessions as $foundId => &$item) if (($item['status'] ?? '') === 'invited' && (int)$item['expires'] >= $now && hash_equals((string)$item['code_hash'], hash('sha256', $code))) {
                $item['status']='pending'; $item['companion_hash']=$r['companion_hash']; $item['phone_requested_at']=$now;
                $expires = (int)$item['expires']; unset($item); return ['status'=>'pending','id'=>$foundId,'expires_at'=>$expires];
            }
            unset($item); throw new DomainException('not_found');
        }
        $id = $this->id($r);
        if ($action === 'invite_status' || $action === 'decide' || $action === 'cancel_invite') {
            $item = &$sessions[$id]; if (!is_array($item ?? null) || !hash_equals((string)($item['invite_hash'] ?? ''), (string)($r['invite_hash'] ?? ''))) throw new DomainException('unauthorized');
            if ((int)($item['expires'] ?? 0) < $now) throw new DomainException('expired');
            if ($action === 'decide') {
                if (($item['status'] ?? '') !== 'pending') throw new DomainException('conflict');
                if (($r['approve'] ?? false) === true) { $item['status']='active'; $item['expires']=$now + max(900, min(86400, $pairTtl)); $item['last_seen']=$now; }
                else { $item['status']='rejected'; $item['desktop_hash']=''; return ['status'=>'rejected']; }
            } elseif ($action === 'cancel_invite') { $item['status']='cancelled'; $item['desktop_hash']=''; return ['status'=>'cancelled']; }
            $status = (string)$item['status']; $expires = (int)$item['expires']; unset($item);
            return ['status'=>$status,'expires_at'=>$expires,'phone_requested'=>$status === 'pending'];
        }
        if ($action === 'publish' || $action === 'read' || $action === 'pair_status' || $action === 'disconnect' || $action === 'disconnect_desktop') {
            $item = &$sessions[$id]; if (!is_array($item ?? null)) throw new DomainException('not_found');
            $role = in_array($action, ['publish','disconnect_desktop'], true) ? 'desktop_hash' : 'companion_hash';
            if (!is_string($r['token_hash'] ?? null) || !hash_equals((string)($item[$role] ?? ''), $r['token_hash'])) throw new DomainException('unauthorized');
            if ($action === 'disconnect' || $action === 'disconnect_desktop') { unset($sessions[$id]); unset($item); return ['status'=>'disconnected']; }
            if ($action === 'pair_status') { $status=(string)($item['status']??'expired'); $expires=(int)($item['expires']??0); unset($item); return ['status'=>$status,'expires_at'=>$expires]; }
            if (($item['status'] ?? '') !== 'active') throw new DomainException('not_found');
            $item['last_seen']=$now;
            if ($action === 'publish') {
                $seq = $r['seq'] ?? null; $snapshot = $r['snapshot'] ?? null;
                if (!is_int($seq) || $seq <= (int)$item['seq'] || !is_array($snapshot)) throw new DomainException('conflict');
                $safe = $this->snapshot($snapshot); $item['seq']=$seq; $item['snapshot']=$safe; $item['snapshot_at']=$now;
                unset($item); return ['status'=>'accepted','seq'=>$seq];
            }
            $snapshot = $item['snapshot'] ?? null; $snapshotAt = (int)($item['snapshot_at'] ?? 0); $seq = (int)($item['seq'] ?? 0); unset($item);
            return ['status'=>'active','snapshot'=>$snapshot,'snapshot_at'=>$snapshotAt,'seq'=>$seq];
        }
        throw new DomainException('not_found');
    }

    private function snapshot(array $x): array {
        $allowed = ['state','connected','paused','data_stale','route','speed_kph','power_w','cadence_rpm','heart_rate_bpm','distance_m','elapsed_s','gradient_pct','captured_at','route_distance_m','route_progress','lap','remaining_m','riders','environment','discipline','route_type'];
        if (array_diff(array_keys($x), $allowed) !== []) throw new DomainException('invalid_request');
        if (!in_array($x['state'] ?? null, ['idle','riding','paused','ended'], true) || !is_bool($x['connected'] ?? null) || !is_bool($x['paused'] ?? null) || !is_bool($x['data_stale'] ?? null)) throw new DomainException('invalid_request');
        foreach (['route'=>100,'captured_at'=>40,'environment'=>30,'discipline'=>30,'route_type'=>30] as $k=>$max) if (isset($x[$k]) && (!is_string($x[$k]) || strlen($x[$k])>$max)) throw new DomainException('invalid_request');
        if (isset($x['environment']) && !in_array($x['environment'], ['Road','Trail','Velodrome','MountainBike','TrainingStudio'], true)) throw new DomainException('invalid_request');
        if (isset($x['discipline']) && !in_array($x['discipline'], ['Road','Gravel','MountainBike','Track','IndoorTraining'], true)) throw new DomainException('invalid_request');
        if (isset($x['route_type']) && !in_array($x['route_type'], ['Loop','Point-to-Point','Out-and-Back'], true)) throw new DomainException('invalid_request');
        foreach (['speed_kph'=>[0,200],'power_w'=>[0,3000],'cadence_rpm'=>[0,250],'heart_rate_bpm'=>[0,250],
            'distance_m'=>[0,10000000],'elapsed_s'=>[0,10000000],'gradient_pct'=>[-30,30],
            'route_distance_m'=>[0,10000000],'route_progress'=>[0,1],'remaining_m'=>[0,10000000]] as $k=>[$min,$max]) {
            if (array_key_exists($k,$x) && $x[$k] !== null && ((!is_int($x[$k]) && !is_float($x[$k])) || !is_finite((float)$x[$k]) || (float)$x[$k]<$min || (float)$x[$k]>$max)) throw new DomainException('invalid_request');
        }
        if (isset($x['lap']) && (!is_int($x['lap']) || $x['lap'] < 1 || $x['lap'] > 100000)) throw new DomainException('invalid_request');
        if (isset($x['riders'])) {
            if (!is_array($x['riders']) || !array_is_list($x['riders']) || count($x['riders']) > 20) throw new DomainException('invalid_request');
            $safeRiders = [];
            foreach ($x['riders'] as $rider) {
                if (!is_array($rider) || !is_string($rider['name'] ?? null) || strlen($rider['name']) > 128
                    || !in_array($rider['kind'] ?? null, ['AI','Rider'], true) || !is_numeric($rider['relative_m'] ?? null)
                    || !is_finite((float)$rider['relative_m']) || abs((float)$rider['relative_m']) > 10000000) throw new DomainException('invalid_request');
                $safeRiders[] = ['name'=>$rider['name'], 'kind'=>$rider['kind'], 'relative_m'=>(float)$rider['relative_m']];
            }
            $x['riders'] = $safeRiders;
        }
        return $x;
    }

    private function routeMetadata(mixed $route): ?array {
        if ($route === null) return null;
        if (!is_array($route) || !is_string($route['version'] ?? null) || strlen($route['version']) > 220
            || !is_string($route['id'] ?? null) || strlen($route['id']) > 100 || !is_string($route['name'] ?? null) || strlen($route['name']) > 100
            || !in_array($route['environment'] ?? null, ['Road','Trail','Velodrome','MountainBike','TrainingStudio'], true)
            || !in_array($route['discipline'] ?? null, ['Road','Gravel','MountainBike','Track','IndoorTraining'], true)
            || !in_array($route['type'] ?? null, ['Loop','Point-to-Point','Out-and-Back'], true) || !is_array($route['points'] ?? null) || !array_is_list($route['points'])
            || count($route['points']) < 2 || count($route['points']) > 70) throw new DomainException('invalid_request');
        foreach (['distance_m'=>[1,10000000],'ascent_m'=>[0,1000000]] as $key=>[$min,$max])
            if (!is_numeric($route[$key] ?? null) || !is_finite((float)$route[$key]) || (float)$route[$key] < $min || (float)$route[$key] > $max) throw new DomainException('invalid_request');
        foreach ($route['points'] as $point) {
            if (!is_array($point) || !array_is_list($point) || count($point) !== 5) throw new DomainException('invalid_request');
            foreach ($point as $n) if (!is_numeric($n) || !is_finite((float)$n) || abs((float)$n) > 10000000) throw new DomainException('invalid_request');
        }
        return ['version'=>$route['version'], 'id'=>$route['id'], 'name'=>$route['name'],
            'environment'=>$route['environment'], 'discipline'=>$route['discipline'], 'type'=>$route['type'], 'distance_m'=>(float)$route['distance_m'],
            'ascent_m'=>(float)$route['ascent_m'], 'points'=>array_map(static fn($point) => array_map('floatval', $point), $route['points'])];
    }
    private function routeForClient(mixed $route, mixed $version): ?array {
        if (!is_array($route) || !is_string($version) || $version === ($route['version'] ?? null)) return null;
        return $route;
    }

    private function dispatchRemembered(array &$s, array $r, int $now, int $inviteTtl): array {
        $action = (string)($r['action'] ?? '');
        $desktops = &$s['remembered_desktops']; if (!is_array($desktops ?? null)) $desktops = [];
        $devices = &$s['remembered_devices']; if (!is_array($devices ?? null)) $devices = [];
        $invites = &$s['remembered_invites']; if (!is_array($invites ?? null)) $invites = [];

        if ($action === 'remembered_create') {
            $desktopId = $this->desktopId($r);
            $this->hashField($r, 'desktop_hash');
            if (isset($desktops[$desktopId]) && !hash_equals((string)$desktops[$desktopId]['credential_hash'], $r['desktop_hash']))
                throw new DomainException('unauthorized');
            if (!isset($desktops[$desktopId]) && count($desktops) >= 500) throw new DomainException('rate_limited');
            $desktops[$desktopId] ??= ['credential_hash'=>$r['desktop_hash'], 'created'=>$now, 'last_seen'=>$now,
                'seq'=>0, 'snapshot'=>null, 'snapshot_at'=>0];
            $desktops[$desktopId]['last_seen'] = $now;
            do {
                $code = strtoupper(bin2hex(random_bytes(5)));
                $exists = false;
                foreach ($invites as $candidate) if (hash_equals((string)($candidate['code_hash'] ?? ''), hash('sha256', $code))) { $exists = true; break; }
            } while ($exists);
            $id = $this->opaque();
            $invites[$id] = ['code_hash'=>hash('sha256', $code), 'desktop_id'=>$desktopId, 'status'=>'invited',
                'created'=>$now, 'expires'=>$now + max(60, min(300, $inviteTtl))];
            return ['id'=>$id, 'code'=>$code, 'expires_at'=>$invites[$id]['expires']];
        }

        if ($action === 'remembered_claim') {
            $code = strtoupper(preg_replace('/[^A-Z0-9]/', '', (string)($r['code'] ?? '')) ?? '');
            if (strlen($code) !== 10) throw new DomainException('not_found');
            foreach ($invites as $inviteId => $invite) {
                if (!hash_equals((string)($invite['code_hash'] ?? ''), hash('sha256', $code))) continue;
                if ((int)($invite['expires'] ?? 0) < $now) throw new DomainException('expired');
                $desktopId = (string)($invite['desktop_id'] ?? '');
                $deviceCount = count(array_filter($devices, static fn($device) => ($device['desktop_id'] ?? '') === $desktopId));
                if ($deviceCount >= 12) throw new DomainException('rate_limited');
                $cookie = $this->opaque(); $deviceId = $this->opaque();
                $number = 1;
                foreach ($devices as $existing) if (($existing['desktop_id'] ?? '') === $desktopId &&
                    preg_match('/^Phone ([1-9][0-9]*)$/D', (string)($existing['label'] ?? ''), $labelMatch) === 1)
                    $number = max($number, (int)$labelMatch[1] + 1);
                $devices[$deviceId] = ['desktop_id'=>$desktopId, 'credential_hash'=>hash('sha256', $cookie),
                    'label'=>'Phone ' . $number, 'created'=>$now, 'last_seen'=>$now, 'expires'=>$now + 90 * 86400];
                $invites[$inviteId]['status'] = 'claimed';
                $invites[$inviteId]['device_id'] = $deviceId;
                unset($invites[$inviteId]['code_hash']);
                return ['status'=>'paired', 'device_id'=>$deviceId, 'label'=>$devices[$deviceId]['label'],
                    '_set_phone_cookie'=>$cookie, '_cookie_expires'=>$devices[$deviceId]['expires']];
            }
            throw new DomainException('not_found');
        }

        if (in_array($action, ['remembered_publish', 'remembered_route_publish', 'remembered_devices', 'remembered_revoke', 'remembered_revoke_all', 'remembered_idle', 'remembered_invite_status', 'remembered_cancel_invite'], true)) {
            $desktopId = $this->desktopId($r);
            $desktop = &$desktops[$desktopId];
            if (!is_array($desktop ?? null)) throw new DomainException('unauthorized');
            $this->hashField($r, 'desktop_hash');
            if (!hash_equals((string)($desktop['credential_hash'] ?? ''), $r['desktop_hash'])) throw new DomainException('unauthorized');
            $desktop['last_seen'] = $now;
            if ($action === 'remembered_route_publish') {
                if (!is_string($r['version'] ?? null) || strlen($r['version']) > 220) throw new DomainException('invalid_request');
                $desktop['route'] = $this->routeMetadata($r['route'] ?? null);
                $desktop['route_version'] = $r['version'];
                return ['status'=>'accepted'];
            }
            if ($action === 'remembered_publish') {
                $sequence = $r['seq'] ?? null;
                if (!is_int($sequence) || $sequence <= (int)$desktop['seq'] || !is_array($r['snapshot'] ?? null)) throw new DomainException('conflict');
                $desktop['seq'] = $sequence; $desktop['snapshot'] = $this->snapshot($r['snapshot']); $desktop['snapshot_at'] = $now;
                return ['status'=>'accepted', 'seq'=>$sequence];
            }
            if ($action === 'remembered_idle') {
                $desktop['snapshot'] = null; $desktop['snapshot_at'] = 0;
                $desktop['route'] = null; $desktop['route_version'] = 'none';
                return ['status'=>'idle'];
            }
            if ($action === 'remembered_invite_status' || $action === 'remembered_cancel_invite') {
                $inviteId = $this->inviteId($r);
                $invite = &$invites[$inviteId];
                if (!is_array($invite ?? null) || ($invite['desktop_id'] ?? '') !== $desktopId) throw new DomainException('not_found');
                if ($action === 'remembered_cancel_invite') {
                    if (($invite['status'] ?? 'invited') !== 'invited') throw new DomainException('conflict');
                    $invite['status'] = 'cancelled'; unset($invite['code_hash']);
                }
                $status = (string)($invite['status'] ?? 'expired');
                $expires = (int)($invite['expires'] ?? 0);
                $deviceId = (string)($invite['device_id'] ?? ''); unset($invite);
                return ['status'=>$status, 'expires_at'=>$expires, 'device_id'=>$deviceId];
            }
            if ($action === 'remembered_devices') {
                $result = [];
                foreach ($devices as $id => $device) if (($device['desktop_id'] ?? '') === $desktopId) {
                    $result[] = ['id'=>$id, 'label'=>(string)$device['label'], 'created_at'=>(int)$device['created'],
                        'last_seen'=>(int)$device['last_seen'], 'expires_at'=>(int)$device['expires']];
                }
                return ['devices'=>$result];
            }
            if ($action === 'remembered_revoke_all') {
                foreach ($devices as $id => $device) if (($device['desktop_id'] ?? '') === $desktopId) unset($devices[$id]);
                return ['status'=>'revoked'];
            }
            $deviceId = $this->deviceId($r);
            if (!isset($devices[$deviceId]) || ($devices[$deviceId]['desktop_id'] ?? '') !== $desktopId) throw new DomainException('not_found');
            unset($devices[$deviceId]);
            return ['status'=>'revoked'];
        }

        if (in_array($action, ['remembered_read', 'remembered_forget'], true)) {
            $cookie = $r['_phone_cookie'] ?? null;
            if ($action === 'remembered_forget' && (!is_string($cookie) || !preg_match('/^[A-Za-z0-9_-]{40,48}$/D', $cookie)))
                return ['status'=>'forgotten', '_clear_phone_cookie'=>true];
            if (!is_string($cookie) || !preg_match('/^[A-Za-z0-9_-]{40,48}$/D', $cookie)) throw new DomainException('unauthorized');
            $credentialHash = hash('sha256', $cookie); $found = null;
            foreach ($devices as $id => $device) if (hash_equals((string)($device['credential_hash'] ?? ''), $credentialHash)) { $found = $id; break; }
            if ($found === null && $action === 'remembered_forget') return ['status'=>'forgotten', '_clear_phone_cookie'=>true];
            if ($found === null) throw new DomainException('unauthorized');
            if ((int)$devices[$found]['expires'] < $now) { unset($devices[$found]); throw new DomainException('expired'); }
            if ($action === 'remembered_forget') {
                unset($devices[$found]);
                return ['status'=>'forgotten', '_clear_phone_cookie'=>true];
            }
            $devices[$found]['last_seen'] = $now;
            if ((int)$devices[$found]['expires'] < $now + 30 * 86400) {
                $devices[$found]['expires'] = $now + 90 * 86400;
                return ['status'=>'remembered', 'snapshot'=>$desktops[$devices[$found]['desktop_id']]['snapshot'] ?? null,
                    'snapshot_at'=>(int)($desktops[$devices[$found]['desktop_id']]['snapshot_at'] ?? 0),
                    'desktop_online'=>(int)($desktops[$devices[$found]['desktop_id']]['last_seen'] ?? 0) > $now - 12,
                    'route'=>$this->routeForClient($desktops[$devices[$found]['desktop_id']]['route'] ?? null, $r['route_version'] ?? null),
                    'route_version'=>(string)($desktops[$devices[$found]['desktop_id']]['route_version'] ?? ''),
                    '_set_phone_cookie'=>$cookie, '_cookie_expires'=>$devices[$found]['expires']];
            }
            $desktop = $desktops[$devices[$found]['desktop_id']] ?? [];
            return ['status'=>'remembered', 'snapshot'=>$desktop['snapshot'] ?? null,
                'snapshot_at'=>(int)($desktop['snapshot_at'] ?? 0), 'desktop_online'=>(int)($desktop['last_seen'] ?? 0) > $now - 12,
                'route'=>$this->routeForClient($desktop['route'] ?? null, $r['route_version'] ?? null),
                'route_version'=>(string)($desktop['route_version'] ?? '')];
        }
        throw new DomainException('not_found');
    }

    private function findCode(array $sessions, string $code): ?string { foreach ($sessions as $id=>$v) if (isset($v['code_hash']) && hash_equals((string)$v['code_hash'], hash('sha256',$code))) return (string)$id; return null; }
    private function desktopId(array $r): string { $v=$r['desktop_id']??''; if (!is_string($v) || !preg_match('/^[A-Za-z0-9_-]{32,64}$/D',$v)) throw new DomainException('invalid_request'); return $v; }
    private function deviceId(array $r): string { $v=$r['device_id']??''; if (!is_string($v) || !preg_match('/^[A-Za-z0-9_-]{32,48}$/D',$v)) throw new DomainException('not_found'); return $v; }
    private function inviteId(array $r): string { $v=$r['invite_id']??''; if (!is_string($v) || !preg_match('/^[A-Za-z0-9_-]{32,48}$/D',$v)) throw new DomainException('not_found'); return $v; }
    private function id(array $r): string { $v=$r['id']??''; if (!is_string($v) || !preg_match('/^[A-Za-z0-9_-]{32,48}$/D',$v)) throw new DomainException('not_found'); return $v; }
    private function hashField(array $r,string $k): void { if (!is_string($r[$k]??null) || !preg_match('/^[a-f0-9]{64}$/D',$r[$k])) throw new DomainException('invalid_request'); }
    private function opaque(): string { return rtrim(strtr(base64_encode(random_bytes(32)), '+/', '-_'), '='); }
}
