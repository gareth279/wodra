<?php
declare(strict_types=1);

namespace Wodra\StravaBroker;

final class FileSessionStore
{
    public function __construct(private readonly string $directory) {}

    /** @return array<string, array<string, mixed>> */
    public function locked(callable $operation): mixed
    {
        if (!is_dir($this->directory) && !mkdir($this->directory, 0700, true) && !is_dir($this->directory)) {
            throw new \RuntimeException('private_storage_unavailable');
        }
        $path = $this->directory . '/link-sessions.json';
        $lock = fopen($this->directory . '/sessions.lock', 'c');
        if ($lock === false || !flock($lock, LOCK_EX)) throw new \RuntimeException('private_storage_unavailable');
        @chmod($this->directory, 0700);
        @chmod($this->directory . '/sessions.lock', 0600);
        try {
            $raw = is_file($path) ? file_get_contents($path) : '{}';
            $sessions = is_string($raw) ? json_decode($raw, true) : null;
            if (!is_array($sessions)) $sessions = [];
            $result = $operation($sessions);
            $temp = $path . '.' . bin2hex(random_bytes(8)) . '.tmp';
            $json = json_encode($sessions, JSON_THROW_ON_ERROR | JSON_UNESCAPED_SLASHES);
            if (file_put_contents($temp, $json, LOCK_EX) !== strlen($json)) {
                @unlink($temp);
                throw new \RuntimeException('private_storage_unavailable');
            }
            @chmod($temp, 0600);
            if (!rename($temp, $path)) {
                @unlink($temp);
                throw new \RuntimeException('private_storage_unavailable');
            }
            @chmod($path, 0600);
            return $result;
        } finally {
            flock($lock, LOCK_UN);
            fclose($lock);
        }
    }
}

final class DeploymentConfigPolicy
{
    public static function validBasePath(mixed $basePath): bool
    {
        return is_string($basePath) && strlen($basePath) <= 200 &&
            preg_match('#^(?:/[A-Za-z0-9._~-]+(?:/[A-Za-z0-9._~-]+)*)?$#D', $basePath) === 1 &&
            !str_contains($basePath, '..');
    }

    public static function routePath(string $requestUri, string $basePath): ?string
    {
        if (!self::validBasePath($basePath)) return null;
        $path = parse_url($requestUri, PHP_URL_PATH);
        if (!is_string($path)) return null;
        if ($basePath === '') return $path;
        if ($path === $basePath || $path === $basePath . '/') return '/';
        if (!str_starts_with($path, $basePath . '/')) return null;
        return substr($path, strlen($basePath));
    }
}

final class StravaHttpClient
{
    private const TOKEN_URL = 'https://www.strava.com/oauth/token';
    private const REVOKE_URL = 'https://www.strava.com/oauth/revoke';

    public function token(array $fields, array $config): array
    {
        return $this->request(self::TOKEN_URL, $fields + [
            'client_id' => (string)$config['client_id'],
            'client_secret' => (string)$config['client_secret'],
        ]);
    }

    public function revoke(string $token, string $hint, array $config): bool
    {
        $body = $this->request(self::REVOKE_URL, ['token' => $token, 'token_type_hint' => $hint], $config);
        return ($body['_http_status'] ?? 0) === 200;
    }

    private function request(string $url, array $fields, ?array $basicConfig = null): array
    {
        if (!function_exists('curl_init')) throw new \RuntimeException('curl_unavailable');
        $responseBody = '';
        $curl = curl_init($url);
        if ($curl === false) throw new \RuntimeException('upstream_unavailable');
        $headers = ['Accept: application/json'];
        if ($basicConfig !== null) $headers[] = 'Authorization: Basic ' . base64_encode((string)$basicConfig['client_id'] . ':' . (string)$basicConfig['client_secret']);
        curl_setopt_array($curl, [
            CURLOPT_POST => true,
            CURLOPT_POSTFIELDS => http_build_query($fields, '', '&', PHP_QUERY_RFC3986),
            CURLOPT_HTTPHEADER => $headers,
            CURLOPT_RETURNTRANSFER => false,
            CURLOPT_FOLLOWLOCATION => false,
            CURLOPT_CONNECTTIMEOUT => 3,
            CURLOPT_TIMEOUT => 10,
            CURLOPT_SSL_VERIFYPEER => true,
            CURLOPT_SSL_VERIFYHOST => 2,
            CURLOPT_PROTOCOLS => CURLPROTO_HTTPS,
            CURLOPT_REDIR_PROTOCOLS => CURLPROTO_HTTPS,
            CURLOPT_WRITEFUNCTION => static function ($handle, string $chunk) use (&$responseBody): int {
                if (strlen($responseBody) + strlen($chunk) > 32768) return 0;
                $responseBody .= $chunk;
                return strlen($chunk);
            },
        ]);
        $ok = curl_exec($curl);
        $status = (int)curl_getinfo($curl, CURLINFO_RESPONSE_CODE);
        // curl_close() becomes a deprecated no-op in PHP 8.5; release CurlHandle normally there.
        if (PHP_VERSION_ID < 80500) curl_close($curl);
        if ($ok === false || $status < 200 || $status >= 300) throw new UpstreamFailure($status);
        if ($url === self::REVOKE_URL) return ['_http_status' => $status];
        $decoded = json_decode($responseBody, true);
        if (!is_array($decoded) || !isset($decoded['access_token'], $decoded['refresh_token'], $decoded['expires_at']))
            throw new UpstreamFailure($status);
        return $decoded;
    }
}

final class UpstreamFailure extends \RuntimeException
{
    public function __construct(public readonly int $status) { parent::__construct('strava_upstream_failed'); }
}

final class LoopbackRedirectPolicy
{
    public static function accepts(mixed $value): bool
    {
        if (!is_string($value) || strlen($value) > 256) return false;
        $parts = parse_url($value);
        return is_array($parts) && ($parts['scheme'] ?? '') === 'http' &&
            in_array(strtolower((string)($parts['host'] ?? '')), ['localhost', '127.0.0.1', '[::1]', '::1'], true) &&
            isset($parts['port']) && $parts['port'] >= 1 && $parts['port'] <= 65535 &&
            ($parts['path'] ?? '') === '/strava/callback' && !array_key_exists('user', $parts) &&
            !array_key_exists('pass', $parts) && !array_key_exists('query', $parts) && !array_key_exists('fragment', $parts);
    }
}

final class LinkSessionRepository
{
    public function __construct(private readonly FileSessionStore $store, private readonly int $ttlSeconds = 600) {}

    public function create(string $profileId, string $redirectUri): array
    {
        $id = rtrim(strtr(base64_encode(random_bytes(32)), '+/', '-_'), '=');
        $state = rtrim(strtr(base64_encode(random_bytes(32)), '+/', '-_'), '=');
        $expires = time() + $this->ttlSeconds;
        $this->store->locked(function (array &$all) use ($id, $state, $profileId, $redirectUri, $expires): void {
            foreach ($all as $oldId => $session) if (($session['expires'] ?? 0) < time()) unset($all[$oldId]);
            if (count($all) >= 1000) throw new \RuntimeException('too_many_sessions');
            $all[$id] = ['state_hash' => hash('sha256', $state), 'profile_id' => strtolower($profileId),
                'redirect_uri' => $redirectUri, 'expires' => $expires, 'used' => false];
        });
        return ['session_id' => $id, 'state' => $state, 'expires_at' => $expires];
    }

    public function consume(string $id, string $state, string $redirectUri): ?array
    {
        return $this->store->locked(function (array &$all) use ($id, $state, $redirectUri): ?array {
            $item = $all[$id] ?? null;
            if (!is_array($item) || ($item['used'] ?? true) || ($item['expires'] ?? 0) < time() ||
                !hash_equals((string)$item['state_hash'], hash('sha256', $state)) ||
                !hash_equals((string)$item['redirect_uri'], $redirectUri)) return null;
            $all[$id]['used'] = true;
            return $item;
        });
    }

    public function cancel(string $id): void
    {
        $this->store->locked(function (array &$all) use ($id): void { unset($all[$id]); });
    }
}

final class App
{
    private const MAX_BODY = 16384;
    private static bool $configurationFailureLogged = false;

    public function __construct(private readonly array $config, private readonly FileSessionStore $sessions,
        private readonly StravaHttpClient $strava, private readonly string $urlBasePath = '') {}

    public function handle(): void
    {
        header('Content-Type: application/json; charset=utf-8');
        try {
            $path = DeploymentConfigPolicy::routePath((string)($_SERVER['REQUEST_URI'] ?? '/'), $this->urlBasePath);
            $failures = self::configurationFailureCodes($this->config, (string)($_SERVER['DOCUMENT_ROOT'] ?? ''),
                $this->urlBasePath);
            if ($failures !== []) {
                if (!self::$configurationFailureLogged) {
                    error_log('[Wodra Strava] configuration check failed: ' . implode(',', $failures));
                    self::$configurationFailureLogged = true;
                }
                $this->respond(503, ['error' => 'service_not_configured']); return;
            }
            if ($path === null) { $this->respond(404, ['error' => 'not_found']); return; }
            if (!$this->isSecureRequest()) { $this->respond(400, ['error' => 'https_required']); return; }
            if (!$this->rateLimit()) { $this->respond(429, ['error' => 'try_again_later']); return; }
            if ($path === '/health' && ($_SERVER['REQUEST_METHOD'] ?? '') === 'GET') {
                if (!$this->healthReady()) { $this->respond(503, ['error' => 'service_not_ready']); return; }
                $this->respond(200, ['status' => 'ok']); return;
            }
            if ((int)($_SERVER['CONTENT_LENGTH'] ?? 0) > self::MAX_BODY) { $this->respond(413, ['error' => 'request_too_large']); return; }
            $body = $this->readJson();
            if ($path === '/session/start' && $this->method('POST')) { $this->start($body); return; }
            if ($path === '/session/complete' && $this->method('POST')) { $this->complete($body); return; }
            if ($path === '/session/cancel' && $this->method('POST')) { $this->cancel($body); return; }
            if ($path === '/oauth/refresh' && $this->method('POST')) { $this->refresh($body); return; }
            if ($path === '/oauth/revoke' && $this->method('POST')) { $this->revoke($body); return; }
            $this->respond(404, ['error' => 'not_found']);
        } catch (UpstreamFailure $error) {
            $this->respond($error->status === 401 ? 401 : 502, ['error' => $error->status === 401 ? 'authorization_expired_or_revoked' : 'strava_unavailable']);
        } catch (\Throwable) {
            $this->respond(503, ['error' => 'service_temporarily_unavailable']);
        }
    }

    private function start(array $body): void
    {
        $profile = $body['profile_id'] ?? null;
        $redirect = $body['redirect_uri'] ?? null;
        if (!self::validGuid($profile) || !self::validLoopbackUri($redirect)) { $this->respond(400, ['error' => 'invalid_link_request']); return; }
        $linkSession = (new LinkSessionRepository($this->sessions, (int)$this->config['link_session_ttl_seconds']))->create($profile, $redirect);
        $this->respond(200, $linkSession + [
            'client_id' => (string)$this->config['client_id'], 'scope' => 'read,activity:write']);
    }

    private function complete(array $body): void
    {
        $id = $body['session_id'] ?? '';
        $state = $body['state'] ?? '';
        $code = $body['code'] ?? '';
        $redirect = $body['redirect_uri'] ?? '';
        if (!self::validOpaque($id) || !self::validOpaque($state) || !is_string($code) || strlen($code) < 8 || strlen($code) > 2048) {
            $this->respond(400, ['error' => 'invalid_authorization_response']); return;
        }
        $session = (new LinkSessionRepository($this->sessions, (int)$this->config['link_session_ttl_seconds']))
            ->consume($id, $state, (string)$redirect); // consume before contacting Strava; never replay an ambiguous code.
        if ($session === null) { $this->respond(400, ['error' => 'link_session_invalid_or_expired']); return; }
        $tokens = $this->strava->token(['grant_type' => 'authorization_code', 'code' => $code,
            'redirect_uri' => $session['redirect_uri']], $this->config);
        $this->respond(200, ['profile_id' => $session['profile_id'], 'tokens' => $tokens]);
    }

    private function cancel(array $body): void
    {
        $id = $body['session_id'] ?? '';
        if (!self::validOpaque($id)) { $this->respond(400, ['error' => 'invalid_link_session']); return; }
        (new LinkSessionRepository($this->sessions))->cancel($id);
        $this->respond(200, ['status' => 'cancelled']);
    }

    private function refresh(array $body): void
    {
        $token = $body['refresh_token'] ?? '';
        if (!self::validOpaque($token, 4096)) { $this->respond(400, ['error' => 'invalid_refresh_request']); return; }
        $tokens = $this->strava->token(['grant_type' => 'refresh_token', 'refresh_token' => $token], $this->config);
        $this->respond(200, $tokens);
    }

    private function revoke(array $body): void
    {
        $token = $body['refresh_token'] ?? '';
        if (!self::validOpaque($token, 4096)) { $this->respond(400, ['error' => 'invalid_revoke_request']); return; }
        $this->strava->revoke($token, 'refresh_token', $this->config);
        $this->respond(200, ['status' => 'revoked']);
    }

    /** @return list<string> Safe check identifiers only; never includes configured values. */
    public static function configurationFailureCodes(array $config, string $documentRoot, string $urlBasePath): array
    {
        $failures = [];
        if (!DeploymentConfigPolicy::validBasePath($urlBasePath)) $failures[] = 'url_base_path_invalid';
        if (!isset($config['client_id']) || preg_match('/^[0-9]{1,20}$/', (string)$config['client_id']) !== 1) $failures[] = 'client_id_invalid';
        if (!isset($config['client_secret']) || !is_string($config['client_secret']) || strlen($config['client_secret']) < 20 || str_starts_with($config['client_secret'], 'REPLACE_')) $failures[] = 'client_secret_missing_or_placeholder';
        if (!isset($config['link_session_ttl_seconds']) || !is_int($config['link_session_ttl_seconds']) || $config['link_session_ttl_seconds'] < 60 || $config['link_session_ttl_seconds'] > 900) $failures[] = 'link_session_ttl_invalid';
        foreach (['private_storage' => 'private_storage_path_invalid', 'rate_limit_directory' => 'rate_limit_path_invalid'] as $key => $failure) {
            if (!isset($config[$key]) || !is_string($config[$key]) || !self::isPrivatePath($config[$key], $documentRoot)) $failures[] = $failure;
        }
        foreach (['curl' => 'php_curl_missing', 'json' => 'php_json_missing', 'openssl' => 'php_openssl_missing'] as $extension => $failure) {
            if (!extension_loaded($extension)) $failures[] = $failure;
        }
        if (PHP_VERSION_ID < 80100) $failures[] = 'php_version_unsupported';
        if (!function_exists('random_bytes')) $failures[] = 'php_random_bytes_missing';
        return $failures;
    }

    public static function isPrivatePath(string $path, string $documentRoot): bool
    {
        if ($path === '' || $path[0] !== '/' || preg_match('~(?:^|/)\.\.(?:/|$)~', $path) === 1) return false;
        $root = realpath($documentRoot);
        if (!is_string($root)) return false;
        $candidate = $path;
        while (!file_exists($candidate)) {
            $parent = dirname($candidate);
            if ($parent === $candidate) return false;
            $candidate = $parent;
        }
        $resolved = realpath($candidate);
        if (!is_string($resolved)) return false;
        $rootPrefix = strtolower(rtrim($root, DIRECTORY_SEPARATOR) . DIRECTORY_SEPARATOR);
        return !str_starts_with(strtolower(rtrim($resolved, DIRECTORY_SEPARATOR) . DIRECTORY_SEPARATOR), $rootPrefix);
    }

    private function healthReady(): bool
    {
        foreach (['private_storage' => 'storage', 'rate_limit_directory' => 'rate_limit'] as $key => $label) {
            $directory = (string)$this->config[$key];
            if (!is_dir($directory) && !mkdir($directory, 0700, true) && !is_dir($directory)) {
                error_log('[Wodra Strava] configuration check failed: ' . $label . '_directory_create_failed');
                return false;
            }
            if (!is_writable($directory) || !@chmod($directory, 0700)) {
                error_log('[Wodra Strava] configuration check failed: ' . $label . '_directory_permissions');
                return false;
            }
            $probe = $directory . '/.health-' . bin2hex(random_bytes(8));
            if (file_put_contents($probe, 'ok', LOCK_EX) !== 2) {
                error_log('[Wodra Strava] configuration check failed: ' . $label . '_directory_write_failed');
                return false;
            }
            @chmod($probe, 0600);
            if (!unlink($probe)) {
                error_log('[Wodra Strava] configuration check failed: ' . $label . '_directory_cleanup_failed');
                return false;
            }
        }
        return true;
    }

    private function rateLimit(): bool
    {
        $directory = (string)$this->config['rate_limit_directory'];
        if (!is_dir($directory) && !mkdir($directory, 0700, true) && !is_dir($directory)) return false;
        $ip = (string)($_SERVER['REMOTE_ADDR'] ?? 'unknown');
        $path = $directory . '/' . hash('sha256', $ip) . '.json';
        $lock = fopen($path . '.lock', 'c');
        if ($lock === false || !flock($lock, LOCK_EX)) return false;
        try {
            $entries = is_file($path) ? json_decode((string)file_get_contents($path), true) : [];
            if (!is_array($entries)) $entries = [];
            $entries = array_values(array_filter($entries, static fn($time) => is_int($time) && $time > time() - 60));
            if (count($entries) >= 30) return false;
            $entries[] = time();
            $encoded = json_encode($entries, JSON_THROW_ON_ERROR);
            if (file_put_contents($path, $encoded, LOCK_EX) !== strlen($encoded)) return false;
            @chmod($directory, 0700); @chmod($path, 0600); @chmod($path . '.lock', 0600);
            return true;
        } finally { flock($lock, LOCK_UN); fclose($lock); }
    }

    private function isSecureRequest(): bool
    {
        // Do not trust X-Forwarded-Proto from arbitrary clients; cPanel must terminate TLS here.
        return strtolower((string)($_SERVER['HTTPS'] ?? '')) === 'on' || (string)($_SERVER['SERVER_PORT'] ?? '') === '443';
    }

    private function method(string $method): bool { return ($_SERVER['REQUEST_METHOD'] ?? '') === $method; }
    private function readJson(): array
    {
        $raw = file_get_contents('php://input', false, null, 0, self::MAX_BODY + 1);
        if (!is_string($raw) || strlen($raw) > self::MAX_BODY) { $this->respond(413, ['error' => 'request_too_large']); exit; }
        $body = json_decode($raw, true);
        if (!is_array($body)) { $this->respond(400, ['error' => 'invalid_json']); exit; }
        return $body;
    }
    private function respond(int $status, array $body): void
    {
        http_response_code($status);
        echo json_encode($body, JSON_UNESCAPED_SLASHES | JSON_INVALID_UTF8_SUBSTITUTE);
    }
    private static function validGuid(mixed $value): bool
    {
        return is_string($value) && preg_match('/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i', $value) === 1;
    }
    private static function validLoopbackUri(mixed $value): bool
    {
        return LoopbackRedirectPolicy::accepts($value);
    }
    private static function validOpaque(mixed $value, int $max = 128): bool
    {
        return is_string($value) && strlen($value) >= 20 && strlen($value) <= $max && preg_match('/^[A-Za-z0-9._~-]+$/', $value) === 1;
    }
}
