// Manual visual acceptance harness. Requires the workstation's Playwright package/browser.
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { chromium } = require('playwright');

const root = path.join(__dirname, '../public');
const output = path.join(process.env.TEMP || process.cwd(), 'wodra-companion-visual-review');
fs.mkdirSync(output, { recursive: true });
const points = Array.from({length:49}, (_, i) => {
  const a = i / 48 * Math.PI * 2;
  return [i / 48 * 250, Math.cos(a) * 54, Math.sin(a) * 31, 8 + Math.sin(a * 2) * 3, Math.cos(a) * 1.2];
});
const route = {version:'review-velodrome-v1', id:'review-loop', name:'250 m Indoor Velodrome', environment:'Velodrome', discipline:'Track', type:'Loop', distance_m:250, ascent_m:0, points};
const riders = [
  {name:'Alex',kind:'AI',relative_m:43},
  {name:'Taylor',kind:'Rider',relative_m:-76},
  {name:'Jamie',kind:'AI',relative_m:252}
];
const server = http.createServer((req, res) => {
  if (req.url === '/api.php') {
    let body=''; req.on('data',chunk=>body+=chunk); req.on('end',()=>{
      const input=JSON.parse(body);
      const response={protocol:2,status:'remembered',desktop_online:true,snapshot_at:Math.floor(Date.now()/1000),
        route:input.route_version===route.version?null:route,route_version:route.version,
        snapshot:{state:'riding',connected:true,paused:false,data_stale:false,route:route.name,environment:'Velodrome',route_type:'Loop',
          speed_kph:38.7,power_w:242,cadence_rpm:92,heart_rate_bpm:151,distance_m:637.5,elapsed_s:1002,gradient_pct:0,
          route_distance_m:250,route_progress:.55,lap:3,remaining_m:null,riders, captured_at:new Date().toISOString()}};
      res.writeHead(200,{'Content-Type':'application/json','Access-Control-Allow-Origin':'*'}); res.end(JSON.stringify(response));
    }); return;
  }
  const pathname=req.url==='/'?'/index.html':decodeURIComponent(req.url.split('?')[0]);
  const file=path.join(root,pathname.replace(/^\//,''));
  if (!file.startsWith(root)) {res.writeHead(403);res.end();return;}
  try {res.writeHead(200,{'Content-Type':file.endsWith('.css')?'text/css':file.endsWith('.js')?'text/javascript':'text/html'});res.end(fs.readFileSync(file));}
  catch {res.writeHead(404);res.end();}
});

(async()=>{
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  const browser=await chromium.launch({headless:true});
  try {
    for (const viewport of [{name:'iphone-compact',width:375,height:667},{name:'iphone-standard',width:390,height:844},{name:'landscape',width:844,height:390}]) {
      const context=await browser.newContext({viewport:{width:viewport.width,height:viewport.height},deviceScaleFactor:2,isMobile:true,hasTouch:true});
      const page=await context.newPage(); await page.goto(`http://127.0.0.1:${server.address().port}/`,{waitUntil:'networkidle'});
      await page.waitForSelector('#ride-view:not([hidden])');
      for (const tab of ['ride','route','riders']) {
        await page.locator(`[data-tab="${tab}"]`).tap(); await page.waitForTimeout(60);
        if (tab==='riders') assert.equal(await page.locator('#riders-list .rider-row').count(),4,'realistic rider data is rendered on the Riders screen');
        const dimensions=await page.evaluate(()=>({width:document.documentElement.clientWidth,scrollWidth:document.documentElement.scrollWidth,
          height:document.documentElement.clientHeight,scrollHeight:document.documentElement.scrollHeight,
          panel:document.querySelector(`[data-panel="${document.querySelector('[aria-current="page"]').dataset.tab}"]`).getBoundingClientRect().height}));
        assert.ok(dimensions.scrollWidth<=dimensions.width,`${viewport.name}/${tab}: horizontal overflow ${JSON.stringify(dimensions)}`);
        if (dimensions.scrollHeight>dimensions.height+1) console.log(await page.evaluate(()=>[...document.querySelectorAll('body *')].map(el=>({tag:el.tagName,id:el.id,cls:el.className,top:el.getBoundingClientRect().top,bottom:el.getBoundingClientRect().bottom,height:el.getBoundingClientRect().height})).filter(x=>x.bottom>innerHeight+1)));
        assert.ok(dimensions.scrollHeight<=dimensions.height+1,`${viewport.name}/${tab}: full-page scroll ${JSON.stringify(dimensions)}`);
        assert.ok(dimensions.panel>100,`${viewport.name}/${tab}: content panel is too short`);
        await page.screenshot({path:path.join(output,`${viewport.name}-${tab}.png`),fullPage:true});
      }
      await page.reload({waitUntil:'networkidle'}); await page.waitForSelector('#ride-view:not([hidden])');
      assert.equal(await page.locator('[data-tab="riders"]').getAttribute('aria-current'),'page',`${viewport.name}: selected screen survives refresh`);
      await context.close();
    }
    console.log(`PASS: real browser mobile layouts captured to ${output}`);
  } finally { await browser.close(); server.close(); }
})().catch(error=>{console.error(error);server.close();process.exitCode=1;});
