(() => {
  'use strict';
  const api = new URL('api.php', location.href).toString();
  const $ = id => document.getElementById(id);
  const pairView = $('pair-view'), rideView = $('ride-view'), msg = $('pair-message'), badge = $('connection');
  const storageKey = 'wodra-companion-pair-v1', pendingKey = 'wodra-companion-pending-v1';
  let pair = null, pending = null, timer = null, busy = false, failures = 0;
  const token = () => { const b = crypto.getRandomValues(new Uint8Array(32)); return btoa(String.fromCharCode(...b)).replaceAll('+','-').replaceAll('/','_').replaceAll('=',''); };
  async function digest(value) { const bytes = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(value)); return [...new Uint8Array(bytes)].map(v => v.toString(16).padStart(2,'0')).join(''); }
  async function post(body) {
    const controller = new AbortController(), timeout = setTimeout(() => controller.abort(), 5000);
    try { const response = await fetch(api,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({protocol:1,...body}),cache:'no-store',credentials:'same-origin',signal:controller.signal}); const data=await response.json(); if(!response.ok) throw new Error(data.error||'request_failed'); return data; }
    finally { clearTimeout(timeout); }
  }
  function showPair(message='') { pairView.hidden=false; rideView.hidden=true; badge.textContent='Not connected';badge.className='pill';msg.textContent=message; }
  function value(id,v,digits=0){$(id).textContent=Number.isFinite(v)?Number(v).toFixed(digits):'—';}
  function duration(v){if(!Number.isFinite(v))return '—';const s=Math.max(0,Math.floor(v)),h=Math.floor(s/3600),m=Math.floor(s%3600/60),r=s%60;return `${h}:${String(m).padStart(2,'0')}:${String(r).padStart(2,'0')}`;}
  function render(data){
    const snap=data.snapshot, age=Date.now()/1000-Number(data.snapshot_at||0), stale=!snap||age>12;
    pairView.hidden=true;rideView.hidden=false;
    if(stale){badge.textContent='Waiting for Wodra';badge.className='pill warn';$('ride-state').textContent='No recent ride data. Check that Wodra is running and paired.';$('ride-state').className='state-banner';$('route-name').textContent=snap?.route||'Waiting for ride';['power','speed','cadence','heart','distance','elapsed','gradient','trainer'].forEach(id=>$(id).textContent='—');$('updated').textContent='';return;}
    const state=snap.state==='ended'?'Ride ended':snap.paused?'Ride paused':snap.state==='riding'?'Ride in progress':'Waiting for ride';
    const banner=$('ride-state');banner.textContent=snap.data_stale?'Trainer data is stale · live measurements unavailable':state;banner.className='state-banner '+(snap.state==='riding'&&snap.connected&&!snap.data_stale?'live':'');
    badge.textContent=snap.state==='ended'?'Ride ended':snap.connected?'Live':'Trainer disconnected';badge.className='pill '+(snap.state==='riding'&&snap.connected&&!snap.data_stale?'live':'warn');
    $('route-name').textContent=snap.route||'Route unavailable';
    value('power',snap.power_w);value('speed',snap.speed_kph,1);value('cadence',snap.cadence_rpm);value('heart',snap.heart_rate_bpm);value('distance',Number.isFinite(snap.distance_m)?snap.distance_m/1000:NaN,2);$('elapsed').textContent=duration(snap.elapsed_s);value('gradient',snap.gradient_pct,1);$('trainer').textContent=snap.connected?'Connected':'Disconnected';
    $('updated').textContent=`Updated ${new Date(data.snapshot_at*1000).toLocaleTimeString([], {hour:'2-digit',minute:'2-digit',second:'2-digit'})}`;
  }
  async function poll(){
    if(document.hidden||busy||(!pair&&!pending))return;busy=true;
    try {
      if(pending){const status=await post({action:'pair_status',id:pending.id,token_hash:await digest(pending.token)});
        if(status.status==='active'){pair=pending;pending=null;sessionStorage.setItem(storageKey,JSON.stringify(pair));sessionStorage.removeItem(pendingKey);pairView.hidden=true;rideView.hidden=false;}
        else if(['rejected','cancelled','expired'].includes(status.status)){pending=null;sessionStorage.removeItem(pendingKey);showPair(status.status==='rejected'?'Pairing was declined in Wodra.':'Pairing expired or was cancelled. Request a new code in Wodra.');return;}
        else{msg.textContent='Waiting for approval in Wodra. Keep this page open.';failures=0;return;}}
      if(pair){render(await post({action:'read',id:pair.id,token_hash:await digest(pair.token)}));failures=0;}
    } catch(error) {
      if(pending&&['expired','not_found','unauthorized'].includes(error.message)){pending=null;sessionStorage.removeItem(pendingKey);showPair('Pairing expired or was cancelled. Request a new code in Wodra.');return;}
      if(pair&&['expired','not_found','unauthorized'].includes(error.message)){pair=null;sessionStorage.removeItem(storageKey);showPair('Pairing is no longer available. Reconnect from Wodra.');return;}
      failures++;if(failures>=2){badge.textContent='Reconnecting';badge.className='pill warn';if(pair){$('ride-state').textContent='Connection unavailable. Retrying automatically.';$('ride-state').className='state-banner';}else msg.textContent='Waiting for Wodra. Retrying securely…';}
    } finally {busy=false;}
  }
  function schedule(){clearTimeout(timer);if((pair||pending)&&!document.hidden)timer=setTimeout(async()=>{await poll();schedule();},Math.min(12000,3000*Math.max(1,failures)));}
  function connect(saved){if(!saved||typeof saved.id!=='string'||typeof saved.token!=='string')return false;pair=saved;pairView.hidden=true;rideView.hidden=false;badge.textContent='Connecting';badge.className='pill warn';poll();schedule();return true;}
  $('pair-form').addEventListener('submit',async e=>{
    e.preventDefault();const code=$('pair-code').value.replace(/[^a-z0-9]/gi,'').toUpperCase();if(code.length!==10){msg.textContent='Enter the 10-character code from Wodra.';return;}
    msg.textContent='Requesting secure pairing…';try{const companion=token(),result=await post({action:'claim',code,companion_hash:await digest(companion)});if(result.status!=='pending'||!result.id)throw new Error('pairing_unavailable');pending={id:result.id,token:companion};sessionStorage.setItem(pendingKey,JSON.stringify(pending));msg.textContent='Waiting for approval in Wodra. Keep this page open.';poll();schedule();}
    catch(error){msg.textContent=error.message==='not_found'?'Code is wrong, expired or already used. Request a new one in Wodra.':'Pairing could not be requested. Try again.';}
  });
  $('disconnect').addEventListener('click',async()=>{if(pair)try{await post({action:'disconnect',id:pair.id,token_hash:await digest(pair.token)});}catch{}sessionStorage.removeItem(storageKey);pair=null;clearTimeout(timer);showPair('Disconnected.');});
  document.addEventListener('visibilitychange',()=>{clearTimeout(timer);if(!document.hidden){poll();schedule();}});
  const fragment=location.hash.slice(1);if(fragment){$('pair-code').value=fragment.replace(/[^a-z0-9]/gi,'').slice(0,10).toUpperCase();history.replaceState(null,'',location.pathname+location.search);}
  let saved=null;try{saved=JSON.parse(sessionStorage.getItem(storageKey)||'null');}catch{sessionStorage.removeItem(storageKey);}
  if(!connect(saved)){try{pending=JSON.parse(sessionStorage.getItem(pendingKey)||'null');}catch{sessionStorage.removeItem(pendingKey);}if(pending){poll();schedule();}else showPair();}
})();
