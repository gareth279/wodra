<?php
declare(strict_types=1);

use Wodra\StravaBroker\FileSessionStore;
use Wodra\StravaBroker\LinkSessionRepository;
use Wodra\StravaBroker\LoopbackRedirectPolicy;
use Wodra\StravaBroker\DeploymentConfigPolicy;
use Wodra\StravaBroker\App;
require __DIR__ . '/../private/Broker.php';

$checks = 0;
$assert = static function (bool $condition, string $message) use (&$checks): void {
    $checks++;
    if (!$condition) throw new RuntimeException($message);
};

$assert(LoopbackRedirectPolicy::accepts('http://127.0.0.1:49152/strava/callback'), 'IPv4 callback with ephemeral port should be accepted');
$assert(LoopbackRedirectPolicy::accepts('http://localhost:65535/strava/callback'), 'localhost callback should be accepted');
foreach ([
    'https://127.0.0.1:49152/strava/callback',
    'http://example.com:49152/strava/callback',
    'http://127.0.0.1:49152/strava/callback?next=https://evil.test',
    'http://user@127.0.0.1:49152/strava/callback',
    'http://127.0.0.1:0/strava/callback',
    'http://127.0.0.1:49152/other',
    'http://127.0.0.1:49152/strava/callback#fragment',
] as $invalid) $assert(!LoopbackRedirectPolicy::accepts($invalid), 'unsafe callback accepted: ' . $invalid);

$assert(DeploymentConfigPolicy::validBasePath('/wodra/strava'), 'explicit nested base path should be accepted');
$assert(DeploymentConfigPolicy::validBasePath(''), 'root deployment path should be accepted');
$assert(!DeploymentConfigPolicy::validBasePath('/wodra/../private'), 'path traversal in base path must be rejected');
foreach ([
    ['/wodra/strava/health?check=1', '/wodra/strava', '/health'],
    ['/wodra/strava/', '/wodra/strava', '/'],
    ['/health', '', '/health'],
] as [$uri, $base, $expected]) $assert(DeploymentConfigPolicy::routePath($uri, $base) === $expected, 'request URI should route from explicit URL base path');
$assert(DeploymentConfigPolicy::routePath('/wrong/health', '/wodra/strava') === null, 'unrelated URL prefix must not match configured base path');
$assert(DeploymentConfigPolicy::routePath('/wodra/strava/health', '/wodra/stravaevil') === null, 'base path prefix must be segment exact');
foreach (['/index.php', '/wodra/strava/index.php', '/php-handler/index.php'] as $scriptName) {
    $_SERVER['SCRIPT_NAME'] = $scriptName;
    $assert(DeploymentConfigPolicy::routePath('/wodra/strava/health', '/wodra/strava') === '/health', 'routing must ignore cPanel SCRIPT_NAME value ' . $scriptName);
}

$configExample = file_get_contents(__DIR__ . '/../private/config.example.php');
$deploymentExample = file_get_contents(__DIR__ . '/../public/deployment.example.php');
$assert(is_string($configExample) && str_contains($configExample, 'REPLACE_WITH_STRAVA_CLIENT_SECRET'), 'private example must use a secret placeholder');
$assert(is_string($deploymentExample) && str_contains($deploymentExample, 'CPANEL_USER') && !str_contains($deploymentExample, 'client_secret'), 'deployment example must use path placeholders and contain no secret setting');
$failureCodes = App::configurationFailureCodes([
    'client_id' => '234943', 'client_secret' => 'REPLACE_WITH_STRAVA_CLIENT_SECRET',
    'private_storage' => '/tmp/wodra-private', 'rate_limit_directory' => '/tmp/wodra-private/rate',
    'link_session_ttl_seconds' => 600,
], sys_get_temp_dir(), '/bad/../path');
$assert(in_array('url_base_path_invalid', $failureCodes, true), 'configuration diagnostics must identify invalid URL base path');
$assert(in_array('client_secret_missing_or_placeholder', $failureCodes, true), 'configuration diagnostics must identify placeholder secret without exposing it');
$assert(!str_contains(implode(',', $failureCodes), 'REPLACE_WITH'), 'configuration diagnostic identifiers must never contain configured values');

$directory = sys_get_temp_dir() . '/wodra-strava-test-' . bin2hex(random_bytes(8));
try {
    $store = new FileSessionStore($directory);
    $repository = new LinkSessionRepository($store, 600);
    $redirect = 'http://127.0.0.1:49152/strava/callback';
    $first = $repository->create('a713e6c1-9fc4-4fe1-9f1e-e0e647530813', $redirect);
    $second = $repository->create('a713e6c1-9fc4-4fe1-9f1e-e0e647530813', $redirect);
    $assert(strlen($first['state']) >= 40 && $first['state'] !== $second['state'] && $first['session_id'] !== $second['session_id'], 'link state/session ids should be unpredictable and unique');
    $assert($repository->consume($first['session_id'], 'wrong-state-value-long-enough-123456', $redirect) === null, 'incorrect state must not consume a session');
    $assert($repository->consume($first['session_id'], $first['state'], 'http://127.0.0.1:49153/strava/callback') === null, 'callback URI must exactly match');
    $secretState = $first['state'];
    $persisted = file_get_contents($directory . '/link-sessions.json');
    $assert(is_string($persisted) && !str_contains($persisted, $secretState), 'raw OAuth state must not be written to storage');
    $assert($repository->consume($first['session_id'], $first['state'], $redirect) !== null, 'matching callback should consume a session');
    $assert($repository->consume($first['session_id'], $first['state'], $redirect) === null, 'a consumed link session must be single-use');
    $repository->cancel($second['session_id']);
    $assert($repository->consume($second['session_id'], $second['state'], $redirect) === null, 'cancelled session must no longer be usable');
    $expired = new LinkSessionRepository($store, -1);
    $expiredLink = $expired->create('a713e6c1-9fc4-4fe1-9f1e-e0e647530813', $redirect);
    $assert($repository->consume($expiredLink['session_id'], $expiredLink['state'], $redirect) === null, 'expired session must not be consumed');
} finally {
    foreach (glob($directory . '/*') ?: [] as $path) @unlink($path);
    @rmdir($directory);
}

fwrite(STDOUT, "PASS: {$checks} Strava broker checks\n");
