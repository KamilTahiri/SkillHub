// Captures README screenshots from the REAL SkillHub window through WebView2's remote debugging port.
// Start SkillHub with WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9333 first, and relaunch it normally afterwards.
// The Windows user name is replaced with "you" in visible text so screenshots carry no personal paths.
//
//   node tests/live/capture-screenshots.mjs [output folder]
import { mkdir, rm, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir, userInfo } from 'node:os';

const out = process.argv[2] ?? join(process.cwd(), 'docs', 'screenshots');
const port = process.env.SKILLHUB_DEBUG_PORT ?? '9333';
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
let targets = [];
for (let attempt = 0; attempt < 60 && !targets.some(t => t.type === 'page' && t.url.startsWith('http://127.0.0.1:')); attempt++) {
  try { targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json(); } catch { /* not ready */ }
  if (!targets.length) await delay(1000);
}
const page = targets.find(t => t.type === 'page' && t.url.startsWith('http://127.0.0.1:'));
if (!page) { console.error('No SkillHub page found on the debugging port.'); process.exit(2); }
const socket = new WebSocket(page.webSocketDebuggerUrl);
await new Promise(resolve => socket.addEventListener('open', resolve, { once: true }));
let id = 0;
const pending = new Map();
socket.addEventListener('message', event => { const message = JSON.parse(event.data); pending.get(message.id)?.(message); });
const send = (method, params = {}) => new Promise((resolve, reject) => {
  const requestId = ++id;
  pending.set(requestId, message => message.error ? reject(new Error(message.error.message)) : resolve(message.result));
  socket.send(JSON.stringify({ id: requestId, method, params }));
});
const evaluate = async expression => {
  const result = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
  if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description ?? 'evaluation failed');
  return result.result.value;
};
const until = async (expression, label, timeout = 120000) => {
  const end = Date.now() + timeout;
  while (Date.now() < end) { if (await evaluate(expression)) return; await delay(300); }
  throw new Error('Timed out waiting for ' + label);
};
const user = userInfo().username;
const anonymize = () => evaluate(`(() => {
  const pattern = new RegExp(${JSON.stringify(user.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'))}, 'gi');
  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
  while (walker.nextNode()) if (pattern.test(walker.currentNode.textContent)) walker.currentNode.textContent = walker.currentNode.textContent.replace(pattern, 'you');
  for (const input of document.querySelectorAll('input')) if (pattern.test(input.value)) input.value = input.value.replace(pattern, 'you');
})()`);
await mkdir(out, { recursive: true });
const shot = async name => {
  await anonymize();
  await evaluate('document.activeElement?.blur?.()');
  await delay(400);
  const { data } = await send('Page.captureScreenshot', { format: 'png' });
  await writeFile(join(out, name + '.png'), Buffer.from(data, 'base64'));
  console.log('saved ' + name + '.png');
};
const click = selector => evaluate(`document.querySelector(${JSON.stringify(selector)})?.click()`);
const textButton = text => evaluate(`[...document.querySelectorAll("button")].find(b => !b.closest("[hidden]") && b.textContent.trim() === ${JSON.stringify(text)})?.click()`);

try {
  await send('Emulation.setDeviceMetricsOverride', { width: 1440, height: 900, deviceScaleFactor: 1, mobile: false });
  await until('document.readyState === "complete" && document.querySelector("#library-results .result-row")', 'library');
  await evaluate('document.querySelector("dialog[open]")?.close()');

  // 1. Library: personal skills with sync state and bundle badges.
  await click('[data-route=library]');
  await click('[data-control=category-user]');
  await delay(800);
  await shot('library');

  // 2. Explore: anthropics/skills with a bundle preview open.
  await click('[data-route=explore]');
  await until('[...(document.querySelector("#explore-screen select")?.options ?? [])].some(o => o.value === "anthropics/skills")', 'explore sources');
  await delay(500);
  await evaluate(`(() => { const select = document.querySelector("#explore-screen select"); select.value = "anthropics/skills"; select.dispatchEvent(new Event("change", { bubbles: true })); })()`);
  await until('document.querySelector(".repo-card") && document.querySelectorAll("#explore-screen .result-row").length > 5', 'repository');
  await evaluate(`[...document.querySelectorAll("#explore-screen .result-button")].find(row => /skill.creator/i.test(row.textContent))?.click()`);
  await until('document.querySelector("#explore-preview .file-tabs, #explore-preview .file-picker")', 'bundle preview');
  await delay(800);
  await shot('explore');

  // 3. Editor 2.0 with Skill Doctor on a personal skill.
  await click('[data-route=library]');
  await click('[data-control=category-user]');
  await until('document.querySelector("#library-results .result-button")', 'personal skills');
  await click('#library-results .result-button');
  await until('[...document.querySelectorAll("#reader-screen button")].some(b => b.textContent.trim() === "Edit skill")', 'reader');
  await textButton('Edit skill');
  await until('document.querySelector(".editor-workspace .editor-toolbar") && document.querySelector(".doctor-score strong")', 'editor');
  await delay(800);
  await shot('editor');

  // 4. Connections.
  await click('[data-route=settings]');
  await textButton('Connections');
  await until('document.querySelector(".connection-card")', 'connections');
  await delay(500);
  await shot('connections');

  // 5. Projects: a temporary demo project, shown at a clean path and removed (with the files it wrote) afterwards.
  const demo = join(tmpdir(), 'skillhub-screenshot', 'acme-web-app');
  await mkdir(demo, { recursive: true });
  const demoId = await evaluate(`(async () => {
    const { api } = await import('/js/api.js');
    const created = await api.addWorkspace(${JSON.stringify(demo)}, 'acme-web-app');
    const skills = (await api.skills()).filter(skill => skill.source_category === 'user').slice(0, 2).map(skill => skill.name);
    const creator = (await api.skills()).find(skill => skill.is_catalog_only && skill.name === 'skill-creator');
    await api.updateWorkspace(created.id, { skills: creator ? [...skills, creator.catalog_id] : skills, targets: ['claude', 'codex', 'gemini', 'cursor'] });
    return created.id;
  })()`);
  try {
    await click('[data-route=projects]');
    await until(`document.querySelector('#projects-screen [data-workspace="${demoId}"]')`, 'projects');
    await evaluate(`document.querySelector('#projects-screen [data-workspace="${demoId}"]').click()`);
    await until('document.querySelector("#projects-screen .workspace-detail h2")?.textContent === "acme-web-app"', 'demo project');
    await evaluate(`(() => { const path = document.querySelector('.workspace-detail .path'); path.firstChild.textContent = '~/Projects/acme-web-app'; })()`);
    await shot('projects');
  } finally {
    await evaluate(`import('/js/api.js').then(({ api }) => api.removeWorkspace(${JSON.stringify(demoId)}, true))`);
    await rm(join(tmpdir(), 'skillhub-screenshot'), { recursive: true, force: true });
  }

  // 6. The welcome guide.
  await click('[data-route=settings]');
  await textButton('Appearance & Startup');
  await until('[...document.querySelectorAll("#settings-screen button")].some(b => b.textContent === "Run Welcome Guide")', 'settings');
  await textButton('Run Welcome Guide');
  await until('document.querySelector("dialog.onboarding[open] .onboarding-tools")', 'welcome guide');
  await delay(500);
  await shot('welcome');
  await evaluate('document.querySelector(".onboarding-foot .primary").click()');
  await delay(400);
  await shot('welcome-skills');
  await evaluate('document.querySelector("dialog.onboarding")?.close()');
} finally {
  await send('Emulation.clearDeviceMetricsOverride').catch(() => {});
  socket.close();
}
