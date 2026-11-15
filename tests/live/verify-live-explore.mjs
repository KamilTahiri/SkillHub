// Verifies Explore against the RUNNING SkillHub daemon (real profile, real ~/.skillhub/cache) through its MCP channel:
// SkillHub.exe mcp-stdio forwards JSON-RPC over the named pipe to the live process. Nothing is imported or changed;
// prepare_skill_import only builds an in-memory review plan that expires on its own.
//
//   node tests/live/verify-live-explore.mjs
import { spawn } from 'node:child_process';
import { join } from 'node:path';

const exe = process.env.SKILLHUB_EXE ?? join(process.env.LOCALAPPDATA, 'SkillHub', 'SkillHub.exe');
const child = spawn(exe, ['mcp-stdio'], { stdio: ['pipe', 'pipe', 'inherit'], windowsHide: true });
let buffer = '';
const waiting = new Map();
child.stdout.on('data', chunk => {
  buffer += chunk;
  let newline;
  while ((newline = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, newline).trim();
    buffer = buffer.slice(newline + 1);
    if (!line) continue;
    const message = JSON.parse(line);
    waiting.get(message.id)?.(message);
    waiting.delete(message.id);
  }
});
let id = 0;
const rpc = (method, params) => new Promise((resolve, reject) => {
  const requestId = ++id;
  const timer = setTimeout(() => reject(new Error(method + ' timed out')), 240000);
  waiting.set(requestId, message => { clearTimeout(timer); message.error ? reject(new Error(JSON.stringify(message.error))) : resolve(message.result); });
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: requestId, method, params }) + '\n');
});
const tool = async (name, args) => {
  const result = await rpc('tools/call', { name, arguments: args });
  const text = result.content?.[0]?.text ?? '';
  if (result.isError) throw new Error(name + ': ' + text);
  return JSON.parse(text);
};

const failures = [];
const check = (condition, message) => { console.log((condition ? 'PASS ' : 'FAIL ') + message); if (!condition) failures.push(message); };
try {
  await rpc('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'skillhub-live-verify', version: '1' } });
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');

  // 1. Every Explore list row from the live daemon is a Markdown document.
  const sources = [['vercel-labs/agent-skills', ''], ['anthropics/skills', ''], ['github/awesome-copilot', ''], ['local/catalog', ''], ['skills.sh', 'python']];
  for (const [repo, query] of sources) {
    const result = await tool('search_community_skills', { repo, query });
    const rows = result.skills ?? result.results ?? [];
    const scripts = rows.filter(row => /\.(py|sh|ps1|js|ts|bat|cmd)$/i.test(row.path) || /\.(py|sh|ps1)$/i.test(row.title ?? ''));
    check(scripts.length === 0, `${repo}${query ? ' "' + query + '"' : ''}: ${rows.length} rows, ${scripts.length} script rows` +
      (scripts.length ? ' → ' + scripts.slice(0, 5).map(row => row.path).join(', ') : ''));
  }
  const search = await tool('search_community_skills', { query: 'python' });
  const searchRows = search.skills ?? search.results ?? [];
  check(searchRows.every(row => !/\.(py|sh|ps1|js|ts)$/i.test(row.path)), `keyword search "python": ${searchRows.length} rows, no script files`);

  // 2. react-best-practices keeps its rules/*.md and AGENTS.md companion files.
  const plan = await tool('prepare_skill_import', { repo: 'vercel-labs/agent-skills', path: 'skills/react-best-practices/SKILL.md' });
  const files = Object.keys(plan.supporting_files ?? plan.supportingFiles ?? {});
  const rules = files.filter(path => path.startsWith('rules/'));
  const skipped = plan.skipped_files ?? plan.skippedFiles ?? [];
  console.log(`react-best-practices @ ${plan.revision?.slice(0, 7)}: ${files.length} supporting files, ${rules.length} in rules/, ${skipped.length} skipped`);
  console.log('  sample: ' + files.slice(0, 6).join(', '));
  check(rules.length >= 70, `rules/*.md included (${rules.length})`);
  check(files.includes('AGENTS.md'), 'AGENTS.md included');
  check(!skipped.some(item => /^rules\/|^AGENTS\.md/.test(item)), 'no rules/ or AGENTS.md file is reported as not imported');
  check((plan.supporting_files ?? {})['rules/async-parallel.md']?.length > 0, 'rules/async-parallel.md has readable content');

  // 3. Script review on a bundle that ships Python scripts (anthropics/skills pdf).
  const pdf = await tool('prepare_skill_import', { repo: 'anthropics/skills', path: 'skills/pdf/SKILL.md' });
  const scripts = pdf.scripts ?? [];
  console.log(`pdf scripts flagged: ${scripts.length} → ${scripts.slice(0, 4).map(script => script.path + (script.concerns.length ? ' [' + script.concerns.join(', ') + ']' : '')).join(', ')}`);
  check(scripts.some(script => script.path.endsWith('.py')), 'Python scripts are listed for review before import');

  // 4. MCP prompts, resources and Skill Doctor on the live library (read-only).
  const prompts = (await rpc('prompts/list', {})).prompts ?? [];
  check(prompts.length > 0, `prompts/list exposes ${prompts.length} skills as prompts`);
  if (prompts.length) {
    const first = prompts[0].name;
    const got = await rpc('prompts/get', { name: first, arguments: { task: 'live check' } });
    check(got.messages?.[0]?.content?.text?.length > 50, `prompts/get returns the instructions of ${first}`);
    const read = await rpc('resources/read', { uri: 'skillhub://skills/' + first });
    check(read.contents?.[0]?.text?.includes('name:'), `resources/read returns the SKILL.md of ${first}`);
    const lint = await tool('lint_skill', { name: first });
    check(typeof lint.score === 'number', `lint_skill scores ${first}: ${lint.score} with ${lint.findings.length} findings`);
  }
} catch (error) {
  failures.push(error.message);
  console.error(error);
} finally {
  child.stdin.end();
  child.kill();
}
console.log(failures.length ? `\n${failures.length} check(s) failed` : '\nAll live checks passed');
process.exit(failures.length ? 1 : 0);
