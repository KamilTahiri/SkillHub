// Reads the REAL SkillHub window's DOM through WebView2's remote debugging port.
// Start SkillHub with WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9333 first, and relaunch it normally afterwards.
//
//   node tests/live/verify-live-dom.mjs
const port = process.env.SKILLHUB_DEBUG_PORT ?? '9333';
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
let targets = [];
for (let attempt = 0; attempt < 60 && !targets.some(t => t.type === 'page' && t.url.startsWith('http://127.0.0.1:')); attempt++) {
  try { targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json(); } catch { /* not ready */ }
  if (!targets.length) await delay(1000);
}
const page = targets.find(t => t.type === 'page' && t.url.startsWith('http://127.0.0.1:'));
if (!page) { console.error('No SkillHub page found on the debugging port.'); process.exit(2); }
console.log('Connected to', page.url);
const socket = new WebSocket(page.webSocketDebuggerUrl);
await new Promise(resolve => socket.addEventListener('open', resolve, { once: true }));
let id = 0;
const pending = new Map();
socket.addEventListener('message', event => {
  const message = JSON.parse(event.data);
  pending.get(message.id)?.(message);
});
const evaluate = expression => new Promise((resolve, reject) => {
  const requestId = ++id;
  pending.set(requestId, message => message.result?.exceptionDetails
    ? reject(new Error(message.result.exceptionDetails.exception?.description ?? 'evaluation failed'))
    : resolve(message.result?.result?.value));
  socket.send(JSON.stringify({ id: requestId, method: 'Runtime.evaluate', params: { expression, awaitPromise: true, returnByValue: true } }));
});
const until = async (expression, label, timeout = 240000) => {
  const end = Date.now() + timeout;
  while (Date.now() < end) { if (await evaluate(expression)) return; await delay(400); }
  throw new Error('Timed out waiting for ' + label);
};
const failures = [];
const check = (condition, message) => { console.log((condition ? 'PASS ' : 'FAIL ') + message); if (!condition) failures.push(message); };

try {
  await until('document.readyState === "complete" && !!document.querySelector("[data-route=explore]")', 'app shell');
  await evaluate('document.querySelector("[data-route=explore]").click()');
  await until('!document.querySelector("#explore-screen").hidden && document.querySelector("#explore-screen select")', 'Explore');
  const allRows = async () => evaluate('[...document.querySelectorAll("#explore-screen .result-row")].map(r => (r.querySelector(".result-path")?.textContent ?? "") + " | " + (r.querySelector(".result-title")?.textContent ?? ""))');

  for (const source of ['featured/all', 'vercel-labs/agent-skills', 'anthropics/skills', 'github/awesome-copilot', 'local/catalog']) {
    await evaluate(`(() => { const s = document.querySelector("#explore-screen .explore-toolbar select"); s.value = ${JSON.stringify(source)}; s.dispatchEvent(new Event("change")); })()`);
    await until('!document.querySelector(".explore-results").classList.contains("is-stale") && document.querySelector(".explore-results .result-row, .explore-results .empty, .explore-results .error")', source);
    // Look at every tab, not only the default one.
    await evaluate('document.querySelector(".group-tabs .btn")?.click()');
    await delay(300);
    const rows = await allRows();
    const scripts = rows.filter(row => /\.(py|sh|ps1|js|ts|bat|cmd)\b/i.test(row));
    check(scripts.length === 0, `${source}: ${rows.length} rendered rows, ${scripts.length} script rows${scripts.length ? ' → ' + scripts.slice(0, 4).join('; ') : ''}`);
  }

  await evaluate('(() => { const s = document.querySelector("#explore-screen .explore-toolbar select"); s.value = "vercel-labs/agent-skills"; s.dispatchEvent(new Event("change")); })()');
  await until('[...document.querySelectorAll("#explore-screen .result-path")].some(p => p.textContent === "skills/react-best-practices/SKILL.md")', 'react-best-practices row');
  await evaluate('[...document.querySelectorAll("#explore-screen .result-row")].find(r => r.querySelector(".result-path")?.textContent === "skills/react-best-practices/SKILL.md").querySelector(".result-button").click()');
  await until('document.querySelector(".import-review .preview-header h2")', 'preview');
  const preview = await evaluate(`(() => {
    const review = document.querySelector(".import-review");
    const picker = review.querySelector(".file-picker");
    return {
      title: review.querySelector(".preview-header h2").textContent,
      meta: [...review.querySelectorAll(".preview-metadata p")].map(p => p.textContent),
      text: review.innerText,
      options: picker ? [...picker.querySelectorAll("option")].map(o => o.value).filter(Boolean) : [...review.querySelectorAll(".file-tab")].map(t => t.dataset.path).filter(Boolean)
    };
  })()`);
  console.log('Preview:', preview.title, '·', preview.meta.join(' · '));
  check(!preview.text.includes('Not imported (binary, too large, or unsupported location)'), 'no "Not imported" list in the rendered preview');
  check(preview.options.filter(path => path.startsWith('rules/')).length >= 70, `rules/*.md offered in the file picker (${preview.options.filter(path => path.startsWith('rules/')).length})`);
  check(preview.options.includes('AGENTS.md'), 'AGENTS.md offered in the file picker');

  await evaluate(`(() => { const p = document.querySelector(".import-review .file-picker"); p.value = "rules/async-parallel.md"; p.dispatchEvent(new Event("change")); })()`);
  await until('document.querySelector(".import-document")?.innerText.includes("rules/async-parallel.md") && !document.querySelector(".import-document .preview-loading")', 'rule file');
  const ruleText = await evaluate('document.querySelector(".import-document").innerText');
  console.log('Rendered rules/async-parallel.md: ' + ruleText.replace(/\s+/g, ' ').slice(0, 160) + '…');
  check(ruleText.length > 200, 'rules/async-parallel.md content renders in the preview');
} catch (error) {
  failures.push(error.message);
  console.error(error);
} finally {
  socket.close();
}
console.log(failures.length ? `\n${failures.length} check(s) failed` : '\nAll live DOM checks passed');
process.exit(failures.length ? 1 : 0);
