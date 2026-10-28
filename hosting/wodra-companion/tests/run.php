<?php
declare(strict_types=1);

$base = rtrim((string)getenv('WODRA_COMPANION_BASE'), '/');
if ($base === '') throw new RuntimeException('Set WODRA_COMPANION_BASE to the local test server.');
$checks = 0;
$assert = static function (bool $ok, string $message) use (&$checks): void { $checks++; if (!$ok) throw new RuntimeException($message); };
$post = static function (array $body) use ($base): array {
    $context = stream_context_create(['http'=>['method'=>'POST','header'=>"Content-Type: application/json\r\nOrigin: http://127.0.0.1:8799\r\n",'content'=>json_encode(['protocol'=>1]+$body, JSON_THROW_ON_ERROR),'ignore_errors'=>true,'timeout'=>5]]);
    $result = file_get_contents($base . '/api.php', false, $context);
    if (!is_string($result)) throw new RuntimeException('Local relay did not return a response.');
    $status = 0; foreach ($http_response_header ?? [] as $header) if (preg_match('~HTTP/\S+\s+(\d+)~', $header, $m)) $status=(int)$m[1];
    return [$status, json_decode($result, true, 32, JSON_THROW_ON_ERROR)];
};
$post2 = static function (array $body, ?string $cookie = null, string $origin = 'http://127.0.0.1:8799') use ($base): array {
	$headers = "Content-Type: application/json\r\nOrigin: $origin\r\n";
    if ($cookie !== null) $headers .= "Cookie: WodraPhone=$cookie\r\n";
    $context = stream_context_create(['http'=>['method'=>'POST','header'=>$headers,
        'content'=>json_encode(['protocol'=>2]+$body, JSON_THROW_ON_ERROR),'ignore_errors'=>true,'timeout'=>5]]);
    $result = file_get_contents($base . '/api.php', false, $context);
    if (!is_string($result)) throw new RuntimeException('Local remembered-device API did not return a response.');
    $status = 0; $headersOut = $http_response_header ?? [];
    foreach ($headersOut as $header) if (preg_match('~HTTP/\S+\s+(\d+)~', $header, $m)) $status=(int)$m[1];
    return [$status, json_decode($result, true, 32, JSON_THROW_ON_ERROR), $headersOut];
};
$hash = static fn(string $s): string => hash('sha256', $s);
$token = static fn(): string => rtrim(strtr(base64_encode(random_bytes(32)), '+/', '-_'), '=');
$create = static function () use ($post, $hash, $token): array {
    $invite=$token();$desktop=$token();[$status,$r]=$post(['action'=>'create','invite_hash'=>$hash($invite),'desktop_hash'=>$hash($desktop)]);
    if ($status!==200) throw new RuntimeException('Create invite failed: HTTP '.$status.' '.json_encode($r));
    return [$r['id'],$r['code'],$invite,$desktop];
};
$claim = static function (string $code) use ($post,$hash,$token): array {
    $phone=$token();[$status,$r]=$post(['action'=>'claim','code'=>$code,'companion_hash'=>$hash($phone)]);
    if ($status!==200) throw new RuntimeException('Claim failed.'); return [$r['id'],$phone];
};
$approve = static function (string $id,string $invite,bool $allow) use ($post,$hash): array {
    return $post(['action'=>'decide','id'=>$id,'invite_hash'=>$hash($invite),'approve'=>$allow]);
};

[$id,$code,$invite,$desktop]=$create();
$assert(strlen($code)===10 && preg_match('/^[A-F0-9]{10}$/',$code)===1, 'Pairing code must have 40 random bits in the displayed alphabet.');
[$status]=$post(['action'=>'claim','code'=>'AAAAAAAAAA','companion_hash'=>$hash($token())]);$assert($status===404,'Wrong code must not claim an invitation.');
[$pairId,$phone]=$claim($code);$assert($pairId===$id,'Claim must return the invitation pair identity.');
[$status]=$post(['action'=>'claim','code'=>$code,'companion_hash'=>$hash($token())]);$assert($status===404,'Pairing code must be single use.');
[$status,$r]=$post(['action'=>'invite_status','id'=>$id,'invite_hash'=>$hash($invite)]);$assert($status===200 && $r['status']==='pending' && $r['phone_requested']===true,'Desktop must observe a pending phone before approval.');
[$status,$r]=$post(['action'=>'pair_status','id'=>$id,'token_hash'=>$hash($phone)]);$assert($status===200 && $r['status']==='pending','Phone must wait for desktop approval.');
[$status,$r]=$approve($id,$invite,false);$assert($status===200 && $r['status']==='rejected','Desktop decline must be explicit.');
[$status,$r]=$post(['action'=>'pair_status','id'=>$id,'token_hash'=>$hash($phone)]);$assert($status===200 && $r['status']==='rejected','Phone should be told when approval was declined.');

[$id2,$code2,$invite2,$desktop2]=$create();[$pairId2,$phone2]=$claim($code2);
[$status,$r]=$approve($id2,$invite2,true);$assert($status===200 && $r['status']==='active','Approved pair must become active.');
[$status,$r]=$post(['action'=>'pair_status','id'=>$id2,'token_hash'=>$hash($phone2)]);$assert($status===200 && $r['status']==='active','Phone token should become active only after desktop approval.');
$snapshot=['state'=>'riding','connected'=>true,'paused'=>false,'data_stale'=>false,'route'=>'Dunedin Test Route','speed_kph'=>31.2,'power_w'=>214,'cadence_rpm'=>89,'heart_rate_bpm'=>null,'distance_m'=>1250.5,'elapsed_s'=>234.25,'gradient_pct'=>-1.2,'captured_at'=>gmdate('c')];
[$status,$r]=$post(['action'=>'publish','id'=>$id2,'token_hash'=>$hash($desktop2),'seq'=>1,'snapshot'=>$snapshot]);$assert($status===200 && $r['status']==='accepted','Desktop role must publish a bounded latest snapshot: HTTP '.$status.' '.json_encode($r));
[$status,$r]=$post(['action'=>'read','id'=>$id2,'token_hash'=>$hash($phone2)]);$assert($status===200 && $r['snapshot']['power_w']===214 && $r['snapshot']['heart_rate_bpm']===null,'Companion should read current telemetry while missing HR stays unavailable.');
[$status]=$post(['action'=>'publish','id'=>$id2,'token_hash'=>$hash($phone2),'seq'=>2,'snapshot'=>$snapshot]);$assert($status===401,'Companion credential must not publish.');
[$status]=$post(['action'=>'read','id'=>$id2,'token_hash'=>$hash($desktop2)]);$assert($status===401,'Desktop credential must not read as the companion role.');
[$status]=$post(['action'=>'publish','id'=>$id2,'token_hash'=>$hash($desktop2),'seq'=>1,'snapshot'=>$snapshot]);$assert($status===409,'Repeated sequence must be rejected.');
$bad=$snapshot;$bad['profile']='private';[$status]=$post(['action'=>'publish','id'=>$id2,'token_hash'=>$hash($desktop2),'seq'=>2,'snapshot'=>$bad]);$assert($status===400,'Unknown snapshot fields must be rejected.');
$bad=$snapshot;$bad['power_w']='214';[$status]=$post(['action'=>'publish','id'=>$id2,'token_hash'=>$hash($desktop2),'seq'=>2,'snapshot'=>$bad]);$assert($status===400,'Telemetry numbers must not be accepted as strings.');
[$id3,$code3,$invite3,$desktop3]=$create();[$pairId3,$phone3]=$claim($code3);[$status]=$approve($id3,$invite3,true);$assert($status===200,'Second pair can be independently approved.');
[$status,$r]=$post(['action'=>'read','id'=>$id3,'token_hash'=>$hash($phone3)]);$assert($status===200 && $r['snapshot']===null,'Separate pair must not see another pair snapshot.');
$workers=[];
putenv('WODRA_TEST_BASE='.$base);putenv('WODRA_TEST_ID='.$id3);putenv('WODRA_TEST_HASH='.$hash($phone3));
for($i=0;$i<6;$i++){
    $pipes=[];$proc=proc_open([PHP_BINARY,__DIR__.'/parallel-poll.php'],[0=>['pipe','r'],1=>['pipe','w'],2=>['pipe','w']],$pipes);
    if(!is_resource($proc))throw new RuntimeException('Could not start concurrent poll client.');
    fclose($pipes[0]);$workers[]=[$proc,$pipes];
}
foreach($workers as [$proc,$pipes]){$out=stream_get_contents($pipes[1]);$err=stream_get_contents($pipes[2]);fclose($pipes[1]);fclose($pipes[2]);$exit=proc_close($proc);$assert($exit===0,'Concurrent authenticated poll failed: '.$out.$err);}
[$status,$r]=$post(['action'=>'disconnect','id'=>$id2,'token_hash'=>$hash($phone2)]);$assert($status===200,'Phone can revoke its pair.');
[$status]=$post(['action'=>'read','id'=>$id2,'token_hash'=>$hash($phone2)]);$assert($status===404,'Revoked pair must become unreadable.');
[$id4,$code4,$invite4,$desktop4]=$create();[$pairId4,$phone4]=$claim($code4);[$status]=$post(['action'=>'cancel_invite','id'=>$id4,'invite_hash'=>$hash($invite4)]);$assert($status===200,'Desktop can cancel a pending request.');
[$status,$r]=$post(['action'=>'pair_status','id'=>$id4,'token_hash'=>$hash($phone4)]);$assert($status===200 && $r['status']==='cancelled','Cancelled invitation should notify the phone.');
[$id5,$code5,$invite5,$desktop5]=$create();
$sessionsPath=(string)getenv('WODRA_COMPANION_TEST_STORAGE').'/sessions.json';
$state=json_decode((string)file_get_contents($sessionsPath),true,64,JSON_THROW_ON_ERROR);
$state['sessions'][$id5]['expires']=time()-1;
file_put_contents($sessionsPath,json_encode($state,JSON_THROW_ON_ERROR),LOCK_EX);
[$status]=$post(['action'=>'claim','code'=>$code5,'companion_hash'=>$hash($token())]);$assert($status===404,'Expired invitations must not be claimable.');
[$status]=$post(['action'=>'invite_status','id'=>$id5,'invite_hash'=>$hash($invite5)]);$assert($status===401 || $status===404 || $status===410,'Expired or removed invitation must never expose pairing state.');

// Protocol v2 separates a stable desktop identity, one-time invitation, remembered
// browser credential and the current live snapshot.
$desktopId=$token();$desktopCredential=$token();$desktopHash=$hash($desktopCredential);
[$status,$invite]=$post2(['action'=>'remembered_create','desktop_id'=>$desktopId,'desktop_hash'=>$desktopHash]);
$assert($status===200 && strlen($invite['code']??'')===10,'Desktop creates a short-lived remembered-device invitation.');
$inviteId=$invite['id'];$pairCode=$invite['code'];
[$status,$inviteState]=$post2(['action'=>'remembered_invite_status','desktop_id'=>$desktopId,'desktop_hash'=>$desktopHash,'invite_id'=>$inviteId]);
$assert($status===200 && $inviteState['status']==='invited','New remembered-device invitation remains available after desktop polling.');
[$status,$paired,$responseHeaders]=$post2(['action'=>'remembered_claim','code'=>$pairCode]);
$setCookie=implode("\n",array_filter($responseHeaders,static fn($header)=>stripos($header,'Set-Cookie: WodraPhone=')===0));
$assert($status===200 && $paired['status']==='paired' && $paired['label']==='Phone 1','QR claim pairs a phone without another approval click.');
$assert(preg_match('/WodraPhone=([A-Za-z0-9_-]{40,48})/',$setCookie,$cookieMatch)===1
    && stripos($setCookie,'httponly')!==false && stripos($setCookie,'samesite=strict')!==false
    && !isset($paired['_set_phone_cookie']),'Browser credential is delivered as HttpOnly SameSite cookie, not JSON.');
$phoneCookie=$cookieMatch[1]??'';
[$status]=$post2(['action'=>'remembered_claim','code'=>$pairCode]);$assert($status===404,'Claimed pairing invitation cannot be replayed.');
[$status,$inviteState]=$post2(['action'=>'remembered_invite_status','desktop_id'=>$desktopId,'desktop_hash'=>$desktopHash,'invite_id'=>$inviteId]);
$assert($status===200 && $inviteState['status']==='claimed' && $inviteState['device_id']===$paired['device_id'],'Desktop observes successful phone pairing.');
$routeMeta=['version'=>'route-test-v1','id'=>'test-route','name'=>'Test Loop','environment'=>'Velodrome','discipline'=>'Track','type'=>'Loop','distance_m'=>250.0,'ascent_m'=>0.0,
    'points'=>[[0,0.0,0.0,0.0,0.0],[125,10.0,5.0,1.0,0.0],[250,0.0,0.0,0.0,0.0]]];
[$status]=$post2(['action'=>'remembered_route_publish','desktop_id'=>$desktopId,'desktop_hash'=>$desktopHash,'version'=>'route-test-v1','route'=>$routeMeta]);
$assert($status===200,'Desktop publishes bounded static route metadata through authenticated relay action.');
$snapshot=['state'=>'riding','connected'=>true,'paused'=>false,'data_stale'=>false,'route'=>'Remembered device test','speed_kph'=>30.0,'power_w'=>210,'cadence_rpm'=>90,'heart_rate_bpm'=>null,'distance_m'=>500.0,'elapsed_s'=>60.0,'gradient_pct'=>0.0,
    'route_distance_m'=>250.0,'route_progress'=>0.0,'lap'=>3,'remaining_m'=>null,'environment'=>'Velodrome','route_type'=>'Loop',
    'riders'=>[['name'=>'Alex','kind'=>'AI','relative_m'=>50.0]],'captured_at'=>gmdate('c')];
[$status,$published]=$post2(['action'=>'remembered_publish','desktop_id'=>$desktopId,'desktop_hash'=>$desktopHash,'seq'=>1,'snapshot'=>$snapshot]);
$assert($status===200 && $published['status']==='accepted','Desktop identity publishes its current ride snapshot.');
[$status,$read]=$post2(['action'=>'remembered_read','route_version'=>''],$phoneCookie);
$assert($status===200 && $read['status']==='remembered' && $read['snapshot']['power_w']===210 && $read['route']['version']==='route-test-v1'
    && $read['snapshot']['lap']===3 && $read['snapshot']['riders'][0]['kind']==='AI','Remembered browser receives live metrics, route metadata and bounded rider summaries.');
[$status,$cachedRead]=$post2(['action'=>'remembered_read','route_version'=>'route-test-v1'],$phoneCookie);
$assert($status===200 && $cachedRead['route']===null,'Static route geometry is omitted when the phone already has its version cached.');
[$status,$olderClientRead]=$post2(['action'=>'remembered_read'],$phoneCookie);
$assert($status===200 && $olderClientRead['route']===null,'Older companion clients that do not send a route version never receive repeated geometry payloads.');
[$status,$devices]=$post2(['action'=>'remembered_devices','desktop_id'=>$desktopId,'desktop_hash'=>$desktopHash]);
$assert($status===200 && count($devices['devices'])===1 && $devices['devices'][0]['label']==='Phone 1'
    && !isset($devices['devices'][0]['credential_hash']),'Desktop can list generic device labels without exposing credentials.');
[$status]=$post2(['action'=>'remembered_devices','desktop_id'=>$token(),'desktop_hash'=>$hash($token())]);
$assert($status===401,'Unrelated desktop cannot enumerate trusted devices.');
[$status]=$post2(['action'=>'remembered_read'],null,'https://attacker.example');
$assert($status===403,'Cross-origin browser requests cannot read remembered telemetry.');
[$status]=$post2(['action'=>'remembered_read']);$assert($status===401,'A browser without the first-party credential cannot discover a desktop session.');
[$status]=$post2(['action'=>'remembered_idle','desktop_id'=>$desktopId,'desktop_hash'=>$desktopHash]);
[$status,$idle]=$post2(['action'=>'remembered_read'],$phoneCookie);
 $assert($status===200 && $idle['snapshot']===null && $idle['route_version']==='none','Ending a ride clears its live snapshot and route cache without removing phone trust.');
[$status]=$post2(['action'=>'remembered_route_publish','desktop_id'=>$desktopId,'desktop_hash'=>$desktopHash,'version'=>'route-test-v1','route'=>$routeMeta]);
 $assert($status===200,'A later ride republishes its static route metadata once.');
[$status]=$post2(['action'=>'remembered_publish','desktop_id'=>$desktopId,'desktop_hash'=>$desktopHash,'seq'=>2,'snapshot'=>$snapshot]);
[$status,$reconnected]=$post2(['action'=>'remembered_read'],$phoneCookie);
$assert($status===200 && $reconnected['snapshot']['route']==='Remembered device test','The same desktop identity resumes publishing after restart or a later ride.');
[$status,$anotherInvite]=$post2(['action'=>'remembered_create','desktop_id'=>$desktopId,'desktop_hash'=>$desktopHash]);
[$status,$anotherPhone,$anotherHeaders]=$post2(['action'=>'remembered_claim','code'=>$anotherInvite['code']]);
preg_match('/WodraPhone=([A-Za-z0-9_-]{40,48})/',implode("\n",$anotherHeaders),$anotherCookieMatch);
$anotherCookie=$anotherCookieMatch[1]??'';
[$status,$devices]=$post2(['action'=>'remembered_devices','desktop_id'=>$desktopId,'desktop_hash'=>$desktopHash]);
$assert($status===200 && count($devices['devices'])===2 && $anotherPhone['label']==='Phone 2','One desktop can remember multiple independent phones.');
[$status]=$post2(['action'=>'remembered_revoke','desktop_id'=>$desktopId,'desktop_hash'=>$desktopHash,'device_id'=>$paired['device_id']]);
$assert($status===200,'Desktop can revoke a phone server-side.');
[$status]=$post2(['action'=>'remembered_read'],$phoneCookie);$assert($status===401,'Revoked phone can no longer read the live snapshot.');
[$status,$otherPhoneRead]=$post2(['action'=>'remembered_read'],$anotherCookie);
$assert($status===200 && $otherPhoneRead['snapshot']['route']==='Remembered device test','Revoking one phone leaves other trusted devices usable.');
$statePath=(string)getenv('WODRA_COMPANION_TEST_STORAGE').'/sessions.json';
$state=json_decode((string)file_get_contents($statePath),true,64,JSON_THROW_ON_ERROR);
$state['remembered_devices'][$anotherPhone['device_id']]['expires']=time()+20*86400;
file_put_contents($statePath,json_encode($state,JSON_THROW_ON_ERROR),LOCK_EX);
[$status,$renewed,$renewalHeaders]=$post2(['action'=>'remembered_read'],$anotherCookie);
$assert($status===200 && stripos(implode("\n",$renewalHeaders),'Set-Cookie: WodraPhone=')!==false,
    'A trusted browser credential nearing expiry is renewed securely.');
$state=json_decode((string)file_get_contents($statePath),true,64,JSON_THROW_ON_ERROR);
$state['remembered_devices'][$anotherPhone['device_id']]['expires']=time()-1;
file_put_contents($statePath,json_encode($state,JSON_THROW_ON_ERROR),LOCK_EX);
[$status]=$post2(['action'=>'remembered_read'],$anotherCookie);
$assert($status===401,'Expired trusted browser credentials are rejected and require a new pairing.');
[$status,$secondInvite]=$post2(['action'=>'remembered_create','desktop_id'=>$desktopId,'desktop_hash'=>$desktopHash]);
[$status,$secondPaired,$secondHeaders]=$post2(['action'=>'remembered_claim','code'=>$secondInvite['code']]);
preg_match('/WodraPhone=([A-Za-z0-9_-]{40,48})/',implode("\n",$secondHeaders),$secondCookieMatch);
$secondCookie=$secondCookieMatch[1]??'';
[$status,$forgot,$forgotHeaders]=$post2(['action'=>'remembered_forget'],$secondCookie);
$assert($status===200 && $forgot['status']==='forgotten' && stripos(implode("\n",$forgotHeaders),'expires=thu, 01 jan 1970')!==false,
    'Forget revokes trust and clears the HttpOnly phone cookie.');
[$status]=$post2(['action'=>'remembered_read'],$secondCookie);$assert($status===401,'Forgotten phone can no longer read telemetry.');
[$status,$forgotUnknown,$forgotUnknownHeaders]=$post2(['action'=>'remembered_forget'],'invalid-cookie');
$assert($status===200 && $forgotUnknown['status']==='forgotten' && stripos(implode("\n",$forgotUnknownHeaders),'Set-Cookie: WodraPhone=')!==false,
    'Forget still clears a stale or malformed browser cookie without granting access.');
$rememberedState=json_decode((string)file_get_contents((string)getenv('WODRA_COMPANION_TEST_STORAGE').'/sessions.json'),true,64,JSON_THROW_ON_ERROR);
$rememberedJson=json_encode($rememberedState,JSON_THROW_ON_ERROR);
$assert(!str_contains($rememberedJson,$phoneCookie) && !str_contains($rememberedJson,$secondCookie),'Server persists only hashes of browser credentials.');

$storage = (string)getenv('WODRA_COMPANION_TEST_STORAGE');
$file = $storage . '/sessions.json';$persisted=file_get_contents($file);
$assert(is_string($persisted) && !str_contains($persisted,$code2) && !str_contains($persisted,$desktop2) && !str_contains($persisted,$phone2),'Raw code and role credentials must not be persisted.');
$assert(strlen((string)$persisted)<1000000,'Relay storage must remain bounded.');

fwrite(STDOUT,"PASS: {$checks} companion relay checks\n");
