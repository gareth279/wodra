<?php
// Copy this file to config.php OUTSIDE public_html and set owner-only permissions.
return [
    'client_id' => '234943',
    'client_secret' => 'REPLACE_WITH_STRAVA_CLIENT_SECRET',
    'private_storage' => '/home/CPANEL_USER/wodra-strava-private',
    // Limit link sessions to Wodra profiles; desktop callback port remains ephemeral.
    'link_session_ttl_seconds' => 600,
    'rate_limit_directory' => '/home/CPANEL_USER/wodra-strava-private/rate',
];
