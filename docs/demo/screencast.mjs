// TestAtlas GUI screencast — drives the generated report.html + map.html and records a video.
// Usage: node screencast.mjs <report.html> <map.html> <outVideoDir> [shotsDir]
import { chromium } from 'playwright';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const [reportPath, mapPath, outDir, shotsDir] = process.argv.slice(2);
if (!reportPath || !mapPath || !outDir) {
  console.error('usage: node screencast.mjs <report.html> <map.html> <outVideoDir> [shotsDir]');
  process.exit(1);
}
const reportUrl = pathToFileURL(path.resolve(reportPath)).href;
const mapUrl = pathToFileURL(path.resolve(mapPath)).href;

const W = 1120, H = 700;
const wait = (ms) => new Promise(r => setTimeout(r, ms));

// injected fake cursor + click ripple so viewers can see what's being clicked
const cursorInit = () => {
  const css = `#__cur{position:fixed;z-index:99999;width:20px;height:20px;margin:-10px 0 0 -10px;border-radius:50%;
    background:rgba(59,91,219,.30);border:2px solid #3b5bdb;pointer-events:none;left:-60px;top:-60px;
    box-shadow:0 1px 4px rgba(0,0,0,.3)}
    .__rip{position:fixed;z-index:99998;width:14px;height:14px;margin:-7px 0 0 -7px;border-radius:50%;
    border:2px solid #3b5bdb;pointer-events:none;animation:__r .55s ease-out forwards}
    @keyframes __r{from{transform:scale(1);opacity:.85}to{transform:scale(3.5);opacity:0}}`;
  const add = () => {
    const s = document.createElement('style'); s.textContent = css; document.head.appendChild(s);
    const c = document.createElement('div'); c.id = '__cur'; document.body.appendChild(c);
    document.addEventListener('mousemove', e => { c.style.left = e.clientX + 'px'; c.style.top = e.clientY + 'px'; }, true);
    document.addEventListener('mousedown', e => {
      const r = document.createElement('div'); r.className = '__rip';
      r.style.left = e.clientX + 'px'; r.style.top = e.clientY + 'px';
      document.body.appendChild(r); setTimeout(() => r.remove(), 560);
    }, true);
  };
  if (document.body) add(); else document.addEventListener('DOMContentLoaded', add);
};

async function run() {
  const browser = await chromium.launch({ args: ['--force-color-profile=srgb'] });
  const ctx = await browser.newContext({
    viewport: { width: W, height: H },
    recordVideo: { dir: outDir, size: { width: W, height: H } },
    colorScheme: 'light',
    deviceScaleFactor: 1,
  });
  await ctx.addInitScript(cursorInit);
  const page = await ctx.newPage();
  let shotN = 0;
  const shot = async (name) => { if (shotsDir) await page.screenshot({ path: path.join(shotsDir, `${String(++shotN).padStart(2, '0')}-${name}.png`) }); };

  // move the visible cursor to an element's center (animated) and optionally click it
  const glideTo = async (locator, { click = false, pause = 500 } = {}) => {
    const box = await locator.boundingBox();
    if (!box) throw new Error('no box for target');
    const x = box.x + box.width / 2, y = box.y + Math.min(box.height / 2, 18);
    await page.mouse.move(x, y, { steps: 28 });
    await wait(pause);
    if (click) { await page.mouse.down(); await wait(90); await page.mouse.up(); }
    return { x, y };
  };
  const smoothScrollTo = async (selector) => {
    await page.evaluate((sel) => {
      const el = document.querySelector(sel);
      if (el) el.scrollIntoView({ behavior: 'smooth', block: 'center' });
    }, selector);
    await wait(1100);
  };
  const scrollLoc = async (locator) => {
    await locator.evaluate((el) => el.scrollIntoView({ behavior: 'smooth', block: 'center' }));
    await wait(1100);
  };

  // ---------- REPORT ----------
  await page.goto(reportUrl, { waitUntil: 'load' });
  await page.mouse.move(W / 2, H / 2, { steps: 5 });
  await wait(1600);                                  // land on the overview / stat cards
  await shot('report-top');

  // 1) API endpoints -> expand one -> scenarios grouped by feature
  await smoothScrollTo('table.grid.ep');
  const epRow = page.locator('tr.ep-row', { hasText: 'products/{id}' }).first();
  await glideTo(epRow, { click: true, pause: 650 });
  await wait(1600);
  await shot('endpoint-expanded');

  // 2) search the feature tree (agent-style "find a test case")
  await smoothScrollTo('#filter');
  const filter = page.locator('#filter');
  await glideTo(filter, { click: true, pause: 350 });
  await page.keyboard.type('cart', { delay: 150 });
  await wait(1800);                                  // tree filters to matching scenarios
  await shot('filter-cart');

  // clear -> expand all -> show a scenario's steps + bindings
  await glideTo(filter, { click: true, pause: 200 });
  await page.keyboard.press('Control+A'); await page.keyboard.press('Backspace');
  await wait(900);
  const expandAll = page.locator('button.mini', { hasText: 'expand all' });
  await glideTo(expandAll, { click: true, pause: 500 });
  await wait(900);
  const scen = page.locator('.feature .scenario', { hasText: 'Place an order' }).first();
  await scrollLoc(scen);
  await wait(1500);                                 // steps -> bindings (step files) visible
  await shot('scenario-steps');

  // 3) collaborators panel = page objects
  const collab = page.locator('details.panel', { has: page.locator('h2', { hasText: 'Collaborators' }) }).first();
  await scrollLoc(collab);
  await wait(1700);
  await shot('collaborators');

  // ---------- MAP ----------
  await page.goto(mapUrl, { waitUntil: 'load' });
  await page.mouse.move(W / 2, H / 2, { steps: 5 });
  await wait(1800);                                 // whole dependency graph
  await shot('map-full');

  // click a rich node -> highlight its dependency edges + open side panel
  const nodeApi = page.locator('g.node[data-id="5"] circle');   // SampleShop.Tests.Api (depends on 3)
  await glideTo(nodeApi, { click: true, pause: 700 });
  await wait(1700);
  await shot('node-pinned');

  // expand the classes behind one dependency
  const expClasses = page.locator('#panel .pi-exp').first();
  await glideTo(expClasses, { click: true, pause: 600 });
  await wait(1600);
  await shot('classes-expanded');

  // click a hub node depended on by many
  const nodeCore = page.locator('g.node[data-id="4"] circle');  // SampleShop.Core (depended on by 4)
  await glideTo(nodeCore, { click: true, pause: 700 });
  await wait(2000);
  await shot('core-pinned');

  const video = page.video();
  await ctx.close();                                // finalizes the video file
  await browser.close();
  if (video) console.log('video:', await video.path());
}

run().catch(e => { console.error(e); process.exit(1); });
