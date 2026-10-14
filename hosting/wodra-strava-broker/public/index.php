<?php
declare(strict_types=1);

use Wodra\StravaBroker\App;
use Wodra\StravaBroker\FileSessionStore;
use Wodra\StravaBroker\StravaHttpClient;

ini_set('display_errors', '0');
ini_set('log_errors', '1');
header('Cache-Control: no-store');
header('X-Content-Type-Options: nosniff');
header('Referrer-Policy: no-referrer');
header('Content-Type: application/json; charset=utf-8');

$fail = static function (string $check): never {
    error_log('[Wodra Strava] configuration check failed: ' . $check);
    http_response_code(503);
    echo '{"error":"service_not_configured"}';
    exit;
};

$deploymentFile = __DIR__ . '/deployment.php';
if (!is_file($deploymentFile)) $fail('deployment_file_missing');
try { $deployment = require $deploymentFile; }
catch (Throwable) { $fail('deployment_file_unreadable'); }
if (!is_array($deployment) || !isset($deployment['private_directory'], $deployment['url_base_path']) ||
    !is_string($deployment['private_directory']) || !is_string($deployment['url_base_path'])) $fail('deployment_config_invalid');

$documentRoot = realpath((string)($_SERVER['DOCUMENT_ROOT'] ?? ''));
$privateDirectory = realpath($deployment['private_directory']);
if (!is_string($documentRoot)) $fail('document_root_unavailable');
if (!is_string($privateDirectory) || !is_dir($privateDirectory)) $fail('private_directory_missing');
$rootPrefix = strtolower(rtrim($documentRoot, DIRECTORY_SEPARATOR) . DIRECTORY_SEPARATOR);
$privatePrefix = strtolower(rtrim($privateDirectory, DIRECTORY_SEPARATOR) . DIRECTORY_SEPARATOR);
if ($privatePrefix === $rootPrefix || str_starts_with($privatePrefix, $rootPrefix)) $fail('private_directory_inside_document_root');
if ((fileperms($privateDirectory) & 0077) !== 0) $fail('private_directory_permissions');
if (!is_file($privateDirectory . '/Broker.php')) $fail('private_broker_file_missing');
if (!is_file($privateDirectory . '/config.php')) $fail('private_config_file_missing');
if ((fileperms($privateDirectory . '/config.php') & 0077) !== 0) $fail('private_config_permissions');

require_once $privateDirectory . '/Broker.php';
try { $config = require $privateDirectory . '/config.php'; }
catch (Throwable) { $fail('private_config_unreadable'); }
if (!is_array($config)) $fail('private_config_invalid');
$failures = App::configurationFailureCodes($config, $documentRoot, $deployment['url_base_path']);
if ($failures !== []) {
    error_log('[Wodra Strava] configuration check failed: ' . implode(',', $failures));
    http_response_code(503);
    echo '{"error":"service_not_configured"}';
    exit;
}

$app = new App($config, new FileSessionStore($config['private_storage']), new StravaHttpClient(), $deployment['url_base_path']);
$app->handle();
