// Reads and screenshots the REAL SkillHub window through WebView2's remote debugging port. Nothing is saved:
// the editor is opened and cancelled, and imports are only previewed.
// Start SkillHub with WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9333 first, and relaunch it normally afterwards.
//
//   SKILLHUB_SHOTS=<folder> node tests/live/verify-live-ui.mjs
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const port = process.env.SKILLHUB_DEBUG_PORT ?? '9333';
const shots = process.env.SKILLHUB_SHOTS;
if (shots) mkdirSync(shots, { recursive: true });
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
const errors = [];
socket.addEventListener('message', event => {
  const message = JSON.parse(event.data);
  if (message.method === 'Runtime.exceptionThrown') errors.push(message.params.exceptionDetails.exception?.description ?? 'exception');
  pending.get(message.id)?.(message);
});
const send = (method, params = {}) => new Promise(resolve => {
  const requestId = ++id;
  pending.set(requestId, resolve);
  socket.send(JSON.stringify({ id: requestId, method, params }));
});
const evaluate = async expression => {
  const message = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
  if (message.result?.exceptionDetails) throw new Error(message.result.exceptionDetails.exception?.description ?? 'evaluation failed');
  return message.result?.result?.value;
};
const until = async (expression, label, timeout = 120000) => {
  const end = Date.now() + timeout;
  while (Date.now() < end) { if (await evaluate(expression)) return; await delay(300); }
  throw new Error('Timed out waiting for ' + label);
};
const shot = async name => {
  if (!shots) return;
  await delay(350);
  const { result } = await send('Page.captureScreenshot', { format: 'png' });
  writeFileSync(join(shots, name + '.png'), Buffer.from(result.data, 'base64'));
};
const failures = [];
const check = (condition, message) => { console.log((condition ? 'PASS ' : 'FAIL ') + message); if (!condition) failures.push(message); };

try {
  await send('Runtime.enable');
  await send('Page.enable');
  await until('!!document.querySelector("#library-results .result-button")', 'Library rows');

  // Library
  await evaluate('document.querySelector("[data-route=library]").click()');
  await until('!document.querySelector("#library-screen").hidden && document.querySelector(".library-row")', 'Library');
  check(await evaluate('document.querySelectorAll(".library-row .row-actions [aria-label^=\\"Copy skill prompt\\"]").length > 0'), 'Library rows offer Copy skill prompt');
  await shot('1-library');

  // Reader: Copy skill prompt, Doctor and History
  await evaluate('document.querySelector("#library-results .result-button").click()');
  await until('!document.querySelector("#reader-screen").hidden && document.querySelector("#reader-screen h1")', 'Reader');
  await until('[...document.querySelectorAll("#reader-screen button")].some(b => b.textContent.includes("Copy skill prompt"))', 'Copy skill prompt');
  check(true, 'Reader has Copy skill prompt');
  await until('document.querySelector(".doctor-panel .doctor-score strong")', 'Doctor score');
  console.log('  Doctor score for ' + await evaluate('document.querySelector("#reader-screen h1").textContent') + ': ' +
    await evaluate('document.querySelector(".doctor-panel .doctor-score strong").textContent'));
  check(await evaluate('document.documentElement.scrollWidth <= innerWidth'), 'Reader has no horizontal overflow');
  await shot('2-reader');
  await evaluate('[...document.querySelectorAll(".reader-tabs button")].find(b => b.textContent === "History").click()');
  await until('!document.querySelector(".history-section")?.innerText.includes("Loading")', 'History');
  console.log('  History: ' + await evaluate('document.querySelector(".history-section").innerText.slice(0, 120).replace(/\\s+/g, " ")'));
  check(await evaluate('!!document.querySelector(".history-section")'), 'History tab renders');
  await shot('3-history');

  // Editor 2.0 (cancelled, nothing saved)
  await evaluate('[...document.querySelectorAll("#reader-screen button")].find(b => b.textContent === "Edit skill").click()');
  await until('document.querySelector(".editor-workspace .editor-toolbar") && document.querySelectorAll(".budget-meter").length === 3', 'Editor workspace');
  await until('document.querySelector(".editor-side .markdown")?.textContent.length > 0', 'Live preview');
  check(true, 'Editor 2.0 shows the toolbar, three budget meters and the live preview');
  await shot('4-editor-preview');
  await evaluate('document.querySelector(".editor-pane-tab[data-pane=doctor]").click()');
  await until('document.querySelector(".editor-side .doctor-score strong")', 'Editor Doctor');
  check(!(await evaluate('document.querySelector(".editor-side").innerText.includes("false")')), 'Doctor pane has no stray text');
  await shot('5-editor-doctor');
  await evaluate('document.querySelector(".editor-pane-tab[data-pane=output]").click()');
  await until('document.querySelector(".editor-side .output-code")?.textContent.includes("name:")', 'Provider output');
  check(true, 'Provider output renders a transpiled file');
  await shot('6-editor-output');
  check(!(await evaluate('document.querySelector("#editor-screen")?.innerText.includes("Draft · unsaved")')), 'Editor has no unsaved changes');
  await evaluate('[...document.querySelectorAll("#editor-screen button")].find(b => b.textContent === "Cancel").click()');
  await until('document.querySelector("#editor-screen").hidden', 'Editor closed');

  // Explore: script review on anthropics/skills pdf
  await evaluate('document.querySelector("[data-route=explore]").click()');
  // The Source list fills in from the daemon; wait for it before choosing a repository.
  await until('!document.querySelector("#explore-screen").hidden && [...document.querySelectorAll("#explore-screen .explore-toolbar select option")].some(o => o.value === "anthropics/skills")', 'Explore sources');
  await evaluate('(() => { const s = document.querySelector("#explore-screen .explore-toolbar select"); s.value = "anthropics/skills"; s.dispatchEvent(new Event("change")); })()');
  await until('[...document.querySelectorAll("#explore-screen .result-path")].some(p => p.textContent === "skills/pdf/SKILL.md")', 'pdf row');
  await evaluate('[...document.querySelectorAll("#explore-screen .result-row")].find(r => r.querySelector(".result-path")?.textContent === "skills/pdf/SKILL.md").querySelector(".result-button").click()');
  await until('document.querySelector(".import-review .preview-header h2")', 'pdf preview');
  const review = await evaluate('document.querySelector(".script-review")?.innerText ?? ""');
  console.log('  Script review: ' + review.replace(/\s+/g, ' ').slice(0, 160));
  check(review.includes('.py'), 'Explore preview lists the Python scripts for review');
  check(await evaluate('document.querySelector(".per-skill-note")?.textContent.includes("only this skill")'), 'Import is per skill, with the multi-select hint');
  await shot('7-explore-scripts');

  // Settings → Connections with the new agents
  await evaluate('document.querySelector("[data-route=settings]").click()');
  await evaluate('[...document.querySelectorAll("#settings-screen .settings-tabs button")].find(b => b.textContent.trim() === "Connections")?.click()');
  await until('document.querySelector(".connection-card")', 'Connections');
  await evaluate('document.querySelector(".connections-other")?.setAttribute("open", "")');
  const other = await evaluate('document.querySelector(".connections-other")?.innerText ?? ""');
  check(['GitHub Copilot', 'Windsurf', 'Cline', 'Roo Code'].every(name => other.includes(name)), 'Copilot, Windsurf, Cline and Roo Code are listed as supported');
  await shot('8-connections');
  check(errors.length === 0, 'No uncaught page errors' + (errors.length ? ': ' + errors.join(' | ') : ''));
} catch (error) {
  failures.push(error.message);
  console.error(error);
} finally {
  socket.close();
}
console.log(failures.length ? `\n${failures.length} check(s) failed` : '\nAll live UI checks passed');
process.exit(failures.length ? 1 : 0);
