'use strict';
// 只在浏览器预览界面时使用的模拟后台：数据取自真实目录，系统状态是固定的样例。
(function () {
  const listeners = [];
  const os = { label: 'Windows 11 专业版 23H2', isWindows11: true, build: 22631 };
  const fixedApplied = new Set(['show-extensions', 'taskbar-left', 'advertising-id', 'web-search', 'delivery-optimization', 'clock-seconds']);
  const backedUp = new Set(['show-extensions', 'taskbar-left', 'advertising-id', 'web-search']);
  const installed = new Set([
    'Microsoft.BingNews', 'Microsoft.BingWeather', 'Microsoft.Getstarted', 'Microsoft.GetHelp', 'Microsoft.MicrosoftOfficeHub',
    'Microsoft.WindowsFeedbackHub', 'MicrosoftWindows.Client.WebExperience', 'Microsoft.MicrosoftSolitaireCollection', 'Microsoft.GamingApp',
    'Microsoft.XboxGamingOverlay', 'Microsoft.XboxIdentityProvider', 'Microsoft.OutlookForWindows', 'MSTeams', 'Microsoft.Todos',
    'Microsoft.YourPhone', 'Microsoft.PowerAutomateDesktop', 'Clipchamp.Clipchamp', 'Microsoft.ZuneMusic', 'Microsoft.WindowsSoundRecorder',
    'Microsoft.WindowsAlarms', 'Microsoft.WindowsMaps', 'MicrosoftCorporationII.QuickAssist',
  ]);
  const GB = 2 ** 30, MB = 2 ** 20;
  const sizes = {
    'user-temp': Math.round(1.84 * GB), 'windows-temp': 212 * MB, 'update-cache': Math.round(2.31 * GB),
    'delivery-cache': 486 * MB, 'error-reports': 97 * MB, 'thumbnails': 143 * MB, 'recycle-bin': 655 * MB,
  };
  let catalog, appsCatalog, cleaners;

  const ready = Promise.all(['tweaks', 'protection', 'apps', 'maintenance'].map((n) => fetch(`catalog/${n}.json`).then((r) => r.json())))
    .then(([t, p, a, m]) => {
      catalog = t;
      catalog.categories.push(...p.categories);
      catalog.tweaks.push(...p.tweaks);
      appsCatalog = a; cleaners = m.cleaners;
    });

  const startType = { auto: '自动', 'delayed-auto': '自动（延迟启动）', manual: '手动', disabled: '禁用' };
  const describe = (x) => x.type === 'reg' ? `${x.path} › ${x.name ? x.name : '(默认)'} = ${typeof x.value === 'string' ? `"${x.value}"` : x.value}`
    : x.type === 'service' ? `服务 ${x.name} 启动类型 → ${startType[x.startType]}`
    : `${x.shell === 'powershell' ? 'PowerShell' : '命令'}：${x.apply}`;
  const applies = (t) => t.os === 'all' || (t.os === '11') === os.isWindows11;
  const status = (t) => ({ id: t.id, state: fixedApplied.has(t.id) ? 'applied' : 'default', hasBackup: backedUp.has(t.id) });
  const appList = () => appsCatalog.apps.map((a) => ({ package: a.package, version: '1.0', installed: installed.has(a.package), provisioned: installed.has(a.package) }));
  const emit = (data) => listeners.forEach((f) => f({ data }));
  const wait = (ms) => new Promise((r) => setTimeout(r, ms));

  const methods = {
    async catalog() {
      return {
        os, version: '1.1.1', categories: catalog.categories,
        tweaks: catalog.tweaks.filter(applies).map((t) => ({ ...t, changes: t.actions.map(describe), actions: undefined, detect: undefined })),
        appGroups: appsCatalog.groups, apps: appsCatalog.apps, cleaners,
      };
    },
    async system() {
      return {
        os: os.label, isWindows11: true, build: os.build, cpu: 'Intel(R) Core(TM) i5-12400F', memoryTotal: 16 * GB, memoryUsed: Math.round(9.3 * GB),
        systemDrive: 'C:', diskTotal: Math.round(237.9 * GB), diskFree: Math.round(41.2 * GB), processCount: 214, uptimeSeconds: 3 * 86400 + 4 * 3600 + 720, computer: 'DESKTOP-EDY',
      };
    },
    async status() { await wait(400); return catalog.tweaks.filter(applies).map(status); },
    async apply({ ids, restorePoint }) {
      if (restorePoint) { emit({ event: 'progress', data: { stage: 'restore', text: '正在创建系统还原点……' } }); await wait(700); }
      for (const [i, id] of ids.entries()) {
        emit({ event: 'progress', data: { text: `正在应用：${catalog.tweaks.find((t) => t.id === id).title}`, done: i, total: ids.length } });
        await wait(120);
        fixedApplied.add(id); backedUp.add(id);
      }
      const list = ids.map((id) => catalog.tweaks.find((t) => t.id === id));
      const order = ['none', 'explorer', 'logoff', 'reboot'];
      return { outcomes: ids.map((id) => ({ id, ok: true })), statuses: list.map(status), restart: list.reduce((a, t) => order.indexOf(t.restart) > order.indexOf(a) ? t.restart : a, 'none') };
    },
    async revert({ ids }) {
      ids.forEach((id) => { fixedApplied.delete(id); backedUp.delete(id); });
      return { outcomes: ids.map((id) => ({ id, ok: true })), statuses: ids.map((id) => status(catalog.tweaks.find((t) => t.id === id))), restart: 'explorer' };
    },
    async apps() { await wait(900); return appList(); },
    async removeApps({ packages }) {
      for (const [i, p] of packages.entries()) { emit({ event: 'progress', data: { text: `正在卸载：${p}`, done: i, total: packages.length } }); await wait(200); installed.delete(p); }
      return { outcomes: packages.map((p) => ({ package: p, ok: true })), apps: appList() };
    },
    async reinstallApp({ package: p }) { await wait(800); installed.add(p); return { outcome: { package: p, ok: true }, apps: appList() }; },
    async scanCleanup({ ids }) { await wait(600); return ids.map((id) => ({ id, bytes: sizes[id] ?? 0, measurable: id !== 'component-store' })); },
    async runCleanup({ ids }) {
      const outcomes = ids.map((id) => ({ id, ok: true, freed: Math.round((sizes[id] ?? 0) * 0.92) }));
      ids.forEach((id) => { if (id in sizes) sizes[id] = Math.round(sizes[id] * 0.08); });
      await wait(800);
      return { outcomes, scan: cleaners.map((c) => ({ id: c.id, bytes: sizes[c.id] ?? 0, measurable: c.id !== 'component-store' })) };
    },
    async restorePoint() { await wait(900); return { ok: true, message: '已创建还原点' }; },
    async restartExplorer() {}, async openUrl() {}, async openLogFolder() {}, async openSystemRestore() {},
    async openDefenderSettings() {}, async openFirewallConsole() {},
  };

  window.__winslimHost = {
    addEventListener: (_, f) => listeners.push(f),
    postMessage: async ({ id, method, params }) => {
      await ready;
      try { const result = await methods[method](params || {}); listeners.forEach((f) => f({ data: { id, ok: true, result } })); }
      catch (e) { listeners.forEach((f) => f({ data: { id, ok: false, error: e.message } })); }
    },
  };
})();
