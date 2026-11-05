'use strict';

const app = {
  data: null,
  view: 'overview',
  query: '',
  ecosystem: 'all',
  problemsOnly: false,
  scanning: false,
  drawerKey: null,
  error: null
};

/* ------------------------------------------------------------------ utils */

const $ = (selector, root = document) => root.querySelector(selector);
const view = () => $('#view');

function esc(value) {
  if (value === null || value === undefined) return '';
  return String(value)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

function fmtBytes(bytes) {
  if (bytes === null || bytes === undefined) return '—';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
  return (unit === 0 ? value.toFixed(0) : value.toFixed(1)) + ' ' + units[unit];
}

function fmtRel(iso) {
  if (!iso) return 'never';
  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) return '—';
  const minutes = Math.round((Date.now() - then) / 60000);
  if (minutes < 1) return 'just now';
  if (minutes < 60) return minutes + 'm ago';
  const hours = Math.round(minutes / 60);
  if (hours < 24) return hours + 'h ago';
  const days = Math.round(hours / 24);
  if (days < 31) return days + 'd ago';
  if (days < 365) return Math.round(days / 30) + 'mo ago';
  return Math.round(days / 365) + 'y ago';
}

function truncate(text, width) {
  const value = String(text || '');
  return value.length <= width ? value : '…' + value.slice(-(width - 1));
}

const STATUS_LABEL = {
  satisfied: 'ok',
  missing: 'missing',
  unsatisfied_range: 'wrong version',
  unknown: 'unknown',
  not_managed: 'not managed',
  ignored: 'ignored',
  conflict: 'conflict'
};

function statusClass(status) {
  if (status === 'satisfied') return 'ok';
  if (status === 'missing' || status === 'unsatisfied_range') return 'bad';
  if (status === 'unknown' || status === 'conflict') return 'warn';
  return 'dim';
}

function statusText(tool) {
  if (tool.installed) return 'available ' + tool.installed + (tool.manager ? ' · ' + tool.manager : '');
  if (tool.status === 'unknown') return tool.detail || 'unreadable';
  if (tool.nearest) return 'nearest ' + tool.nearest;
  return 'not installed';
}

function toast(message, bad) {
  const element = $('#toast');
  element.textContent = message;
  element.className = 'toast' + (bad ? ' bad' : '');
  element.hidden = false;
  clearTimeout(toast._timer);
  toast._timer = setTimeout(() => { element.hidden = true; }, bad ? 6000 : 3200);
}

/* ------------------------------------------------------------------ data */

async function load() {
  try {
    const response = await fetch('/api/state', { cache: 'no-store' });
    if (!response.ok) throw new Error('HTTP ' + response.status);
    app.data = await response.json();
    app.error = null;
  } catch (error) {
    app.error = error.message;
  }
  render();
}

async function rescan(measure) {
  if (app.scanning) return;
  app.scanning = true;
  renderScanButton();

  try {
    const response = await fetch('/api/scan', {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'X-DevTools-Request': '1' },
      body: JSON.stringify({ measure: !!measure })
    });

    const body = await response.json().catch(() => ({}));

    if (!response.ok) throw new Error(body.error || 'scan failed');

    await load();
    toast('scanned ' + body.projects + ' projects · ' + body.toolchains + ' toolchains');
  } catch (error) {
    toast(error.message, true);
  } finally {
    app.scanning = false;
    renderScanButton();
  }
}

/* ------------------------------------------------------------------ render */

function render() {
  renderScanButton();

  if (!app.data) {
    view().innerHTML = '<div class="empty"><div class="empty-title">Starting…</div></div>';
    return;
  }

  if (app.error) {
    view().innerHTML =
      '<div class="empty"><div class="empty-title">Cannot reach the index</div>' +
      '<div class="empty-text errors">' + esc(app.error) + '</div>' +
      '<button class="button primary" onclick="load()">Retry</button></div>';
    return;
  }

  const machine = app.data.machine || {};
  const counts = machine.stats || null;

  $('#machine-label').textContent = machine.scannedAt
    ? (machine.hostname || 'local') + ' · scanned ' + fmtRel(machine.scannedAt)
    : (machine.hostname || '');

  $('#count-projects').textContent = counts ? counts.activeProjects : '';
  $('#count-toolchains').textContent = counts ? counts.toolchainsInstalled : '';

  if (machine.empty) {
    view().innerHTML =
      '<div class="empty">' +
      '<div class="empty-title">No index yet</div>' +
      '<div class="empty-text">devtools has not scanned this machine. A scan reads directory names only — ' +
      'it does not open or upload your files.</div>' +
      '<button class="button primary" onclick="rescan(true)">Scan now</button></div>';
    return;
  }

  if (app.view === 'overview') renderOverview();
  else if (app.view === 'projects') renderProjects();
  else if (app.view === 'toolchains') renderToolchains();
  else renderDisk();
}

function renderScanButton() {
  const button = $('#scan-button');
  button.disabled = app.scanning;
  button.innerHTML = app.scanning
    ? '<span class="spinner"></span>Scanning'
    : 'Scan';
}

function filteredProjects() {
  const projects = (app.data.projects || []).filter(p => p.state !== 'suppressed');
  const query = app.query.trim().toLowerCase();

  return projects.filter(project => {
    if (app.problemsOnly && project.problemCount === 0) return false;
    if (app.ecosystem !== 'all' && !(project.ecosystems || []).includes(app.ecosystem)) return false;
    if (!query) return true;
    return (project.name + ' ' + project.path + ' ' + (project.git || '')).toLowerCase().includes(query);
  });
}

/* ------------------------------------------------------------------ overview */

function renderOverview() {
  const stats = app.data.machine.stats || {};
  const projects = app.data.projects || [];
  const missing = app.data.missing || [];
  const conflicts = app.data.conflicts || [];

  const ecosystems = Object.entries(stats.projectsByEcosystem || {})
    .sort((a, b) => b[1] - a[1]);
  const maxEco = Math.max(1, ...ecosystems.map(([, count]) => count));

  const problemCard = stats.projectsWithProblems > 0 ? 'is-bad' : '';
  const reclaimCard = stats.reclaimableBytes ? '' : '';

  view().innerHTML = `
    ${app.data.machine.partial ? '<section class="section"><div class="chip warn">Incomplete scan: unavailable projects may show stale results.</div></section>' : ''}
    <section class="section">
      <div class="cards">
        <div class="card ${problemCard}">
          <div class="card-label">Needs attention</div>
          <div class="card-value">${stats.projectsWithProblems || 0}
            <small>of ${stats.activeProjects || 0} projects</small></div>
          <div class="card-foot">${missing.length} distinct toolchain${missing.length === 1 ? '' : 's'} missing</div>
        </div>
        <div class="card">
          <div class="card-label">Toolchains installed</div>
          <div class="card-value">${stats.toolchainsInstalled || 0}</div>
          <div class="card-foot">${esc(app.data.machine.environment || 'Unknown environment')} · ${stats.unusedToolchains || 0} unused by any project</div>
        </div>
        <div class="card ${reclaimCard}">
          <div class="card-label">Reclaimable</div>
          <div class="card-value">${fmtBytes(stats.reclaimableBytes)}</div>
          <div class="card-foot">build artifacts across projects</div>
        </div>
        <div class="card">
          <div class="card-label">Projects</div>
          <div class="card-value">${stats.activeProjects || 0}</div>
          <div class="card-foot">${stats.suppressedProjects || 0} suppressed as fixtures</div>
        </div>
      </div>
    </section>

    ${missing.length ? `
    <section class="section">
      <div class="section-head">
        <div class="section-title">Missing toolchains</div>
        <div class="section-note">devtools install</div>
      </div>
      <div class="panel">
        ${missing.map(item => `
          <div class="panel-row">
            <span class="chip bad">${esc(item.tool)} ${esc(item.constraint)}</span>
            <span class="dim" style="flex:1">${esc((item.projects || []).slice(0, 5).join(', '))}${item.projects && item.projects.length > 5 ? ' +' + (item.projects.length - 5) : ''}</span>
            <span class="faint mono">${esc(item.suggestedVersion || '')}</span>
          </div>`).join('')}
      </div>
    </section>` : ''}

    <section class="section">
      <div class="section-head">
        <div class="section-title">Ecosystems</div>
        <div class="section-note">${stats.activeProjects || 0} active projects</div>
      </div>
      <div class="panel" style="padding:16px">
        <div class="bars">
          ${ecosystems.length === 0 ? '<span class="dim">no projects detected yet</span>' : ecosystems.map(([name, count]) => `
            <div class="bar-row">
              <span class="dim">${esc(name)}</span>
              <span class="bar-track"><span class="bar-fill" style="width:${Math.max(2, Math.round(count / maxEco * 100))}%"></span></span>
              <span class="dim" style="text-align:right">${count}</span>
            </div>`).join('')}
        </div>
      </div>
    </section>

    ${conflicts.length ? `
    <section class="section">
      <div class="section-head"><div class="section-title">Conflicting pins</div></div>
      <div class="panel">
        ${conflicts.slice(0, 8).map(c => `<div class="panel-row"><span class="chip warn">${esc(c.tool)}</span><span class="dim">${esc(c.text)}</span></div>`).join('')}
      </div>
    </section>` : ''}

    <section class="section">
      <div class="section-head">
        <div class="section-title">Recently touched</div>
        <div class="section-note">${projects.length} projects indexed</div>
      </div>
      <div class="panel table-wrap">
        <table class="grid">
          <thead><tr><th>Project</th><th>Ecosystems</th><th>Toolchain status</th><th>Last activity</th></tr></thead>
          <tbody>
            ${projects.slice(0, 8).map(project => `
              <tr data-key="${esc(project.key)}">
                <td><span class="project-name">${esc(project.name)}</span>${project.stale ? ' <span class="chip warn">stale scan</span>' : ''}<div class="path">${esc(truncate(project.path, 62))}</div></td>
                <td><div class="chips">${(project.ecosystems || []).map(e => `<span class="chip eco">${esc(e)}</span>`).join('')}</div></td>
                <td>
                  <div class="chips">
                    ${(project.tools || []).slice(0, 4).map(tool =>
                      `<span class="chip ${statusClass(tool.status)}">${esc(tool.tool)} ${esc(tool.installed || tool.required)}</span>`).join('')}
                    ${(project.tools || []).length === 0 ? '<span class="chip ghost">no requirements</span>' : ''}
                  </div>
                </td>
                <td class="dim mono nowrap">${fmtRel(project.lastActivity)}</td>
              </tr>`).join('')}
          </tbody>
        </table>
      </div>
    </section>`;
}

/* ------------------------------------------------------------------ projects */

function renderProjects() {
  const projects = filteredProjects();
  const ecosystems = ['all', ...new Set((app.data.projects || [])
    .flatMap(p => p.ecosystems || []))].sort();

  view().innerHTML = `
    <section class="section">
      <div class="filters">
        ${ecosystems.map(ecosystem =>
          `<button class="filter ${app.ecosystem === ecosystem ? 'is-active' : ''}" data-eco="${esc(ecosystem)}">${esc(ecosystem)}</button>`).join('')}
        <span class="grow"></span>
        <button class="filter ${app.problemsOnly ? 'is-active' : ''}" data-problems="1">problems only</button>
      </div>

      <div class="panel table-wrap">
        <table class="grid">
          <thead>
            <tr><th>Project</th><th>Ecosystems</th><th>Toolchains</th><th>Status</th><th>Last</th><th>Path</th></tr>
          </thead>
          <tbody>
            ${projects.length === 0 ? '<tr><td colspan="6" class="dim" style="padding:26px;text-align:center">no projects match</td></tr>' : ''}
            ${projects.map(project => `
              <tr data-key="${esc(project.key)}">
                <td><span class="project-name">${esc(project.name)}</span>${project.stale ? ' <span class="chip warn">stale scan</span>' : ''}${project.incomplete ? ' <span class="chip warn">incomplete</span>' : ''}</td>
                <td><div class="chips">${(project.ecosystems || []).map(e => `<span class="chip eco">${esc(e)}</span>`).join('')}</div></td>
                <td><div class="chips">
                  ${(project.tools || []).map(tool =>
                    `<div><span class="chip ${statusClass(tool.status)}">${esc(tool.tool)} ${esc(tool.required)} · ${esc(statusText(tool))}</span><div class="dim">${esc(tool.source || '')}</div></div>`).join('')}
                  ${(project.tools || []).length === 0 ? '<span class="chip ghost">—</span>' : ''}
                </div></td>
                <td class="nowrap">${project.problemCount > 0
                  ? `<span class="chip bad">${project.problemCount} issue${project.problemCount === 1 ? '' : 's'}</span>`
                  : '<span class="chip ok">ok</span>'}</td>
                <td class="dim mono nowrap">${fmtRel(project.lastActivity)}</td>
                <td class="path">${esc(truncate(project.path, 50))}</td>
              </tr>`).join('')}
          </tbody>
        </table>
      </div>
      <div class="section-note" style="margin-top:10px">${projects.length} shown</div>
    </section>`;
}

/* ------------------------------------------------------------------ toolchains */

function renderToolchains() {
  const toolchains = (app.data.toolchains || []).slice().sort((a, b) =>
    a.tool.localeCompare(b.tool) || b.version.localeCompare(a.version, undefined, { numeric: true }));

  const byTool = {};
  for (const toolchain of toolchains) {
    byTool[toolchain.tool] = byTool[toolchain.tool] || [];
    byTool[toolchain.tool].push(toolchain);
  }

  view().innerHTML = `
    <section class="section">
      <div class="section-head">
        <div class="section-title">Installed toolchains</div>
        <div class="section-note">${toolchains.length} total · ${Object.keys(byTool).length} tools</div>
      </div>
      <div class="panel table-wrap">
        <table class="grid">
          <thead><tr><th>Tool</th><th>Version</th><th>Manager</th><th>Used by</th><th>Health</th><th>Path</th></tr></thead>
          <tbody>
            ${toolchains.map(t => `
              <tr style="cursor:default">
                <td class="mono">${esc(t.tool)}</td>
                <td class="mono">${esc(t.version)}${t.globalDefault ? ' <span class="chip ghost">default</span>' : ''}</td>
                <td class="dim">${esc(t.manager || '—')}</td>
                <td>${t.usedBy > 0 ? `<span class="chip ok">${t.usedBy}</span>` : '<span class="chip ghost">unused</span>'}</td>
                <td>${t.health === 'ok' ? '<span class="chip ok">ok</span>' : `<span class="chip warn">${esc(t.health)}</span>`}</td>
                <td class="path">${esc(truncate(t.path, 60))}</td>
              </tr>`).join('')}
            ${toolchains.length === 0 ? '<tr><td colspan="6" class="dim" style="padding:26px;text-align:center">no toolchains discovered</td></tr>' : ''}
          </tbody>
        </table>
      </div>
    </section>`;
}

/* ------------------------------------------------------------------ disk */

function renderDisk() {
  const projects = (app.data.projects || [])
    .filter(p => p.size !== null && p.size !== undefined)
    .sort((a, b) => (b.size || 0) - (a.size || 0));

  if (projects.length === 0) {
    view().innerHTML = `
      <div class="empty">
        <div class="empty-title">No size data yet</div>
        <div class="empty-text">Measuring disk usage walks every project, so it is opt-in.</div>
        <button class="button primary" onclick="rescan(true)">Scan with measurement</button>
      </div>`;
    return;
  }

  const total = projects.reduce((sum, p) => sum + (p.size || 0), 0);
  const artifacts = projects.reduce((sum, p) => sum + (p.artifacts || 0), 0);
  const max = Math.max(1, ...projects.map(p => p.size || 0));

  view().innerHTML = `
    <section class="section">
      <div class="cards">
        <div class="card"><div class="card-label">Measured</div><div class="card-value">${fmtBytes(total)}</div><div class="card-foot">${projects.length} projects</div></div>
        <div class="card"><div class="card-label">Build artifacts</div><div class="card-value">${fmtBytes(artifacts)}</div><div class="card-foot">node_modules, target, obj, dist</div></div>
      </div>
    </section>

    <section class="section">
      <div class="section-head">
        <div class="section-title">Largest projects</div>
        <div class="section-note">devtools prune</div>
      </div>
      <div class="panel" style="padding:16px">
        <div class="bars">
          ${projects.slice(0, 25).map(p => `
            <div class="bar-row" style="grid-template-columns:200px 1fr 110px">
              <span class="dim" style="overflow:hidden;text-overflow:ellipsis">${esc(p.name)}</span>
              <span class="bar-track"><span class="bar-fill ${p.artifacts > (p.size || 0) * 0.5 ? 'alt' : ''}" style="width:${Math.max(2, Math.round((p.size || 0) / max * 100))}%"></span></span>
              <span class="dim" style="text-align:right">${fmtBytes(p.size)}${p.artifacts ? ' <span class="faint">(' + fmtBytes(p.artifacts) + ')</span>' : ''}</span>
            </div>`).join('')}
        </div>
      </div>
    </section>`;
}

/* ------------------------------------------------------------------ drawer */

async function openDrawer(key) {
  app.drawerKey = key;
  const drawer = $('#drawer');
  drawer.classList.add('is-open');
  drawer.setAttribute('aria-hidden', 'false');
  $('#backdrop').hidden = false;
  drawer.innerHTML = '<div class="empty"><div class="empty-title">Loading…</div></div>';

  try {
    const response = await fetch('/api/project?key=' + encodeURIComponent(key), { cache: 'no-store' });
    if (!response.ok) throw new Error('project not found');
    renderDrawer(await response.json());
  } catch (error) {
    drawer.innerHTML = '<div class="empty"><div class="empty-text errors">' + esc(error.message) + '</div></div>';
  }
}

function renderDrawer(payload) {
  const project = payload.project;
  const env = payload.env || { set: {}, pathPrepend: [], notes: [] };
  const envLines = [
    ...Object.entries(env.set || {}).map(([k, v]) => 'export ' + k + "='" + v + "'"),
    ...(env.pathPrepend || []).map(p => 'export PATH=\'' + p + ':$PATH\'')
  ].join('\n');

  $('#drawer').innerHTML = `
    <div class="drawer-head">
      <div>
        <h2 class="drawer-title">${esc(project.name)}</h2>
        <div class="path">${esc(project.path)}</div>
      </div>
      <button class="drawer-close" data-action="close" aria-label="Close">✕</button>
    </div>

    <div style="margin:18px 0 22px">
      <dl class="kv">
        ${project.git ? `<dt>git</dt><dd>${esc(project.git)}${project.branch ? ' <span class="faint">(' + esc(project.branch) + ')</span>' : ''}</dd>` : ''}
        <dt>ecosystems</dt><dd>${(project.ecosystems || []).map(e => `<span class="chip eco">${esc(e)}</span>`).join(' ') || '<span class="faint">none</span>'}</dd>
        <dt>last activity</dt><dd class="dim">${fmtRel(project.lastActivity)}</dd>
        <dt>confidence</dt><dd class="dim mono">${project.confidence}</dd>
        ${project.size ? `<dt>disk</dt><dd class="dim mono">${fmtBytes(project.size)}${project.artifacts ? ' <span class="faint">(' + fmtBytes(project.artifacts) + ' artifacts)</span>' : ''}</dd>` : ''}
      </dl>
    </div>

    <div class="section-title" style="margin-bottom:8px">Requirements</div>
    <div class="panel" style="padding:4px 16px;margin-bottom:22px">
      ${(project.tools || []).length === 0 ? '<div class="dim" style="padding:14px 0">No toolchain requirements declared.</div>' : ''}
      ${(project.tools || []).map(tool => `
        <div class="tool-row">
          <div class="tool-name">${esc(tool.tool)}</div>
          <div>
            <div class="tool-req">${esc(tool.required)}</div>
            <div class="tool-source">${esc(tool.source || '')}${tool.detail ? ' · ' + esc(tool.detail) : ''}</div>
          </div>
          <div class="tool-status ${statusClass(tool.status)}">
            ${tool.status === 'satisfied' ? '✔ ' : (tool.status === 'unknown' ? '? ' : (tool.status === 'missing' || tool.status === 'unsatisfied_range' ? '✘ ' : '· '))}
            ${esc(statusText(tool))}
          </div>
        </div>`).join('')}
    </div>

    ${envLines ? `
    <div class="section-title" style="margin-bottom:8px">Environment</div>
    <pre class="env">${esc(envLines)}</pre>
    ${(env.notes || []).length ? `<div class="faint" style="margin-top:8px;font-size:12px">${env.notes.map(esc).join('<br>')}</div>` : ''}
    ` : ''}
  `;
}

function closeDrawer() {
  app.drawerKey = null;
  $('#drawer').classList.remove('is-open');
  $('#drawer').setAttribute('aria-hidden', 'true');
  $('#backdrop').hidden = true;
}

/* ------------------------------------------------------------------ events */

document.addEventListener('click', event => {
  const tab = event.target.closest('.tab');
  if (tab) {
    app.view = tab.dataset.view;
    document.querySelectorAll('.tab').forEach(el => el.classList.toggle('is-active', el === tab));
    render();
    return;
  }

  const eco = event.target.closest('[data-eco]');
  if (eco) {
    app.ecosystem = eco.dataset.eco;
    render();
    return;
  }

  if (event.target.closest('[data-problems]')) {
    app.problemsOnly = !app.problemsOnly;
    render();
    return;
  }

  if (event.target.closest('[data-action="close"]')) {
    closeDrawer();
    return;
  }

  const row = event.target.closest('tr[data-key]');
  if (row) {
    openDrawer(row.dataset.key);
  }
});

$('#backdrop').addEventListener('click', closeDrawer);

$('#scan-button').addEventListener('click', () => rescan(app.view === 'disk'));

$('#search').addEventListener('input', event => {
  app.query = event.target.value;
  if (app.view !== 'projects') {
    app.view = 'projects';
    document.querySelectorAll('.tab').forEach(el => el.classList.toggle('is-active', el.dataset.view === 'projects'));
  }
  render();
});

document.addEventListener('keydown', event => {
  if (event.key === 'Escape') closeDrawer();

  if (event.key === '/' && document.activeElement !== $('#search')) {
    event.preventDefault();
    $('#search').focus();
  }
});

load();
setInterval(() => {
  if (app.data && app.data.machine && app.data.machine.scannedAt) {
    $('#machine-label').textContent =
      (app.data.machine.hostname || 'local') + ' · scanned ' + fmtRel(app.data.machine.scannedAt);
  }
}, 30000);
