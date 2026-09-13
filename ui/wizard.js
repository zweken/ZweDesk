/* ZweDesk — provisioning wizard and environment page (shares the globals of app.js). */
'use strict';

// ============================================================ wizard state
const W = {
  step: 0,
  dir: null,          // dir.get payload
  plan: null,         // provision.plan payload for the current username
  planFor: '',
  result: null,
  form: null,
  reset() {
    this.step = 0; this.plan = null; this.planFor = ''; this.result = null;
    const d = this.dir || {};
    const def = d.defaults || {};
    this.form = {
      firstName: '', lastName: '', sam: '', pwMode: 'generate', password: '', password2: '', showPw: false,
      mustChange: d.mustChangeDefault !== false, ouDn: def.ouDn || '', ouQ: '', dc: '',
      groups: new Set(def.groups || []), groupQ: '',
      poolId: def.poolId || '', poolEntitle: 'group', poolGroup: '',
      serverId: (d.servers && d.servers[0]) ? d.servers[0].id : 0, gb: toGb(Number(d.defaultQuotaBytes || 5 * GB)),
    };
  },
};
const WSTEPS = ['Account', 'Access', 'Storage', 'Review', 'Done'];

async function loadWizard() {
  $('#pageSub').textContent = 'Create the AD account, group memberships, home folder, quota, DFS link and Horizon entitlement in one pass';
  if (!W.dir) {
    $('#content').innerHTML = '<div class="empty"><span class="spin"></span><div class="muted small mt">Reading OUs, groups and pools…</div></div>';
    try { W.dir = await rpc.call('dir.get', { domainId: S.domainId }); }
    catch (ex) {
      $('#content').innerHTML = `<div class="card"><div class="note danger">${esc(ex.message)}</div>${ex.details ? `<pre class="details">${esc(ex.details)}</pre>` : ''}<p class="muted small mt">The directory listing runs on the directory server of this environment (${esc(S.domain ? (S.domain.dcFqdn || S.domain.dfsHostFqdn) : '')}). Check it under Environment, then try again.</p><button class="btn mt" id="wzRetry">Retry</button></div>`;
      $('#wzRetry').onclick = () => loadWizard();
      return;
    }
    W.reset();
  }
  if (!W.form) W.reset();
  renderWizard();
}

function wzServer() { return (W.dir.servers || []).find(s => s.id === Number(W.form.serverId)); }
function wzPool() { return (W.dir.pools || []).find(p => p.poolId === W.form.poolId); }
function wzOu() { return (W.dir.ous || []).find(o => o.dn === W.form.ouDn); }
function wzGroupName(dn) { const g = (W.dir.groups || []).find(x => x.dn === dn); return g ? g.name : dn; }

function wzValidate(step) {
  const f = W.form, errs = [];
  const exists = W.plan && W.plan.account;
  if (step === 0) {
    if (!f.sam.trim()) errs.push('User name is required.');
    else if (!/^[A-Za-z0-9][A-Za-z0-9._-]{0,19}$/.test(f.sam.trim())) errs.push('User name: 1–20 characters, letters, digits, ".", "_" or "-".');
    if (!exists) {
      if (!f.firstName.trim() || !f.lastName.trim()) errs.push('First name and last name are required for a new account.');
      if (!f.ouDn) errs.push('Choose the OU for the new account.');
      if (f.pwMode === 'manual') {
        if (f.password.length < 8) errs.push('Password must be at least 8 characters.');
        if (f.password !== f.password2) errs.push('The two passwords differ.');
      }
    }
  }
  if (step === 2) {
    if (!wzServer()) errs.push('Choose a file server.');
    if (!(parseFloat(f.gb) > 0)) errs.push('Quota must be a positive number of GB.');
  }
  return errs;
}

function renderWizard() {
  const f = W.form;
  const stepper = `<div class="stepper">${WSTEPS.map((t, i) => `<div class="stp ${i === W.step ? 'on' : i < W.step ? 'done' : ''}"><span class="n">${i < W.step ? ICON.check : i + 1}</span><span>${t}</span></div>`).join('')}</div>`;
  let body = '';
  switch (W.step) {
    case 0: body = wzAccount(); break;
    case 1: body = wzAccess(); break;
    case 2: body = wzStorage(); break;
    case 3: body = wzReview(); break;
    case 4: body = wzResult(); break;
  }
  const back = W.step > 0 && W.step < 4 ? '<button class="btn" id="wzBack">Back</button>' : '';
  const next = W.step < 3 ? '<button class="btn primary" id="wzNext">Continue</button>' : W.step === 3 ? `<button class="btn primary" id="wzCreate" ${W.plan && W.plan.blockers.length ? 'disabled' : ''}>${ICON.plus} Create user</button>` : '<button class="btn primary" id="wzAgain">Provision another user</button>';
  $('#content').innerHTML = `<div class="wizard">${stepper}<div class="card wz-body">${body}</div><div class="wz-foot"><div id="wzErr" class="form-error hidden"></div><span class="grow"></span>${back}${next}</div></div>`;
  bindWizard();
}

function wzShowErrors(errs) { const e = $('#wzErr'); if (!errs.length) { e.classList.add('hidden'); return; } e.innerHTML = errs.map(esc).join('<br>'); e.classList.remove('hidden'); }

// ------------------------------------------------------------ step 1: account
function wzAccount() {
  const f = W.form, d = W.dir;
  const exists = W.plan && W.planFor === f.sam.trim().toLowerCase() && W.plan.account;
  const ous = (d.ous || []).filter(o => !f.ouQ || o.canonical.toLowerCase().includes(f.ouQ.toLowerCase()) || o.dn.toLowerCase().includes(f.ouQ.toLowerCase()));
  const depth = o => Math.max(0, o.canonical.split('/').filter(Boolean).length - 1);
  const dcs = d.dcs || [];
  const currentDc = S.domain ? (S.domain.dcFqdn || S.domain.dfsHostFqdn) : '';
  return `
    <h2>Account</h2>
    <p class="muted small">The user name is checked against Active Directory when you continue. If the account already exists, the wizard only adds what is missing (groups, folder, quota, link).</p>
    <div class="form-grid">
      <label class="field"><span>User name (sAMAccountName)</span><input id="wzSam" type="text" value="${esc(f.sam)}" placeholder="u1234" spellcheck="false" maxlength="20"></label>
      <label class="field"><span>Directory server (domain controller)</span><select id="wzDc"><option value="">${esc(currentDc)} (environment default)</option>${dcs.filter(x => x.hostName.toLowerCase() !== currentDc.toLowerCase()).map(x => `<option value="${esc(x.hostName)}" ${f.dc === x.hostName ? 'selected' : ''}>${esc(x.hostName)}${x.site ? ' — ' + esc(x.site) : ''}</option>`).join('')}</select></label>
      <label class="field"><span>First name</span><input id="wzFirst" type="text" value="${esc(f.firstName)}" ${exists ? 'disabled' : ''}></label>
      <label class="field"><span>Last name</span><input id="wzLast" type="text" value="${esc(f.lastName)}" ${exists ? 'disabled' : ''}></label>
    </div>
    ${exists ? `<div class="note warn">Account <b>${esc(W.plan.account.samAccountName)}</b> already exists (${esc(W.plan.account.displayName || '')}, ${esc(W.plan.account.distinguishedName)}). No account will be created; password and OU do not apply.</div>` : `
    <div class="form-grid">
      <div class="field"><span>Password</span>
        <div class="row gap wrap">
          <label class="check" style="margin:0"><input type="radio" name="wzPw" value="generate" ${f.pwMode === 'generate' ? 'checked' : ''}> <span>Generate a strong password and show it once at the end</span></label>
          <label class="check" style="margin:0"><input type="radio" name="wzPw" value="manual" ${f.pwMode === 'manual' ? 'checked' : ''}> <span>Enter manually</span></label>
        </div>
        <div id="wzPwManual" class="${f.pwMode === 'manual' ? '' : 'hidden'}">
          <div class="row gap mt"><input id="wzPw1" type="${f.showPw ? 'text' : 'password'}" value="${esc(f.password)}" placeholder="Password"><input id="wzPw2" type="${f.showPw ? 'text' : 'password'}" value="${esc(f.password2)}" placeholder="Repeat"><button class="btn small" id="wzPwShow" type="button">${f.showPw ? 'Hide' : 'Show'}</button></div>
        </div>
        <label class="check mt"><input id="wzMust" type="checkbox" ${f.mustChange ? 'checked' : ''}> <span>User must change password at next logon</span></label>
      </div>
      <div class="field"><span>Organizational unit</span>
        <div class="search"><input id="wzOuQ" type="text" value="${esc(f.ouQ)}" placeholder="Filter OUs…"></div>
        <div class="picker" id="wzOuList">${ous.map(o => `<div class="pick ${o.dn === f.ouDn ? 'on' : ''}" data-ou="${esc(o.dn)}" style="padding-left:${10 + depth(o) * 14}px" title="${esc(o.dn)}"><span class="badge muted tiny">${o.kind === 'domain' ? 'domain' : o.kind === 'container' ? 'CN' : 'OU'}</span> ${esc(o.name)}</div>`).join('') || '<div class="muted small" style="padding:8px">No OU matches.</div>'}</div>
        <span class="hint">${f.ouDn ? 'Selected: ' + esc(f.ouDn) : 'Group Policy linked to the OU (folder redirection, FSLogix, …) applies automatically.'}</span>
      </div>
    </div>`}`;
}

// ------------------------------------------------------------ step 2: access
function wzAccess() {
  const f = W.form, d = W.dir;
  const q = f.groupQ.toLowerCase();
  const groups = (d.groups || []).filter(g => !q || g.name.toLowerCase().includes(q) || (g.description || '').toLowerCase().includes(q)).slice(0, 400);
  const pools = (d.pools || []).filter(p => p.enabled);
  const pool = wzPool();
  const hz = d.horizon || {};
  return `
    <h2>Access</h2>
    <div class="form-grid">
      <div class="field"><span>Group memberships</span>
        <div class="search"><input id="wzGrpQ" type="text" value="${esc(f.groupQ)}" placeholder="Filter groups…"></div>
        <div class="picker tall" id="wzGrpList">${groups.map(g => `<label class="pick" title="${esc(g.dn)}"><input type="checkbox" data-grp="${esc(g.dn)}" ${f.groups.has(g.dn) ? 'checked' : ''}> <span>${esc(g.name)}</span><span class="muted tiny ellipsis">${esc(g.description || g.scope)}</span></label>`).join('') || '<div class="muted small" style="padding:8px">No group matches.</div>'}</div>
        <div class="row gap wrap mt" id="wzGrpSel">${[...f.groups].map(dn => `<span class="badge accent">${esc(wzGroupName(dn))} <button class="x" data-ungrp="${esc(dn)}" title="Remove">×</button></span>`).join('') || '<span class="muted small">No groups selected.</span>'}</div>
      </div>
      <div class="field"><span>Horizon desktop pool</span>
        ${!hz.configured ? '<div class="note">Horizon is not configured for this environment. Set the Connection Server under <b>Environment → Horizon</b> to entitle users to a pool from here.</div>' :
          !pools.length ? '<div class="note warn">No pools are cached yet. Open <b>Environment → Horizon → Test connection</b> to read the pools.</div>' : `
        <select id="wzPool"><option value="">— no pool —</option>${pools.map(p => `<option value="${esc(p.poolId)}" ${p.poolId === f.poolId ? 'selected' : ''}>${esc(p.displayName || p.name)} (${esc(p.name)})</option>`).join('')}</select>
        ${pool ? `<div class="note mt"><b>${esc(pool.displayName || pool.name)}</b> · entitled groups: ${pool.entitledGroups.length ? esc(pool.entitledGroups.join(', ')) : '<i>none</i>'}${pool.entitledUsers ? ` · ${pool.entitledUsers} direct user(s)` : ''}
          <div class="row gap wrap mt">
            <label class="check" style="margin:0"><input type="radio" name="wzEnt" value="group" ${pool.entitledGroups.length ? '' : 'disabled'} ${f.poolEntitle === 'group' && pool.entitledGroups.length ? 'checked' : ''}> <span>Add the user to the pool's AD group</span></label>
            ${pool.entitledGroups.length > 1 ? `<select id="wzPoolGroup" style="width:auto">${pool.entitledGroups.map(g => `<option value="${esc(g)}" ${f.poolGroup === g ? 'selected' : ''}>${esc(g)}</option>`).join('')}</select>` : ''}
            <label class="check" style="margin:0"><input type="radio" name="wzEnt" value="user" ${f.poolEntitle === 'user' || !pool.entitledGroups.length ? 'checked' : ''}> <span>Entitle the user directly in Horizon (REST API)</span></label>
          </div></div>` : ''}`}
      </div>
    </div>`;
}

// ------------------------------------------------------------ step 3: storage
function wzStorage() {
  const f = W.form, d = W.dir;
  const s = wzServer();
  return `
    <h2>Storage</h2>
    <div class="form-grid">
      <label class="field"><span>File server</span><select id="wzServer">${(d.servers || []).map(x => `<option value="${x.id}" ${x.id === Number(f.serverId) ? 'selected' : ''}>${esc(x.name)} — ${esc(x.localRoot)}</option>`).join('')}</select>
        ${s ? `<span class="hint">${s.shareName ? `Share \\\\${esc(s.fqdn)}\\${esc(s.shareName)}` : '<span style="color:var(--warn)">share unknown — run Diagnostics first</span>'} · FSRM ${s.fsrmInstalled === true ? 'installed' : s.fsrmInstalled === false ? '<span style="color:var(--danger)">missing</span>' : 'unknown'}</span>` : ''}</label>
      <label class="field"><span>Quota (GB)</span><input id="wzGb" type="number" min="0.1" step="0.5" value="${esc(f.gb)}"><span class="hint">Hard FSRM quota on the user folder. The DFS link ${esc(S.domain ? S.domain.dfsRoot : '')}\\${esc(f.sam || '<user>')} is created automatically.</span></label>
    </div>`;
}

// ------------------------------------------------------------ step 4: review
function wzReview() {
  const f = W.form, p = W.plan || { blockers: [], folders: [] }, s = wzServer(), pool = wzPool();
  const exists = !!p.account;
  const name = (p.account && p.account.samAccountName) || f.sam.trim();
  const rows = [
    ['Account', exists ? `exists — ${esc(p.account.distinguishedName)}` : `<b>create</b> ${esc(f.sam.trim())} · ${esc(f.firstName.trim() + ' ' + f.lastName.trim())} · ${esc(f.sam.trim())}@${esc(S.domain ? S.domain.name : '')} in ${esc(f.ouDn)}${f.mustChange ? ' · must change password' : ''} · password ${f.pwMode === 'generate' ? 'generated (shown once)' : 'entered manually'}`],
    ['Directory server', esc(f.dc || (S.domain ? (S.domain.dcFqdn || S.domain.dfsHostFqdn) : ''))],
    ['Groups', f.groups.size ? [...f.groups].map(dn => esc(wzGroupName(dn))).join(', ') + (pool && f.poolEntitle === 'group' ? ` <span class="muted">+ pool group ${esc(f.poolGroup || (pool.entitledGroups[0] || ''))}</span>` : '') : (pool && f.poolEntitle === 'group' ? `pool group ${esc(f.poolGroup || (pool.entitledGroups[0] || ''))}` : '<span class="muted">none</span>')],
    ['Horizon pool', pool ? `${esc(pool.displayName || pool.name)} — ${f.poolEntitle === 'group' && pool.entitledGroups.length ? 'via AD group' : 'direct entitlement (REST)'}` : '<span class="muted">none</span>'],
    ['Folder', p.folders.some(x => s && x.serverId === s.id) ? `exists on ${esc(s.name)} — reused` : `<b>create</b> ${esc(s ? s.localRoot : '')}\\${esc(name)} on ${esc(s ? s.name : '?')} with standard ACL`],
    ['Quota', `${esc(f.gb)} GB (FSRM hard quota)`],
    ['DFS link', p.link ? (p.linkOk ? `exists — ${esc(p.link.linkPath)}` : `<span style="color:var(--danger)">exists but points elsewhere: ${esc(p.link.targets.join(', '))}</span>`) : `<b>create</b> ${esc(S.domain ? S.domain.dfsRoot : '')}\\${esc(name)} → \\\\${esc(s ? s.fqdn : '?')}\\${esc(s && s.shareName ? s.shareName : '?')}\\${esc(name)}`],
  ];
  return `
    <h2>Review</h2>
    ${p.blockers.length ? `<div class="note danger"><b>Cannot start:</b><br>${p.blockers.map(esc).join('<br>')}</div>` : ''}
    <div class="kv" style="grid-template-columns:150px 1fr">${rows.map(([k, v]) => `<span class="k">${k}</span><span class="v">${v}</span>`).join('')}</div>
    <p class="muted small">Steps run in this order: account → groups → folder → quota → DFS link → Horizon. A failure stops the dependent steps; nothing is deleted or rolled back, and running the wizard again with the same user name continues where it stopped. Every step is written to the audit log.</p>`;
}

// ------------------------------------------------------------ step 5: result
function wzResult() {
  const r = W.result;
  if (!r) return '<div class="empty">No result.</div>';
  const st = s => s === 'ok' ? 'ok' : s === 'exists' ? 'info' : s === 'fail' ? 'fail' : 'skip';
  return `
    <h2>${r.ok ? 'User provisioned' : 'Finished with errors'}</h2>
    <ul class="steps">${r.steps.map(s => `<li><span class="dot ${st(s.status)}" style="margin-top:5px"></span><div><div>${esc(s.label)} <span class="badge ${s.status === 'ok' ? 'ok' : s.status === 'fail' ? 'danger' : 'muted'}">${esc(s.status)}</span></div>${s.detail ? `<div class="muted small">${esc(s.detail)}</div>` : ''}${s.error ? `<div class="err">${esc(s.error)}</div>` : ''}</div></li>`).join('')}</ul>
    ${r.password ? `<div class="note ok mt"><b>Password for ${esc(r.user ? r.user.samAccountName : '')}</b> ${r.passwordGenerated ? '(generated — shown only now, not stored anywhere)' : ''}<div class="row gap mt"><code class="pw selectable" id="wzPwOut">${esc(r.password)}</code><button class="btn small" id="wzPwCopy">Copy</button></div></div>` : ''}
    <div class="kv mt" style="grid-template-columns:150px 1fr">
      ${r.user ? `<span class="k">Account</span><span class="v">${esc(r.user.samAccountName)} · ${esc(r.upn || '')} · ${esc(r.user.distinguishedName || '')}</span>` : ''}
      <span class="k">Folder</span><span class="v">${esc(r.path || '')}</span>
      <span class="k">DFS link</span><span class="v">${esc(r.linkPath || '')} → ${esc(r.targetPath || '')}</span>
    </div>
    ${!r.ok ? '<div class="note warn mt">Fix the cause of the failed step (Diagnostics shows the state of every server), then run the wizard again with the same user name — completed steps are detected and skipped.</div>' : ''}`;
}

// ------------------------------------------------------------ bindings & navigation
function bindWizard() {
  const f = W.form;
  const b = $('#wzBack'); if (b) b.onclick = () => { W.step--; renderWizard(); };
  const n = $('#wzNext'); if (n) n.onclick = () => wzNext(n);
  const c = $('#wzCreate'); if (c) c.onclick = () => wzRun(c);
  const a = $('#wzAgain'); if (a) a.onclick = () => { W.reset(); renderWizard(); };
  if (W.step === 0) {
    $('#wzSam').oninput = e => { f.sam = e.target.value; };
    $('#wzDc').onchange = e => { f.dc = e.target.value; };
    const fi = $('#wzFirst'), la = $('#wzLast');
    if (fi) fi.oninput = e => { f.firstName = e.target.value; };
    if (la) la.oninput = e => { f.lastName = e.target.value; };
    $$('input[name=wzPw]').forEach(r => r.onchange = () => { f.pwMode = r.value; $('#wzPwManual').classList.toggle('hidden', f.pwMode !== 'manual'); });
    const p1 = $('#wzPw1'), p2 = $('#wzPw2'), sh = $('#wzPwShow');
    if (p1) p1.oninput = e => { f.password = e.target.value; };
    if (p2) p2.oninput = e => { f.password2 = e.target.value; };
    if (sh) sh.onclick = () => { f.showPw = !f.showPw; renderWizard(); };
    const mc = $('#wzMust'); if (mc) mc.onchange = e => { f.mustChange = e.target.checked; };
    const oq = $('#wzOuQ'); if (oq) oq.oninput = e => { f.ouQ = e.target.value; renderWizard(); const i = $('#wzOuQ'); i.focus(); i.setSelectionRange(f.ouQ.length, f.ouQ.length); };
    $$('[data-ou]').forEach(el => el.onclick = () => { f.ouDn = el.dataset.ou; renderWizard(); });
    setTimeout(() => { const s = $('#wzSam'); if (s && !s.value) s.focus(); }, 30);
  }
  if (W.step === 1) {
    $('#wzGrpQ').oninput = e => { f.groupQ = e.target.value; renderWizard(); const i = $('#wzGrpQ'); i.focus(); i.setSelectionRange(f.groupQ.length, f.groupQ.length); };
    $$('[data-grp]').forEach(cb => cb.onchange = () => { if (cb.checked) f.groups.add(cb.dataset.grp); else f.groups.delete(cb.dataset.grp); renderWizard(); });
    $$('[data-ungrp]').forEach(x => x.onclick = () => { f.groups.delete(x.dataset.ungrp); renderWizard(); });
    const ps = $('#wzPool'); if (ps) ps.onchange = e => { f.poolId = e.target.value; const p = wzPool(); f.poolEntitle = p && p.entitledGroups.length ? 'group' : 'user'; f.poolGroup = p && p.entitledGroups.length ? p.entitledGroups[0] : ''; renderWizard(); };
    $$('input[name=wzEnt]').forEach(r => r.onchange = () => { f.poolEntitle = r.value; renderWizard(); });
    const pg = $('#wzPoolGroup'); if (pg) pg.onchange = e => { f.poolGroup = e.target.value; };
  }
  if (W.step === 2) {
    $('#wzServer').onchange = e => { f.serverId = Number(e.target.value); renderWizard(); };
    $('#wzGb').oninput = e => { f.gb = e.target.value; };
  }
  if (W.step === 4) {
    const cp = $('#wzPwCopy'); if (cp) cp.onclick = () => copyText(W.result.password);
  }
}

async function wzNext(btn) {
  const f = W.form;
  if (W.step === 0) {
    // resolve the account first so the validation knows whether it is new
    const sam = f.sam.trim();
    if (!sam) { wzShowErrors(['User name is required.']); return; }
    busy(btn, true, 'Checking…');
    try {
      W.plan = await rpc.call('provision.plan', { domainId: S.domainId, sam, serverId: Number(f.serverId) || 0 });
      W.planFor = sam.toLowerCase();
    } catch (ex) { busy(btn, false); wzShowErrors([ex.message]); return; }
    busy(btn, false);
    if (W.plan.account && (f.firstName || f.lastName)) toast('', 'Account exists', `${W.plan.account.samAccountName} already exists — the wizard will reuse it.`);
  }
  const errs = wzValidate(W.step);
  if (errs.length) { wzShowErrors(errs); if (W.step === 0) renderWizard(); return; }
  W.step++;
  if (W.step === 3) {
    // refresh the plan with the chosen server so folder/link/blockers are exact
    try { W.plan = await rpc.call('provision.plan', { domainId: S.domainId, sam: f.sam.trim(), serverId: Number(f.serverId) }); W.planFor = f.sam.trim().toLowerCase(); }
    catch (ex) { W.plan = { account: null, folders: [], link: null, linkOk: false, blockers: [ex.message] }; }
  }
  renderWizard();
}

async function wzRun(btn) {
  const f = W.form, pool = wzPool();
  const groups = [...f.groups];
  let poolEntitle = 'none';
  if (pool) {
    if (f.poolEntitle === 'group' && pool.entitledGroups.length) { const g = f.poolGroup || pool.entitledGroups[0]; groups.push(g.includes('\\') ? g.split('\\').pop() : g); }
    else poolEntitle = 'user';
  }
  const ok = await confirmDialog('Create user', `<p>Provision <b>${esc(f.sam.trim())}</b> now? ${W.plan && W.plan.account ? 'The existing account is reused.' : 'A new Active Directory account will be created.'}</p>`, { label: 'Create' });
  if (!ok) return;
  busy(btn, true, 'Provisioning…');
  $('#wzBack') && ($('#wzBack').disabled = true);
  try {
    W.result = await rpc.call('provision.run', {
      domainId: S.domainId, firstName: f.firstName, lastName: f.lastName, sam: f.sam.trim(),
      password: W.plan && W.plan.account ? null : (f.pwMode === 'manual' ? f.password : null),
      mustChange: f.mustChange, ouDn: f.ouDn, groups, serverId: Number(f.serverId), bytes: Math.round(parseFloat(f.gb) * GB),
      poolId: pool ? pool.poolId : null, poolEntitle, dcFqdn: f.dc || null,
    });
    W.step = 4;
    S.loaded = {};
    renderWizard();
    toast(W.result.ok ? 'ok' : 'warn', W.result.ok ? 'User provisioned' : 'Finished with errors', f.sam.trim());
  } catch (ex) { busy(btn, false); $('#wzBack') && ($('#wzBack').disabled = false); showError('Provisioning failed', ex); }
}

// ============================================================ environment page
async function loadEnv() {
  S.domains = await rpc.call('domains.list');
  S.domain = S.domains.find(d => d.id === S.domainId) || S.domain;
  S.servers = await rpc.call('servers.list', { domainId: S.domainId });
  S.pools = await rpc.call('env.pools', { domainId: S.domainId });
  renderEnv();
}

function renderEnv() {
  const d = S.domain || {};
  $('#pageSub').textContent = `Environment "${d.name || ''}" — Active Directory, DFS namespace, file servers, Horizon`;
  const others = S.domains.filter(x => x.id !== S.domainId);
  $('#content').innerHTML = `
    <div class="grid cols-2">
      <div class="card"><div class="card-head"><h2>Active Directory</h2><button class="btn ghost small" data-editenv>Edit</button></div>
        <div class="kv"><span class="k">Domain</span><span class="v">${esc(d.name)}</span><span class="k">NetBIOS</span><span class="v">${esc(d.netbios)}</span><span class="k">Directory server</span><span class="v mono">${esc(d.dcFqdn || d.dfsHostFqdn)}</span></div>
        <p class="muted small">The directory server runs the Active Directory cmdlets (account creation, OUs, groups). Any domain controller of ${esc(d.name)} works.</p>
      </div>
      <div class="card"><div class="card-head"><h2>DFS namespace</h2><div class="row gap"><button class="btn ghost small" id="dfsRoots">Find namespaces</button><button class="btn ghost small" id="dfsDiscover" ${d.dfsRoot ? '' : 'disabled'}>Discover namespace servers</button><button class="btn ghost small" data-editenv>Edit</button></div></div>
        <div class="kv"><span class="k">Root</span><span class="v mono">${esc(d.dfsRoot || '') || '<span class="badge warn">not set — click Find namespaces</span>'}</span><span class="k">DFS host</span><span class="v mono">${esc(d.dfsHostFqdn)}</span></div>
        <div id="dfsNs"></div>
        <p class="muted small">Every user gets a link ${esc(d.dfsRoot)}\\&lt;user&gt; pointing to the folder on its file server. The DFS host is the machine that runs the DFSN commands — it should be one of the namespace servers of the root (found in AD with the button above); the file servers that hold the folders are configured below.</p>
      </div>
    </div>
    <div class="card mt"><div class="card-head"><h2>File servers</h2></div>
      ${S.servers.length ? `<table class="table compact"><thead><tr><th>Name</th><th>FQDN</th><th>User data root</th><th>Share</th><th>FSRM</th><th>Enabled</th><th>Last scan</th><th></th></tr></thead><tbody>
        ${S.servers.map(s => `<tr><td class="name">${esc(s.name)}</td><td class="mono small">${esc(s.fqdn)}</td><td class="mono small">${esc(s.localRoot)}</td><td class="mono small">${esc(s.shareName || '—')}</td><td>${s.fsrmInstalled === true ? '<span class="badge ok">installed</span>' : s.fsrmInstalled === false ? '<span class="badge danger">missing</span>' : '<span class="badge muted">unknown</span>'}</td><td>${s.enabled ? '<span class="badge ok">yes</span>' : '<span class="badge muted">no</span>'}</td><td class="small">${esc(ago(s.lastScanAt))}${s.lastError ? `<br><span style="color:var(--danger)">${esc(s.lastError)}</span>` : ''}</td>
          <td class="actions"><button class="btn ghost small" data-test="${s.id}">Test</button><button class="btn ghost small" data-esrv="${s.id}">Edit</button><button class="btn ghost small" data-dsrv="${s.id}" style="color:var(--danger)">Delete</button></td></tr>`).join('')}
      </tbody></table>` : '<div class="empty">No file servers yet. Add the servers that hold the user folders.</div>'}
    </div>
    <div class="card mt"><div class="card-head"><div class="row gap"><h2>Horizon</h2>${d.horizonConfigured ? '<span class="badge ok">enabled</span>' : '<span class="badge muted">not configured</span>'}</div><div class="row gap"><button class="btn small" id="hzTest" ${d.horizonConfigured ? '' : 'disabled'}>Test connection & read pools</button><button class="btn ghost small" data-editenv>Edit</button></div></div>
      <div class="kv"><span class="k">Connection Server</span><span class="v mono">${esc(d.horizonUrl || '—')}</span><span class="k">Sign-in</span><span class="v">${d.horizonAuth === 'stored' ? `dedicated account ${esc(d.horizonUser)}${d.hasHorizonPassword ? ' (password stored)' : ' <span style="color:var(--danger)">(no password stored)</span>'}` : d.horizonAuth === 'session' ? 'the account you signed in with' : '—'}${d.horizonDomain ? ' · domain ' + esc(d.horizonDomain) : ''}</span><span class="k">Certificate</span><span class="v">${d.horizonIgnoreCert ? '<span class="badge warn">errors ignored</span>' : 'must be trusted'}</span><span class="k">Entitlement</span><span class="v">${d.horizonEntitleMode === 'user' ? 'direct user entitlement (REST)' : 'via the pool\'s AD group (recommended)'}</span></div>
      <div id="hzPools">${renderPools(S.pools)}</div>
    </div>
    ${others.length ? `<div class="card mt"><div class="card-head"><h2>Other environments</h2></div><table class="table compact"><thead><tr><th>Domain</th><th>DFS root</th><th>Servers</th><th></th></tr></thead><tbody>${others.map(x => `<tr><td class="name">${esc(x.name)}</td><td class="mono small">${esc(x.dfsRoot)}</td><td>${x.servers}</td><td class="actions"><button class="btn ghost small" data-switch="${x.id}">Switch…</button><button class="btn ghost small" data-ddom="${x.id}" style="color:var(--danger)">Delete</button></td></tr>`).join('')}</tbody></table><p class="muted small mt">Switching asks for that environment's credentials.</p></div>` : ''}`;
  $$('[data-editenv]').forEach(b => b.onclick = () => editEnvironment(S.domain));
  $$('[data-esrv]').forEach(b => b.onclick = () => editServer(S.servers.find(s => s.id === Number(b.dataset.esrv))));
  $$('[data-dsrv]').forEach(b => b.onclick = async () => {
    const s = S.servers.find(x => x.id === Number(b.dataset.dsrv));
    if (!await confirmDialog('Delete server', `<p>Remove <b>${esc(s.name)}</b> and its scan data from ZweDesk? Nothing on the server is touched.</p>`, { label: 'Delete', kind: 'danger' })) return;
    try { await rpc.call('servers.delete', { id: s.id }); toast('ok', 'Server removed'); await loadEnv(); } catch (ex) { showError('Not removed', ex); }
  });
  $$('[data-test]').forEach(b => b.onclick = async () => {
    busy(b, true, 'Testing…');
    try { const r = await rpc.call('servers.test', { id: Number(b.dataset.test) }); toast('ok', 'WinRM OK', r.detail); }
    catch (ex) { showError('Server test failed', ex); } finally { busy(b, false); }
  });
  $$('[data-switch]').forEach(b => b.onclick = async () => { await logout(); $('#loginDomain').value = b.dataset.switch; await prefillRemembered(); });
  $$('[data-ddom]').forEach(b => b.onclick = async () => {
    const x = S.domains.find(y => y.id === Number(b.dataset.ddom));
    if (!await confirmDialog('Delete environment', `<p>Delete <b>${esc(x.name)}</b> with its ${x.servers} server(s) and all scan data from ZweDesk? Nothing in AD or on the servers is touched.</p>`, { label: 'Delete', kind: 'danger' })) return;
    try { await rpc.call('domains.delete', { id: x.id }); toast('ok', 'Environment deleted'); await loadEnv(); } catch (ex) { showError('Not deleted', ex); }
  });
  const saveEnv = async (patch, okTitle, okDetail) => {
    try {
      await rpc.call('env.save', { id: d.id, name: d.name, netbios: d.netbios, dfsRoot: d.dfsRoot, dfsHostFqdn: d.dfsHostFqdn, dcFqdn: d.dcFqdn, horizonUrl: d.horizonUrl, horizonAuth: d.horizonAuth, horizonUser: d.horizonUser, horizonDomain: d.horizonDomain, horizonIgnoreCert: d.horizonIgnoreCert, horizonEntitleMode: d.horizonEntitleMode, horizonPassword: null, ...patch });
      toast('ok', okTitle, okDetail); W.dir = null; await loadEnv();
    } catch (ex) { showError('Not saved', ex); }
  };
  const dr = $('#dfsRoots'); if (dr) dr.onclick = async () => {
    busy(dr, true, 'Reading AD…');
    try {
      const r = await rpc.call('env.discoverRoots', { domainId: S.domainId });
      $('#dfsNs').innerHTML = r.roots.length
        ? `<div class="note">Domain-based namespaces of <b>${esc(d.name)}</b>: ${r.roots.map(x => `<span class="badge mono">${esc(x)}</span> ${x.toLowerCase() === (r.current || '').toLowerCase() ? '<span class="badge ok">current</span>' : `<button class="btn small" data-useroot="${esc(x)}">Use as root</button>`}`).join(' ')}</div>`
        : '<div class="note warn">No domain-based DFS namespace found in Active Directory (stand-alone namespaces are not listed there — enter the root with Edit).</div>';
      $$('[data-useroot]').forEach(b => b.onclick = () => saveEnv({ dfsRoot: b.dataset.useroot }, 'DFS root set', b.dataset.useroot));
    } catch (ex) { showError('Discovery failed', ex); } finally { busy(dr, false); }
  };
  const dd = $('#dfsDiscover'); if (dd) dd.onclick = async () => {
    busy(dd, true, 'Reading AD…');
    try {
      const r = await rpc.call('env.discoverDfs', { domainId: S.domainId });
      $('#dfsNs').innerHTML = r.servers.length
        ? `<div class="note ${r.currentIsNamespaceServer ? 'ok' : 'warn'}">Namespace servers of <span class="mono">${esc(r.dfsRoot)}</span>: ${r.servers.map(h => `<span class="badge mono">${esc(h)}</span> ${h.toLowerCase() === (r.current || '').toLowerCase() ? '<span class="badge ok">current</span>' : `<button class="btn small" data-usedfs="${esc(h)}">Use as DFS host</button>`}`).join(' ')}${r.currentIsNamespaceServer ? '' : `<div class="small mt">The configured DFS host <b>${esc(r.current)}</b> is not a namespace server.</div>`}</div>`
        : '<div class="note warn">No namespace servers found in AD for this root (stand-alone namespace, or the root name is wrong).</div>';
      $$('[data-usedfs]').forEach(b => b.onclick = () => saveEnv({ dfsHostFqdn: b.dataset.usedfs }, 'DFS host set', b.dataset.usedfs));
    } catch (ex) { showError('Discovery failed', ex); } finally { busy(dd, false); }
  };
  const t = $('#hzTest'); if (t) t.onclick = async () => {
    busy(t, true, 'Connecting…');
    try {
      const r = await rpc.call('env.horizonTest', { domainId: S.domainId });
      S.pools = r.pools; W.dir = null;
      $('#hzPools').innerHTML = renderPools(S.pools);
      toast('ok', 'Horizon', r.message);
    } catch (ex) { showError('Horizon connection failed', ex); } finally { busy(t, false); }
  };
}

function renderPools(pools) {
  if (!pools || !pools.length) return '<p class="muted small">No pools read yet.</p>';
  return `<table class="table compact mt"><thead><tr><th>Pool</th><th>Name</th><th>Type</th><th>Entitled groups</th><th>Direct users</th><th>Enabled</th></tr></thead><tbody>
    ${pools.map(p => `<tr><td class="name">${esc(p.displayName || p.name)}</td><td class="mono small">${esc(p.name)}</td><td class="small">${esc(p.type || '')}</td><td class="small">${p.entitledGroups.length ? esc(p.entitledGroups.join(', ')) : '<span class="muted">none</span>'}</td><td class="small">${p.entitledUsers}</td><td>${p.enabled ? '<span class="badge ok">yes</span>' : '<span class="badge muted">no</span>'}</td></tr>`).join('')}
  </tbody></table><p class="muted tiny">Read ${esc(fmtDate(pools[0].fetchedAt))}.</p>`;
}

function editEnvironment(d) {
  const md = S.info && S.info.machineDomain;
  const m = modal({
    title: d ? 'Edit environment' : 'Add environment', wide: true,
    body: `<div class="section-title">Active Directory</div>
      <div class="form-grid">
        <label class="field"><span>DNS domain name</span><input id="eName" type="text" value="${esc(d ? d.name : (md ? md.dnsName : ''))}" placeholder="corp.example.com">${!d && md ? '<span class="hint">This computer is a member of ' + esc(md.dnsName) + '.</span>' : ''}</label>
        <label class="field"><span>NetBIOS name</span><input id="eNb" type="text" value="${esc(d ? d.netbios : (md ? md.netbios : ''))}" placeholder="CORP"><span class="hint">Corrected automatically at sign-in.</span></label>
        <label class="field span2"><span>Directory server (domain controller FQDN)</span><input id="eDc" type="text" value="${esc(d ? (d.dcFqdn || d.dfsHostFqdn) : (S.info ? S.info.localFqdn : ''))}"><span class="hint">Runs New-ADUser, Get-ADOrganizationalUnit, Add-ADGroupMember through WinRM.</span></label>
      </div>
      <div class="section-title">DFS namespace</div>
      <div class="form-grid">
        <label class="field"><span>Namespace root</span><input id="eRoot" type="text" value="${esc(d ? d.dfsRoot : '')}" placeholder="\\\\corp.example.com\\Users"><span class="hint">Leave empty to take it from Active Directory at sign-in (Environment → Find namespaces lists all of them).</span></label>
        <label class="field"><span>DFS host (FQDN with the DFSN module)</span><input id="eHost" type="text" value="${esc(d ? d.dfsHostFqdn : (S.info ? S.info.localFqdn : ''))}"></label>
      </div>
      <div class="section-title">Horizon Connection Server</div>
      <div class="form-grid">
        <label class="field"><span>Sign-in</span><select id="eHzAuth"><option value="none" ${!d || d.horizonAuth === 'none' ? 'selected' : ''}>Horizon not used</option><option value="session" ${d && d.horizonAuth === 'session' ? 'selected' : ''}>Use the account I sign in with</option><option value="stored" ${d && d.horizonAuth === 'stored' ? 'selected' : ''}>Dedicated Horizon administrator</option></select></label>
        <label class="field"><span>Connection Server URL</span><input id="eHzUrl" type="text" value="${esc(d ? d.horizonUrl : '')}" placeholder="https://horizon.corp.example.com"></label>
        <label class="field"><span>Administrator (for dedicated account)</span><input id="eHzUser" type="text" value="${esc(d ? d.horizonUser : '')}" placeholder="CORP\\hzadmin"></label>
        <label class="field"><span>Password (stored encrypted for this Windows account)</span><input id="eHzPw" type="password" placeholder="${d && d.hasHorizonPassword ? 'unchanged' : ''}"></label>
        <label class="field"><span>Login domain (NetBIOS, optional)</span><input id="eHzDom" type="text" value="${esc(d ? d.horizonDomain : '')}" placeholder="CORP"></label>
        <label class="field"><span>Entitlement method</span><select id="eHzMode"><option value="group" ${!d || d.horizonEntitleMode !== 'user' ? 'selected' : ''}>Add user to the pool's AD group (recommended)</option><option value="user" ${d && d.horizonEntitleMode === 'user' ? 'selected' : ''}>Direct user entitlement (REST API)</option></select></label>
        <label class="check span2"><input id="eHzCert" type="checkbox" ${d && d.horizonIgnoreCert ? 'checked' : ''}> <span>Ignore certificate errors (self-signed Connection Server certificate)</span></label>
      </div>`,
    footer: [
      { label: 'Cancel', onClick: m => m.close() },
      { label: 'Save', kind: 'primary', onClick: async (m, b) => {
        busy(b, true, 'Saving…');
        try {
          const r = await rpc.call('env.save', { id: d ? d.id : null, name: $('#eName').value, netbios: $('#eNb').value, dfsRoot: $('#eRoot').value, dfsHostFqdn: $('#eHost').value, dcFqdn: $('#eDc').value,
            horizonUrl: $('#eHzUrl').value, horizonAuth: $('#eHzAuth').value, horizonUser: $('#eHzUser').value, horizonDomain: $('#eHzDom').value, horizonIgnoreCert: $('#eHzCert').checked, horizonEntitleMode: $('#eHzMode').value, horizonPassword: $('#eHzPw').value || null });
          m.close(); toast('ok', 'Environment saved'); W.dir = null;
          S.domains = await rpc.call('domains.list');
          if (S.session) { S.domain = S.domains.find(x => x.id === S.domainId) || S.domain; $('#sideDomain').textContent = S.domain ? S.domain.name : ''; if (S.page === 'env') await loadEnv(); else if (S.page === 'wizard') await loadWizard(); }
          else showLogin();
        } catch (ex) { busy(b, false); showError('Not saved', ex); }
      } },
    ],
  });
  setTimeout(() => $('#eName').focus(), 30);
}
