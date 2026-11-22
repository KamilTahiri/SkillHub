// The skills.sh CLI (`npx skills add owner/repo --skill name`) and its links, both ways.

const OWNER = '[A-Za-z0-9-]{1,100}';
const REPO = '[A-Za-z0-9_.-]{1,100}';
const SKILL = '[A-Za-z0-9._-]{1,100}';

/**
 * Reads `npx skills add owner/repo [--skill name]` (also bunx, pnpm dlx, `skills add`, `-s`, `owner/repo@name`),
 * `skills.sh/owner/repo[/name]` links and GitHub links. Returns { repo, skill } (skill may be null) or null for anything else.
 */
export function parseSkillsCommand(text) {
  let value = String(text ?? '').trim();
  if (!value) return null;
  let skill = null;
  const flag = value.match(new RegExp('(?:^|\\s)(?:--skill|-s)(?:\\s+|=)["\']?(' + SKILL + ')["\']?', 'i'));
  if (flag) { skill = flag[1]; value = (value.slice(0, flag.index) + ' ' + value.slice(flag.index + flag[0].length)).trim(); }
  const cli = /^(?:(?:npx|bunx|pnpm\s+dlx)\s+(?:-y\s+)?(?:@[\w-]+\/)?skills(?:\.sh)?|skills(?:\.sh)?)\s+(?:add|install|i)\s+/i;
  const isCli = cli.test(value);
  value = value.replace(cli, '').replace(/\s+(?:-y|--yes|-g|--global)\b/gi, '').trim().replace(/^["']|["']$/g, '');
  const link = value.match(new RegExp('^(?:https?://)?(?:www\\.)?(skills\\.sh|github\\.com)/(' + OWNER + ')/(' + REPO + ')(?:/(.*))?$', 'i'));
  if (link) {
    const repo = link[2] + '/' + link[3].replace(/\.git$/i, '');
    const rest = (link[4] ?? '').split(/[?#]/)[0].split('/').filter(Boolean);
    if (link[1].toLowerCase() === 'skills.sh' && rest[0]) skill ??= rest[0];
    return { repo, skill };
  }
  const short = value.match(new RegExp('^(' + OWNER + ')/(' + REPO + ')(?:@(' + SKILL + '))?$'));
  if (short && (isCli || short[3] || skill)) return { repo: short[1] + '/' + short[2].replace(/\.git$/i, ''), skill: skill ?? short[3] ?? null };
  return null;
}

/** The skill's folder name inside its repository: `skills/pdf/SKILL.md` → `pdf`. */
export function skillFolderName(path, fallback) {
  const parts = String(path ?? '').split('/').filter(Boolean);
  if (parts.length >= 2 && /^skill\.md$/i.test(parts.at(-1))) return parts.at(-2);
  if (parts.length && /\.mdc?$/i.test(parts.at(-1))) return parts.at(-1).replace(/\.mdc?$/i, '');
  if (parts.length && !parts.at(-1).includes('.')) return parts.at(-1);
  return fallback ?? null;
}

/** `npx skills add owner/repo --skill name`, or null when the source is not a GitHub repository. */
export function skillsAddCommand(repo, skill) {
  if (!repo || !new RegExp('^' + OWNER + '/' + REPO + '$').test(repo) || /^(local|featured)\//i.test(repo)) return null;
  return 'npx skills add ' + repo + (skill && new RegExp('^' + SKILL + '$').test(skill) ? ' --skill ' + skill : '');
}
