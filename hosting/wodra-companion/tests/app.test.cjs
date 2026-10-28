const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const source = fs.readFileSync(require('node:path').join(__dirname, '../public/app.js'), 'utf8');
const elements = new Map();
const handlers = new Map();
const saved = [];
const element = id => {
  if (!elements.has(id)) elements.set(id, {id, hidden:false, value:'', textContent:'', className:'', checked:true,
    addEventListener(name, callback) { handlers.set(`${id}:${name}`, callback); }});
  return elements.get(id);
};
let reads = 0;
const actions = [];
const fetch = async (_url, options) => {
  const body = JSON.parse(options.body); actions.push(body.action);
  const reply = (ok, data) => ({ok, json:async()=>({protocol:body.protocol,...data})});
  if (body.action === 'remembered_claim') return reply(true,{status:'paired', device_id:'device-id'});
  if (body.action === 'remembered_forget') return reply(true,{status:'forgotten'});
  if (body.action === 'remembered_read') {
    reads++;
    if (reads === 1) return reply(false,{error:'unauthorized'});
    if (reads === 2) return reply(true,{status:'remembered',desktop_online:false,snapshot:null,snapshot_at:0});
    return reply(true,{status:'remembered',desktop_online:true,snapshot_at:Math.floor(Date.now()/1000),snapshot:{
      state:'riding',connected:true,paused:false,data_stale:false,route:'Test route',speed_kph:30,power_w:200,
      cadence_rpm:90,heart_rate_bpm:null,distance_m:500,elapsed_s:60,gradient_pct:0}});
  }
  throw new Error(`Unexpected companion API action ${body.action}`);
};
const document = {hidden:false, addEventListener(name, callback) { handlers.set(`document:${name}`, callback); },
  getElementById:id=>element(id)};
const store = {getItem:()=>null, setItem:(key,value)=>saved.push(['set',key,value]), removeItem:key=>saved.push(['remove',key,null])};
const window = {WodraWakeLock:{setRideActive:()=>Promise.resolve()}};
const context = {document, window, location:{href:'https://wodra.g5.co.nz/',hash:'',pathname:'/',search:''},
  history:{replaceState(){}}, localStorage:store, sessionStorage:store, fetch,
  setTimeout:()=>1, clearTimeout(){}, AbortController, URL, Date, Math, Number, String, JSON, Error, Promise};
vm.runInNewContext(source, context);

function bootFailure(fetchStub) {
  const scenarioElements = new Map();
  const scenarioElement = id => {
    if (!scenarioElements.has(id)) scenarioElements.set(id,{id,hidden:false,value:'',textContent:'',className:'',checked:true,
      addEventListener(){}});
    return scenarioElements.get(id);
  };
  const scenarioDocument = {hidden:false,addEventListener(){},getElementById:scenarioElement};
  vm.runInNewContext(source,{document:scenarioDocument,window:{WodraWakeLock:{setRideActive:()=>Promise.resolve()}},
    location:{href:'https://wodra.g5.co.nz/',hash:'',pathname:'/',search:''},history:{replaceState(){}},
    localStorage:store,sessionStorage:store,fetch:fetchStub,setTimeout:()=>1,clearTimeout(){},AbortController,
    URL,Date,Math,Number,String,JSON,Error,Promise});
  return scenarioElements;
}

(async()=>{
  await new Promise(resolve=>setImmediate(resolve));
  assert.equal(element('pair-view').hidden,false,'unremembered phone shows pairing form');
  element('pair-code').value='TRACE12345';
  await handlers.get('pair-form:submit')({preventDefault(){}});
  await new Promise(resolve=>setImmediate(resolve));
  await new Promise(resolve=>setImmediate(resolve));
  assert.equal(element('ride-view').hidden,false,'first-time QR/code claim automatically opens the remembered ride view');
  assert.match(element('ride-state').textContent,/Waiting for Wodra/i,'idle desktop is shown as a friendly waiting state');
  assert.equal(element('reconnect').hidden,true,'normal trusted state hides the manual reconnect action');
  await handlers.get('disconnect:click')();
  assert.equal(element('pair-view').hidden,false,'temporary disconnect returns to the pairing view');
  assert.equal(element('reconnect').hidden,false,'temporary disconnect keeps an explicit reconnect action');
  await handlers.get('reconnect:click')();
  await new Promise(resolve=>setImmediate(resolve));
  await new Promise(resolve=>setImmediate(resolve));
  assert.equal(element('ride-view').hidden,false,'remembered cookie reconnects without another pairing request');
  assert.equal(element('route-name').textContent,'Test route','reconnect renders the current ride');
  await handlers.get('forget-phone:click')();
  assert.equal(actions.filter(action=>action==='remembered_claim').length,1,'reconnect does not pair a second time');
  assert.equal(actions.includes('remembered_forget'),true,'forget calls server-side revocation');
  assert.equal(saved.some(([operation,key])=>operation==='set'&&(key==='wodra-companion-pair-v1'||key==='wodra-companion-pending-v1')),false,
    'protocol 2 does not store a long-lived bearer token in browser storage');
  for (const [error,expected] of [
    ['not_configured',/has not been configured/i],
    ['protocol_mismatch',/version is incompatible/i]
  ]) {
    const scenario = bootFailure(async()=>({ok:false,json:async()=>({protocol:error==='protocol_mismatch'?1:2,error:error==='invalid_response'?'unspecified':error})}));
    await new Promise(resolve=>setImmediate(resolve));
    assert.match(scenario.get('pair-message').textContent,expected,`${error} has a specific companion message`);
  }
  const malformed = bootFailure(async()=>({ok:true,json:async()=>{throw new Error('bad response');}}));
  await new Promise(resolve=>setImmediate(resolve));
  assert.match(malformed.get('pair-message').textContent,/invalid response/i,'invalid JSON has a specific companion message');
  console.log('PASS: companion claim, idle, temporary disconnect, remembered reconnect, server-side forget and browser-storage policy');
})().catch(error=>{console.error(error);process.exitCode=1;});
