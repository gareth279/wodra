<?php
declare(strict_types=1);
$base=(string)getenv('WODRA_TEST_BASE');$id=(string)getenv('WODRA_TEST_ID');$hash=(string)getenv('WODRA_TEST_HASH');
$ctx=stream_context_create(['http'=>['method'=>'POST','header'=>"Content-Type: application/json\r\nOrigin: http://127.0.0.1:8799\r\n",'content'=>json_encode(['protocol'=>1,'action'=>'read','id'=>$id,'token_hash'=>$hash]),'ignore_errors'=>true,'timeout'=>5]]);
$body=file_get_contents($base.'/api.php',false,$ctx);$status=0;foreach($http_response_header??[] as $h)if(preg_match('~HTTP/\S+\s+(\d+)~',$h,$m))$status=(int)$m[1];
if($status!==200||!is_string($body)||!str_contains($body,'"protocol":1'))exit(1);
