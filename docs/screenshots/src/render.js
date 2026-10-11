// Renders the README screenshots from HTML mockups of the app's UI.
// Usage: node docs/screenshots/src/render.js   (needs Playwright + Chromium)
const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright');

const here = __dirname;
const out = path.join(here, '..');
const css = fs.readFileSync(path.join(here, 'ui.css'), 'utf8');
const appIcon = 'data:image/png;base64,' + fs.readFileSync(path.join(here, '..', '..', 'icon.png')).toString('base64');

const stroke = {
  back: '<path d="m12 19-7-7 7-7"/><path d="M19 12H5"/>',
  fwd: '<path d="M5 12h14"/><path d="m12 5 7 7-7 7"/>',
  up: '<path d="m5 12 7-7 7 7"/><path d="M12 19V5"/>',
  refresh: '<path d="M21 12a9 9 0 1 1-9-9c2.52 0 4.93 1 6.74 2.74L21 8"/><path d="M21 3v5h-5"/>',
  search: '<circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/>',
  download: '<path d="M12 3v12"/><path d="m7 10 5 5 5-5"/><path d="M4 20h16"/>',
  upload: '<path d="M12 15V3"/><path d="m7 8 5-5 5 5"/><path d="M4 20h16"/>',
  replica: '<rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8"/><path d="M12 16v4"/><path d="m9.5 9.5 2.5 2.5 2.5-2.5"/><path d="M12 6.5V12"/>',
  link: '<path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71"/><path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"/>',
  drive: '<path d="M22 12H2"/><path d="M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z"/><path d="M6 16h.01"/><path d="M10 16h.01"/>',
  sync: '<path d="M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8"/><path d="M21 3v5h-5"/><path d="M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16"/><path d="M8 16H3v5"/>',
  down: '<path d="m6 9 6 6 6-6"/>',
  right: '<path d="m9 18 6-6-6-6"/>',
  more: '<circle cx="5" cy="12" r="1"/><circle cx="12" cy="12" r="1"/><circle cx="19" cy="12" r="1"/>',
  cloud: '<path d="M17.5 19H9a7 7 0 1 1 6.71-9h1.79a4.5 4.5 0 1 1 0 9Z"/>',
  pc: '<rect x="2" y="3" width="20" height="14" rx="2"/><path d="M8 21h8"/><path d="M12 17v4"/>',
  check: '<path d="M20 6 9 17l-5-5"/>',
  warn: '<path d="m21.73 18-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3"/><path d="M12 9v4"/><path d="M12 17h.01"/>',
  min: '<path d="M5 12h14"/>',
  max: '<rect x="5" y="5" width="14" height="14" rx="1"/>',
  close: '<path d="M18 6 6 18"/><path d="m6 6 12 12"/>',
  sortup: '<path d="m7 14 5-5 5 5"/>',
};
const i = (name, cls = 'i') => `<svg class="${cls}" viewBox="0 0 24 24">${stroke[name]}</svg>`;

const filled = {
  folder: '<svg class="ico" viewBox="0 0 20 20"><path d="M1.5 4.5a1.5 1.5 0 0 1 1.5-1.5h4.6l1.9 2H17a1.5 1.5 0 0 1 1.5 1.5V8h-17z" fill="#e8a425"/><path d="M1.5 7h17v8.5A1.5 1.5 0 0 1 17 17H3a1.5 1.5 0 0 1-1.5-1.5z" fill="#ffc83d"/><path d="M1.5 7h17v1h-17z" fill="#ffd96a"/></svg>',
  doc: (c, t) => `<svg class="ico" viewBox="0 0 20 20"><path d="M4.5 1.5h7.5l4 4v12.5a.5.5 0 0 1-.5.5h-11a.5.5 0 0 1-.5-.5V2a.5.5 0 0 1 .5-.5z" fill="#fff" stroke="#b9bec7"/><path d="M12 1.5v4h4" fill="#eef0f3" stroke="#b9bec7" stroke-linejoin="round"/>${c ? `<rect x="2" y="9.5" width="11" height="7" rx="1.2" fill="${c}"/><text x="7.5" y="14.9" font-family="Inter" font-weight="700" font-size="5.2" fill="#fff" text-anchor="middle">${t}</text>` : '<path d="M7 9.5h6M7 12h6M7 14.5h4" stroke="#b9bec7"/>'}</svg>`,
  gdrive: '<svg class="ico" viewBox="0 0 24 21"><path d="M8 0h8l8 14h-8z" fill="#ffba00"/><path d="M8 0 0 14l4 7 8-14z" fill="#0f9d58"/><path d="M4 21h16l4-7H8z" fill="#4285f4"/></svg>',
  onedrive: '<svg class="ico" viewBox="0 0 24 24"><path d="M9.5 7.5a6 6 0 0 1 10.6 3.2A4.2 4.2 0 0 1 19.6 19H8.5z" fill="#0364b8"/><path d="M4.8 19a3.8 3.8 0 0 1-.6-7.6 5.6 5.6 0 0 1 10.3-1.8l-.1.1A4.5 4.5 0 0 1 21.6 14 4.6 4.6 0 0 1 19 19z" fill="#28a8ea"/></svg>',
  dropbox: '<svg class="ico" viewBox="0 0 24 24" fill="#0061fe"><path d="m7 3 5 3.2-5 3.2-5-3.2zM17 3l5 3.2-5 3.2-5-3.2zM2 12.6l5-3.2 5 3.2-5 3.2zM17 9.4l5 3.2-5 3.2-5-3.2zM7 17l5-3.2 5 3.2-5 3.2z"/></svg>',
  pc: '<svg class="ico" viewBox="0 0 20 20"><rect x="1.5" y="3" width="17" height="11" rx="1.5" fill="#2b88d8"/><rect x="3" y="4.5" width="14" height="8" rx=".5" fill="#9fd3fb"/><path d="M7 17h6M10 14v3" stroke="#5e646e" stroke-width="1.5" stroke-linecap="round"/></svg>',
  linked: '<svg class="ico" viewBox="0 0 20 20"><path d="M1.5 4.5a1.5 1.5 0 0 1 1.5-1.5h4.6l1.9 2H17a1.5 1.5 0 0 1 1.5 1.5V8h-17z" fill="#e8a425"/><path d="M1.5 7h17v8.5A1.5 1.5 0 0 1 17 17H3a1.5 1.5 0 0 1-1.5-1.5z" fill="#ffc83d"/><circle cx="14.5" cy="14.5" r="4.5" fill="#0067c0"/><path d="M13.6 15.4l1.8-1.8M12.9 14.3l-.5.5a1.2 1.2 0 0 0 1.7 1.7l.5-.5M16.1 14.7l.5-.5a1.2 1.2 0 0 0-1.7-1.7l-.5.5" stroke="#fff" stroke-width="1" stroke-linecap="round" fill="none"/></svg>',
};
const xlsx = filled.doc('#107c41', 'X'), docx = filled.doc('#185abd', 'W'), pdf = filled.doc('#d13438', 'PDF'), pptx = filled.doc('#c43e1c', 'P'), txt = filled.doc();

function pill(kind, text) {
  if (!kind) return `<span class="m">${text}</span>`;
  const g = kind === 'local' ? 'pc' : kind === 'cloud' ? (/both/i.test(text) ? 'warn' : 'cloud') : 'check';
  return `<span class="pill ${kind}">${i(g)}${text}</span>`;
}

function chrome(crumbs, searchText, active) {
  const tbs = [['download', 'Refresh from cloud'], ['upload', 'Upload to cloud'], null, ['replica', 'Local replica'], ['link', 'Link folder'], ['drive', 'Add drive'], null, ['sync', 'Sync all']];
  return `
  <div class="titlebar"><img class="appicon" src="${appIcon}"><span>Drive Folder Sync</span><span class="grow"></span>
    <span class="ctl">${i('min')}</span><span class="ctl">${i('max')}</span><span class="ctl">${i('close')}</span></div>
  <div class="toolbar">${tbs.map(t => t ? `<span class="tb${active === t[1] ? ' on' : ''}">${i(t[0])}${t[1]}</span>` : '<span class="sep"></span>').join('')}
    <span class="tb" style="padding:7px 4px;color:var(--muted)">${i('down')}</span><span class="grow"></span><span class="tb">${i('more')}</span></div>
  <div class="navrow"><span class="nb">${i('back')}</span><span class="nb off">${i('fwd')}</span><span class="nb">${i('up')}</span><span class="nb">${i('refresh')}</span>
    <div class="addr">${crumbs.map((c, n) => (n ? `<span class="chev">${i('right')}</span>` : '') + (c.icon ? `<span style="display:flex;margin-right:2px;color:var(--muted)">${c.icon}</span>` : '') + `<span class="crumb">${c.t}</span>`).join('')}</div>
    <div class="search">${i('search')}<span>${searchText}</span></div></div>`;
}

function tree(nodes) {
  return `<div class="tree">${nodes.map(n => `<div class="node${n.sel ? ' sel' : ''}" style="padding-left:${4 + n.lvl * 16}px"><span class="tw">${n.tw ? i(n.tw) : ''}</span>${n.icon || filled.folder}<span class="t">${n.t}</span></div>`).join('')}</div>`;
}

function list(cols, rows) {
  const grid = `grid-template-columns:${cols.map(c => c.w).join(' ')}`;
  return `<div class="list"><div class="head" style="${grid}">${cols.map(c => `<div class="${c.cls || ''}${c.sorted ? ' sorted' : ''}">${c.t}</div>`).join('')}</div>
  <div style="padding-top:4px">${rows.map(r => `<div class="row${r.sel ? ' sel' : ''}${r.hot ? ' hot' : ''}" style="${grid}">
    <div class="name">${r.icon || filled.folder}<span>${r.n}</span>${r.d ? `<span class="d">${r.d}</span>` : ''}</div>
    <div>${pill(r.k, r.s)}</div>${cols.slice(2).map(c => `<div class="m ${c.cls || ''}">${r[c.key] || ''}</div>`).join('')}</div>`).join('')}</div></div>`;
}

function page(inner, w, h) {
  return `<!doctype html><html><head><meta charset="utf-8"><style>${css}</style></head><body><div class="stage"><div class="win" style="width:${w}px;height:${h}px;position:relative">${inner}</div></div></body></html>`;
}

// ---------- 1. Side by side ----------
const sideCols = [{ t: 'Name', w: '1.7fr', sorted: true }, { t: 'Status', w: '1.45fr' }, { t: 'Size', w: '64px', key: 'z', cls: 'r' }, { t: 'Date modified', w: '1.05fr', key: 'm' }];
const leftRows = [
  { n: 'Assets', s: 'Folder', m: '09-10-2026 16:40' },
  { n: 'Contracts', s: 'Only in the cloud', k: 'cloud', m: '07-10-2026 11:02' },
  { n: 'Budget 2026.xlsx', icon: xlsx, s: 'Changed on this PC', k: 'local', z: '48 KB', m: '11-10-2026 09:31', sel: true },
  { n: 'Launch plan.docx', icon: docx, s: 'Changed in the cloud', k: 'cloud', z: '126 KB', m: '11-10-2026 08:57' },
  { n: 'Pitch deck.pptx', icon: pptx, s: 'Changed on both sides', k: 'cloud', z: '4.2 MB', m: '10-10-2026 18:20' },
  { n: 'Press release.docx', icon: docx, s: 'In both places', k: 'same', z: '34 KB', m: '06-10-2026 14:12' },
  { n: 'Roadmap.pdf', icon: pdf, s: 'In both places', k: 'same', z: '2.1 MB', m: '03-10-2026 10:45' },
  { n: 'Vendor list.xlsx', icon: xlsx, s: 'Only in the cloud', k: 'cloud', z: '22 KB', m: '02-10-2026 15:08' },
];
const rightRows = [
  { n: 'Assets', s: 'Folder', m: '09-10-2026 16:40' },
  { n: 'Budget 2026.xlsx', icon: xlsx, s: 'Changed on this PC', k: 'local', z: '51 KB', m: '11-10-2026 10:14', },
  { n: 'Launch plan.docx', icon: docx, s: 'Changed in the cloud', k: 'cloud', z: '119 KB', m: '10-10-2026 17:36' },
  { n: 'Pitch deck.pptx', icon: pptx, s: 'Changed on both sides', k: 'cloud', z: '4.1 MB', m: '11-10-2026 09:02' },
  { n: 'Press release.docx', icon: docx, s: 'In both places', k: 'same', z: '34 KB', m: '06-10-2026 14:12' },
  { n: 'Roadmap.pdf', icon: pdf, s: 'In both places', k: 'same', z: '2.1 MB', m: '03-10-2026 10:45' },
];
const sideTree = (root, rootIcon) => tree([
  { lvl: 0, tw: 'down', t: root, icon: rootIcon },
  { lvl: 1, tw: 'right', t: 'Clients' },
  { lvl: 1, tw: 'right', t: 'Finance' },
  { lvl: 1, tw: 'down', t: 'Projects' },
  { lvl: 2, tw: 'right', t: 'Q4 Launch', sel: true },
  { lvl: 2, tw: 'right', t: 'Website redesign' },
  { lvl: 1, tw: 'right', t: 'Templates' },
]);
const sideBySide = page(`
  ${chrome([{ t: 'This PC', icon: i('pc') }, { t: 'Work Drive' }, { t: 'Projects' }, { t: 'Q4 Launch' }], 'Search Q4 Launch')}
  <div class="page"><div><div style="display:flex;align-items:center;gap:12px"><h1>Q4 Launch</h1><span class="badge">${i('check').replace('class="i"', 'class="i" style="width:12px;height:12px;color:var(--accent);stroke-width:2.2"')}Synced today 09:12</span></div>
    <div class="detail">Drag files or folders to the other side. A dropped folder keeps its contents and the same path.</div></div>
    <div class="legend"><span class="l">Changed on this PC</span><span class="c">Changed in the cloud</span></div></div>
  <div class="split">
    <div class="side"><div class="cap"><span class="tag">Drive</span><b>Work Drive · alex@company.com</b><span class="path">G:\\Projects\\Q4 Launch</span></div>
      <div class="body">${sideTree('Work Drive', filled.gdrive)}${list(sideCols, leftRows)}</div></div>
    <div class="side"><div class="cap"><span class="tag r">Local replica</span><b>alex@company.com</b><span class="path">C:\\Users\\Alex\\Work Drive\\alex@company.com\\Projects\\Q4 Launch</span></div>
      <div class="body">${sideTree('Local replica', filled.pc)}${list(sideCols, rightRows)}</div></div>
  </div>
  <div class="status"><span>8 items</span><span>1 selected · 48 KB</span><span class="grow"></span><span>Linked · last sync today 09:12</span></div>
`, 1600, 600);

// ---------- 2. This PC overview ----------
const ovCols = [{ t: 'Name', w: '2.2fr', sorted: true }, { t: 'Status', w: '1.3fr' }, { t: 'Last synced', w: '1fr', key: 'ls' }];
const drives = [
  { n: 'Google Drive', d: 'alex@example.com', icon: filled.gdrive, s: 'Connected', k: 'same', ls: 'Today 09:12' },
  { n: 'Work Drive', d: 'alex@company.com', icon: filled.gdrive, s: 'Connected', k: 'same', ls: 'Today 09:12', sel: true },
  { n: 'OneDrive', d: 'C:\\Users\\Alex\\OneDrive', icon: filled.onedrive, s: 'Connected', k: 'same', ls: 'Yesterday 18:40' },
  { n: 'Dropbox', d: 'C:\\Users\\Alex\\Dropbox', icon: filled.dropbox, s: 'Connected', k: 'same', ls: '—' },
  { n: 'Linked folders', icon: filled.linked, s: '3 linked', ls: '' },
];
const replicas = [
  { n: 'alex@example.com', d: 'Google Drive', s: 'Selected folders and files', ls: 'Today 09:12' },
  { n: 'alex@company.com', d: 'Work Drive', s: 'Has local changes', k: 'local', ls: 'Today 09:12', sel: true },
  { n: 'OneDrive', d: 'OneDrive', s: 'Selected folders and files', ls: 'Yesterday 18:40' },
  { n: 'Dropbox', d: 'Dropbox', s: 'Not created yet', ls: '—' },
];
const overview = page(`
  ${chrome([{ t: 'This PC', icon: i('pc') }], 'Search This PC')}
  <div class="page"><div><div style="display:flex;align-items:center;gap:12px"><h1>This PC</h1><span class="badge">4 drives</span></div>
    <div class="detail">Open a drive. Its folders stay on the left, and the local replica stays on the right.</div></div>
    <div class="legend"><span class="l">Changed on this PC</span><span class="c">Changed in the cloud</span></div></div>
  <div class="split">
    <div class="side"><div class="cap"><span class="tag">Drives</span><b>Cloud folders on this PC</b></div>
      <div class="body">${tree([{ lvl: 0, tw: 'down', t: 'This PC', icon: filled.pc, sel: true }, { lvl: 1, t: 'Google Drive', icon: filled.gdrive }, { lvl: 1, t: 'Work Drive', icon: filled.gdrive }, { lvl: 1, t: 'OneDrive', icon: filled.onedrive }, { lvl: 1, t: 'Dropbox', icon: filled.dropbox }, { lvl: 1, t: 'Linked folders', icon: filled.linked }])}${list(ovCols, drives)}</div></div>
    <div class="side"><div class="cap"><span class="tag r">Local replicas</span><b>C:\\Users\\Alex\\Work Drive</b></div>
      <div class="body">${tree([{ lvl: 0, tw: 'down', t: 'Local replicas', icon: filled.pc, sel: true }, { lvl: 1, tw: 'right', t: 'alex@example.com' }, { lvl: 1, tw: 'right', t: 'alex@company.com' }, { lvl: 1, tw: 'right', t: 'OneDrive' }, { lvl: 1, t: 'Dropbox' }])}${list(ovCols, replicas)}</div></div>
  </div>
  <div class="status"><span>5 items</span><span class="grow"></span><span>Starts at sign-in · uploads at shutdown</span></div>
`, 1600, 480);

// ---------- 3. Installer ----------
const setupDrives = [
  { n: 'Google Drive', d: 'alex@example.com (G:)', p: 'G:\\', icon: filled.gdrive },
  { n: 'Google Drive', d: 'alex@company.com (H:)', p: 'H:\\My Drive', icon: filled.gdrive },
  { n: 'OneDrive', p: 'C:\\Users\\Alex\\OneDrive', icon: filled.onedrive },
  { n: 'Dropbox', p: 'C:\\Users\\Alex\\Dropbox', icon: filled.dropbox },
];
const included = ['Starts when you sign in and refreshes linked folders from the cloud', 'Uploads linked folders when Windows shuts down', 'Adds “Link folder for sync” to the folder right-click menu', 'Adds Drive Folder Sync to the Start menu'];
const setup = page(`
  <div class="titlebar" style="background:#fff;border-bottom:1px solid var(--border)"><img class="appicon" src="${appIcon}"><span>Install Drive Folder Sync</span><span class="grow"></span><span class="ctl">${i('close')}</span></div>
  <div style="height:128px;position:relative;overflow:hidden;background:linear-gradient(135deg,#0067c0,#003e8a);display:flex;align-items:center;padding:0 32px;gap:18px">
    <div style="position:absolute;right:-70px;top:-70px;width:240px;height:240px;border-radius:50%;background:rgba(255,255,255,.07)"></div>
    <div style="position:absolute;right:-60px;top:40px;width:150px;height:150px;border-radius:50%;background:rgba(255,255,255,.07)"></div>
    <img src="${appIcon}" style="width:56px;height:56px;border-radius:12px;box-shadow:0 6px 16px rgba(0,0,0,.25)">
    <div><div style="color:#fff;font-size:25px;font-weight:600;letter-spacing:-.02em">Drive Folder Sync</div>
      <div style="color:#d6e8fa;margin-top:4px">Keep the folders you choose in sync with Google Drive, OneDrive, and Dropbox</div></div>
    <div style="position:absolute;right:16px;top:12px;color:#bed7f2;font-size:11.5px">Version 1.3.1</div></div>
  <div style="padding:24px 32px 0;flex:1">
    <div style="font-weight:600">Drives found on this PC</div>
    <div style="margin-top:8px;border:1px solid var(--border);border-radius:8px;padding:4px">
      ${setupDrives.map((d, n) => `<div style="display:flex;align-items:center;gap:12px;height:48px;padding:0 10px 0 8px;${n < setupDrives.length - 1 ? 'box-shadow:inset 0 -1px 0 var(--border)' : ''}">
        <div style="width:32px;height:32px;border-radius:6px;background:var(--chrome);display:grid;place-items:center">${d.icon}</div>
        <div style="flex:1;min-width:0"><div style="font-weight:600">${d.n}${d.d ? ` <span style="font-weight:400;color:var(--muted)">· ${d.d}</span>` : ''}</div><div style="color:var(--muted);font-size:11.5px;margin-top:2px">${d.p}</div></div>
        ${pill('same', 'Connected')}</div>`).join('')}
    </div>
    <div style="font-weight:600;margin-top:22px">What gets installed</div>
    <div style="margin-top:6px">${included.map(t => `<div style="display:flex;align-items:center;gap:12px;height:26px"><span style="color:var(--accent);display:flex">${i('check').replace('class="i"', 'class="i" style="width:14px;height:14px;stroke-width:2.2"')}</span>${t}</div>`).join('')}</div>
    <div style="color:var(--muted);margin-top:14px;line-height:1.55">Installs for Alex only. Nothing is copied until you link a folder.</div>
  </div>
  <div style="background:var(--chrome);border-top:1px solid var(--border);padding:16px 32px;display:flex;gap:10px">
    <span style="height:32px;padding:0 18px;border:1px solid var(--border-strong);background:#fff;border-radius:5px;display:grid;place-items:center">Uninstall</span><span style="flex:1"></span>
    <span style="height:32px;width:110px;border:1px solid var(--border-strong);background:#fff;border-radius:5px;display:grid;place-items:center">Cancel</span>
    <span style="height:32px;width:110px;background:var(--accent);color:#fff;border-radius:5px;display:grid;place-items:center;font-weight:600">Install</span></div>
`, 620, 690);

(async () => {
  const browser = await chromium.launch();
  const ctx = await browser.newContext({ deviceScaleFactor: 2, viewport: { width: 1800, height: 1000 } });
  const pageObj = await ctx.newPage();
  for (const [name, html] of [['side-by-side', sideBySide], ['overview', overview], ['setup', setup]]) {
    fs.writeFileSync(path.join(here, name + '.html'), html);
    await pageObj.setContent(html, { waitUntil: 'load' });
    await pageObj.evaluate(() => document.fonts.ready);
    await (await pageObj.$('.stage')).screenshot({ path: path.join(out, name + '.png') });
    console.log('wrote', name + '.png');
  }
  await browser.close();
})();
