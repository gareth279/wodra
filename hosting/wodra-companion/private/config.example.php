<?php
declare(strict_types=1);

// Copy outside the public_html/document-root tree. Never commit the configured copy.
return [
    'origin' => 'https://your-companion-domain.example',
    'storage' => '/home/CPANEL_USER/wodra-private/companion',
    'invite_ttl_seconds' => 180,
    'pair_ttl_seconds' => 28800,
    'allow_insecure_local' => false,
];
