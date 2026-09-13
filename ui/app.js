/* ZweDesk UI — vanilla JS, talks to the C# host over WebView2 messages (see src/App/Bridge.cs). */
'use strict';

// ============================================================ helpers
const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const GB = 1024 * 1024 * 1024;

function fmtBytes(n) {
  if (n === null || n === undefined) return '—';
  const u = ['B', 'KB', 'MB', 'GB', 'TB'];
  let v = Number(n), i = 0;
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
  const d = i >= 3 ? (v < 10 ? 2 : 1) : (v < 10 && i > 0 ? 1 : 0);
  return v.toFixed(d) + ' ' + u[i];
}
function toGb(n) { return n == null ? '' : (n / GB).toFixed(n % GB === 0 ? 0 : 1); }
function fmtDate(iso) {
  if (!iso) return '—';
  const d = new Date(iso);
  if (isNaN(d)) return String(iso);
  return d.toLocaleString(undefined, { year: 'numeric', month: 'short', day: '2-digit', hour: '2-digit', minute: '2-digit' });
}
function ago(iso) {
  if (!iso) return 'never';
  const s = (Date.now() - new Date(iso).getTime()) / 1000;
  if (s < 60) return 'just now';
  if (s < 3600) return Math.floor(s / 60) + ' min ago';
  if (s < 86400) return Math.floor(s / 3600) + ' h ago';
  return Math.floor(s / 86400) + ' d ago';
}
function pctClass(p) { return p == null ? '' : p >= 100 ? 'danger' : p >= 90 ? 'warn' : ''; }
function usageCell(f) {
  if (f.quotaBytes == null) {
    return `<div class="usage"><div class="txt"><span>${f.usedBytes == null ? '<span class="muted">size unknown</span>' : esc(fmtBytes(f.usedBytes))}</span><span class="badge warn">no quota</span></div></div>`;
  }
  const pct = f.pct ?? 0;
  const free = Math.max(0, f.quotaBytes - (f.usedBytes || 0));
  return `<div class="usage"><div class="txt"><span>${esc(fmtBytes(f.usedBytes))} / ${esc(fmtBytes(f.quotaBytes))} <span class="muted">· ${esc(fmtBytes(free))} free</span></span><span class="${pctClass(pct)}">${pct.toFixed(0)}%</span></div>
    <div class="bar"><div class="fill ${pctClass(pct)}" style="width:${Math.min(100, pct)}%"></div></div></div>`;
}
const ICON = {
  dashboard: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="3" width="7" height="9" rx="1.5"/><rect x="14" y="3" width="7" height="5" rx="1.5"/><rect x="14" y="12" width="7" height="9" rx="1.5"/><rect x="3" y="16" width="7" height="5" rx="1.5"/></svg>',
  server: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="4" width="18" height="6" rx="1.5"/><rect x="3" y="14" width="18" height="6" rx="1.5"/><circle cx="7" cy="7" r="1" fill="currentColor"/><circle cx="7" cy="17" r="1" fill="currentColor"/></svg>',
  folder: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/></svg>',
  alert: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M12 3 2 20h20L12 3z"/><path d="M12 10v4M12 17h.01"/></svg>',
  link: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1.5 1.5"/><path d="M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1.5-1.5"/></svg>',
  diag: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M3 12h4l3-8 4 16 3-8h4"/></svg>',
  audit: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M8 6h13M8 12h13M8 18h13"/><path d="M3 6h.01M3 12h.01M3 18h.01"/></svg>',
  settings: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3h0a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8v0a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z"/></svg>',
  refresh: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" width="14" height="14"><path d="M21 12a9 9 0 1 1-2.6-6.4"/><path d="M21 3v6h-6"/></svg>',
  plus: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" width="14" height="14"><path d="M12 5v14M5 12h14"/></svg>',
  search: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/></svg>',
  x: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" width="16" height="16"><path d="M6 6l12 12M18 6 6 18"/></svg>',
  more: '<svg viewBox="0 0 24 24" fill="currentColor" width="16" height="16"><circle cx="5" cy="12" r="2"/><circle cx="12" cy="12" r="2"/><circle cx="19" cy="12" r="2"/></svg>',
  sync: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" width="14" height="14"><path d="M4 4v5h5M20 20v-5h-5"/><path d="M20 9A8 8 0 0 0 5.6 5.6L4 9M4 15a8 8 0 0 0 14.4 3.4L20 15"/></svg>',
  download: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" width="14" height="14"><path d="M12 3v12M6 11l6 6 6-6M4 21h16"/></svg>',
  user: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="8" r="4"/><path d="M4 21a8 8 0 0 1 16 0"/></svg>',
  check: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" width="14" height="14"><path d="m5 12 5 5L20 7"/></svg>',
};

// ============================================================ RPC bridge
const rpc = (() => {
  const bridge = window.chrome && window.chrome.webview;
  let seq = 0;
  const pending = new Map();
  const listeners = new Map();
  function handle(m) {
    if (!m) return;
    if (m.event) { (listeners.get(m.event) || []).forEach(f => { try { f(m.data); } catch (e) { console.error(e); } }); return; }
    const p = pending.get(m.id);
    if (!p) return;
    pending.delete(m.id);
    if (m.ok) p.resolve(m.result);
    else { const e = new Error(m.error || 'Error'); e.details = m.details; e.cancelled = !!m.cancelled; p.reject(e); }
  }
  if (bridge) bridge.addEventListener('message', ev => handle(ev.data));
  function send(msg) { if (bridge) bridge.postMessage(msg); else devShim(msg, handle); }
  function call(op, params = {}) {
    const id = 'r' + (++seq);
    const promise = new Promise((resolve, reject) => { pending.set(id, { resolve, reject }); send({ id, op, params }); });
    promise.id = id;
    promise.cancel = () => send({ id: 'c' + id, op: 'rpc.cancel', params: { targetId: id } });
    return promise;
  }
  function on(evt, f) { if (!listeners.has(evt)) listeners.set(evt, []); listeners.get(evt).push(f); }
  return { call, on, native: !!bridge };
})();

window.addEventListener('error', e => { try { rpc.call('ui.error', { message: String(e.message), stack: e.error && e.error.stack ? String(e.error.stack) : '' }); } catch (_) { } });
window.addEventListener('unhandledrejection', e => { const r = e.reason || {}; if (r.cancelled) return; try { rpc.call('ui.error', { message: 'unhandled: ' + (r.message || r), stack: r.stack || '' }); } catch (_) { } });

// Browser preview only (no WebView2 host): answers a few operations with sample data so the layout can be checked.
function devShim(msg, reply) {
  const names = ['u1024', 'u1179', 'U2010', 'u3018', 'U4055', 'u8571', 'U8629', 'u8642', '_Quarantine'];
  const folders = names.map((n, i) => ({ id: i + 1, serverId: 1, serverName: 'FS1', name: n, path: 'D:\\Users\\' + n, owner: 'DEMO\\' + n, userRights: 'Modify', extraAccess: i === 2 ? [{ id: 'DEMO\\Domain Users', rights: 'ReadAndExecute', type: 'Allow', inherited: false }] : [], quotaBytes: i % 3 === 0 ? null : 5 * GB, usedBytes: (i + 1) * 0.7 * GB, pct: i % 3 === 0 ? null : Math.min(140, (i + 1) * 14), lastWriteUtc: new Date().toISOString(), scannedAt: new Date().toISOString(), linkStatus: i % 4 === 1 ? 'missing' : 'ok', linkTargets: ['\\\\fs1.demo.local\\Users$\\' + n], issues: i === 3 ? ['DUPLICATE_FOLDER'] : i % 3 === 0 ? ['NO_QUOTA'] : [], isUserFolder: !n.startsWith('_') }));
  const answers = {
    'app.info': () => ({ version: '1.0.2-preview', mode: 'preview', frameless: false, machine: 'BROWSER', localFqdn: 'browser.local', dataDir: 'C:\\...', dbPath: 'C:\\...\\zwedesk.db', logFile: 'C:\\...\\vdi.log', webview: 'n/a', windowsUser: 'preview' }),
    'domains.list': () => ([{ id: 1, name: 'demo.local', netbios: 'DEMO', dfsRoot: '\\\\demo.local\\Users', dfsHostFqdn: 'dc1.demo.local', servers: 2, hasRemembered: false }]),
    'auth.remembered': () => null,
    'auth.session': () => ({ loggedIn: false }),
    'auth.login': p => ({ user: 'DEMO\\' + p.user, domain: 'demo.local', netbios: 'DEMO', domainId: 1, mode: 'preview', servers: [{ id: 1, name: 'FS1', ok: true, detail: 'FS1 as DEMO\\' + p.user }, { id: 2, name: 'FS2', ok: false, detail: 'WinRM cannot connect' }] }),
    'settings.get': () => ({ default_quota_bytes: String(5 * GB), theme: 'dark' }),
    'servers.list': () => ([{ id: 1, domainId: 1, name: 'FS1', fqdn: 'FS1.demo.local', shareName: 'Users$', localRoot: 'D:\\Users', enabled: true, lastScanAt: new Date().toISOString(), fsrmInstalled: true }, { id: 2, domainId: 1, name: 'FS2', fqdn: 'FS2.demo.local', shareName: null, localRoot: 'D:\\Users', enabled: true, lastScanAt: null, lastError: 'WinRM cannot connect', fsrmInstalled: false }]),
    'dashboard.get': () => ({ domain: { id: 1, name: 'demo.local', netbios: 'DEMO', dfsRoot: '\\\\demo.local\\Users', dfsHostFqdn: 'dc1.demo.local' }, servers: answers['servers.list']().map(s => ({ ...s, folders: 8, usedBytes: 30 * GB, quotaBytes: 60 * GB })), counts: { servers: 2, folders: 76, links: 70, issues: 12, withQuota: 50, over90: 3, over100: 1 }, issuesByKind: [{ kind: 'NO_QUOTA', count: 5 }, { kind: 'DUPLICATE_FOLDER', count: 4 }, { kind: 'FOLDER_WITHOUT_LINK', count: 3 }], topUsage: folders.slice(0, 6), critical: folders.filter(f => f.pct >= 90), lastScan: new Date().toISOString(), totalUsed: 60 * GB, totalQuota: 250 * GB }),
    'folders.list': () => folders,
    'folders.details': p => ({ folder: folders[p.folderId - 1], aces: [{ id: 'NT AUTHORITY\\SYSTEM', rights: 'FullControl', type: 'Allow', inherited: false }, { id: 'DEMO\\u1024', rights: 'Modify, Synchronize', type: 'Allow', inherited: false }], link: { linkPath: '\\\\demo.local\\Users\\u1024', state: 'Online', targets: [{ target: '\\\\FS1.demo.local\\Users$\\u1024', state: 'Online' }] }, audit: [], server: { id: 1, name: 'FS1', fqdn: 'FS1.demo.local', shareName: 'Users$', localRoot: 'D:\\Users', fsrmInstalled: true } }),
    'issues.list': () => ([{ id: 1, kind: 'DUPLICATE_FOLDER', nameKey: 'u3018', details: { name: 'u3018', copies: [{ server: 'FS1', path: 'D:\\Users\\u3018', lastWriteUtc: '2024-06-24T11:56:21Z', usedBytes: 2 * GB }, { server: 'FS2', path: 'D:\\Users\\U3018', lastWriteUtc: '2024-09-07T10:24:39Z', usedBytes: 1 * GB }] } }, { id: 2, kind: 'NO_QUOTA', nameKey: 'c0099@dfs1', details: { name: 'u1024', server: 'FS1', folderId: 1, path: 'D:\\Users\\u1024' } }, { id: 3, kind: 'LINK_WITHOUT_FOLDER', nameKey: 'c7777', details: { name: 'u9001', linkPath: '\\\\demo.local\\Users\\u9001', targets: ['\\\\FS1.demo.local\\Users$\\u9001'] } }, { id: 4, kind: 'FOLDER_WITHOUT_LINK', nameKey: 'u1179', details: { name: 'u1179', server: 'FS1', serverId: 1, folderId: 2, path: 'D:\\Users\\u1179', duplicate: false } }]),
    'links.list': () => ({ links: folders.filter(f => f.isUserFolder).map(f => ({ id: f.id, name: f.name, linkPath: '\\\\demo.local\\Users\\' + f.name, state: 'Online', targets: [{ target: f.linkTargets[0], state: 'Online' }], foldersOn: ['FS1'], status: 'ok' })), unverified: [] }),
    'audit.list': () => ({ rows: [{ id: 1, ts: new Date().toISOString(), actor: 'DEMO\\admin', server: 'FS1', op: 'set_quota', target: 'D:\\Users\\u1024', ok: true }], total: 1 }),
    'diag.run': () => ({ servers: [{ id: 1, name: 'FS1', fqdn: 'FS1.demo.local', localRoot: 'D:\\Users', shareName: 'Users$', rows: [{ key: 'dns', label: 'DNS resolution', status: 'ok', detail: '10.10.0.3' }, { key: 'winrm', label: 'WinRM', status: 'ok', detail: 'FS1 as DEMO\\admin' }, { key: 'fsrm', label: 'FSRM role', status: 'fail', detail: 'not installed', fix: 'installFsrm' }] }], dfsHost: { host: 'dc1.demo.local', dfsRoot: '\\\\demo.local\\Users', rows: [{ key: 'dfsn', label: 'PowerShell module DFSN', status: 'ok', detail: 'available' }] } }),
    'scan.run': () => ({ servers: [{ id: 1, name: 'FS1', ok: true, count: 51 }], links: 70, dfsError: null, issues: 12 }),
    'sync.dryRun': () => ({ toCreate: [{ folderId: 2, name: 'u1179', server: 'FS1', path: 'D:\\Users\\u1179', linkPath: '\\\\demo.local\\Users\\u1179', targetPath: '\\\\FS1.demo.local\\Users$\\u1179', ready: true }], manual: [{ name: 'u3018', copies: [{ server: 'FS1' }, { server: 'FS2' }] }], dead: [{ name: 'u9001', linkPath: '\\\\demo.local\\Users\\u9001' }], mismatch: [] }),
    'users.lookup': p => ({ found: !p.code.toLowerCase().startsWith('x'), user: { samAccountName: p.code, sid: 'S-1-5-21-1-2-3-1105', displayName: 'User ' + p.code, disabled: false }, existingFolders: [], existingLink: null }),
    'dir.get': () => ({ ous: [{ dn: 'DC=demo,DC=local', name: 'demo.local', canonical: 'demo.local/', kind: 'domain' }, { dn: 'OU=VDI Users,DC=demo,DC=local', name: 'VDI Users', canonical: 'demo.local/VDI Users', kind: 'ou' }, { dn: 'OU=Sales,OU=VDI Users,DC=demo,DC=local', name: 'Sales', canonical: 'demo.local/VDI Users/Sales', kind: 'ou' }], groups: [{ name: 'VDI-Users', sam: 'VDI-Users', dn: 'CN=VDI-Users,OU=Groups,DC=demo,DC=local', scope: 'Global', description: 'All VDI users' }, { name: 'VDI-Sales', sam: 'VDI-Sales', dn: 'CN=VDI-Sales,OU=Groups,DC=demo,DC=local', scope: 'Global' }], dcs: [{ hostName: 'DC1.demo.local', ip: '10.10.0.1', site: 'Default', globalCatalog: true }], defaults: { ouDn: 'OU=VDI Users,DC=demo,DC=local', groups: ['CN=VDI-Users,OU=Groups,DC=demo,DC=local'], poolId: 'd7f1a2b3-sales' }, pools: [{ poolId: 'd7f1a2b3-sales', name: 'Sales-Win11', displayName: 'Sales desktops', type: 'AUTOMATED', enabled: true, entitledGroups: ['DEMO\\VDI-Sales'], entitledUsers: 0 }], horizon: { configured: true, horizonUrl: 'https://horizon.demo.local', entitleMode: 'group' }, servers: answers['servers.list'](), defaultQuotaBytes: 5 * GB, mustChangeDefault: true }),
    'provision.plan': p => ({ account: null, folders: [], link: null, linkOk: false, blockers: [] }),
    'provision.run': p => ({ ok: true, steps: [{ id: 'account', label: 'Create AD account ' + p.sam, status: 'ok', detail: 'CN=' + p.sam }, { id: 'groups', label: 'Add to 2 group(s)', status: 'ok', detail: 'added: VDI-Users, VDI-Sales' }, { id: 'folder', label: 'Create D:\\Users\\' + p.sam, status: 'ok' }, { id: 'quota', label: 'Set FSRM quota 5 GB', status: 'ok' }, { id: 'link', label: 'Create DFS link', status: 'ok' }], user: { samAccountName: p.sam, sid: 'S-1-5-21-1-2-3-2001', distinguishedName: 'CN=' + p.sam }, password: p.password || 'Xk7#pQ2m!vR9', passwordGenerated: !p.password, path: 'D:\\Users\\' + p.sam, linkPath: '\\\\demo.local\\Users\\' + p.sam, targetPath: '\\\\FS1.demo.local\\Users$\\' + p.sam, displayName: p.firstName + ' ' + p.lastName, upn: p.sam + '@demo.local' }),
    'password.generate': () => ({ password: 'Xk7#pQ2m!vR9' }),
    'env.horizonTest': () => ({ ok: true, message: 'Connected (preview)', pools: [{ poolId: 'd7f1a2b3-sales', name: 'Sales-Win11', displayName: 'Sales desktops', type: 'AUTOMATED', enabled: true, entitledGroups: ['DEMO\\VDI-Sales'], entitledUsers: 0 }] }),
    'env.pools': () => ([]),
  };
  setTimeout(() => {
    const f = answers[msg.op];
    if (msg.op === 'rpc.cancel') return;
    reply(f ? { id: msg.id, ok: true, result: f(msg.params || {}) } : { id: msg.id, ok: true, result: { ok: true } });
  }, 120);
}

// ============================================================ UI primitives
function toast(kind, title, msg, ms = 5000) {
  const t = document.createElement('div');
  t.className = 'toast ' + kind;
  t.innerHTML = `<div class="t">${esc(title)}</div>${msg ? `<div class="small">${esc(msg)}</div>` : ''}`;
  $('#toasts').appendChild(t);
  setTimeout(() => { t.style.opacity = '0'; t.style.transition = 'opacity .3s'; setTimeout(() => t.remove(), 300); }, ms);
}

function modal({ title, body, footer = [], wide = false, onClose }) {
  const host = $('#modalHost');
  const bd = document.createElement('div');
  bd.className = 'modal-backdrop';
  bd.innerHTML = `<div class="modal ${wide ? 'wide' : ''}"><div class="modal-head"><h2>${esc(title)}</h2><button class="btn ghost icon" data-close title="Close">${ICON.x}</button></div><div class="modal-body"></div><div class="modal-foot"></div></div>`;
  host.appendChild(bd);
  const m = {
    el: bd, body: bd.querySelector('.modal-body'), foot: bd.querySelector('.modal-foot'), locked: false,
    close() { if (!bd.isConnected) return; bd.remove(); onClose && onClose(); },
    setFooter(buttons) {
      m.foot.innerHTML = '';
      buttons.forEach(b => {
        const btn = document.createElement('button');
        btn.className = 'btn ' + (b.kind || '');
        btn.innerHTML = b.html || esc(b.label);
        btn.disabled = !!b.disabled;
        btn.onclick = () => b.onClick(m, btn);
        m.foot.appendChild(btn);
      });
    },
  };
  if (typeof body === 'string') m.body.innerHTML = body; else if (body) m.body.appendChild(body);
  m.setFooter(footer);
  bd.querySelector('[data-close]').onclick = () => { if (!m.locked) m.close(); };
  bd.addEventListener('mousedown', e => { if (e.target === bd && !m.locked) m.close(); });
  return m;
}

function confirmDialog(title, html, { label = 'Confirm', kind = 'primary' } = {}) {
  return new Promise(resolve => {
    const m = modal({
      title, body: html, onClose: () => resolve(false),
      footer: [
        { label: 'Cancel', onClick: m => m.close() },
        { label, kind, onClick: m => { m.el.remove(); resolve(true); } },
      ],
    });
  });
}

function showError(title, err) {
  const details = err && err.details ? `<pre class="details">${esc(err.details)}</pre>` : '';
  modal({ title, body: `<div class="note danger">${esc(err && err.message ? err.message : String(err))}</div>${details}`, footer: [{ label: 'Close', onClick: m => m.close() }] });
}

function busy(btn, on, label) {
  if (!btn) return;
  if (on) { btn.dataset.label = btn.innerHTML; btn.disabled = true; btn.innerHTML = `<span class="spin"></span> ${esc(label || 'Working…')}`; }
  else { btn.disabled = false; if (btn.dataset.label) btn.innerHTML = btn.dataset.label; }
}

function contextMenu(anchor, items) {
  $$('.menu').forEach(m => m.remove());
  const menu = document.createElement('div');
  menu.className = 'menu';
  items.forEach(it => {
    if (it === '-') { const s = document.createElement('div'); s.style.cssText = 'height:1px;background:var(--border);margin:4px 0'; menu.appendChild(s); return; }
    const b = document.createElement('button');
    b.textContent = it.label;
    b.disabled = !!it.disabled;
    if (it.danger) b.className = 'danger';
    b.onclick = () => { menu.remove(); it.onClick(); };
    menu.appendChild(b);
  });
  document.body.appendChild(menu);
  const r = anchor.getBoundingClientRect();
  menu.style.top = Math.min(window.innerHeight - menu.offsetHeight - 8, r.bottom + 4) + 'px';
  menu.style.left = Math.min(window.innerWidth - menu.offsetWidth - 8, r.right - menu.offsetWidth) + 'px';
  const off = e => { if (!menu.contains(e.target)) { menu.remove(); document.removeEventListener('mousedown', off); } };
  setTimeout(() => document.addEventListener('mousedown', off), 0);
}

function copyText(t) { try { navigator.clipboard.writeText(t); toast('ok', 'Copied', t, 2000); } catch (_) { } }

// ============================================================ state
const S = {
  info: null, session: null, domainId: null, domain: null, domains: [], servers: [],
  page: 'dashboard', dashboard: null, folders: [], issues: [], links: [], diag: null,
  audit: { rows: [], total: 0, offset: 0, filter: '' }, settings: {},
  filters: { q: '', server: 'all', chip: 'all' }, sort: { key: 'name', dir: 1 }, issueKind: 'all', linkQ: '',
  progress: null, loaded: {},
};

const KIND = {
  DUPLICATE_FOLDER: { label: 'Folder on multiple servers', cls: 'danger', desc: 'The same user code exists on more than one server. No DFS link is created automatically — decide which copy is the live one.' },
  LINK_TARGET_MISMATCH: { label: 'DFS link points to the wrong server', cls: 'danger', desc: 'The link target host does not hold this folder.' },
  FOLDER_WITHOUT_LINK: { label: 'Folder without DFS link', cls: 'warn', desc: 'The folder exists but has no entry in the DFS namespace.' },
  LINK_WITHOUT_FOLDER: { label: 'DFS link without folder', cls: 'danger', desc: 'The link points at a file server that was scanned, and that server has no folder of this name. Links are never deleted automatically.' },
  EXTRA_ACCESS: { label: 'Unexpected permissions', cls: 'warn', desc: 'An identity outside the standard pattern (user, SYSTEM, Administrators, Domain Admins) has access.' },
  NO_QUOTA: { label: 'No quota', cls: 'info', desc: 'No FSRM quota is applied, so usage is not tracked and not limited.' },
};

const PAGES = {
  dashboard: { title: 'Dashboard', icon: ICON.dashboard },
  wizard: { title: 'New User', icon: ICON.user },
  folders: { title: 'User Folders', icon: ICON.folder },
  issues: { title: 'Issues', icon: ICON.alert },
  links: { title: 'DFS Links', icon: ICON.link },
  env: { title: 'Environment', icon: ICON.server },
  diag: { title: 'Diagnostics', icon: ICON.diag },
  audit: { title: 'Audit Log', icon: ICON.audit },
  settings: { title: 'Settings', icon: ICON.settings },
};

function applyTheme(t) { document.documentElement.dataset.theme = t; S.settings.theme = t; if (rpc.native && S.info && S.info.frameless) rpc.call('window.theme', { dark: t !== 'light' }).catch(() => { }); }

// ============================================================ window chrome
// The native title bar is gone (see MainForm): the bar at the top of the page is the caption. Dragging and the buttons go
// through the bridge; in a plain browser the bar is only decoration.
function setupChrome() {
  const sub = $('#chromeSub');
  sub.innerHTML = `v${esc(S.info.version)}` + (S.info.mode === 'mock' ? ' <span class="badge warn">DEMO MODE</span>' : S.info.mode === 'preview' ? ' <span class="badge warn">BROWSER PREVIEW</span>' : '');
  if (!rpc.native || !S.info.frameless) return;
  $('#chromeBtns').classList.remove('hidden');
  const onBar = e => !e.target.closest('button,input,select,a');
  const drag = $('#chromeDrag');
  drag.addEventListener('mousedown', e => { if (e.button === 0 && onBar(e)) { e.preventDefault(); rpc.call('window.drag').catch(() => { }); } });
  drag.addEventListener('dblclick', e => { if (onBar(e)) rpc.call('window.toggleMax').catch(() => { }); });
  $('#wMin').onclick = () => rpc.call('window.minimize').catch(() => { });
  $('#wClose').onclick = () => rpc.call('window.close').catch(() => { });
}

// ============================================================ login
async function showLogin() {
  $('#app').classList.add('hidden');
  $('#login').classList.remove('hidden');
  const sel = $('#loginDomain');
  sel.innerHTML = S.domains.map(d => `<option value="${d.id}">${esc(d.name)}${d.netbios ? ' (' + esc(d.netbios) + ')' : ''}</option>`).join('');
  const first = S.domains.length === 0;
  $('#loginForm').classList.toggle('hidden', first);
  $('#loginFirstRun').classList.toggle('hidden', !first);
  $('#loginMode').textContent = S.info.mode === 'mock' ? 'DEMO MODE — mock data' : S.info.mode === 'preview' ? 'BROWSER PREVIEW' : 'Live';
  $('#loginMode').className = 'badge ' + (S.info.mode === 'real' ? 'ok' : 'warn');
  $('#loginMachine').textContent = `${S.info.localFqdn} · v${S.info.version}`;
  if (!first) { await prefillRemembered(); setTimeout(() => ($('#loginUser').value ? $('#loginPass') : $('#loginUser')).focus(), 50); }
}

async function prefillRemembered() {
  const id = Number($('#loginDomain').value);
  try {
    const r = await rpc.call('auth.remembered', { domainId: id });
    if (r) { $('#loginUser').value = r.user || ''; $('#loginPass').value = r.password || ''; $('#loginRemember').checked = true; }
    else { $('#loginPass').value = ''; $('#loginRemember').checked = false; }
  } catch (_) { }
}

async function doLogin(e) {
  e.preventDefault();
  const btn = $('#loginBtn'), err = $('#loginError');
  err.classList.add('hidden');
  busy(btn, true, 'Signing in…');
  try {
    const r = await rpc.call('auth.login', { domainId: Number($('#loginDomain').value), user: $('#loginUser').value.trim(), password: $('#loginPass').value, remember: $('#loginRemember').checked });
    await enterApp(r);
    (r.notices || []).forEach(n => toast('', 'Environment adjusted', n, 12000));
    (r.servers || []).filter(s => !s.ok).forEach(s => toast('warn', `${s.name}: not reachable`, s.detail, 8000));
  } catch (ex) {
    err.textContent = ex.message;
    err.classList.remove('hidden');
  } finally { busy(btn, false); }
}

async function enterApp(loginResult) {
  S.session = loginResult;
  S.domainId = loginResult.domainId;
  S.domains = await rpc.call('domains.list');
  S.domain = S.domains.find(d => d.id === S.domainId) || null;
  $('#login').classList.add('hidden');
  $('#app').classList.remove('hidden');
  $('#sideDomain').textContent = S.domain ? S.domain.name : '';
  const u = loginResult.user || '';
  $('#sideUser').innerHTML = `<span class="avatar">${esc(u.split('\\').pop().slice(0, 2).toUpperCase())}</span><span class="ellipsis" title="${esc(u)}">${esc(u)}</span>`;
  S.loaded = {};
  renderNav();
  nav('dashboard');
}

async function logout() {
  try { await rpc.call('auth.logout'); } catch (_) { }
  S.session = null; S.domainId = null; S.dashboard = null; S.folders = []; S.issues = []; S.links = []; S.diag = null;
  closeDrawer();
  S.domains = await rpc.call('domains.list');
  showLogin();
}

// ============================================================ navigation
function renderNav() {
  const counts = { issues: S.dashboard ? S.dashboard.counts.issues : null };
  $('#nav').innerHTML = Object.entries(PAGES).map(([k, p], i) =>
    (i === 5 ? '<div class="sep"></div>' : '') +
    `<button data-page="${k}" class="${S.page === k ? 'on' : ''}">${p.icon}<span>${esc(p.title)}</span>${counts[k] ? `<span class="cnt">${counts[k]}</span>` : ''}</button>`).join('');
  $$('#nav button').forEach(b => b.onclick = () => nav(b.dataset.page));
}

async function nav(page) {
  S.page = page;
  $$('#nav button').forEach(b => b.classList.toggle('on', b.dataset.page === page));
  $('#pageTitle').textContent = PAGES[page].title;
  $('#pageSub').textContent = '';
  closeDrawer();
  renderTopActions();
  const c = $('#content');
  c.innerHTML = '<div class="empty"><span class="spin"></span></div>';
  try {
    await ({ dashboard: loadDashboard, wizard: loadWizard, folders: loadFolders, issues: loadIssues, links: loadLinks, env: loadEnv, diag: renderDiag, audit: loadAudit, settings: renderSettings })[page]();
  } catch (ex) {
    c.innerHTML = `<div class="card"><div class="note danger">${esc(ex.message)}</div></div>`;
  }
}

function renderTopActions() {
  const a = $('#topActions');
  const scanBtn = `<button class="btn" data-act="scan">${ICON.refresh} Scan</button>`;
  const newBtn = `<button class="btn primary" data-act="new">${ICON.plus} New user</button>`;
  const map = {
    dashboard: scanBtn + newBtn,
    folders: `<button class="btn ghost" data-act="export">${ICON.download} Export CSV</button>` + scanBtn + newBtn,
    issues: scanBtn,
    links: `<button class="btn" data-act="sync">${ICON.sync} Sync links</button>` + scanBtn,
    env: `<button class="btn" data-act="addDomain">${ICON.plus} Add environment</button><button class="btn primary" data-act="addServer">${ICON.plus} Add file server</button>`,
    wizard: `<button class="btn ghost" data-act="wizardReset">${ICON.refresh} Start over</button>`,
    diag: `<button class="btn primary" data-act="diag">${ICON.diag} Run diagnostics</button>`,
    audit: `<button class="btn" data-act="reloadAudit">${ICON.refresh} Refresh</button>`,
    settings: '',
  };
  a.innerHTML = map[S.page] || '';
  $$('button', a).forEach(b => b.onclick = () => ({ scan: runScan, new: () => nav('wizard'), export: exportCsv, sync: openSync, addDomain: () => editEnvironment(null), addServer: () => editServer(null), diag: runDiag, reloadAudit: () => loadAudit(true), wizardReset: () => { W.reset(); renderWizard(); } })[b.dataset.act](b));
}

// ============================================================ progress strip
rpc.on('progress', d => {
  if (!S.progress || S.progress.scope !== d.scope) S.progress = { scope: d.scope, servers: {}, dfs: null, message: '', cancel: S.progress && S.progress.scope === d.scope ? S.progress.cancel : null };
  const p = S.progress;
  if (d.servers) d.servers.forEach(s => p.servers[s.id] = { name: s.name, status: s.status });
  if (d.serverId) p.servers[d.serverId] = { name: d.server, status: d.status, count: d.count, message: d.message };
  if (d.dfs) p.dfs = { status: d.dfs, count: d.count, message: d.message };
  if (d.message) p.message = d.message;
  if (d.current) p.message = `${d.message || ''} (${d.current}/${d.total})`;
  if (d.status === 'done') { p.done = true; setTimeout(() => { if (S.progress === p) { S.progress = null; renderProgress(); } }, 1500); }
  renderProgress();
});

function renderProgress() {
  const el = $('#statusStrip');
  const p = S.progress;
  if (!p) { el.classList.add('hidden'); return; }
  el.classList.remove('hidden');
  const label = { scan: 'Scanning', diag: 'Running diagnostics', sync: 'Creating DFS links', create: 'Creating user folder', fix: 'Applying fix', provision: 'Provisioning user', dir: 'Reading directory' }[p.scope] || p.scope;
  const chips = Object.values(p.servers).map(s => `<span class="chip ${s.status}" title="${esc(s.message || '')}">${esc(s.name)}${s.status === 'done' ? ` · ${s.count ?? 0}` : s.status === 'error' ? ' · failed' : ''}</span>`).join('')
    + (p.dfs ? `<span class="chip ${p.dfs.status}" title="${esc(p.dfs.message || '')}">DFS links${p.dfs.status === 'done' ? ` · ${p.dfs.count ?? 0}` : p.dfs.status === 'error' ? ' · failed' : ''}</span>` : '');
  el.innerHTML = `${p.done ? '<span class="dot ok"></span>' : '<span class="spin"></span>'}<strong>${esc(p.done ? 'Done' : label)}</strong><span class="muted">${esc(p.message || '')}</span><div class="chips">${chips}</div><span class="grow"></span>${p.cancel && !p.done ? '<button class="btn small" id="cancelOp">Cancel</button>' : ''}`;
  const c = $('#cancelOp');
  if (c) c.onclick = () => { p.cancel(); c.disabled = true; };
}

// ============================================================ global operations
async function runScan(btn) {
  if (S.scanning) return;
  S.scanning = true;
  busy(btn, true, 'Scanning…');
  const p = rpc.call('scan.run', { domainId: S.domainId });
  S.progress = { scope: 'scan', servers: {}, dfs: null, message: '', cancel: () => p.cancel() };
  renderProgress();
  try {
    const r = await p;
    const failed = (r.servers || []).filter(s => !s.ok);
    if (failed.length === 0 && !r.dfsError) toast('ok', 'Scan complete', `${(r.servers || []).reduce((a, s) => a + (s.count || 0), 0)} folders, ${r.links} DFS links, ${r.issues} issue(s)`);
    failed.forEach(s => toast('danger', `${s.name}: scan failed`, s.error, 9000));
    if (r.dfsError) toast('danger', 'DFS link scan failed', r.dfsError, 9000);
    S.loaded = {};
    await nav(S.page);
  } catch (ex) {
    if (!ex.cancelled) showError('Scan failed', ex); else toast('warn', 'Scan cancelled');
  } finally { S.scanning = false; busy(btn, false); }
}

async function exportCsv(btn) {
  busy(btn, true, 'Exporting…');
  try {
    const r = await rpc.call('export.csv', { domainId: S.domainId });
    if (!r.cancelled) toast('ok', 'Exported', r.path);
  } catch (ex) { showError('Export failed', ex); } finally { busy(btn, false); }
}

// ============================================================ dashboard
async function loadDashboard() {
  S.dashboard = await rpc.call('dashboard.get', { domainId: S.domainId });
  renderNav();
  const d = S.dashboard, c = d.counts;
  $('#pageSub').textContent = `${d.domain.name} · ${d.domain.dfsRoot} · last scan ${ago(d.lastScan)}`;
  const noData = !d.lastScan;
  const tiles = `
    <div class="grid tiles mb">
      <div class="tile clickable" data-go="servers"><div class="label">Servers</div><div class="value">${c.servers}</div><div class="sub">${d.servers.filter(s => s.lastError).length ? `<span class="badge danger">${d.servers.filter(s => s.lastError).length} not reachable</span>` : c.servers ? 'all reachable at last contact' : 'none configured'}</div></div>
      <div class="tile clickable" data-go="folders"><div class="label">User folders</div><div class="value">${c.folders}</div><div class="sub">${c.withQuota} with quota</div></div>
      <div class="tile clickable ${d.dfsScanError ? 'danger' : ''}" data-go="links"><div class="label">DFS links</div><div class="value">${c.links}</div><div class="sub" ${d.dfsScanError ? `title="${esc(d.dfsScanError)}"` : ''}>${d.dfsScanError ? '<span class="badge danger">link scan failed</span>' : esc(d.domain.dfsRoot || 'no DFS root set')}</div></div>
      <div class="tile clickable ${c.issues ? 'warn' : ''}" data-go="issues"><div class="label">Issues</div><div class="value">${c.issues}</div><div class="sub">${d.issuesByKind.slice(0, 2).map(k => `${k.count} ${KIND[k.kind] ? KIND[k.kind].label.toLowerCase() : k.kind}`).join(', ') || 'nothing to fix'}</div></div>
      <div class="tile clickable ${c.over90 ? 'danger' : ''}" data-go="folders" data-chip="over90"><div class="label">≥ 90% full</div><div class="value">${c.over90}</div><div class="sub">${c.over100} at or over quota</div></div>
      <div class="tile"><div class="label">Used / quota</div><div class="value" style="font-size:20px">${esc(fmtBytes(d.totalUsed))}</div><div class="sub">of ${esc(fmtBytes(d.totalQuota))} allocated</div></div>
    </div>`;
  const servers = d.servers.map(s => `
    <div class="diag-row" style="grid-template-columns: 14px 1fr auto auto;">
      <span class="dot ${!s.enabled ? 'skip' : s.lastError ? 'fail' : s.lastScanAt ? 'ok' : 'warn'}"></span>
      <div><div class="strong">${esc(s.name)} <span class="muted small">${esc(s.fqdn)}</span></div>
        <div class="muted small">${s.lastError ? `<span style="color:var(--danger)">${esc(s.lastError)}</span>` : `${s.folders} folders · ${esc(fmtBytes(s.usedBytes))} used · scanned ${ago(s.lastScanAt)}`}</div></div>
      <span class="badge ${s.fsrmInstalled === true ? 'ok' : s.fsrmInstalled === false ? 'danger' : 'muted'}">FSRM ${s.fsrmInstalled === true ? 'on' : s.fsrmInstalled === false ? 'missing' : '?'}</span>
      <span class="badge mono ${s.shareName ? '' : 'warn'}">${esc(s.shareName ? '\\\\' + s.fqdn + '\\' + s.shareName : 'share unknown')}</span>
    </div>`).join('');
  const kinds = d.issuesByKind.map(k => `<div class="diag-row" style="grid-template-columns: 14px 1fr auto; cursor:pointer" data-kind="${k.kind}"><span class="dot ${KIND[k.kind] ? KIND[k.kind].cls : ''}"></span><span>${esc(KIND[k.kind] ? KIND[k.kind].label : k.kind)}</span><span class="badge">${k.count}</span></div>`).join('') || '<div class="muted small">No issues detected.</div>';
  const top = (rows, empty) => rows.length ? `<table class="table compact"><thead><tr><th>Folder</th><th>Server</th><th>Usage</th></tr></thead><tbody>${rows.map(f => `<tr class="clickable" data-folder="${f.id}"><td class="name">${esc(f.name)}</td><td>${esc(f.serverName)}</td><td>${usageCell(f)}</td></tr>`).join('')}</tbody></table>` : `<div class="muted small">${empty}</div>`;
  const dfsErr = d.dfsScanError ? `<div class="card mb"><div class="row gap"><span class="dot fail"></span><div><div class="strong">The DFS namespace could not be read</div><div class="muted small" style="color:var(--danger)">${esc(d.dfsScanError)}</div><div class="muted small">Links shown are from the last successful read. Check the DFS host under Diagnostics, then scan again.</div></div></div></div>` : '';
  $('#content').innerHTML = dfsErr + (noData ? `<div class="card mb"><div class="row gap"><span class="dot warn"></span><div><div class="strong">No scan data yet</div><div class="muted small">Run <b>Scan</b> to read the folders of every server and the DFS namespace. Run <b>Diagnostics</b> first if this is a new setup.</div></div><span class="grow"></span><button class="btn primary" id="dashScan">${ICON.refresh} Scan now</button></div></div>` : '') + tiles + `
    <div class="grid cols-2">
      <div class="card"><div class="card-head"><h2>Servers</h2><button class="btn ghost small" data-go="diag">Diagnostics</button></div>${servers || '<div class="muted small">No servers configured.</div>'}</div>
      <div class="card"><div class="card-head"><h2>Issues by type</h2><button class="btn ghost small" data-go="issues">Open</button></div>${kinds}</div>
      <div class="card"><div class="card-head"><h2>Critical — 90% or more</h2></div>${top(d.critical, 'No folder is near its quota.')}</div>
      <div class="card"><div class="card-head"><h2>Largest folders</h2></div>${top(d.topUsage, 'No usage data yet.')}</div>
    </div>`;
  $$('[data-go]').forEach(el => el.onclick = () => { if (el.dataset.chip) S.filters.chip = el.dataset.chip; nav(el.dataset.go === 'servers' ? 'env' : el.dataset.go); });
  $$('[data-kind]').forEach(el => el.onclick = () => { S.issueKind = el.dataset.kind; nav('issues'); });
  $$('[data-folder]').forEach(el => el.onclick = () => openFolder(Number(el.dataset.folder)));
  const b = $('#dashScan'); if (b) b.onclick = () => runScan(b);
}

// ============================================================ folders
async function loadFolders() {
  S.folders = await rpc.call('folders.list', { domainId: S.domainId });
  S.servers = await rpc.call('servers.list', { domainId: S.domainId });
  renderFolders();
}

function filteredFolders() {
  let rows = S.folders;
  const f = S.filters;
  if (f.server !== 'all') rows = rows.filter(r => String(r.serverId) === f.server);
  switch (f.chip) {
    case 'noquota': rows = rows.filter(r => r.isUserFolder && r.quotaBytes == null); break;
    case 'over90': rows = rows.filter(r => r.pct != null && r.pct >= 90); break;
    case 'issues': rows = rows.filter(r => r.issues.length); break;
    case 'dup': rows = rows.filter(r => r.issues.includes('DUPLICATE_FOLDER')); break;
    case 'nolink': rows = rows.filter(r => r.isUserFolder && r.linkStatus === 'missing'); break;
    case 'extra': rows = rows.filter(r => r.extraAccess.length); break;
    case 'other': rows = rows.filter(r => !r.isUserFolder); break;
    default: rows = rows.filter(r => r.isUserFolder);
  }
  if (f.q) { const q = f.q.toLowerCase(); rows = rows.filter(r => r.name.toLowerCase().includes(q) || (r.owner || '').toLowerCase().includes(q) || r.path.toLowerCase().includes(q)); }
  const k = S.sort.key, dir = S.sort.dir;
  const val = r => k === 'usage' ? (r.pct ?? (r.usedBytes != null ? -1 : -2)) : k === 'used' ? (r.usedBytes ?? -1) : k === 'lastWrite' ? (r.lastWriteUtc || '') : (r[k] ?? '');
  rows = rows.slice().sort((a, b) => { const x = val(a), y = val(b); return (typeof x === 'number' ? x - y : String(x).localeCompare(String(y), undefined, { sensitivity: 'base' })) * dir; });
  return rows;
}

function renderFolders() {
  const rows = filteredFolders();
  const user = S.folders.filter(f => f.isUserFolder);
  const chips = [
    ['all', 'All', user.length], ['noquota', 'No quota', user.filter(r => r.quotaBytes == null).length], ['over90', '≥ 90%', user.filter(r => r.pct >= 90).length],
    ['issues', 'With issues', user.filter(r => r.issues.length).length], ['dup', 'Duplicates', user.filter(r => r.issues.includes('DUPLICATE_FOLDER')).length],
    ['nolink', 'No DFS link', user.filter(r => r.linkStatus === 'missing').length], ['extra', 'Extra access', user.filter(r => r.extraAccess.length).length],
    ['other', 'Other folders', S.folders.length - user.length],
  ];
  const th = (key, label, extra = '') => `<th class="sortable ${extra}" data-sort="${key}">${label}${S.sort.key === key ? `<span class="arrow">${S.sort.dir > 0 ? '▲' : '▼'}</span>` : ''}</th>`;
  $('#pageSub').textContent = `${rows.length} of ${user.length} user folders`;
  $('#content').innerHTML = `
    <div class="toolbar">
      <div class="search">${ICON.search}<input id="fq" type="text" placeholder="Search folder, owner, path…" value="${esc(S.filters.q)}"></div>
      <select id="fserver"><option value="all">All servers</option>${S.servers.map(s => `<option value="${s.id}" ${S.filters.server === String(s.id) ? 'selected' : ''}>${esc(s.name)}</option>`).join('')}</select>
      ${chips.map(([k, l, n]) => `<button class="chip ${S.filters.chip === k ? 'on' : ''}" data-chip="${k}">${l}<span class="n">${n}</span></button>`).join('')}
    </div>
    ${S.folders.length === 0 ? `<div class="card"><div class="empty"><div class="big">No folders yet</div>Run a scan to read the user folders from the servers.</div></div>` : `
    <div class="table-wrap"><table class="table"><thead><tr>
      ${th('serverName', 'Server')}${th('name', 'Folder')}${th('owner', 'Owner')}${th('userRights', 'User rights')}<th>Extra access</th>${th('usage', 'Used / quota')}${th('lastWrite', 'Last write')}${th('linkStatus', 'DFS link')}<th></th>
    </tr></thead><tbody>
      ${rows.map(f => `<tr class="clickable" data-id="${f.id}">
        <td>${esc(f.serverName)}</td>
        <td><div class="name">${esc(f.name)}</div><div class="path">${esc(f.path)}</div></td>
        <td class="small">${esc(f.owner || '—')}</td>
        <td><span class="badge ${f.userRights === 'Modify' || f.userRights === 'Full' ? 'ok' : f.userRights === 'None' ? 'danger' : 'warn'}">${esc(f.userRights || '?')}</span></td>
        <td>${f.extraAccess.length ? `<span class="badge warn" title="${esc(f.extraAccess.map(a => a.id + ': ' + a.rights).join('\n'))}">${f.extraAccess.length} extra</span>` : '<span class="muted">—</span>'}</td>
        <td>${usageCell(f)}</td>
        <td class="small nowrap">${esc(fmtDate(f.lastWriteUtc))}</td>
        <td>${linkBadge(f)}</td>
        <td class="actions"><button class="btn ghost icon" data-menu="${f.id}">${ICON.more}</button></td>
      </tr>`).join('') || `<tr><td colspan="9" class="center muted">No folder matches the filter.</td></tr>`}
    </tbody></table></div>`}`;
  $('#fq').oninput = e => { S.filters.q = e.target.value; renderFolders(); $('#fq').focus(); $('#fq').setSelectionRange(S.filters.q.length, S.filters.q.length); };
  $('#fserver').onchange = e => { S.filters.server = e.target.value; renderFolders(); };
  $$('[data-chip]').forEach(b => b.onclick = () => { S.filters.chip = b.dataset.chip; renderFolders(); });
  $$('[data-sort]').forEach(h => h.onclick = () => { if (S.sort.key === h.dataset.sort) S.sort.dir *= -1; else S.sort = { key: h.dataset.sort, dir: 1 }; renderFolders(); });
  $$('tr[data-id]').forEach(tr => tr.onclick = e => { if (e.target.closest('[data-menu]')) return; openFolder(Number(tr.dataset.id)); });
  $$('[data-menu]').forEach(b => b.onclick = e => { e.stopPropagation(); folderMenu(b, S.folders.find(f => f.id === Number(b.dataset.menu))); });
}

function linkBadge(f) {
  if (f.issues.includes('DUPLICATE_FOLDER')) return `<span class="badge danger" title="Folder exists on several servers">duplicate</span>`;
  return { ok: '<span class="badge ok">linked</span>', missing: '<span class="badge warn">no link</span>', mismatch: `<span class="badge danger" title="${esc(f.linkTargets.join('\n'))}">wrong target</span>` }[f.linkStatus] || esc(f.linkStatus);
}

function folderMenu(anchor, f) {
  contextMenu(anchor, [
    { label: 'Details', onClick: () => openFolder(f.id) },
    { label: f.quotaBytes == null ? 'Set quota…' : 'Change quota…', onClick: () => setQuotaDialog(f) },
    { label: 'Compute size now', onClick: () => computeSize(f) },
    { label: 'Create DFS link', disabled: f.linkStatus !== 'missing' || f.issues.includes('DUPLICATE_FOLDER') || !f.isUserFolder, onClick: () => createLink(f) },
    '-',
    { label: 'Copy path', onClick: () => copyText(f.path) },
    { label: 'Copy DFS link path', disabled: f.linkStatus === 'missing', onClick: () => copyText((S.domain ? S.domain.dfsRoot : '') + '\\' + f.name) },
  ]);
}

async function refreshAfterChange() {
  S.loaded = {};
  if (S.page === 'folders') { S.folders = await rpc.call('folders.list', { domainId: S.domainId }); renderFolders(); }
  else await nav(S.page);
}

function setQuotaDialog(f) {
  const cur = f.quotaBytes == null ? '' : toGb(f.quotaBytes);
  const dflt = toGb(Number(S.settings.default_quota_bytes || 5 * GB));
  const m = modal({
    title: (f.quotaBytes == null ? 'Set quota — ' : 'Change quota — ') + f.name,
    body: `<div class="note">Server <b>${esc(f.serverName)}</b> · <span class="mono">${esc(f.path)}</span><br>Current: ${f.quotaBytes == null ? 'no quota' : esc(fmtBytes(f.quotaBytes))}${f.usedBytes != null ? ` · used ${esc(fmtBytes(f.usedBytes))}` : ''}</div>
      <label class="field"><span>New quota (GB)</span><input id="qgb" type="number" min="0.1" step="0.5" value="${esc(cur || dflt)}"><span class="hint">A hard FSRM quota is created or updated on the server. Usage above the new limit is not deleted; the user simply cannot write more.</span></label>`,
    footer: [
      { label: 'Cancel', onClick: m => m.close() },
      { label: 'Apply quota', kind: 'primary', onClick: async (m, btn) => {
        const gb = parseFloat($('#qgb').value);
        if (!(gb > 0)) { toast('warn', 'Enter a size in GB'); return; }
        busy(btn, true, 'Applying…'); m.locked = true;
        try {
          const r = await rpc.call('folders.setQuota', { folderId: f.id, bytes: Math.round(gb * GB) });
          toast('ok', `Quota set for ${f.name}`, `${fmtBytes(r.quotaBytes)} on ${f.serverName} · used ${fmtBytes(r.usedBytes)}`);
          m.close(); await refreshAfterChange();
        } catch (ex) { m.locked = false; busy(btn, false); showError('Quota not applied', ex); }
      } },
    ],
  });
  setTimeout(() => $('#qgb').select(), 30);
}

async function computeSize(f) {
  toast('', 'Computing size…', `${f.name} on ${f.serverName}`, 3000);
  try {
    const r = await rpc.call('folders.computeSize', { folderId: f.id });
    toast('ok', `${f.name}: ${fmtBytes(r.usedBytes)}`);
    await refreshAfterChange();
  } catch (ex) { showError('Size not computed', ex); }
}

async function createLink(f) {
  const target = `\\\\${(S.servers.find(s => s.id === f.serverId) || {}).fqdn || f.serverName}\\<share>\\${f.name}`;
  if (!await confirmDialog('Create DFS link', `<p>Create <span class="mono">${esc((S.domain ? S.domain.dfsRoot : '') + '\\' + f.name)}</span> pointing to the folder on <b>${esc(f.serverName)}</b>?</p><p class="muted small">Target: ${esc(target)} (share name as discovered by Diagnostics)</p>`, { label: 'Create link' })) return;
  try {
    const r = await rpc.call('folders.createLink', { folderId: f.id });
    const row = (r.results || [])[0];
    if (row && row.ok) toast('ok', 'DFS link created', row.linkPath); else showError('Link not created', { message: row ? row.error : 'unknown' });
    await refreshAfterChange();
  } catch (ex) { showError('Link not created', ex); }
}

// ------------------------------------------------------------ folder drawer
async function openFolder(id) {
  const d = $('#drawer');
  $('#drawerBackdrop').classList.remove('hidden');
  d.classList.remove('hidden');
  d.innerHTML = '<div class="drawer-body"><span class="spin"></span></div>';
  $('#drawerBackdrop').onclick = closeDrawer;
  try {
    const r = await rpc.call('folders.details', { folderId: id });
    const f = r.folder;
    const aces = r.aces.map(a => `<tr><td class="small selectable">${esc(a.id)}</td><td class="small">${esc(a.rights)}</td><td><span class="badge ${a.type === 'Allow' ? 'ok' : 'danger'}">${esc(a.type)}</span></td><td class="small muted">${a.inherited ? 'inherited' : ''}</td></tr>`).join('');
    d.innerHTML = `
      <div class="drawer-head"><div><h2>${esc(f.name)}</h2><div class="muted small">${esc(f.serverName)} · ${esc(f.path)}</div></div><button class="btn ghost icon" id="drawerClose">${ICON.x}</button></div>
      <div class="drawer-body">
        <div class="row gap wrap mb">${f.issues.map(k => `<span class="badge ${KIND[k] ? KIND[k].cls : ''}">${esc(KIND[k] ? KIND[k].label : k)}</span>`).join('') || '<span class="badge ok">no issues</span>'}</div>
        ${usageCell(f)}
        <div class="kv mt">
          <span class="k">Owner</span><span class="v">${esc(f.owner || '—')}</span>
          <span class="k">User rights</span><span class="v">${esc(f.userRights || '?')}</span>
          <span class="k">Quota</span><span class="v">${f.quotaBytes == null ? 'none' : esc(fmtBytes(f.quotaBytes))}</span>
          <span class="k">Used</span><span class="v">${esc(fmtBytes(f.usedBytes))}${f.pct != null ? ` (${f.pct}%)` : ''}</span>
          <span class="k">Last write</span><span class="v">${esc(fmtDate(f.lastWriteUtc))}</span>
          <span class="k">Scanned</span><span class="v">${esc(fmtDate(f.scannedAt))}</span>
          <span class="k">DFS link</span><span class="v">${r.link ? `<span class="mono">${esc(r.link.linkPath)}</span> <span class="badge ${f.linkStatus === 'ok' ? 'ok' : 'danger'}">${esc(r.link.state || '')}</span><br>${r.link.targets.map(t => `<span class="mono small">→ ${esc(t.target)} (${esc(t.state)})</span>`).join('<br>')}` : '<span class="badge warn">none</span>'}</span>
        </div>
        <div class="row gap wrap">
          <button class="btn small" id="dQuota">${f.quotaBytes == null ? 'Set quota' : 'Change quota'}</button>
          <button class="btn small" id="dSize">Compute size</button>
          <button class="btn small" id="dLink" ${f.linkStatus !== 'missing' || f.issues.includes('DUPLICATE_FOLDER') ? 'disabled' : ''}>Create DFS link</button>
          <button class="btn ghost small" id="dCopy">Copy path</button>
        </div>
        <div class="section-title">Access control list</div>
        <table class="table compact"><thead><tr><th>Identity</th><th>Rights</th><th>Type</th><th></th></tr></thead><tbody>${aces || '<tr><td colspan="4" class="muted">ACL not read</td></tr>'}</tbody></table>
        ${f.extraAccess.length ? `<div class="note warn mt"><b>Outside the standard pattern:</b> ${f.extraAccess.map(a => esc(a.id + ' (' + a.rights + ')')).join(', ')}</div>` : ''}
        <div class="section-title">Change history</div>
        ${r.audit.length ? `<table class="table compact"><thead><tr><th>When</th><th>Who</th><th>What</th><th></th></tr></thead><tbody>${r.audit.map(a => `<tr><td class="small nowrap">${esc(fmtDate(a.ts))}</td><td class="small">${esc(a.actor)}</td><td class="small">${esc(a.op)}${a.paramsJson ? ` <span class="muted mono">${esc(a.paramsJson)}</span>` : ''}</td><td>${a.ok ? '<span class="badge ok">ok</span>' : `<span class="badge danger" title="${esc(a.error || '')}">failed</span>`}</td></tr>`).join('')}</tbody></table>` : '<div class="muted small">No changes recorded for this folder.</div>'}
      </div>`;
    $('#drawerClose').onclick = closeDrawer;
    $('#dQuota').onclick = () => setQuotaDialog(f);
    $('#dSize').onclick = () => computeSize(f);
    $('#dLink').onclick = () => createLink(f);
    $('#dCopy').onclick = () => copyText(f.path);
  } catch (ex) {
    d.innerHTML = `<div class="drawer-head"><h2>Folder</h2><button class="btn ghost icon" id="drawerClose">${ICON.x}</button></div><div class="drawer-body"><div class="note danger">${esc(ex.message)}</div></div>`;
    $('#drawerClose').onclick = closeDrawer;
  }
}
function closeDrawer() { $('#drawer').classList.add('hidden'); $('#drawerBackdrop').classList.add('hidden'); }

// ============================================================ issues
async function loadIssues() {
  S.issues = await rpc.call('issues.list', { domainId: S.domainId });
  renderIssues();
}

function renderIssues() {
  const counts = {};
  S.issues.forEach(i => counts[i.kind] = (counts[i.kind] || 0) + 1);
  const rows = S.issueKind === 'all' ? S.issues : S.issues.filter(i => i.kind === S.issueKind);
  $('#pageSub').textContent = `${S.issues.length} open issue(s)`;
  const card = i => {
    const d = i.details, k = KIND[i.kind] || { label: i.kind, cls: '', desc: '' };
    let desc = '', actions = '';
    switch (i.kind) {
      case 'DUPLICATE_FOLDER':
        desc = d.copies.map(c => `<code>${esc(c.server)}</code> ${esc(c.path)} · last write ${esc(fmtDate(c.lastWriteUtc))} · ${esc(fmtBytes(c.usedBytes))}`).join('<br>') + (d.link ? `<br>Current link: <code>${esc(d.link)}</code> → ${esc((d.targets || []).join(', '))}` : '<br>No DFS link exists for this user.');
        actions = d.copies.map(c => `<button class="btn small" data-open="${c.folderId}">Open ${esc(c.server)} copy</button>`).join('');
        break;
      case 'LINK_TARGET_MISMATCH':
        desc = `<code>${esc(d.linkPath)}</code> → ${esc((d.targets || []).join(', ') || 'no target')}<br>Folder exists on: ${esc((d.foldersOn || []).join(', '))}${d.badTargets && d.badTargets.length ? `<br>Bad target(s): ${esc(d.badTargets.join(', '))}` : ''}<br><span class="muted">Fix the target in DFS Management; the tool never deletes or re-points existing links.</span>`;
        break;
      case 'FOLDER_WITHOUT_LINK':
        desc = `<code>${esc(d.server)}</code> ${esc(d.path)}${d.duplicate ? ' — also a duplicate; resolve that first.' : ''}`;
        actions = d.duplicate ? '' : `<button class="btn small" data-link="${d.folderId}">Create DFS link</button>`;
        break;
      case 'LINK_WITHOUT_FOLDER':
        desc = `<code>${esc(d.linkPath)}</code> → ${esc((d.targets || []).join(', '))} · state ${esc(d.state || '?')}<br><span class="muted">Remove the link in DFS Management if the user is gone, or restore the folder.</span>`;
        actions = `<button class="btn ghost small" data-copy="${esc(d.linkPath)}">Copy link path</button>`;
        break;
      case 'EXTRA_ACCESS':
        desc = `<code>${esc(d.server)}</code> ${esc(d.path)}<br>` + (d.extra || []).map(a => `${esc(a.id)}: ${esc(a.rights)}${a.inherited ? ' (inherited)' : ''}`).join('<br>');
        actions = `<button class="btn small" data-open="${d.folderId}">View ACL</button>`;
        break;
      case 'NO_QUOTA':
        desc = `<code>${esc(d.server)}</code> ${esc(d.path)}${d.usedBytes != null ? ` · ${esc(fmtBytes(d.usedBytes))} used` : ''}`;
        actions = `<button class="btn small" data-quota="${d.folderId}">Set quota</button><button class="btn ghost small" data-size="${d.folderId}">Compute size</button>`;
        break;
    }
    return `<div class="issue ${k.cls}"><div class="stripe"></div><div><div class="title">${esc(k.label)} — ${esc(d.name)}</div><div class="desc">${desc}</div></div><div class="row gap">${actions}</div></div>`;
  };
  $('#content').innerHTML = `
    <div class="toolbar">
      <button class="chip ${S.issueKind === 'all' ? 'on' : ''}" data-kind="all">All<span class="n">${S.issues.length}</span></button>
      ${Object.entries(KIND).map(([k, v]) => `<button class="chip ${S.issueKind === k ? 'on' : ''}" data-kind="${k}">${esc(v.label)}<span class="n">${counts[k] || 0}</span></button>`).join('')}
    </div>
    ${S.issueKind !== 'all' && KIND[S.issueKind] ? `<p class="muted small mb">${esc(KIND[S.issueKind].desc)}</p>` : ''}
    ${rows.length ? rows.map(card).join('') : `<div class="card"><div class="empty"><div class="big">${S.issues.length ? 'No issue of this type' : 'No issues'}</div>${S.issues.length ? '' : 'Everything found by the last scan is consistent.'}</div></div>`}`;
  $$('[data-kind]').forEach(b => b.onclick = () => { S.issueKind = b.dataset.kind; renderIssues(); });
  const byId = id => S.folders.find(f => f.id === id);
  const ensureFolders = async () => { if (!S.folders.length) { S.folders = await rpc.call('folders.list', { domainId: S.domainId }); S.servers = await rpc.call('servers.list', { domainId: S.domainId }); } };
  $$('[data-open]').forEach(b => b.onclick = () => openFolder(Number(b.dataset.open)));
  $$('[data-quota]').forEach(b => b.onclick = async () => { await ensureFolders(); const f = byId(Number(b.dataset.quota)); if (f) setQuotaDialog(f); });
  $$('[data-size]').forEach(b => b.onclick = async () => { await ensureFolders(); const f = byId(Number(b.dataset.size)); if (f) computeSize(f); });
  $$('[data-link]').forEach(b => b.onclick = async () => { await ensureFolders(); const f = byId(Number(b.dataset.link)); if (f) createLink(f); });
  $$('[data-copy]').forEach(b => b.onclick = () => copyText(b.dataset.copy));
}

// ============================================================ DFS links
async function loadLinks() {
  const r = await rpc.call('links.list', { domainId: S.domainId });
  S.links = r.links; S.linksUnverified = r.unverified || [];
  renderLinks();
}

function renderLinks() {
  const q = S.linkQ.toLowerCase();
  const rows = q ? S.links.filter(l => l.name.toLowerCase().includes(q) || l.targets.some(t => t.target.toLowerCase().includes(q))) : S.links;
  const unv = S.linksUnverified || [];
  const st = {
    ok: '<span class="badge ok" title="A target of the link holds the folder">folder found</span>',
    nofolder: '<span class="badge danger" title="Every file server was scanned and none has a folder of this name">no folder</span>',
    mismatch: '<span class="badge danger" title="The folder exists on a file server the link does not point to">wrong target</span>',
    external: '<span class="badge muted" title="The targets are not configured file servers; not checked">other server</span>',
    notarget: '<span class="badge danger" title="The link has no target at all">no target</span>',
    unknown: `<span class="badge warn" title="Not checked: the target server has no successful scan (${esc(unv.join(', '))})">not verified</span>`,
  };
  const bad = S.links.filter(l => l.status === 'nofolder' || l.status === 'mismatch' || l.status === 'notarget').length;
  const unknown = S.links.filter(l => l.status === 'unknown').length;
  $('#pageSub').textContent = `${S.links.length} links under ${S.domain ? S.domain.dfsRoot : ''} · ${bad} need attention` + (unknown ? ` · ${unknown} not verified` : '');
  $('#content').innerHTML = `
    ${unknown ? `<div class="note warn mb"><b>${esc(unv.join(', '))}</b>: no successful folder scan, so links pointing there cannot be checked. "DFS state" comes from the namespace and is unaffected. See the reason on the Dashboard or under Environment, then run Scan again.</div>` : ''}
    <div class="toolbar"><div class="search">${ICON.search}<input id="lq" type="text" placeholder="Search link or target…" value="${esc(S.linkQ)}"></div>
      <div class="legend"><span><span class="dot ok"></span> folder found on a target</span><span><span class="dot danger"></span> no folder / wrong target</span><span><span class="dot warn"></span> not verified</span></div></div>
    ${S.links.length === 0 ? '<div class="card"><div class="empty"><div class="big">No DFS links read yet</div>Run a scan. If the scan reports a DFS error, check the DFS host in Diagnostics.</div></div>' : `
    <div class="table-wrap"><table class="table"><thead><tr><th>Link</th><th>Targets</th><th title="State of the link in the DFS namespace (Online / Offline)">DFS state</th><th>Folder on</th><th title="Result of comparing the link with the folders found on the file servers">Folder check</th></tr></thead><tbody>
      ${rows.map(l => `<tr><td><div class="name">${esc(l.name)}</div><div class="path">${esc(l.linkPath)}</div></td><td class="mono small selectable">${l.targets.map(t => `${esc(t.target)} <span class="muted">(${esc(t.state)})</span>`).join('<br>') || '<span class="muted">none</span>'}</td><td class="small">${esc(l.state || '')}</td><td class="small">${esc(l.foldersOn.join(', ') || '—')}</td><td>${st[l.status] || esc(l.status)}</td></tr>`).join('') || '<tr><td colspan="5" class="center muted">No link matches.</td></tr>'}
    </tbody></table></div>`}`;
  $('#lq').oninput = e => { S.linkQ = e.target.value; renderLinks(); const i = $('#lq'); i.focus(); i.setSelectionRange(S.linkQ.length, S.linkQ.length); };
}

async function openSync(btn) {
  busy(btn, true, 'Planning…');
  let plan;
  try { plan = await rpc.call('sync.dryRun', { domainId: S.domainId }); }
  catch (ex) { busy(btn, false); showError('Cannot plan', ex); return; }
  busy(btn, false);
  const list = plan.toCreate;
  const body = `
    <p class="muted small">Dry run based on the last scan. Only <b>Links to be created</b> changes anything; nothing is ever deleted or re-pointed.</p>
    ${(plan.unverified || []).length ? `<div class="note warn">No successful folder scan of <b>${esc(plan.unverified.join(', '))}</b> — folders there are unknown, so the lists below are incomplete. Fix the connection (Dashboard shows the reason) and scan again.</div>` : ''}
    ${plan.linksRead === false ? '<div class="note warn">The DFS namespace has not been read successfully yet, so no link can be planned. Check the DFS host in Diagnostics and scan again.</div>' : ''}
    <div class="section-title">Links to be created (${list.length})</div>
    ${list.length ? `<div class="list-check">${list.map(r => `<label><input type="checkbox" data-fid="${r.folderId}" ${r.ready ? 'checked' : 'disabled'}><span class="strong">${esc(r.name)}</span><span class="mono">${r.ready ? esc(r.linkPath + '  →  ' + r.targetPath) : '<span style="color:var(--warn)">' + esc(r.reason) + '</span>'}</span></label>`).join('')}</div>` : '<div class="muted small">Every single-server folder already has a link.</div>'}
    <div class="section-title">Needs manual decision — folder on several servers (${plan.manual.length})</div>
    ${plan.manual.length ? `<div class="note warn">${plan.manual.map(d => `<b>${esc(d.name)}</b>: ${esc(d.copies.map(c => c.server).join(' + '))}`).join('<br>')}</div>` : '<div class="muted small">None.</div>'}
    <div class="section-title">Links without a folder on any server (${plan.dead.length})</div>
    ${plan.dead.length ? `<div class="note">${plan.dead.map(d => `<span class="mono">${esc(d.linkPath)}</span>`).join('<br>')}<br><span class="muted small">Review in DFS Management; not touched by this tool.</span></div>` : '<div class="muted small">None.</div>'}
    <div class="section-title">Wrong targets (${plan.mismatch.length})</div>
    ${plan.mismatch.length ? `<div class="note danger">${plan.mismatch.map(d => `<b>${esc(d.name)}</b>: ${esc((d.targets || []).join(', '))} — folder on ${esc((d.foldersOn || []).join(', '))}`).join('<br>')}</div>` : '<div class="muted small">None.</div>'}
    <div id="syncResult"></div>`;
  const m = modal({
    title: 'Sync DFS links', body, wide: true,
    footer: [
      { label: 'Close', onClick: m => m.close() },
      { label: `Create selected links`, kind: 'primary', disabled: !list.some(r => r.ready), onClick: async (m, b) => {
        const ids = $$('input[data-fid]:checked', m.el).map(i => Number(i.dataset.fid));
        if (!ids.length) { toast('warn', 'Nothing selected'); return; }
        if (!await confirmDialog('Create DFS links', `<p>Create <b>${ids.length}</b> link(s) under <span class="mono">${esc(S.domain.dfsRoot)}</span>?</p>`, { label: `Create ${ids.length} link(s)` })) return;
        busy(b, true, 'Creating…'); m.locked = true;
        try {
          const r = await rpc.call('sync.apply', { domainId: S.domainId, folderIds: ids });
          $('#syncResult').innerHTML = `<div class="section-title">Result</div><ul class="steps">${r.results.map(x => `<li><span class="dot ${x.ok ? 'ok' : 'fail'}" style="margin-top:5px"></span><div>${esc(x.name || x.folderId)}${x.ok ? ` <span class="muted mono small">${esc(x.linkPath)}</span>` : `<div class="err">${esc(x.error)}</div>`}</div></li>`).join('')}</ul>`;
          toast(r.created === ids.length ? 'ok' : 'warn', `${r.created} of ${ids.length} link(s) created`);
          m.setFooter([{ label: 'Close', kind: 'primary', onClick: async m => { m.close(); S.loaded = {}; await nav(S.page); } }]);
          m.locked = false;
        } catch (ex) { m.locked = false; busy(b, false); showError('Sync failed', ex); }
      } },
    ],
  });
}

// ============================================================ file servers (environment page lives in wizard.js)
function editServer(s) {
  const m = modal({
    title: s ? 'Edit server' : 'Add server',
    body: `<div class="form-grid">
      <label class="field"><span>Name</span><input id="sName" type="text" value="${esc(s ? s.name : '')}" placeholder="FS1"></label>
      <label class="field"><span>FQDN</span><input id="sFqdn" type="text" value="${esc(s ? s.fqdn : '')}" placeholder="fs1.corp.example.com"></label>
      <label class="field"><span>User data root (local path on the server)</span><input id="sRoot" type="text" value="${esc(s ? s.localRoot : 'D:\\Users')}"></label>
      <label class="field"><span>Share name (optional — discovered by Diagnostics)</span><input id="sShare" type="text" value="${esc(s && s.shareName ? s.shareName : '')}" placeholder="Users$"></label>
      <label class="check span2"><input id="sEnabled" type="checkbox" ${!s || s.enabled ? 'checked' : ''}> <span>Enabled (included in scans)</span></label>
    </div>`,
    footer: [
      { label: 'Cancel', onClick: m => m.close() },
      { label: 'Save', kind: 'primary', onClick: async (m, b) => {
        busy(b, true, 'Saving…');
        try {
          await rpc.call('servers.save', { id: s ? s.id : null, domainId: S.domainId, name: $('#sName').value, fqdn: $('#sFqdn').value, localRoot: $('#sRoot').value, shareName: $('#sShare').value, enabled: $('#sEnabled').checked });
          m.close(); toast('ok', 'Server saved'); S.loaded = {}; if (S.page === 'env') await loadEnv(); else await nav(S.page);
        } catch (ex) { busy(b, false); showError('Not saved', ex); }
      } },
    ],
  });
  setTimeout(() => $('#sName').focus(), 30);
}

// ============================================================ diagnostics
function renderDiag() {
  if (!S.diag) {
    $('#content').innerHTML = `<div class="card"><div class="empty"><div class="big">Check the prerequisites on every server</div>DNS · WinRM with your credentials · administrator rights · FSRM role · user data root · SMB share · DFS host tools<br><br><button class="btn primary" id="diagRun">${ICON.diag} Run diagnostics</button></div></div>`;
    $('#diagRun').onclick = b => runDiag(b);
    return;
  }
  const row = (r, ctx) => `<div class="diag-row ${r.status}"><span class="dot ${r.status}"></span><span class="label">${esc(r.label)}</span><span class="detail">${esc(r.detail)}</span><span>${r.fix ? `<button class="btn small" data-fix="${r.fix}" data-server="${ctx.id || ''}">Fix</button>` : ''}</span></div>`;
  const overall = rows => rows.some(r => r.status === 'fail') ? 'fail' : rows.some(r => r.status === 'warn') ? 'warn' : 'ok';
  $('#pageSub').textContent = `Checked ${fmtDate(S.diag.at)}`;
  $('#content').innerHTML = S.diag.servers.map(s => `<div class="card"><div class="card-head"><div class="row gap"><span class="dot ${overall(s.rows)}"></span><h2>${esc(s.name)}</h2><span class="muted small">${esc(s.fqdn)} · ${esc(s.localRoot)}${s.shareName ? ' · share ' + esc(s.shareName) : ''}</span></div></div>${s.rows.map(r => row(r, s)).join('')}</div>`).join('') +
    `<div class="card"><div class="card-head"><div class="row gap"><span class="dot ${overall(S.diag.dfsHost.rows)}"></span><h2>DFS host</h2><span class="muted small">${esc(S.diag.dfsHost.host)} · ${esc(S.diag.dfsHost.dfsRoot)}</span></div></div>${S.diag.dfsHost.rows.map(r => row(r, {})).join('')}</div>`;
  $$('[data-fix]').forEach(b => b.onclick = async () => {
    const kind = b.dataset.fix, serverId = b.dataset.server ? Number(b.dataset.server) : null;
    const what = kind === 'installFsrm' ? 'Install the <b>File Server Resource Manager</b> role on this server (Install-WindowsFeature FS-Resource-Manager)?' : 'Install the <b>DFS management tools</b> on the DFS host (Install-WindowsFeature RSAT-DFS-Mgmt-Con)?';
    if (!await confirmDialog('Apply fix', `<p>${what}</p><p class="muted small">This changes the server configuration. It can take a few minutes; a reboot is normally not required.</p>`, { label: 'Install' })) return;
    busy(b, true, 'Installing…');
    try { const r = await rpc.call('diag.fix', { domainId: S.domainId, kind, serverId }); toast(r.ok ? 'ok' : 'warn', r.message || 'Done'); await runDiag(); }
    catch (ex) { busy(b, false); showError('Fix failed', ex); }
  });
}

async function runDiag(btn) {
  busy(btn, true, 'Running…');
  try {
    const r = await rpc.call('diag.run', { domainId: S.domainId });
    S.diag = { ...r, at: new Date().toISOString() };
    S.servers = await rpc.call('servers.list', { domainId: S.domainId });
    if (S.page === 'diag') renderDiag();
  } catch (ex) { showError('Diagnostics failed', ex); } finally { busy(btn, false); }
}

// ============================================================ audit
async function loadAudit(reset) {
  if (reset) S.audit.offset = 0;
  const r = await rpc.call('audit.list', { limit: 100, offset: S.audit.offset, filter: S.audit.filter });
  S.audit.rows = S.audit.offset ? S.audit.rows.concat(r.rows) : r.rows;
  S.audit.total = r.total;
  renderAudit();
}

function renderAudit() {
  $('#pageSub').textContent = `${S.audit.total} entries`;
  $('#content').innerHTML = `
    <div class="toolbar"><div class="search">${ICON.search}<input id="aq" type="text" placeholder="Filter by operation, target, actor…" value="${esc(S.audit.filter)}"></div></div>
    <div class="table-wrap"><table class="table compact"><thead><tr><th>When</th><th>Who</th><th>Operation</th><th>Server</th><th>Target</th><th>Parameters</th><th>Result</th></tr></thead><tbody>
      ${S.audit.rows.map(a => `<tr><td class="small nowrap">${esc(fmtDate(a.ts))}</td><td class="small">${esc(a.actor)}</td><td><span class="badge ${a.op.startsWith('create') || a.op === 'set_quota' || a.op.startsWith('install') ? 'accent' : ''}">${esc(a.op)}</span></td><td class="small">${esc(a.server || '')}</td><td class="mono small selectable">${esc(a.target || '')}</td><td class="mono tiny muted selectable">${esc(a.paramsJson || '')}</td><td>${a.ok ? '<span class="badge ok">ok</span>' : `<span class="badge danger" title="${esc(a.error || '')}">failed</span>`}${a.error ? `<div class="tiny selectable" style="color:var(--danger);max-width:320px">${esc(a.error)}</div>` : ''}</td></tr>`).join('') || '<tr><td colspan="7" class="center muted">No entries.</td></tr>'}
    </tbody></table></div>
    ${S.audit.rows.length < S.audit.total ? `<div class="center mt"><button class="btn" id="aMore">Load more</button></div>` : ''}`;
  let t;
  $('#aq').oninput = e => { S.audit.filter = e.target.value; clearTimeout(t); t = setTimeout(() => loadAudit(true).then(() => { const i = $('#aq'); i.focus(); i.setSelectionRange(i.value.length, i.value.length); }), 300); };
  const more = $('#aMore'); if (more) more.onclick = () => { S.audit.offset += 100; loadAudit(); };
}

// ============================================================ settings
async function renderSettings() {
  S.settings = await rpc.call('settings.get');
  const i = S.info;
  $('#content').innerHTML = `
    <div class="grid cols-2">
      <div class="card"><div class="card-head"><h2>Defaults</h2></div>
        <label class="field"><span>Default quota for new folders (GB)</span><input id="setQuota" type="number" min="0.1" step="0.5" value="${esc(toGb(Number(S.settings.default_quota_bytes || 5 * GB)))}"></label>
        <label class="check"><input id="setMust" type="checkbox" ${S.settings.must_change_default !== '0' ? 'checked' : ''}> <span>New accounts must change their password at next logon (wizard default)</span></label>
        <label class="field"><span>Theme</span><select id="setTheme"><option value="dark" ${S.settings.theme !== 'light' ? 'selected' : ''}>Dark</option><option value="light" ${S.settings.theme === 'light' ? 'selected' : ''}>Light</option></select></label>
        <button class="btn primary" id="setSave">Save</button>
      </div>
      <div class="card"><div class="card-head"><h2>About</h2></div>
        <div class="kv">
          <span class="k">Version</span><span class="v">${esc(i.version)} · ${esc(i.mode === 'mock' ? 'DEMO MODE (mock data)' : i.mode)}</span>
          <span class="k">This computer</span><span class="v">${esc(i.localFqdn)} · ${esc(i.windowsUser || '')}</span>
          <span class="k">WebView2</span><span class="v">${esc(i.webview)}</span>
          <span class="k">Database</span><span class="v mono">${esc(i.dbPath)}</span>
          <span class="k">Log file</span><span class="v mono">${esc(i.logFile)}</span>
        </div>
        <div class="row gap"><button class="btn small" id="openLogs">Open log folder</button><button class="btn small" id="openData">Open data folder</button><button class="btn small" id="setExport">${ICON.download} Export folders CSV</button></div>
        <p class="muted small mt">Copying the database file to another machine moves the configuration (domains, servers, audit history). Credentials saved with "Remember" are bound to this Windows account and do not travel.</p>
      </div>
    </div>`;
  $('#setSave').onclick = async b => {
    busy(b, true, 'Saving…');
    try {
      const gb = parseFloat($('#setQuota').value);
      if (gb > 0) await rpc.call('settings.set', { key: 'default_quota_bytes', value: String(Math.round(gb * GB)) });
      await rpc.call('settings.set', { key: 'must_change_default', value: $('#setMust').checked ? '1' : '0' });
      W.dir = null;
      const theme = $('#setTheme').value;
      await rpc.call('settings.set', { key: 'theme', value: theme });
      applyTheme(theme);
      S.settings = await rpc.call('settings.get');
      toast('ok', 'Settings saved');
    } catch (ex) { showError('Not saved', ex); } finally { busy(b, false); }
  };
  $('#openLogs').onclick = () => rpc.call('shell.openLogs');
  $('#openData').onclick = () => rpc.call('shell.openDataDir');
  $('#setExport').onclick = b => exportCsv(b);
}

// ============================================================ boot
async function boot() {
  try {
    S.info = await rpc.call('app.info');
    setupChrome();
    S.settings = await rpc.call('settings.get');
    applyTheme(S.settings.theme === 'light' ? 'light' : 'dark');
    S.domains = await rpc.call('domains.list');
    const sess = await rpc.call('auth.session');
    if (sess.loggedIn) {
      const d = S.domains.find(x => x.id === sess.domainId);
      await enterApp({ user: sess.user, domainId: sess.domainId, domain: d ? d.name : '' });
    } else await showLogin();
  } catch (ex) {
    $('#login').classList.remove('hidden');
    $('#loginError').textContent = 'Startup failed: ' + ex.message;
    $('#loginError').classList.remove('hidden');
  }
}

$('#loginForm').addEventListener('submit', doLogin);
$('#loginDomain').addEventListener('change', prefillRemembered);
$('#loginAddDomain').onclick = () => editEnvironment(null);
$('#logoutBtn').onclick = logout;
$('#themeBtn').onclick = async () => { const t = S.settings.theme === 'light' ? 'dark' : 'light'; applyTheme(t); try { await rpc.call('settings.set', { key: 'theme', value: t }); } catch (_) { } };
document.addEventListener('dragstart', e => e.preventDefault());
document.addEventListener('dragover', e => e.preventDefault());
document.addEventListener('drop', e => e.preventDefault());
document.addEventListener('focusin', e => { if (e.target && e.target.matches && e.target.matches('input[type=text], textarea')) e.target.spellcheck = false; });
document.addEventListener('keydown', e => {
  if (e.key === 'Escape') { const m = $$('.modal-backdrop').pop(); if (m) { m.remove(); return; } closeDrawer(); }
  if (e.key === 'F5') { e.preventDefault(); if (S.session) nav(S.page); }
});
boot();
