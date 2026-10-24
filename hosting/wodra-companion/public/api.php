<?php
declare(strict_types=1);

// Single-file JSON API keeps cPanel deployment independent of rewrite rules.
header('Content-Type: application/json; charset=utf-8');
header('Cache-Control: no-store, no-cache, must-revalidate, max-age=0');
header('Pragma: no-cache');
header('X-Content-Type-Options: nosniff');
header('Referrer-Policy: no-referrer');
header('Content-Security-Policy: default-src \'self\'; connect-src \'self\'; style-src \'self\'; script-src \'self\'; img-src \'self\' data:; object-src \'none\'; base-uri \'none\'; frame-ancestors \'none\'; form-action \'self\'');

function reply(int $status, array $body): never {
    http_response_code($status);
    echo json_encode(['protocol' => 1] + $body, JSON_UNESCAPED_SLASHES | JSON_INVALID_UTF8_SUBSTITUTE);
    exit;
}

function companion_storage_is_private(string $path, string $documentRoot): bool {
    $absolute = DIRECTORY_SEPARATOR === '\\' ? preg_match('/^[A-Za-z]:\\\\/', $path) === 1 : str_starts_with($path, '/');
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
    $configPath = getenv('WODRA_COMPANION_CONFIG') ?: dirname(__DIR__) . '/private/config.php';
    if (!is_file($configPath)) reply(503, ['error' => 'not_configured']);
    if (!companion_storage_is_private((string)$configPath, (string)($_SERVER['DOCUMENT_ROOT'] ?? ''))) reply(503, ['error' => 'configuration_not_private']);
    $config = require $configPath;
    if (!is_array($config) || PHP_VERSION_ID < 80200 || !extension_loaded('json') || !function_exists('random_bytes'))
        reply(503, ['error' => 'requirements_not_met']);
    $secure = strtolower((string)($_SERVER['HTTPS'] ?? '')) === 'on' || (string)($_SERVER['SERVER_PORT'] ?? '') === '443';
    $localTest = !empty($config['allow_insecure_local']) && in_array((string)($_SERVER['REMOTE_ADDR'] ?? ''), ['127.0.0.1', '::1'], true);
    if (!$secure && !$localTest)
        reply(400, ['error' => 'https_required']);
    $method = $_SERVER['REQUEST_METHOD'] ?? '';
    if ($method !== 'POST') reply(405, ['error' => 'method_not_allowed']);
    if (!str_starts_with(strtolower((string)($_SERVER['CONTENT_TYPE'] ?? '')), 'application/json')) reply(415, ['error' => 'json_required']);
    $origin = $_SERVER['HTTP_ORIGIN'] ?? '';
    if ($origin !== '' && !hash_equals((string)($config['origin'] ?? ''), $origin)) reply(403, ['error' => 'origin_rejected']);
    $length = (int)($_SERVER['CONTENT_LENGTH'] ?? 0);
    if ($length > 8192) reply(413, ['error' => 'request_too_large']);
    $raw = file_get_contents('php://input', false, null, 0, 8193);
    if (!is_string($raw) || strlen($raw) > 8192) reply(413, ['error' => 'request_too_large']);
    $request = json_decode($raw, true, 32, JSON_THROW_ON_ERROR);
    if (!is_array($request) || ($request['protocol'] ?? null) !== 1 || !is_string($request['action'] ?? null)) reply(400, ['error' => 'invalid_request']);
    if (!companion_storage_is_private((string)($config['storage'] ?? ''), (string)($_SERVER['DOCUMENT_ROOT'] ?? ''))) reply(503, ['error' => 'storage_not_private']);
    $store = new CompanionStore((string)($config['storage'] ?? ''));
    $response = $store->execute($request, (int)($config['invite_ttl_seconds'] ?? 180), (int)($config['pair_ttl_seconds'] ?? 28800), (string)($_SERVER['REMOTE_ADDR'] ?? 'unknown'));
    reply(200, $response);
} catch (DomainException $e) {
    $map = ['rate_limited' => 429, 'unauthorized' => 401, 'not_found' => 404, 'expired' => 410, 'conflict' => 409];
    reply($map[$e->getMessage()] ?? 400, ['error' => $e->getMessage()]);
} catch (Throwable) {
    // Do not include filesystem paths, secrets or request data in responses/logs.
    reply(503, ['error' => 'temporarily_unavailable']);
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
        if (!in_array($action, ['create', 'claim'], true)) return;
        $key = hash('sha256', $ip . '|pairing|' . $action);
        $times = array_values(array_filter($s['rates'][$key] ?? [], static fn($t) => is_int($t) && $t > $now - 60));
        if (count($times) >= ($action === 'claim' ? 12 : 6)) throw new DomainException('rate_limited');
        $times[] = $now; $s['rates'][$key] = $times;
    }

    private function dispatch(array &$s, array $r, int $now, int $inviteTtl, int $pairTtl): array {
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
        $allowed = ['state','connected','paused','data_stale','route','speed_kph','power_w','cadence_rpm','heart_rate_bpm','distance_m','elapsed_s','gradient_pct','captured_at'];
        if (array_diff(array_keys($x), $allowed) !== []) throw new DomainException('invalid_request');
        if (!in_array($x['state'] ?? null, ['idle','riding','paused','ended'], true) || !is_bool($x['connected'] ?? null) || !is_bool($x['paused'] ?? null) || !is_bool($x['data_stale'] ?? null)) throw new DomainException('invalid_request');
        foreach (['route'=>100,'captured_at'=>40] as $k=>$max) if (isset($x[$k]) && (!is_string($x[$k]) || strlen($x[$k])>$max)) throw new DomainException('invalid_request');
        foreach (['speed_kph'=>[0,200],'power_w'=>[0,3000],'cadence_rpm'=>[0,250],'heart_rate_bpm'=>[0,250],
            'distance_m'=>[0,10000000],'elapsed_s'=>[0,10000000],'gradient_pct'=>[-30,30]] as $k=>[$min,$max]) {
            if (array_key_exists($k,$x) && $x[$k] !== null && ((!is_int($x[$k]) && !is_float($x[$k])) || !is_finite((float)$x[$k]) || (float)$x[$k]<$min || (float)$x[$k]>$max)) throw new DomainException('invalid_request');
        }
        return $x;
    }
    private function findCode(array $sessions, string $code): ?string { foreach ($sessions as $id=>$v) if (isset($v['code_hash']) && hash_equals((string)$v['code_hash'], hash('sha256',$code))) return (string)$id; return null; }
    private function id(array $r): string { $v=$r['id']??''; if (!is_string($v) || !preg_match('/^[A-Za-z0-9_-]{32,48}$/D',$v)) throw new DomainException('not_found'); return $v; }
    private function hashField(array $r,string $k): void { if (!is_string($r[$k]??null) || !preg_match('/^[a-f0-9]{64}$/D',$r[$k])) throw new DomainException('invalid_request'); }
    private function opaque(): string { return rtrim(strtr(base64_encode(random_bytes(32)), '+/', '-_'), '='); }
}
