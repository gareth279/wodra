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

$storage = (string)getenv('WODRA_COMPANION_TEST_STORAGE');
$file = $storage . '/sessions.json';$persisted=file_get_contents($file);
$assert(is_string($persisted) && !str_contains($persisted,$code2) && !str_contains($persisted,$desktop2) && !str_contains($persisted,$phone2),'Raw code and role credentials must not be persisted.');
$assert(strlen((string)$persisted)<1000000,'Relay storage must remain bounded.');

fwrite(STDOUT,"PASS: {$checks} companion relay checks\n");
