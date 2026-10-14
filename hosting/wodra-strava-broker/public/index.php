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

$privateDir = getenv('WODRA_STRAVA_PRIVATE_DIR');
$privatePath = is_string($privateDir) && $privateDir !== '' ? realpath($privateDir) : false;
$documentRoot = realpath((string)($_SERVER['DOCUMENT_ROOT'] ?? ''));
$insidePublicRoot = is_string($privatePath) && is_string($documentRoot) &&
    str_starts_with(strtolower($privatePath . DIRECTORY_SEPARATOR), strtolower(rtrim($documentRoot, DIRECTORY_SEPARATOR) . DIRECTORY_SEPARATOR));
if (!is_string($privatePath) || $insidePublicRoot || !is_file($privatePath . '/config.php') || !is_file($privatePath . '/Broker.php')) {
    http_response_code(503);
    header('Content-Type: application/json');
    echo '{"error":"service_not_configured"}';
    exit;
}

require_once $privatePath . '/Broker.php';
$config = require $privatePath . '/config.php';
$app = new App($config, new FileSessionStore($config['private_storage']), new StravaHttpClient());
$app->handle();
