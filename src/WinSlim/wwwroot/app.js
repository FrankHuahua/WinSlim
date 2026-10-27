'use strict';

const REPO_URL = 'https://github.com/OWNER/win-slim';

/* ---------- 与后台通信 ---------- */
const host = (window.chrome && window.chrome.webview) || window.__winslimHost;
const rpc = { n: 0, pending: new Map(), listeners: new Set() };
host.addEventListener('message', (e) => {
  const m = typeof e.data === 'string' ? JSON.parse(e.data) : e.data;
  if (m.event) { rpc.listeners.forEach((f) => f(m.event, m.data)); return; }
  const p = rpc.pending.get(m.id);
  if (!p) return;
  rpc.pending.delete(m.id);
  if (m.ok) p.resolve(m.result); else p.reject(new Error(m.error || '操作失败'));
});
function call(method, params) {
  return new Promise((resolve, reject) => {
    const id = String(++rpc.n);
    rpc.pending.set(id, { resolve, reject });
    host.postMessage({ id, method, params });
  });
}
rpc.listeners.add((event, data) => {
  if (event === 'progress' && S.busy) { S.busy = { ...S.busy, ...data }; renderTray(); }
});

/* ---------- 状态 ---------- */
const S = {
  catalog: null,
  system: null,
  status: new Map(),      // 优化项 id → { state, hasBackup }
  staged: new Map(),      // 优化项 id → 'apply' | 'revert'
  apps: null,             // [{ package, version, installed, provisioned }]
  appSel: new Set(),
  deprovision: true,
  scan: null,             // id → { bytes, measurable }
  cleanSel: new Set(['user-temp', 'windows-temp', 'update-cache', 'delivery-cache', 'error-reports', 'thumbnails']),
  busy: null,
  notices: [],
  tab: null,
  filter: 'all',
  open: new Set(),
  query: '',
  restorePoint: true,
};

const LEVEL = { safe: '安全', moderate: '需留意', high: '高风险' };
const RESTART = { none: '立即生效', explorer: '需重启资源管理器', logoff: '需注销后生效', reboot: '需重启电脑' };
const RESTART_ORDER = ['none', 'explorer', 'logoff', 'reboot'];

const $ = (sel, root = document) => root.querySelector(sel);
const view = $('#view');
const tray = $('#tray');
const dialog = $('#dialog');

function esc(s) {
  return String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}
function bytes(n, plain = false) {
  if (n == null) return plain ? '—' : '<span class="muted">—</span>';
  const gb = n / 2 ** 30;
  const [v, u] = gb >= 1 ? [gb.toFixed(1), 'GB'] : [String(Math.round(n / 2 ** 20)), 'MB'];
  return plain ? `${v} ${u}` : `<span class="num">${v}</span><span class="unit">${u}</span>`;
}
function duration(sec) {
  const d = Math.floor(sec / 86400), h = Math.floor((sec % 86400) / 3600), m = Math.floor((sec % 3600) / 60);
  return d > 0 ? `${d} 天 ${h} 小时` : h > 0 ? `${h} 小时 ${m} 分` : `${m} 分钟`;
}
const tweaks = () => S.catalog?.tweaks ?? [];
const stateOf = (t) => S.status.get(t.id)?.state ?? null;
const isApplied = (t) => stateOf(t) === 'applied';
const category = (id) => S.catalog.categories.find((c) => c.id === id);
const categoriesInUse = () => S.catalog.categories.filter((c) => tweaks().some((t) => t.category === c.id));
const strongestRestart = (list) => list.reduce((a, t) => (RESTART_ORDER.indexOf(t.restart) > RESTART_ORDER.indexOf(a) ? t.restart : a), 'none');

/* ---------- 路由 ---------- */
function route() {
  const [, name = 'overview', arg] = location.hash.split('/');
  return { name, arg };
}
window.addEventListener('hashchange', () => { view.scrollTop = 0; render(); });

/* ---------- 渲染 ---------- */
function render() {
  if (!S.catalog) return;
  const r = route();
  document.querySelectorAll('#nav a').forEach((a) => {
    if (a.dataset.route === r.name) a.setAttribute('aria-current', 'page');
    else a.removeAttribute('aria-current');
  });
  const staged = $('#nav-staged');
  staged.hidden = S.staged.size === 0;
  staged.textContent = `待处理 ${S.staged.size}`;

  const top = view.scrollTop;
  const html = {
    overview: renderOverview,
    tweaks: () => renderTweaks(r.arg),
    apps: renderApps,
    cleanup: renderCleanup,
    history: renderHistory,
  }[r.name]?.() ?? renderOverview();
  view.innerHTML = noticesHtml() + html;
  view.scrollTop = top;
  renderTray();
}

function noticesHtml() {
  return S.notices.map((n, i) => `
    <div class="notice" role="status">
      <span>${esc(n.text)}</span>
      ${n.action ? `<button class="link" data-action="notice-act" data-i="${i}">${esc(n.action.label)}</button>` : ''}
      <button class="link" data-action="notice-close" data-i="${i}">知道了</button>
    </div>`).join('');
}

function renderOverview() {
  const all = tweaks();
  const rec = all.filter((t) => t.recommended);
  const recPending = rec.filter((t) => !isApplied(t));
  const applied = all.filter(isApplied).length;
  const loading = S.status.size === 0;
  const installedApps = S.apps ? S.catalog.apps.filter((a) => S.apps.find((x) => x.package === a.package)?.installed) : null;
  const cleanable = S.scan ? Object.values(S.scan).reduce((s, x) => s + (x.bytes || 0), 0) : null;

  const lede = loading ? '正在读取这台电脑的当前设置……' : [
    recPending.length ? `推荐的 ${rec.length} 项优化里还有 <b>${recPending.length} 项</b>没做` : `推荐的 ${rec.length} 项优化都已经做了`,
    installedApps ? (installedApps.length ? `装着 <b>${installedApps.length} 个</b>可以卸载的预装应用` : '没有发现可以卸载的预装应用') : null,
    cleanable != null ? `大约 <b>${bytes(cleanable, true)}</b> 可以清理` : null,
  ].filter(Boolean).join('；') + '。';

  const ledgerRows = categoriesInUse().map((c) => {
    const list = all.filter((t) => t.category === c.id);
    const done = list.filter(isApplied).length;
    const stagedApply = list.filter((t) => S.staged.get(t.id) === 'apply').length;
    const recLeft = list.filter((t) => t.recommended && !isApplied(t)).length;
    const pct = (n) => (100 * n / list.length).toFixed(2) + '%';
    return `
      <li>
        <span class="name">${esc(c.title)}<small>${loading ? '' : recLeft ? `推荐未做 ${recLeft} 项` : '推荐项已做完'}</small></span>
        <span class="track" aria-label="已优化 ${done} 项，共 ${list.length} 项"><b style="width:${pct(done)}"></b><s style="left:${pct(done)};width:${pct(stagedApply)}"></s></span>
        <span class="fig num">${loading ? '<span class="skeleton"></span>' : `${done} <span class="muted">/ ${list.length}</span>`}</span>
        <span class="go"><a class="link muted" href="#/tweaks/${c.id}">查看</a></span>
      </li>`;
  }).join('');

  const sys = S.system;
  const memPct = sys ? (100 * sys.memoryUsed / sys.memoryTotal) : 0;
  const diskUsedPct = sys ? (100 * (sys.diskTotal - sys.diskFree) / sys.diskTotal) : 0;

  const cleanRows = S.scan ? S.catalog.cleaners
    .filter((c) => S.scan[c.id]?.measurable)
    .sort((a, b) => S.scan[b.id].bytes - S.scan[a.id].bytes)
    .slice(0, 4)
    .map((c) => `<li><span>${esc(c.title)}</span><span class="fig">${bytes(S.scan[c.id].bytes)}</span></li>`).join('') : '';

  const recent = all.filter((t) => S.status.get(t.id)?.hasBackup).slice(0, 5);

  return `
    <div class="title-row">
      <div>
        <h1>概览</h1>
        <p class="lede">${lede}</p>
      </div>
      <button class="btn primary" data-action="stage-recommended" ${recPending.length && !loading ? '' : 'disabled'}>
        ${recPending.length ? `检查并应用 ${recPending.length} 项推荐优化` : '推荐项都已应用'}
      </button>
    </div>

    <div class="grid-overview">
      <section class="card" aria-labelledby="ledger-h">
        <div class="card-head"><h2 id="ledger-h">减负账本</h2><span class="meta num">${loading ? '' : `共 ${all.length} 项，已优化 ${applied} 项`}</span></div>
        <div class="answer">
          <span class="big num">${loading ? '<span class="skeleton" style="height:28px;width:40px"></span>' : recPending.length}</span>
          <p>${loading ? '' : recPending.length ? '项推荐优化还没做。推荐项只包含没有明显副作用的改动。' : '项推荐优化待处理。可以按需看看“需留意”的项。'}</p>
        </div>
        <div class="card-body"><ul class="ledger">${ledgerRows}</ul></div>
      </section>

      <div class="stack">
        <section class="card" aria-labelledby="pc-h">
          <div class="card-head"><h2 id="pc-h">这台电脑</h2><span class="meta">${esc(sys?.computer ?? '')}</span></div>
          <div class="card-body">
            ${sys ? `
            <ul class="kv">
              <li><span>系统</span><span>${esc(sys.os)}</span></li>
              <li><span>处理器</span><span title="${esc(sys.cpu)}">${esc(sys.cpu)}</span></li>
              <li><span>进程</span><span class="num">${sys.processCount}</span></li>
              <li><span>已运行</span><span>${duration(sys.uptimeSeconds)}</span></li>
            </ul>
            <div style="margin-top:10px">
              <div class="meter-cap"><span>内存已用</span><span class="num">${bytes(sys.memoryUsed, true)} / ${bytes(sys.memoryTotal, true)}</span></div>
              <div class="meter"><b style="width:${memPct.toFixed(1)}%"></b></div>
              <div class="meter-cap"><span>系统盘 ${esc(sys.systemDrive)} 已用</span><span class="num">剩余 ${bytes(sys.diskFree, true)} / ${bytes(sys.diskTotal, true)}</span></div>
              <div class="meter"><b style="width:${diskUsedPct.toFixed(1)}%"></b></div>
            </div>` : '<p class="muted">正在读取……</p>'}
          </div>
        </section>

        <section class="card" aria-labelledby="clean-h">
          <div class="card-head"><h2 id="clean-h">可清理</h2><a class="link muted" href="#/cleanup">去清理</a></div>
          <div class="card-body">
            ${S.scan ? `<div class="answer" style="padding:0 0 8px;border:0"><span class="big">${bytes(cleanable)}</span></div><ul class="rows">${cleanRows}</ul>` : '<p class="muted">正在扫描临时文件和缓存……</p>'}
          </div>
        </section>
      </div>
    </div>

    <div class="grid-two">
      <section class="card" aria-labelledby="apps-h">
        <div class="card-head"><h2 id="apps-h">预装应用</h2><span class="meta num">${installedApps ? `已安装 ${installedApps.length} 个` : ''}</span></div>
        <div class="card-body">
          ${installedApps == null ? '<p class="muted">正在列出已安装的应用……</p>'
            : installedApps.length ? `<ul class="rows">${installedApps.slice(0, 5).map((a) => `<li><span>${esc(a.name)}</span><span class="muted">${esc(S.catalog.appGroups.find((g) => g.id === a.group)?.title ?? '')}</span></li>`).join('')}</ul>`
            : '<p class="empty">目录里的预装应用在这台电脑上都已经卸载了。</p>'}
          <div class="card-foot"><a class="link" href="#/apps">${installedApps?.length > 5 ? `查看全部 ${installedApps.length} 个` : '管理预装应用'}</a></div>
        </div>
      </section>

      <section class="card" aria-labelledby="recent-h">
        <div class="card-head"><h2 id="recent-h">WinSlim 做过的改动</h2><span class="meta num">${loading ? '' : `${all.filter((t) => S.status.get(t.id)?.hasBackup).length} 项可还原`}</span></div>
        <div class="card-body">
          ${recent.length ? `<ul class="rows">${recent.map((t) => `<li><span>${esc(t.title)}</span><span class="muted">${esc(category(t.category)?.title)}</span></li>`).join('')}</ul>`
            : '<p class="empty">还没有通过 WinSlim 做过改动。每应用一项，都会先备份原来的值，可以随时还原。</p>'}
          <div class="card-foot"><a class="link" href="#/history">查看改动记录</a></div>
        </div>
      </section>
    </div>`;
}

function filteredTweaks(cat) {
  const q = S.query.trim().toLowerCase();
  let list = q
    ? tweaks().filter((t) => [t.title, t.desc, t.effect, t.risk].some((s) => s.toLowerCase().includes(q)))
    : tweaks().filter((t) => t.category === cat);
  if (S.filter === 'recommended') list = list.filter((t) => t.recommended);
  if (S.filter === 'pending') list = list.filter((t) => !isApplied(t));
  return list;
}

function renderTweaks(arg) {
  const cats = categoriesInUse();
  const cat = cats.find((c) => c.id === arg) ? arg : (S.tab && cats.find((c) => c.id === S.tab) ? S.tab : cats[0].id);
  S.tab = cat;
  const q = S.query.trim();
  const list = filteredTweaks(cat);
  const c = category(cat);
  const all = tweaks();
  const recHere = list.filter((t) => t.recommended && !isApplied(t) && !S.staged.has(t.id));

  return `
    <div class="title-row">
      <div>
        <h1>${q ? `搜索“${esc(q)}”` : '优化项'}</h1>
        <p class="lede">${q ? `找到 ${list.length} 项。` : `共 ${all.length} 项适用于这台电脑的 ${esc(S.catalog.os.label)}，已优化 ${all.filter(isApplied).length} 项。打开开关只是加入待处理，点击底部的“检查并应用”之后才会生效。`}</p>
      </div>
      <div class="segmented" role="group" aria-label="筛选">
        ${[['all', '全部'], ['recommended', '推荐'], ['pending', '未优化']].map(([k, v]) => `<button data-action="filter" data-v="${k}" aria-pressed="${S.filter === k}">${v}</button>`).join('')}
      </div>
    </div>

    ${q ? '' : `
    <div class="tabs" role="tablist">
      ${cats.map((x) => `<button role="tab" data-action="tab" data-v="${x.id}" aria-selected="${x.id === cat}">${esc(x.title)}<span class="num">${all.filter((t) => t.category === x.id).length}</span></button>`).join('')}
    </div>
    <div class="toolbar">
      <span class="summary">${esc(c.summary)}</span>
      ${recHere.length ? `<button class="link" data-action="stage-list" data-ids="${recHere.map((t) => t.id).join(',')}">把这里的 ${recHere.length} 项推荐加入待处理</button>` : ''}
    </div>`}

    ${list.length ? `<div class="list">${list.map(itemHtml).join('')}</div>`
      : `<p class="empty">${q ? '没有匹配的优化项。可以换个关键词，例如“广告”“任务栏”“休眠”。' : S.filter === 'pending' ? '这一类都已经优化了。' : '这一类没有符合筛选条件的项。'}</p>`}`;
}

function itemHtml(t) {
  const st = S.status.get(t.id);
  const state = st?.state;
  const staged = S.staged.get(t.id);
  const checked = staged ? staged === 'apply' : state === 'applied';
  const open = S.open.has(t.id);
  const stateText = state == null ? '<span class="skeleton"></span>'
    : state === 'applied' ? '<span class="state applied">已优化</span>'
    : state === 'partial' ? '<span class="state">部分生效</span>'
    : '<span class="state">系统默认</span>';
  const osNote = t.os === '11' ? '仅 Windows 11' : t.os === '10' ? '仅 Windows 10' : '';
  return `
    <div class="item" data-id="${t.id}" data-level="${t.level}" data-open="${open}">
      <div class="item-row" data-action="toggle-open" data-id="${t.id}">
        <span class="chev" aria-hidden="true"><svg width="12" height="12" viewBox="0 0 12 12" fill="none" stroke="currentColor" stroke-width="1.5"><path d="m4.5 2.5 3.5 3.5-3.5 3.5"/></svg></span>
        <div class="item-title"><b>${esc(t.title)}</b><span class="sub">${esc(t.desc)}</span></div>
        <div class="item-tags"><span class="pill ${t.level}">${LEVEL[t.level]}</span>${t.recommended ? '<span class="pill safe">推荐</span>' : ''}</div>
        <div class="item-state">${stateText}</div>
        <div class="item-staged">${staged ? `<span class="staged">${staged === 'apply' ? '待应用' : '待还原'}</span>` : `<span class="state">${RESTART[t.restart]}</span>`}</div>
        <label class="switch" title="${checked ? '关闭以还原' : '打开以应用'}">
          <input type="checkbox" data-action="stage" data-id="${t.id}" ${checked ? 'checked' : ''} ${state == null || S.busy ? 'disabled' : ''} aria-label="${esc(t.title)}">
          <i></i>
        </label>
      </div>
      ${open ? `
      <div class="item-detail">
        <div class="explain">
          <div><h3>这是什么</h3><p>${esc(t.desc)}</p></div>
          <div><h3>效果</h3><p>${esc(t.effect)}</p></div>
          <div class="risk"><h3>${t.level === 'safe' ? '代价' : t.level === 'high' ? '后果（高风险）' : '后果（需留意）'}</h3><p>${esc(t.risk)}</p></div>
        </div>
        <div class="changes">
          <h3>改动内容${osNote ? ` · ${osNote}` : ''} · ${RESTART[t.restart]}</h3>
          <ul class="mono">${t.changes.map((x) => `<li>${esc(x)}</li>`).join('')}</ul>
        </div>
        <div class="detail-actions">
          ${staged
            ? `<button class="btn" data-action="unstage" data-id="${t.id}">移出待处理</button><span class="note">点击底部的“检查改动并应用”后才会真正修改。</span>`
            : state === 'applied'
            ? `<button class="btn" data-action="stage-one" data-id="${t.id}" data-mode="revert">还原这一项</button><span class="note">${st?.hasBackup ? '还原时写回应用前备份的原值。' : '这一项不是 WinSlim 改的，还原时按 Windows 默认值处理。'}</span>`
            : `<button class="btn" data-action="stage-one" data-id="${t.id}" data-mode="apply">加入待处理</button><span class="note">应用前会备份当前的值。</span>`}
        </div>
      </div>` : ''}
    </div>`;
}

function renderApps() {
  const appsState = (pkg) => S.apps?.find((x) => x.package === pkg);
  const installed = S.apps ? S.catalog.apps.filter((a) => appsState(a.package)?.installed) : [];
  const sections = S.catalog.appGroups.map((g) => {
    const rows = S.catalog.apps.filter((a) => a.group === g.id);
    return `
      <section class="section" aria-labelledby="g-${g.id}">
        <h2 id="g-${g.id}">${esc(g.title)}</h2>
        <table>
          <thead><tr><th class="sel"><span class="muted" hidden>选择</span></th><th style="width:28%">应用</th><th>说明</th><th style="width:84px">风险</th><th class="act">状态</th></tr></thead>
          <tbody>
            ${rows.map((a) => {
              const st = appsState(a.package);
              const inst = st?.installed;
              return `
              <tr data-installed="${S.apps ? !!inst : 'unknown'}">
                <td class="sel">${inst ? `<input type="checkbox" class="check" data-action="app-sel" data-pkg="${esc(a.package)}" ${S.appSel.has(a.package) ? 'checked' : ''} aria-label="选择 ${esc(a.name)}">` : ''}</td>
                <td class="name"><b>${esc(a.name)}</b><span class="mono">${esc(a.package)}</span></td>
                <td class="note">${esc(a.note)}</td>
                <td><span class="pill ${a.level}">${LEVEL[a.level]}</span></td>
                <td class="act">${!S.apps ? '<span class="skeleton"></span>'
                  : inst ? `<span class="state applied">已安装</span>`
                  : a.store ? `<button class="link muted" data-action="reinstall" data-pkg="${esc(a.package)}">重新安装</button>`
                  : '<span class="state">未安装</span>'}</td>
              </tr>`;
            }).join('')}
          </tbody>
        </table>
      </section>`;
  }).join('');

  return `
    <div class="title-row">
      <div>
        <h1>预装应用</h1>
        <p class="lede">${S.apps ? `目录里收录了 ${S.catalog.apps.length} 个常见的预装应用，这台电脑上装了 <b>${installed.length} 个</b>。` : '正在列出已安装的应用……'}卸载的是当前用户的副本，卸错了也可以从微软商店重新安装。</p>
      </div>
      ${installed.length ? `<button class="btn" data-action="app-select-safe">选中所有“安全”的应用</button>` : ''}
    </div>
    ${sections}`;
}

function renderCleanup() {
  const sys = S.system;
  const total = S.scan ? Object.values(S.scan).reduce((s, x) => s + (x.bytes || 0), 0) : null;
  return `
    <div class="title-row">
      <div>
        <h1>磁盘清理</h1>
        <p class="lede">${sys ? `系统盘 ${esc(sys.systemDrive)} 剩余 ${bytes(sys.diskFree, true)}，共 ${bytes(sys.diskTotal, true)}。` : ''}${total != null ? `扫描到大约 <b>${bytes(total, true)}</b> 可以清理。` : '正在扫描……'}正在使用的文件会自动跳过。</p>
      </div>
      <button class="btn" data-action="rescan" ${S.busy ? 'disabled' : ''}>重新扫描</button>
    </div>
    <div class="list">
      ${S.catalog.cleaners.map((c) => {
        const sc = S.scan?.[c.id];
        return `
        <label class="clean-row">
          <input type="checkbox" class="check" data-action="clean-sel" data-id="${c.id}" ${S.cleanSel.has(c.id) ? 'checked' : ''}>
          <div>
            <h3>${esc(c.title)}</h3>
            <p>${esc(c.desc)}${esc(c.effect)}</p>
            <p class="warn"><b>注意：</b>${esc(c.risk)}</p>
          </div>
          <span class="size">${!S.scan ? '<span class="skeleton"></span>' : sc?.measurable ? bytes(sc.bytes) : '<span class="muted" style="font-size:13px">清理时计算</span>'}</span>
        </label>`;
      }).join('')}
    </div>`;
}

function renderHistory() {
  const changed = tweaks().filter((t) => S.status.get(t.id)?.hasBackup);
  return `
    <div class="title-row">
      <div>
        <h1>改动记录</h1>
        <p class="lede">WinSlim 改过的每一项都备份了原来的值，可以一项一项还原，也可以一次全部还原。</p>
      </div>
    </div>

    <div class="setting">
      <div><h2>WinSlim 做过的改动</h2><p>还原时写回应用前的原值，而不是笼统的“默认值”。</p></div>
      <div class="panel">
        ${changed.length ? `
          <ul class="rows">${changed.map((t) => `
            <li><span>${esc(t.title)}</span><span class="muted">${esc(category(t.category)?.title)}</span>
            <span class="fig">${S.staged.get(t.id) === 'revert' ? '<span class="staged">待还原</span>' : `<button class="link" data-action="stage-one" data-id="${t.id}" data-mode="revert">还原</button>`}</span></li>`).join('')}
          </ul>
          <div style="margin-top:14px"><button class="btn" data-action="stage-revert-all">全部还原（${changed.length} 项）</button></div>`
        : '<p class="empty">还没有通过 WinSlim 做过改动。</p>'}
      </div>
    </div>

    <div class="setting">
      <div><h2>系统还原点</h2><p>还原点是 Windows 自带的整机设置快照，可以在 WinSlim 打不开时也恢复系统。</p></div>
      <div class="panel">
        <p>默认每次应用优化前都会自动创建一个还原点。也可以现在手动创建一个。系统保护没有开启时会创建失败，WinSlim 不会替你开启它。</p>
        <button class="btn" data-action="restore-point" ${S.busy ? 'disabled' : ''}>立即创建还原点</button>
        <button class="link muted" data-action="open-restore" style="margin-left:12px">打开“系统还原”</button>
      </div>
    </div>

    <div class="setting">
      <div><h2>操作日志</h2><p>每次应用、还原、卸载和清理都会记录时间和结果。</p></div>
      <div class="panel">
        <p class="mono">C:\\ProgramData\\WinSlim\\winslim.log</p>
        <button class="btn" data-action="open-log">打开日志文件夹</button>
      </div>
    </div>

    <div class="setting">
      <div><h2>关于 WinSlim</h2><p>版本 ${esc(S.catalog.version ?? '')} · 当前系统 ${esc(S.catalog.os.label)}（内部版本 ${S.catalog.os.build}）</p></div>
      <div class="panel">
        <p>WinSlim 是在“Windows11轻松设置 v1.07”的基础上重新编写的。下面这些有意没有保留：</p>
        <ul>
          <li>关闭 Defender、DEP、UAC，或以 TrustedInstaller 身份改服务：安全代价太大，也不属于“减负”。</li>
          <li>从私人服务器用 HTTP 下载软件并以管理员身份运行：改为只用微软商店和 winget。</li>
          <li>强制删除 Edge 和 WebView2：会破坏小组件、商店应用和 WinSlim 自己的界面。</li>
          <li>把更新暂停到 2042 年：只放宽暂停上限，暂停多久由你自己在“设置”里选。</li>
        </ul>
        <p style="margin:12px 0 0"><button class="link" data-action="open-url" data-url="${REPO_URL}">在 GitHub 上查看源代码</button></p>
      </div>
    </div>`;
}

/* ---------- 底部托盘 ---------- */
function renderTray() {
  const r = route().name;
  let html = '';
  if (S.busy) {
    const pct = S.busy.total ? (100 * (S.busy.done ?? 0) / S.busy.total) : 0;
    html = `<div class="what progress-line"><span class="track"><b style="width:${S.busy.total ? pct.toFixed(1) + '%' : '30%'}"></b></span><span>${esc(S.busy.text ?? '正在处理……')}</span></div>`;
  } else if (r === 'apps' && S.appSel.size) {
    html = `
      <div class="what"><b class="num">已选 ${S.appSel.size} 个应用</b> <span>· 卸载后可以随时从微软商店装回来</span></div>
      <label class="check"><input type="checkbox" data-action="deprovision" ${S.deprovision ? 'checked' : ''}>阻止新用户登录时自动安装</label>
      <button class="btn" data-action="app-clear">取消选择</button>
      <button class="btn primary" data-action="remove-apps">卸载所选</button>`;
  } else if (r === 'cleanup' && S.cleanSel.size) {
    const sum = S.scan ? [...S.cleanSel].reduce((s, id) => s + (S.scan[id]?.bytes || 0), 0) : null;
    html = `
      <div class="what"><b class="num">已选 ${S.cleanSel.size} 项</b> <span>· 预计释放 ${sum != null ? bytes(sum, true) : '—'}${S.cleanSel.has('component-store') ? '，另加组件存储（需要较长时间）' : ''}</span></div>
      <button class="btn primary" data-action="run-cleanup" ${S.scan ? '' : 'disabled'}>清理所选</button>`;
  } else if (S.staged.size && r !== 'apps' && r !== 'cleanup') {
    const list = [...S.staged.keys()].map((id) => tweaks().find((t) => t.id === id));
    const nApply = [...S.staged.values()].filter((v) => v === 'apply').length;
    const nRevert = S.staged.size - nApply;
    const restart = strongestRestart(list);
    html = `
      <div class="what"><b class="num">${[nApply ? `待应用 ${nApply} 项` : '', nRevert ? `待还原 ${nRevert} 项` : ''].filter(Boolean).join('，')}</b> <span>· ${RESTART[restart]}</span></div>
      <button class="btn" data-action="stage-clear">清空</button>
      <button class="btn primary" data-action="review">检查改动并应用</button>`;
  }
  tray.hidden = !html;
  tray.innerHTML = html;
}

/* ---------- 对话框 ---------- */
function openDialog(html) {
  dialog.innerHTML = html;
  dialog.showModal();
  return new Promise((resolve) => {
    dialog.onclick = (e) => {
      const b = e.target.closest('[data-dlg]');
      if (b) { dialog.close(); resolve(b.dataset.dlg === 'ok'); }
    };
    dialog.oncancel = () => resolve(false);
  });
}

async function review() {
  const items = [...S.staged.entries()].map(([id, mode]) => ({ t: tweaks().find((x) => x.id === id), mode }));
  const applies = items.filter((x) => x.mode === 'apply');
  const risky = applies.filter((x) => x.t.level !== 'safe');
  const restart = strongestRestart(items.map((x) => x.t));
  const ok = await openDialog(`
    <div class="dlg-head">
      <h2>检查改动</h2>
      <p>将${applies.length ? `应用 ${applies.length} 项` : ''}${applies.length && items.length - applies.length ? '，' : ''}${items.length - applies.length ? `还原 ${items.length - applies.length} 项` : ''}。应用前会备份每一处原值。${restart !== 'none' ? `全部生效${RESTART[restart].replace('需', '需要')}。` : ''}</p>
    </div>
    <div class="dlg-body">
      ${risky.length ? `
        <div class="group">
          <h3>请确认你接受这些后果</h3>
          <ul class="risk-list">${risky.map((x) => `<li><b>${esc(x.t.title)}</b>（${LEVEL[x.t.level]}）：${esc(x.t.risk)}</li>`).join('')}</ul>
        </div>` : ''}
      ${items.map((x) => `
        <div class="group">
          <h3>${esc(x.t.title)} <span class="pill ${x.t.level}">${LEVEL[x.t.level]}</span> <span class="staged">${x.mode === 'apply' ? '应用' : '还原'}</span></h3>
          <ul class="mono">${x.mode === 'apply' ? x.t.changes.map((c) => `<li>${esc(c)}</li>`).join('') : `<li>${S.status.get(x.t.id)?.hasBackup ? '写回应用前备份的原值' : '按 Windows 默认值还原'}</li>`}</ul>
        </div>`).join('')}
    </div>
    <div class="dlg-foot">
      <label class="check"><input type="checkbox" id="dlg-rp" ${S.restorePoint ? 'checked' : ''}>先创建系统还原点</label>
      <span class="grow"></span>
      <button class="btn" data-dlg="cancel">返回</button>
      <button class="btn primary" data-dlg="ok">${risky.length ? '我已了解，应用' : '应用'}</button>
    </div>`);
  S.restorePoint = !!$('#dlg-rp', dialog)?.checked;
  if (ok) await commitTweaks();
}

async function commitTweaks() {
  const applyIds = [...S.staged].filter(([, m]) => m === 'apply').map(([id]) => id);
  const revertIds = [...S.staged].filter(([, m]) => m === 'revert').map(([id]) => id);
  S.busy = { text: '正在准备……' };
  render();
  const failures = [];
  let restart = 'none';
  try {
    for (const [method, ids] of [['revert', revertIds], ['apply', applyIds]]) {
      if (!ids.length) continue;
      const res = await call(method, { ids, restorePoint: method === 'apply' && S.restorePoint });
      res.statuses.forEach((s) => S.status.set(s.id, s));
      res.outcomes.forEach((o) => {
        if (o.ok) S.staged.delete(o.id);
        else failures.push(`${tweaks().find((t) => t.id === o.id)?.title}：${o.error}`);
      });
      if (res.restoreNote) S.notices.push({ text: res.restoreNote });
      if (RESTART_ORDER.indexOf(res.restart) > RESTART_ORDER.indexOf(restart)) restart = res.restart;
    }
  } catch (err) {
    failures.push(err.message);
  }
  S.busy = null;
  if (failures.length) S.notices.push({ text: `${failures.length} 项没有成功。${failures.join('；')}` });
  if (restart === 'explorer') S.notices.push({ text: '部分改动要重启文件资源管理器才能生效。任务栏会闪一下，已打开的文件夹窗口会关闭。', action: { label: '立即重启资源管理器', run: () => call('restartExplorer') } });
  if (restart === 'logoff') S.notices.push({ text: '部分改动要注销并重新登录后才能生效。' });
  if (restart === 'reboot') S.notices.push({ text: '部分改动要重启电脑后才能生效。' });
  if (!failures.length && restart === 'none') S.notices.push({ text: '已完成，改动已经生效。' });
  render();
}

async function removeApps() {
  const chosen = S.catalog.apps.filter((a) => S.appSel.has(a.package));
  const careful = chosen.filter((a) => a.level !== 'safe');
  const ok = await openDialog(`
    <div class="dlg-head">
      <h2>卸载 ${chosen.length} 个应用</h2>
      <p>${S.deprovision ? '同时从系统映像里移除，新建的用户也不会再自动装上。' : '只卸载当前用户的副本。'}之后可以在这个页面点“重新安装”，从微软商店装回来。</p>
    </div>
    <div class="dlg-body">
      ${careful.length ? `<div class="group"><h3>这些应用卸载后会影响其他功能</h3><ul class="risk-list">${careful.map((a) => `<li><b>${esc(a.name)}</b>：${esc(a.note)}</li>`).join('')}</ul></div>` : ''}
      <div class="group"><h3>将卸载</h3><ul>${chosen.map((a) => `<li>${esc(a.name)} <span class="mono">${esc(a.package)}</span></li>`).join('')}</ul></div>
    </div>
    <div class="dlg-foot"><span class="grow"></span><button class="btn" data-dlg="cancel">返回</button><button class="btn primary" data-dlg="ok">卸载</button></div>`);
  if (!ok) return;
  S.busy = { text: '正在卸载……', done: 0, total: chosen.length };
  render();
  try {
    const res = await call('removeApps', { packages: chosen.map((a) => a.package), deprovision: S.deprovision });
    S.apps = res.apps;
    const failed = res.outcomes.filter((o) => !o.ok);
    S.appSel.clear();
    S.notices.push({ text: failed.length
      ? `卸载了 ${res.outcomes.length - failed.length} 个；${failed.length} 个没有成功：${failed.map((o) => `${S.catalog.apps.find((a) => a.package === o.package)?.name}（${o.error}）`).join('；')}`
      : `已卸载 ${res.outcomes.length} 个应用。` });
  } catch (err) {
    S.notices.push({ text: err.message });
  }
  S.busy = null;
  render();
}

async function runCleanup() {
  const ids = S.catalog.cleaners.map((c) => c.id).filter((id) => S.cleanSel.has(id));
  const warn = S.catalog.cleaners.filter((c) => ['recycle-bin', 'component-store', 'error-reports'].includes(c.id) && S.cleanSel.has(c.id));
  if (warn.length) {
    const ok = await openDialog(`
      <div class="dlg-head"><h2>确认清理</h2><p>下面几项清理后无法撤销。</p></div>
      <div class="dlg-body"><ul class="risk-list">${warn.map((c) => `<li><b>${esc(c.title)}</b>：${esc(c.risk)}</li>`).join('')}</ul></div>
      <div class="dlg-foot"><span class="grow"></span><button class="btn" data-dlg="cancel">返回</button><button class="btn primary" data-dlg="ok">清理</button></div>`);
    if (!ok) return;
  }
  S.busy = { text: '正在清理……', done: 0, total: ids.length };
  render();
  try {
    const res = await call('runCleanup', { ids });
    const freed = res.outcomes.reduce((s, o) => s + (o.freed || 0), 0);
    S.scan = Object.fromEntries(res.scan.map((x) => [x.id, x]));
    const failed = res.outcomes.filter((o) => !o.ok);
    S.notices.push({ text: `已释放 ${bytes(freed, true)}。${failed.length ? `${failed.length} 项没有完成：${failed.map((o) => o.error).join('；')}` : ''}` });
    S.system = await call('system');
  } catch (err) {
    S.notices.push({ text: err.message });
  }
  S.busy = null;
  render();
}

/* ---------- 交互 ---------- */
function stage(id, mode) {
  const t = tweaks().find((x) => x.id === id);
  const current = isApplied(t);
  if ((mode === 'apply') === current) S.staged.delete(id);
  else S.staged.set(id, mode);
}

document.addEventListener('click', async (e) => {
  const el = e.target.closest('[data-action]');
  if (!el || el.disabled) return;
  const a = el.dataset.action;
  if (a === 'toggle-open') {
    if (e.target.closest('.switch')) return;
    const id = el.dataset.id;
    S.open.has(id) ? S.open.delete(id) : S.open.add(id);
    render();
  } else if (a === 'filter') { S.filter = el.dataset.v; render(); }
  else if (a === 'tab') { location.hash = `#/tweaks/${el.dataset.v}`; }
  else if (a === 'stage-one') { stage(el.dataset.id, el.dataset.mode); render(); }
  else if (a === 'unstage') { S.staged.delete(el.dataset.id); render(); }
  else if (a === 'stage-list') { el.dataset.ids.split(',').forEach((id) => stage(id, 'apply')); render(); }
  else if (a === 'stage-recommended') {
    tweaks().filter((t) => t.recommended && !isApplied(t)).forEach((t) => stage(t.id, 'apply'));
    render();
    review();
  }
  else if (a === 'stage-revert-all') { tweaks().filter((t) => S.status.get(t.id)?.hasBackup).forEach((t) => stage(t.id, 'revert')); render(); }
  else if (a === 'stage-clear') { S.staged.clear(); render(); }
  else if (a === 'review') review();
  else if (a === 'notice-close') { S.notices.splice(+el.dataset.i, 1); render(); }
  else if (a === 'notice-act') {
    const n = S.notices[+el.dataset.i];
    S.notices.splice(+el.dataset.i, 1);
    render();
    await n.action.run();
  }
  else if (a === 'app-select-safe') {
    S.catalog.apps.filter((x) => x.level === 'safe' && S.apps?.find((s) => s.package === x.package)?.installed).forEach((x) => S.appSel.add(x.package));
    render();
  }
  else if (a === 'app-clear') { S.appSel.clear(); render(); }
  else if (a === 'remove-apps') removeApps();
  else if (a === 'reinstall') {
    const app = S.catalog.apps.find((x) => x.package === el.dataset.pkg);
    S.busy = { text: `正在从微软商店重新安装：${app.name}` };
    render();
    try {
      const res = await call('reinstallApp', { package: app.package });
      S.apps = res.apps;
      S.notices.push({ text: res.outcome.ok ? `已重新安装 ${app.name}。` : res.outcome.error });
    } catch (err) { S.notices.push({ text: err.message }); }
    S.busy = null;
    render();
  }
  else if (a === 'run-cleanup') runCleanup();
  else if (a === 'rescan') { S.scan = null; render(); loadScan(); }
  else if (a === 'restore-point') {
    S.busy = { text: '正在创建系统还原点……' };
    render();
    try { const r = await call('restorePoint'); S.notices.push({ text: r.message }); } catch (err) { S.notices.push({ text: err.message }); }
    S.busy = null;
    render();
  }
  else if (a === 'open-restore') call('openSystemRestore');
  else if (a === 'open-log') call('openLogFolder');
  else if (a === 'open-url') call('openUrl', { url: el.dataset.url });
});

document.addEventListener('change', (e) => {
  const el = e.target;
  const a = el.dataset.action;
  if (a === 'stage') { stage(el.dataset.id, el.checked ? 'apply' : 'revert'); render(); }
  else if (a === 'app-sel') { el.checked ? S.appSel.add(el.dataset.pkg) : S.appSel.delete(el.dataset.pkg); renderTray(); }
  else if (a === 'clean-sel') { el.checked ? S.cleanSel.add(el.dataset.id) : S.cleanSel.delete(el.dataset.id); renderTray(); }
  else if (a === 'deprovision') { S.deprovision = el.checked; }
});

const search = $('#search');
search.addEventListener('input', () => {
  S.query = search.value;
  if (route().name !== 'tweaks') location.hash = '#/tweaks';
  else render();
});
document.addEventListener('keydown', (e) => {
  if (e.ctrlKey && e.key.toLowerCase() === 'k') { e.preventDefault(); search.focus(); search.select(); }
  if (e.key === 'Escape' && document.activeElement === search && search.value) { search.value = ''; S.query = ''; render(); }
});

/* ---------- 启动 ---------- */
async function loadScan() {
  try {
    const res = await call('scanCleanup', { ids: S.catalog.cleaners.map((c) => c.id) });
    S.scan = Object.fromEntries(res.map((x) => [x.id, x]));
  } catch { S.scan = {}; }
  render();
}

(async function boot() {
  const [catalog, system] = await Promise.all([call('catalog'), call('system')]);
  S.catalog = catalog;
  S.system = system;
  $('#scope').innerHTML = `<strong>${esc(system.computer)}</strong>${esc(catalog.os.label)}`;
  $('#host').textContent = `内部版本 ${catalog.os.build} · 已以管理员身份运行`;
  render();
  call('status').then((list) => { list.forEach((s) => S.status.set(s.id, s)); render(); });
  call('apps').then((list) => { S.apps = list; render(); }).catch(() => { S.apps = []; render(); });
  loadScan();
})();
