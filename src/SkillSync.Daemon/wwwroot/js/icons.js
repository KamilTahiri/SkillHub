const NS = 'http://www.w3.org/2000/svg';
const line = (d) => [['path', { d }]];
const outlines = {
  library: line('M4 5h12a2 2 0 0 1 2 2v12H6a2 2 0 0 1-2-2V5Zm0 12a2 2 0 0 1 2-2h12M8 9h6M20 7v14H7'),
  explore: [['circle', { cx: 12, cy: 12, r: 9 }], ...line('m16 8-2.5 5.5L8 16l2.5-5.5L16 8Z')],
  settings: [...line('m9 3-.7 2.7-1.8 1-2.7-.7-2 3.4 2 2v2.1l-2 2 2 3.5 2.7-.7 1.8 1L9 22h4l.7-2.7 1.8-1 2.7.7 2-3.5-2-2v-2.1l2-2-2-3.4-2.7.7-1.8-1L13 3H9Z'), ['circle', { cx: 11, cy: 12.5, r: 3 }]],
  sun: [['circle', { cx: 12, cy: 12, r: 4 }], ...line('M12 2v2m0 16v2M2 12h2m16 0h2M5 5l1.5 1.5m11 11L19 19M19 5l-1.5 1.5m-11 11L5 19')],
  moon: line('M20.7 15.2A9 9 0 0 1 8.8 3.3a9 9 0 1 0 11.9 11.9Z'),
  filters: line('M4 7h16M4 12h16M4 17h16M8 5v4m8 1v4m-6 1v4'),
  chevron: line('m6 9 6 6 6-6'),
  wrench: line('M14.7 6.3a4 4 0 0 0 5 5L21 13l-8 8-3-3 1.3-1.3a4 4 0 0 1-5-5L3 9.4 6 6.4l2.3 2.3 2-2L8 4.4l3-3 3.7 3.7Z'),
  plug: line('M9 3v5m6-5v5M6 8h12v3a6 6 0 0 1-12 0V8Zm6 9v4'),
  unplug: line('m3 3 18 18M9 3v4m6-4v5m3 0v3a6 6 0 0 1-1.3 3.7M14.5 16.6A6 6 0 0 1 6 11V8h2M12 17v4'),
  more: [['circle', { cx: 5, cy: 12, r: 1.2 }], ['circle', { cx: 12, cy: 12, r: 1.2 }], ['circle', { cx: 19, cy: 12, r: 1.2 }]],
  restart: line('M3 12a9 9 0 1 0 3-6.7M3 4v5h5'),
  check: line('m5 12.5 4.5 4.5L19 7.5'),
  copy: line('M9 9h11v11H9zM5 15H4V4h11v1'),
  plus: line('M12 5v14M5 12h14'),
  'rows-compact': line('M4 5h16M4 9.5h16M4 14h16M4 18.5h16'),
  'rows-comfortable': line('M4 6h16M4 12h16M4 18h16'),
  pin: line('M9 4h6l-1 6 3 3H7l3-3-1-6Zm3 9v7'),
  unpin: line('m4 4 16 16M9 4h6l-1 6 3 3h-4M7 13l3-3M12 13v7'),
  download: line('M12 4v11m-4.5-4.5L12 15l4.5-4.5M5 20h14'),
  info: [['circle', { cx: 12, cy: 12, r: 9 }], ...line('M12 11v5m0-8.5v.5')],
  search: [['circle', { cx: 11, cy: 11, r: 7 }], ...line('m20 20-4-4')],
  edit: line('M4 20h4L19 9l-4-4L4 16v4Zm9-13 4 4'),
  open: line('M14 4h6v6m0-6-9 9M18 14v6H4V6h6'),
  close: line('M6 6l12 12M18 6 6 18'),
  undo: line('M9 14 4 9l5-5M4 9h10a6 6 0 0 1 0 12h-3'),
  sync: line('M20 11a8 8 0 0 0-14.6-4.5L4 8m0-4v4h4m-4 5a8 8 0 0 0 14.6 4.5L20 16m0 4v-4h-4'),
  palette: line('M4 6h16M4 12h10M4 18h7'),
  external: line('M14 4h6v6m0-6-9 9M18 14v6H4V6h6'),
  codex: [["path",{"d":"M22.282 9.821a5.985 5.985 0 0 0-.516-4.91 6.046 6.046 0 0 0-6.51-2.9A6.065 6.065 0 0 0 4.981 4.18a5.985 5.985 0 0 0-3.998 2.9 6.046 6.046 0 0 0 .743 7.097 5.98 5.98 0 0 0 .51 4.911 6.051 6.051 0 0 0 6.515 2.9A5.985 5.985 0 0 0 13.26 24a6.056 6.056 0 0 0 5.772-4.206 5.99 5.99 0 0 0 3.997-2.9 6.056 6.056 0 0 0-.747-7.073zM13.26 22.43a4.476 4.476 0 0 1-2.876-1.04l.141-.081 4.779-2.758a.795.795 0 0 0 .392-.681v-6.737l2.02 1.168a.071.071 0 0 1 .038.052v5.583a4.504 4.504 0 0 1-4.494 4.494zM3.6 18.304a4.47 4.47 0 0 1-.535-3.014l.142.085 4.783 2.759a.771.771 0 0 0 .78 0l5.843-3.369v2.332a.08.08 0 0 1-.033.062L9.74 19.95a4.5 4.5 0 0 1-6.14-1.646zM2.34 7.896a4.485 4.485 0 0 1 2.366-1.973V11.6a.766.766 0 0 0 .388.676l5.815 3.355-2.02 1.168a.076.076 0 0 1-.071 0l-4.83-2.786A4.504 4.504 0 0 1 2.34 7.872zm16.597 3.855l-5.833-3.387L15.119 7.2a.076.076 0 0 1 .071 0l4.83 2.791a4.494 4.494 0 0 1-.676 8.105v-5.678a.79.79 0 0 0-.407-.667zm2.01-3.023l-.141-.085-4.774-2.782a.776.776 0 0 0-.785 0L9.409 9.23V6.897a.066.066 0 0 1 .028-.061l4.83-2.787a4.5 4.5 0 0 1 6.68 4.66zm-12.64 4.135l-2.02-1.164a.08.08 0 0 1-.038-.057V6.075a4.5 4.5 0 0 1 7.375-3.453l-.142.08L8.704 5.46a.795.795 0 0 0-.393.681zm1.097-2.365l2.602-1.5 2.607 1.5v2.999l-2.597 1.5-2.607-1.5z"}]],
  gemini: [["path",{"d":"M12 1C12 7.075 16.925 12 23 12C16.925 12 12 16.925 12 23C12 16.925 7.075 12 1 12C7.075 12 12 7.075 12 1Z"}]],
  claude: Array.from({ length: 12 }, (_, ray) => ['rect', { x: 11, y: ray % 2 ? 2.6 : 1.4, width: 2, height: ray % 2 ? 8 : 9.2, rx: 1,
    transform: 'rotate(' + ray * 30 + ' 12 12)' }]),
  cursor: [['path', { d: 'M12 2 21 7v10l-9 5-9-5V7l9-5Z', opacity: .35 }], ['path', { d: 'M3 7l9 5 9-5-9-5-9 5Z' }], ['path', { d: 'M12 12v10l9-5V7l-9 5Z', opacity: .7 }]]
};

export function icon(name) {
  const svg = document.createElementNS(NS, 'svg');
  svg.setAttribute('class', 'ui-icon icon-' + name);
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('aria-hidden', 'true');
  svg.setAttribute('focusable', 'false');
  const filled = ['codex', 'gemini', 'claude', 'cursor'].includes(name);
  svg.setAttribute('fill', filled ? 'currentColor' : 'none');
  if (!filled) {
    svg.setAttribute('stroke', 'currentColor');
    svg.setAttribute('stroke-width', '1.7');
    svg.setAttribute('stroke-linecap', 'round');
    svg.setAttribute('stroke-linejoin', 'round');
  }
  for (const [element, attrs] of outlines[name] ?? []) {
    const child = document.createElementNS(NS, element);
    for (const [key, value] of Object.entries(attrs)) child.setAttribute(key, value);
    svg.append(child);
  }
  return svg;
}

export const PROVIDER_NAMES = { gemini: 'Gemini', codex: 'Codex', claude: 'Claude', cursor: 'Cursor' };
