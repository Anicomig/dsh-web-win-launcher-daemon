/* ============================================================
   纯逻辑段：不碰 window / document / 桥。
   Node 可以直接加载这一段做单测（tools\test-page-logic.mjs）。
   分界标记见下方 “以下是 UI”，标记之后才允许碰 DOM。
   ============================================================ */

var GROUPS = [
  { id: 'runtime', label: '运行参数' },
  { id: 'paths',   label: '路径' },
  { id: 'timing',  label: '检测与超时' },
  { id: 'startup', label: '开机启动' },
  { id: 'env',     label: '环境信息' }
];

var CROSS_NOTE = { timing: '宽限期须小于硬超时；两个轮询间隔不得为 0。' };

var FIELDS = [
  { key:'PORT', varName:'CONFIG_PORT', type:'int', group:'runtime',
    label:'端口', min:1, max:65535, def:3080, warnUnder:1024,
    help:'1–65535，建议 3000–3999。小于 1024 需管理员权限。修改后须重启守护进程生效。' },
  { key:'HOST', varName:'CONFIG_HOST', type:'str', group:'runtime',
    label:'监听地址', readonly:true, help:'' },
  { key:'DSH_PROFILE', varName:'CONFIG_DSH_PROFILE', type:'str', group:'runtime',
    label:'配置名',
    help:'建议使用 web。只允许字母、数字、点、下划线、连字符，不可为 desktop。' },
  { key:'NO_OPEN', varName:'CONFIG_NO_OPEN', type:'bool', group:'runtime',
    label:'启动后打开页面', invert:true,
    help:'守护进程拉起 dsh web 后自动打开浏览器页面。' },
  { key:'GATEWAY_LOG_ENABLED', varName:'CONFIG_GATEWAY_LOG_ENABLED', type:'bool', group:'runtime',
    label:'捕获 dsh web 输出',
    help:'将 dsh web 的标准输出与错误写入日志。排障时开启。' },

  /* 「打开方式」不是 ps1 字段，所以不放进 FIELDS：
     它存在 DshLauncher\DshLauncher.json 里，切换即写盘，不参与保存按钮与脏标记。
     见 renderOpenMode()。 */

  { key:'DSH_HOME', varName:'CONFIG_DSH_HOME', type:'path', kind:'dir', group:'paths',
    label:'DSH 主目录',
    help:'DSH 数据根目录，目录名须为 .dsh，一般位于用户目录下，例如 C:\\Users\\<用户名>\\.dsh。' },
  { key:'WORKSPACE_DIR', varName:'CONFIG_WORKSPACE_DIR', type:'path', kind:'dir', group:'paths',
    label:'工作目录', help:'DSH 会话的默认工作根目录。' },
  { key:'NODE_EXE', varName:'CONFIG_NODE_EXE', type:'path', kind:'file', group:'paths',
    label:'Node 可执行', help:'必须是 node.exe 的完整路径。node 不在 PATH 时尤其重要。' },
  { key:'DSH_CLI', varName:'CONFIG_DSH_CLI', type:'path', kind:'file', group:'paths',
    label:'DSH CLI', help:'必须指向 @deepseek-ai\\dsh\\lib\\bin.js。' },

  { key:'PORT_CHECK_INTERVAL', varName:'CONFIG_PORT_CHECK_INTERVAL', type:'int', group:'timing',
    label:'端口轮询间隔', min:1, max:60, def:2,
    help:'等待启动阶段探测端口的间隔，建议 2 秒。不得为 0，否则日志会被刷爆。' },
  { key:'PROC_CHECK_INTERVAL', varName:'CONFIG_PROC_CHECK_INTERVAL', type:'int', group:'timing',
    label:'进程轮询间隔', min:1, max:60, def:3,
    help:'等待启动阶段探测进程的间隔，建议 3 秒。不得为 0。' },
  { key:'DAEMON_PORT_ONLY_INTERVAL', varName:'CONFIG_DAEMON_PORT_ONLY_INTERVAL', type:'int', group:'timing',
    label:'仅端口轮询间隔', min:2, max:3600, def:15,
    help:'进入 ACTIVE 后仅按端口判活的轮询间隔，建议 15 秒。范围 2–3600。' },
  { key:'LAUNCH_GRACE_SEC', varName:'CONFIG_LAUNCH_GRACE_SEC', type:'int', group:'timing',
    label:'启动宽限期', min:5, max:1800, def:120,
    help:'宽限内不判定启动失败，建议 120 秒。须小于硬超时。' },
  { key:'STARTUP_TIMEOUT_SEC', varName:'CONFIG_STARTUP_TIMEOUT_SEC', type:'int', group:'timing',
    label:'硬超时', min:10, max:3600, def:180,
    help:'超过即判定启动失败，建议 180 秒。须大于启动宽限期。' },
  { key:'RESTART_BACKOFF_MAX_SEC', varName:'CONFIG_RESTART_BACKOFF_MAX_SEC', type:'int', group:'timing',
    label:'退避上限', min:1, max:3600, def:300,
    help:'连续重启失败后的最长等待，建议 300 秒。' }
];

/* 值段的三种形态：双引号串、单引号串（内部允许空格），或裸词。
   绝不能用 (\S+) —— 它会把 "D:\Program Files\nodejs\node.exe" 截成 "D:\Program。
   与 DshConfigHost 侧 ConfigEditor 的匹配规则保持一致。 */
var VAL_PAT = '"[^"\\r\\n]*"|\'[^\'\\r\\n]*\'|[^\\s#][^ \\t\\r\\n]*';

function parseConfig(text) {
  var out = {};
  FIELDS.forEach(function (f) {
    var re = new RegExp('^\\s*\\$' + f.varName + '\\s*=\\s*(' + VAL_PAT + ')', 'm');
    var m = String(text).match(re);
    if (!m) return;
    var raw = m[1];
    if (f.type === 'int') {
      var n = parseInt(raw, 10);
      if (!isNaN(n)) out[f.key] = n;
    } else if (f.type === 'bool') {
      out[f.key] = (raw === '$true');
    } else {
      out[f.key] = raw.replace(/^"|"$/g, '').replace(/^'|'$/g, '');
    }
  });
  return out;
}

function validateField(f, value) {
  var s = (value === undefined || value === null) ? '' : String(value).trim();
  if (f.type === 'int') {
    if (s === '') return { level:'err', msg:'不能为空' };
    if (!/^-?\d+$/.test(s)) return { level:'err', msg:'必须是整数' };
    var n = Number(s);
    if (n < f.min) return { level:'err', msg:'不得小于 ' + f.min };
    if (n > f.max) return { level:'err', msg:'不得大于 ' + f.max };
    if (f.warnUnder !== undefined && n < f.warnUnder)
      return { level:'warn', msg:'小于 ' + f.warnUnder + ' 的端口需管理员权限' };
    return { level:'ok' };
  }
  if (f.type === 'bool') return { level:'ok' };

  if (s === '') return { level:'err', msg:'不能为空' };
  switch (f.key) {
    case 'NODE_EXE':
      if (!/node\.exe$/i.test(s)) return { level:'err', msg:'必须以 node.exe 结尾' };
      if (!/nodejs/i.test(s)) return { level:'warn', msg:'路径中没有 nodejs，确认是故意的？' };
      return { level:'ok' };
    case 'DSH_CLI':
      if (!/bin\.js$/i.test(s)) return { level:'err', msg:'必须以 bin.js 结尾' };
      if (s.indexOf('@deepseek-ai\\dsh\\') < 0) return { level:'err', msg:'必须位于 @deepseek-ai\\dsh\\ 下' };
      return { level:'ok' };
    case 'DSH_HOME':
      if (!/(^|[\\/])\.dsh$/i.test(s)) return { level:'err', msg:'目录名应为 .dsh' };
      return { level:'ok' };
    case 'DSH_PROFILE':
      if (!/^[A-Za-z0-9._-]+$/.test(s)) return { level:'err', msg:'只允许字母、数字、. _ -' };
      if (s === 'desktop') return { level:'err', msg:'不可使用 desktop' };
      return { level:'ok' };
    case 'OPEN_MODE':
      if (s !== 'browser' && s !== 'app')
        return { level:'err', msg:'必须是 browser 或 app' };
      return { level:'ok' };
    case 'EDGE_APP_ID':
      // 32 个 a-p 字符；空仅在 OPEN_MODE=browser 时允许；app 模式必须有值
      if (s !== '' && !/^[a-p]{32}$/.test(s))
        return { level:'err', msg:'必须是 32 个 a-p 字符（app-id），或者留空' };
      return { level:'ok' };
    default:
      if (/[\r\n]/.test(s)) return { level:'err', msg:'不能包含换行' };
      return { level:'ok' };
  }
}

/* 跨字段规则：只报告，不阻断保存（沿用现状）。 */
function validateCross(values) {
  var errs = [];
  var g = Number(values.LAUNCH_GRACE_SEC), t = Number(values.STARTUP_TIMEOUT_SEC);
  if (isFinite(g) && isFinite(t) && g >= t)
    errs.push({ key:'LAUNCH_GRACE_SEC', msg:'启动宽限期须小于硬超时' });
  ['PORT_CHECK_INTERVAL','PROC_CHECK_INTERVAL'].forEach(function (k) {
    if (Number(values[k]) === 0) errs.push({ key:k, msg:'不得为 0，否则日志会被刷爆' });
  });
  // 注：OPEN_MODE / EDGE_APP_ID 不在 FIELDS 里（存 DshLauncher.json，切换即写盘），
  // 它们的有效性由宿主 SetOpenMode 把关，不参与这里的检查。
  return errs;
}

/* 表单值 -> 桥的 edits。布尔的 invert 只在这里处理一次。
   name/kind 由页面送出，DshConfigHost 因此不需要第二份字段表（避免两处定义漂移）。 */
function buildEdits(values) {
  var out = [];
  FIELDS.forEach(function (f) {
    if (f.readonly) return;
    var v = values[f.key];
    if (f.type === 'bool') {
      var b = !!v;
      v = f.invert ? !b : b;           // NO_OPEN：界面 on = 会打开 = NO_OPEN false
    } else if (f.type === 'int') {
      v = Number(v);
    } else {
      v = String(v === undefined || v === null ? '' : v).trim();
    }
    out.push({ name: f.varName, kind: ((f.type === 'path' || f.type === 'choice') ? 'str' : f.type), value: v });
  });
  return out;
}

/* 反向：磁盘值 -> 界面值（同样只在这里 invert 一次）。
   choice 字段在旧配置里可能整个不存在，这时界面会显示 def（默认选项），
   所以 baseline 也必须填 def —— 否则 collect() 拿到 'browser' 而 baseline 是
   undefined，markDirty 会永久判为「已修改」，保存按钮永远可用。 */
function toUiValues(stored) {
  var ui = {};
  FIELDS.forEach(function (f) {
    var v = stored[f.key];
    if (f.type === 'bool' && f.invert && v !== undefined) v = !v;
    if (f.type === 'choice' && (v === undefined || v === null || v === '')) {
      v = (f.def !== undefined) ? f.def : ((f.options && f.options[0]) ? f.options[0][0] : '');
    }
    ui[f.key] = v;
  });
  return ui;
}

function esc(s) {
  return String(s === undefined || s === null ? '' : s)
    .replace(/[&<>"]/g, function (c) {
      return { '&':'&amp;', '<':'&lt;', '>':'&gt;', '"':'&quot;' }[c];
    });
}

/* ===== 以下是 UI ===== */

var B = (function () {
  function raw() {
    var o = window.chrome && window.chrome.webview;
    if (!o || !o.hostObjects) throw new Error('不在 DshConfigHost 内：请通过 DshConfigHost.exe 打开本页');
    return o.hostObjects.bridge;
  }
  function call(name, arg) {
    var p;
    try { p = (arg === undefined) ? raw()[name]() : raw()[name](arg); }
    catch (e) { return Promise.resolve({ ok:false, message:'调用 ' + name + ' 失败：' + e.message }); }
    return Promise.resolve(p).then(function (s) {
      try { return JSON.parse(s); }
      catch (e) { return { ok:false, message: name + ' 返回了非 JSON：' + String(s).slice(0,120) }; }
    }, function (e) {
      return { ok:false, message: name + ' 调用异常：' + (e && e.message ? e.message : e) };
    });
  }
  return {
    getEnv:       function () { return call('GetEnvironment'); },
    read:         function () { return call('ReadConfig'); },
    write:        function (edits) { return call('WriteConfig', JSON.stringify({ edits: edits })); },
    defaults:     function () { return call('GetDefaults'); },
    verify:       function (paths) { return call('VerifyPaths', JSON.stringify({ paths: paths })); },
    autostart:    function () { return call('GetAutostart'); },
    setAutostart: function (a, t) { return call('SetAutostart', JSON.stringify({ action:a, target:t })); },
    restart:      function () { return call('RestartDaemon'); },
    openFolder:   function (p) { return call('OpenInFolder', JSON.stringify({ path: p })); },
    detectApp:    function (opts) { return call('DetectEdgeApp', JSON.stringify(opts || {})); },
    openUrl:      function (u) { return call('OpenUrl', JSON.stringify({ url: u })); },
    getOpenMode:  function () { return call('GetOpenMode', '{}'); },
    setOpenMode:  function (mode, appId) { return call('SetOpenMode', JSON.stringify({ mode: mode, appId: appId })); }
  };
})();

/* 文件夹图标用内联 SVG：不依赖 Segoe MDL2 之类的图标字体在不在 */
var FOLDER_SVG = '<svg viewBox="0 0 16 16" aria-hidden="true">'
  + '<path d="M1.6 3.7h4.0l1.2 1.6h6.6v7.0H1.6z" fill="none" stroke="currentColor" '
  + 'stroke-width="1.3" stroke-linejoin="round"/></svg>';

function folderBtn(openKey, title) {
  return '<button type="button" class="ico" data-open="' + openKey + '" '
       + 'title="' + esc(title) + '" aria-label="' + esc(title) + '">' + FOLDER_SVG + '</button>';
}

/*
  打开方式的处理。它不走 ps1、不走保存按钮 —— 切换后立即写 DshLauncher.json，
  托盘下次右键读到的就是新值。所以这里不调 markDirty（它与 ps1 的脏标记无关）。

  选「网页应用」时先自动检测：命中就存 mode=app + appId；没命中就只显示引导，
  不改磁盘（避免存了一个 app 却没有 app-id 的无效状态）。
*/
async function onAppModePick(v) {
  if (v === 'browser') {
    // 必须走 applyOpenMode：它负责「写盘成功后同步 openModeState 并重绘胶囊」。
    // 早先这里只调了 B.setOpenMode 而没更新状态，于是胶囊视觉不跟着变，
    // click 委托里的去重判断也会因为状态陈旧而误判。
    clearAppGuide();
    await applyOpenMode('browser', '');
    return;
  }

  setStatus('正在检测 Edge 网页应用...', '');
  var r = await B.detectApp({});
  if (!r.ok) { setStatus('检测失败：' + (r.message || '未知错误'), 'err'); return; }

  if (r.found) {
    clearAppGuide();
    // 必须走 applyOpenMode（而不是直接 B.setOpenMode）：
    // 它负责写盘成功后同步 openModeState 并重绘胶囊。
    await applyOpenMode('app', r.appId);
    return;
  }
  // 没检测到：显示引导，但不写盘
  showAppGuide(r);
  setStatus('未检测到网页应用，按引导操作后点「重新检测」', 'warn');
}

/* 移除引导面板（检测成功时调用） */
function clearAppGuide() {
  var old = document.getElementById('app_guide_panel');
  if (old && old.parentNode) old.parentNode.removeChild(old);
}

/*
  「打开方式」区块：不属于 ps1 表单，单独渲染在「运行参数」这一节末尾。
  当前值来自 DshLauncher.json；切换即写盘。
*/
var openModeState = { mode: 'browser', appId: '' };

function renderOpenMode() {
  var sec = $('sec-runtime');
  if (!sec) return;
  var old = $('openmode_row');
  if (old && old.parentNode) old.parentNode.removeChild(old);

  var html = '<div class="f" id="openmode_row">'
    + '<label>打开方式</label>'
    + '<div class="ctl"><div class="choices" id="f_OPEN_MODE">'
    +   '<span class="opt" data-v="browser" data-key="OPEN_MODE">默认浏览器</span>'
    +   '<span class="opt" data-v="app" data-key="OPEN_MODE">Edge 网页应用</span>'
    + '</div></div>'
    + '<div class="acts"><div class="q" data-help="OPENMODE">?</div></div>'
    + '<div class="row-msg" id="m_OPEN_MODE"></div>'
    + '<div class="hint" id="h_OPENMODE" hidden>'
    +   '「默认浏览器」用系统默认浏览器打开网页；「Edge 网页应用」打开 Edge 里安装的 PWA 独立窗口'
    +   '（无地址栏、看起来像桌面应用）。此项存在 DshLauncher.json 里，<b>切换后立即生效</b>，'
    +   '不需要点保存；托盘下次右键就会显示对应的菜单项。</div>'
    + '</div>';

  sec.insertAdjacentHTML('beforeend', html);
  paintOpenMode();
  bindHelp();
}

function paintOpenMode() {
  var grp = $('f_OPEN_MODE');
  if (!grp) return;
  // mode 必须是合法值：脏值会让两个选项都不亮或都亮，
  // 早先就是状态没同步导致「默认浏览器」和「Edge 网页应用」同时 active。
  var want = (openModeState.mode === 'app') ? 'app' : 'browser';
  Array.prototype.forEach.call(grp.querySelectorAll('.opt'), function (el) {
    el.classList.toggle('active', el.getAttribute('data-v') === want);
  });
  var msg = $('m_OPEN_MODE');
  if (msg) {
    if (openModeState.mode === 'app' && openModeState.appId) {
      msg.className = 'row-msg good';
      msg.textContent = '已启用网页应用 · ' + openModeState.appId;
    } else {
      msg.className = 'row-msg';
      msg.textContent = '';
    }
  }
}

/* 把检测到的 app 写入 JSON 并刷新界面状态 */
async function applyOpenMode(mode, appId) {
  var r = await B.setOpenMode(mode, appId || '');
  if (!r.ok) { setStatus('保存失败：' + (r.message || ''), 'err'); return false; }
  openModeState.mode = r.mode;
  openModeState.appId = r.appId || '';
  paintOpenMode();
  setStatus((r.message || '已保存') + '——托盘下次右键即生效', 'ok');
  return true;
}

/* 目标网址：优先用当前表单里的端口 */
function targetUrl() {
  var p = $('f_PORT') ? ($('f_PORT').value || '').trim() : '';
  if (!/^\d+$/.test(p)) p = '3080';
  return 'http://127.0.0.1:' + p + '/';
}

/*
  未检测到时的引导面板。
  布局要点：挂到 #sec-runtime 这一节后面（整行宽度），
  而不是塞在 .f 网格行里 —— 网格第二列只有 minmax(0,1fr)，塞进去会被挤成一条。
  「打开网址」走宿主 Explorer 打开，因为页面导航被 NavigationStarting 拦了，
  <a target=_blank> 点不动。另有「复制」按钮可直接取走网址。
*/
function showAppGuide(data) {
  var apps = (data && data.apps) || [];
  var url = targetUrl();

  clearAppGuide();

  var sec = $('sec-runtime');
  if (!sec) return;

  var html = '<div class="app-guide" id="app_guide_panel">'
    + '<div class="ag-title">尚未检测到对应的 Edge 网页应用</div>'
    + '<div class="ag-sub">这个应用可能还没安装，或者装好后没打开过。按下面三步走：</div>'
    + '<ol class="ag-steps">'
    + '<li>确认 dsh web 在跑，然后用 Edge 打开这个地址：'
    +     '<div class="ag-url"><code>' + esc(url) + '</code>'
    +       '<button type="button" id="btnOpenUrl">打开</button>'
    +       '<button type="button" id="btnCopyUrl">复制</button>'
    +     '</div></li>'
    + '<li>Edge 右上角菜单 → <b>应用</b> → <b>将此站点作为应用安装</b></li>'
    + '<li>把装好的应用<b>打开一次</b>（这样 Edge 才会留下记录），然后回来点「重新检测」</li>'
    + '</ol>'
    + '<div class="ag-actions"><button type="button" id="btnRedetectApp" class="ag-primary">重新检测</button></div>';

  if (apps.length > 0) {
    html += '<div class="ag-sub ag-more">已经检测到 ' + apps.length + ' 个网页应用。'
         +  '如果其中一个就是它，可以直接选：</div>'
         +  '<div class="ag-list">';
    for (var i = 0; i < apps.length; i++) {
      var a = apps[i];
      html += '<label class="ag-item">'
        + '<input type="radio" name="ag_pick" value="' + esc(a.appId) + '">'
        + '<span class="ag-name">' + esc(a.name || '（无名称）') + '</span>'
        + '<span class="ag-id">' + esc(a.appId) + '</span>'
        + '<span class="ag-u">' + esc(a.url || '') + '</span>'
        + '</label>';
    }
    html += '</div>'
         + '<div class="ag-actions">'
         + '<button type="button" id="btnUseExistingApp">用选中的这个</button>'
         + '</div>';
  }

  html += '</div>';

  var panel = document.createElement('div');
  panel.innerHTML = html;
  panel = panel.firstChild;
  // 整节之后插入，占满可用宽度
  sec.parentNode.insertBefore(panel, sec.nextSibling);

  setStatus('未检测到网页应用，请按引导操作后重新检测', 'warn');

  var openBtn = document.getElementById('btnOpenUrl');
  if (openBtn) {
    openBtn.onclick = async function () {
      // 页面自身导航会被宿主拦掉，交给宿主用系统默认浏览器打开
      var r = await B.openUrl(url);
      if (!r.ok) setStatus('打开失败：' + (r.message || ''), 'err');
      else setStatus('已用默认浏览器打开 ' + url, 'ok');
    };
  }
  var copyBtn = document.getElementById('btnCopyUrl');
  if (copyBtn) {
    copyBtn.onclick = function () {
      var ok = false;
      try {
        var ta = document.createElement('textarea');
        ta.value = url;
        ta.style.position = 'fixed';
        ta.style.opacity = '0';
        document.body.appendChild(ta);
        ta.select();
        ok = document.execCommand('copy');
        document.body.removeChild(ta);
      } catch (e) { ok = false; }
      setStatus(ok ? '已复制 ' + url : '复制失败，请手动选中网址复制', ok ? 'ok' : 'warn');
    };
  }

  var btn = document.getElementById('btnRedetectApp');
  if (btn) {
    btn.onclick = async function () {
      setStatus('正在重新检测...', '');
      var r2 = await B.detectApp({});
      if (!r2.ok) { setStatus('检测失败：' + (r2.message || ''), 'err'); return; }
      if (r2.found) {
        clearAppGuide();
        await applyOpenMode('app', r2.appId);
      } else {
        showAppGuide(r2);
        setStatus('仍未检测到，确认应用已打开过一次再试', 'warn');
      }
    };
  }
  var use = document.getElementById('btnUseExistingApp');
  if (use) {
    use.onclick = async function () {
      var sel = document.querySelector('input[name=ag_pick]:checked');
      if (!sel) { setStatus('请先选中一个应用', 'warn'); return; }
      clearAppGuide();
      await applyOpenMode('app', sel.value);
    };
  }
}

/* data-open 的值有两种：字段 key（取输入框当前值，这样未保存的编辑也能打开），
   或 env:xxx（取环境信息里的路径）。 */
async function openFolderOf(btn) {
  var key = btn.getAttribute('data-open');
  var p = '';
  if (key.indexOf('env:') === 0) {
    p = envOpenTargets[key.slice(4)] || '';
  } else {
    var el = $('f_' + key);
    p = el ? String(el.value || '').trim() : '';
  }
  if (!p) { setStatus('路径为空，无法打开', 'err'); return; }
  btn.disabled = true;
  var r = await B.openFolder(p);
  btn.disabled = false;
  if (!r.ok) setStatus(r.message || '打开失败', 'err');
  else setStatus('已打开 ' + r.opened, 'ok');
}

var restartWaiters = [];
window.chrome.webview.addEventListener('message', function (e) {
  var m;
  try { m = (typeof e.data === 'string') ? JSON.parse(e.data) : e.data; } catch (_) { return; }
  if (m && m.action === 'restart-daemon') {
    var w = restartWaiters.splice(0, restartWaiters.length);
    w.forEach(function (f) { f(m); });
  }
});
function waitRestart(timeoutMs) {
  return new Promise(function (res) {
    var t = setTimeout(function () { res({ ok:false, message:'重启超时未收到结果' }); }, timeoutMs || 30000);
    restartWaiters.push(function (m) { clearTimeout(t); res(m); });
  });
}
/* ---------------- 渲染 ---------------- */

function $(id) { return document.getElementById(id); }

var baseline = null;
var autoBaseline = null;   // 打开窗口时启动文件夹的真实状态，「重置」用它撤销自启改动
var dirty = false;

function setStatus(text, cls) {
  var s = $('status');
  if (!s) return;
  s.textContent = text;
  s.className = cls || '';
}

function renderNav() {
  var html = '';
  GROUPS.forEach(function (g) {
    html += '<span class="pill" data-g="' + g.id + '">' + esc(g.label) + '</span>';
  });
  $('nav').innerHTML = html;
  Array.prototype.forEach.call($('nav').children, function (el) {
    el.onclick = function () {
      var sec = $('sec-' + el.getAttribute('data-g'));
      if (sec) sec.scrollIntoView({ behavior: 'smooth', block: 'start' });
    };
  });
}

function control(f) {
  if (f.type === 'bool') {
    return '<div class="ctl"><div class="switch" id="f_' + f.key + '" role="switch" tabindex="0"></div></div>';
  }
  if (f.type === 'choice') {
    // 复用顶部的 .pill 胶囊样式，跟整套界面一致；选项在 FIELDS.options 给出 [value,label] 对。
    var opts = f.options || [];
    var inner = '';
    for (var i = 0; i < opts.length; i++) {
      inner += '<span class="opt" data-v="' + esc(opts[i][0]) + '" data-key="' + esc(f.key) + '">' + esc(opts[i][1]) + '</span>';
    }
    return '<div class="ctl"><div class="choices" id="f_' + f.key + '">' + inner + '</div></div>';
  }
  return '<div class="ctl"><input type="text" id="f_' + f.key + '"'
       + (f.type === 'int' ? ' inputmode="numeric" style="max-width:120px"' : '')
       + (f.readonly ? ' readonly' : '') + '></div>';
}

/* 第三列：路径字段给一个「打开所在文件夹」，有说明的给一个「?」 */
function actsFor(f) {
  var inner = '';
  if (f.type === 'path') inner += folderBtn(f.key, '打开所在文件夹');
  if (f.help) inner += '<div class="q" data-help="' + f.key + '">?</div>';
  return '<div class="acts">' + inner + '</div>';
}

function renderForm() {
  var html = '';
  GROUPS.forEach(function (g) {
    if (g.id === 'startup' || g.id === 'env') return;   // 由数据到位后单独渲染
    var fs = FIELDS.filter(function (f) { return f.group === g.id; });
    html += '<section id="sec-' + g.id + '"><h2>' + esc(g.label) + '</h2>';
    if (CROSS_NOTE[g.id]) html += '<div class="gnote">' + esc(CROSS_NOTE[g.id]) + '</div>';
    fs.forEach(function (f) {
      html += '<div class="f"><label for="f_' + f.key + '">' + esc(f.label) + '</label>'
            + control(f)
            + actsFor(f)
            + '<div class="row-msg" id="m_' + f.key + '"></div>'
            + (f.help ? '<div class="hint" id="h_' + f.key + '" hidden>' + esc(f.help) + '</div>' : '')
            + '</div>';
    });
    html += '</section>';
  });
  $('main').insertAdjacentHTML('beforeend', html);
}

function paint(values) {
  FIELDS.forEach(function (f) {
    var el = $('f_' + f.key);
    if (!el) return;
    var v = values[f.key];
    if (f.type === 'bool') {
      el.classList.toggle('on', !!v);
      el.setAttribute('aria-checked', !!v ? 'true' : 'false');
    } else if (f.type === 'choice') {
      var cur = (v === undefined || v === null) ? f.def : v;
      var pills = el.querySelectorAll('.opt');
      for (var i = 0; i < pills.length; i++) {
        pills[i].classList.toggle('active', pills[i].getAttribute('data-v') === String(cur));
      }
    } else {
      el.value = (v === undefined || v === null) ? '' : v;
    }
  });
}

function collect() {
  var out = {};
  FIELDS.forEach(function (f) {
    var el = $('f_' + f.key);
    if (!el) return;
    if (f.type === 'bool') {
      out[f.key] = el.classList.contains('on');
    } else if (f.type === 'choice') {
      var a = el.querySelector('.opt.active');
      out[f.key] = a ? a.getAttribute('data-v') : (f.def || '');
    } else {
      out[f.key] = el.value;
    }
  });
  return out;
}

/* ---------------- 校验 ---------------- */

function refreshValidation() {
  var v = collect();
  FIELDS.forEach(function (f) {
    var msg = $('m_' + f.key);
    var el = $('f_' + f.key);
    if (!msg || !el) return;
    if (f.type === 'bool') { el.classList.remove('bad'); msg.textContent = ''; msg.className = 'row-msg'; return; }
    var r = validateField(f, v[f.key]);
    el.classList.toggle('bad', r.level === 'err');
    if (r.level === 'err' || r.level === 'warn') {
      msg.textContent = r.msg;
      msg.className = 'row-msg ' + (r.level === 'err' ? 'err' : 'warn');
    } else if (msg.className.indexOf('check') >= 0 || msg.className.indexOf('good') >= 0
            || msg.className.indexOf('err') >= 0) {
      // 格式刚被修好：把上一条校验结论清掉，避免"合法但说不存在"的滞后提示
      msg.textContent = ''; msg.className = 'row-msg';
      lastSent.delete(f.key);
    } else {
      msg.textContent = ''; msg.className = 'row-msg';
    }
  });
  validateCross(v).forEach(function (e) {
    var msg = $('m_' + e.key);
    var el = $('f_' + e.key);
    if (msg) { msg.textContent = e.msg; msg.className = 'row-msg err'; }
    if (el) el.classList.add('bad');
  });
  return v;
}

function markDirty() {
  var v = collect();
  var changed = FIELDS.some(function (f) {
    var a = (v[f.key] === undefined || v[f.key] === null) ? '' : String(v[f.key]);
    var b = (baseline && baseline[f.key] !== undefined && baseline[f.key] !== null) ? String(baseline[f.key]) : '';
    return a !== b;
  });
  dirty = changed;
  setStatus(changed ? '未保存' : '已保存', changed ? 'dirty' : 'ok');
  $('btnSave').disabled = !changed;
  $('btnSaveRestart').disabled = !changed;
}

/* ---------------- 自动路径校验（批处理 + 节流） ---------------- */

var pendingVerify = new Map();
var verifyTimer = null;
var lastSent = new Map();

function showVerify(key, s) {
  var msg = $('m_' + key);
  var el = $('f_' + key);
  if (!msg || !el) return;
  if (s.level === 'none') { msg.textContent = ''; msg.className = 'row-msg'; return; }
  msg.textContent = s.text || '';
  msg.className = 'row-msg ' + s.level;
  if (s.level === 'err') el.classList.add('bad');
}

function queueVerify(f, value) {
  if (f.type !== 'path') return;
  var v = String(value === undefined || value === null ? '' : value).trim();
  // 格式就不合法时不发校验：否则会出现"红字说不合法 + 旁边写存在"的矛盾提示
  if (validateField(f, v).level === 'err') { lastSent.delete(f.key); return; }
  if (v === '') { lastSent.delete(f.key); showVerify(f.key, { level:'none' }); return; }
  if (lastSent.get(f.key) === v) return;                 // 同值不重复发
  pendingVerify.set(f.key, { key: f.key, path: v, kind: f.kind || 'dir' });
  showVerify(f.key, { level:'check', text:'校验中…' });
  if (!verifyTimer) verifyTimer = setTimeout(flushVerify, 150);
}

async function flushVerify() {
  verifyTimer = null;
  var list = Array.from(pendingVerify.values());
  pendingVerify.clear();
  if (!list.length) return;
  var r = await B.verify(list);
  if (!r.ok) {
    list.forEach(function (it) { showVerify(it.key, { level:'err', text:r.message }); });
    return;
  }
  r.results.forEach(function (it) {
    lastSent.set(it.key, it.path);
    if (it.exists) {
      var resolved = it.resolved && it.resolved.toLowerCase() !== String(it.path).toLowerCase()
        ? '（' + it.resolved + '）' : '';
      showVerify(it.key, { level:'good', text:'存在' + resolved });
    } else {
      showVerify(it.key, { level:'err', text:'不存在' });
    }
  });
}

/* ---------------- 顶栏高亮（滚动跟随） ---------------- */

function spyNav() {
  var main = $('main');
  var y = main.scrollTop + 28;
  var cur = null, topMost = Infinity;
  GROUPS.forEach(function (g) {
    var sec = $('sec-' + g.id);
    if (!sec) return;
    if (sec.offsetTop <= y && sec.offsetTop > (cur === null ? -Infinity : topMost)) { cur = g.id; topMost = sec.offsetTop; }
  });
  if (cur === null) {
    GROUPS.forEach(function (g) {
      var sec = $('sec-' + g.id);
      if (sec && sec.offsetTop < topMost) { cur = g.id; topMost = sec.offsetTop; }
    });
  }
  /* 已滚到底：最后一个区块的顶部永远到不了视口顶部（视口比它高），
     按"最接近顶部"的规则它永远选不中。滚到底就把高亮给最后一个分组。 */
  if (main.scrollTop + main.clientHeight >= main.scrollHeight - 4) {
    for (var gi = GROUPS.length - 1; gi >= 0; gi--) { if ($('sec-' + GROUPS[gi].id)) { cur = GROUPS[gi].id; break; } }
  }
  Array.prototype.forEach.call($('nav').children, function (el) {
    el.classList.toggle('active', el.getAttribute('data-g') === cur);
  });
}

/* ---------------- 「?」提示：同时只展开一个 ---------------- */

function toggleHelp(q) {
  var key = q.getAttribute('data-help');
  var box = $('h_' + key);
  if (!box) return;
  var wasOpen = !box.hidden;
  Array.prototype.forEach.call($('main').querySelectorAll('.hint'), function (b) { b.hidden = true; });
  Array.prototype.forEach.call($('main').querySelectorAll('.q'), function (b) { b.classList.remove('open'); });
  if (!wasOpen) { box.hidden = false; q.classList.add('open'); }
}
/* ---------------- 开机启动 / 环境信息 ---------------- */

var AUTO_ROWS = [
  { id: 'daemon', label: '后台守护进程' },
  { id: 'tray',   label: '托盘 DshLauncher' }
];

function renderStartup(states) {
  var html = '<section id="sec-startup"><h2>开机启动</h2>';
  AUTO_ROWS.forEach(function (r) {
    var s = (states && states[r.id]) || { state: 'off' };
    var txt = s.state === 'on' ? '已启用' : (s.state === 'error' ? '异常' : '未启用');
    html += '<div class="f"><label>' + esc(r.label) + '</label>'
          + '<div class="ctl" style="display:flex;align-items:center;gap:10px">'
          +   '<div class="switch' + (s.state === 'on' ? ' on' : '') + '" data-auto="' + r.id + '" role="switch"></div>'
          +   '<span class="badge ' + esc(s.state) + '" id="b_' + r.id + '">' + txt + '</span>'
          + '</div>'
          + '<div class="q" data-help="auto_' + r.id + '">?</div>'
          + '<div class="row-msg" id="m_auto_' + r.id + '">' + esc(s.note || '') + '</div>'
          + '<div class="hint" id="h_auto_' + r.id + '" hidden>写入当前用户的启动文件夹：'
          +   esc(window.__startupDir || '') + '\\</div>'
          + '</div>';
  });
  html += '</section>';
  $('main').insertAdjacentHTML('beforeend', html);
  bindAuto();
  bindHelp();
}

function repaintAuto(states) {
  AUTO_ROWS.forEach(function (r) {
    var el = $('main').querySelector('[data-auto="' + r.id + '"]');
    var b = $('b_' + r.id);
    if (!el || !b) return;
    var s = (states && states[r.id]) || { state: 'off' };
    el.classList.toggle('on', s.state === 'on');
    b.className = 'badge ' + s.state;
    b.textContent = s.state === 'on' ? '已启用' : (s.state === 'error' ? '异常' : '未启用');
    var msg = $('m_auto_' + r.id);
    if (msg) { msg.textContent = s.note || ''; msg.className = 'row-msg' + (s.note ? ' warn' : ''); }
  });
}

function bindAuto() {
  Array.prototype.forEach.call($('main').querySelectorAll('[data-auto]'), function (el) {
    el.onclick = async function () {
      el.style.pointerEvents = 'none';
      var wasOn = el.classList.contains('on');
      var r = await B.setAutostart(wasOn ? 'disable' : 'enable', el.getAttribute('data-auto'));
      el.style.pointerEvents = '';
      if (!r.ok) { setStatus(r.message || '操作失败', 'err'); if (r.states) repaintAuto(r.states); return; }
      repaintAuto(r.states);
      setStatus(r.message, 'ok');
    };
  });
}

function bindHelp() {
  Array.prototype.forEach.call($('main').querySelectorAll('.q'), function (q) {
    if (q.__bound) return;
    q.__bound = true;
    q.onclick = function () { toggleHelp(q); };
  });
}

/* 环境信息里可打开的三行：key -> 要打开的路径 */
var envOpenTargets = {};

function renderEnv(env) {
  envOpenTargets = {
    installRoot: env.installRoot || '',
    configFile:  env.configFile  || '',
    startupDir:  env.startupDir  || ''
  };
  var rows = [
    ['安装目录',   env.installRoot, 'env:installRoot'],
    ['配置文件',   env.configFile,  'env:configFile'],
    ['启动文件夹', env.startupDir,  'env:startupDir'],
    ['DshConfigHost', 'DshConfigHost ' + env.hostVersion, null],
    ['WebView2',   env.webview2Version, null],
    ['守护进程',   env.daemonRunning ? ('运行中 · PID ' + env.daemonPid) : '未运行', null]
  ];
  var html = '<section id="sec-env"><h2>环境信息</h2><div class="env">';
  rows.forEach(function (r) {
    html += '<span class="k">' + esc(r[0]) + '</span>'
          + '<span class="v"' + (r[0] === '守护进程' ? ' id="v_daemon"' : '') + '>' + esc(r[1]) + '</span>'
          + '<span class="acts">' + (r[2] ? folderBtn(r[2], '打开所在文件夹') : '') + '</span>';
  });
  html += '</div></section>';
  $('main').insertAdjacentHTML('beforeend', html);
}

function renderEnvRefresh(env) {
  var el = $('v_daemon');
  if (el) el.textContent = env.daemonRunning ? ('运行中 · PID ' + env.daemonPid) : '未运行';
}

/* ---------------- 四个动作 ---------------- */

async function doSave(silent) {
  var btn = silent ? $('btnSaveRestart') : $('btnSave');
  var old = btn.textContent;
  if (!silent) { btn.textContent = '保存中…'; btn.disabled = true; }
  try {
    refreshValidation();
    var r = await B.write(buildEdits(collect()));
    if (!r.ok) throw new Error(r.message || '保存失败');

    var back = await B.read();
    if (!back.ok) throw new Error('写入后回读失败：' + back.message);
    baseline = toUiValues(parseConfig(back.text));
    paint(baseline);
    dirty = false;
    setStatus(r.message, 'ok');
    $('btnSave').disabled = true;
    $('btnSaveRestart').disabled = true;
    if (r.skipped && r.skipped.length) setStatus(r.message + '（未匹配：' + r.skipped.join('、') + '）', 'warn');
    return r;
  } finally {
    /* 只在仍然脏的时候恢复可用：成功路径下 dirty 已是 false，
       无条件恢复会让「保存」在保存完成后仍可点，再点就是无意义的重复写入。 */
    if (!silent) { btn.textContent = old; if (dirty) btn.disabled = false; }
  }
}

async function doRestart() {
  var btn = $('btnSaveRestart');
  var old = btn.textContent;
  btn.disabled = true;
  btn.textContent = '重启中…';
  setStatus('正在重启守护进程…', '');
  var kick = await B.restart();
  if (!kick.ok) {
    btn.disabled = false; btn.textContent = old;
    setStatus(kick.message, 'err');
    return;
  }
  var r = await waitRestart(30000);
  btn.disabled = false; btn.textContent = old;
  setStatus(r.message || (r.ok ? '已重启' : '重启失败'), r.ok ? 'ok' : 'err');
  renderEnvRefresh(await B.getEnv());
}

async function doDefaults() {
  if (dirty && !confirm('恢复默认会丢弃尚未保存的修改。继续吗？')) return;
  var d = await B.defaults();
  if (!d.ok) { setStatus(d.message, 'err'); return; }
  var merged = {};
  FIELDS.forEach(function (f) {
    var v = (d.values && d.values[f.key] !== undefined) ? d.values[f.key] : f.def;
    if (v === undefined) return;
    merged[f.key] = (f.type === 'bool') ? (f.invert ? !v : !!v) : v;
  });
  paint(Object.assign(collect(), merged));
  markDirty();
  FIELDS.forEach(function (f) { if (f.type === 'path') { lastSent.delete(f.key); queueVerify(f, merged[f.key]); } });
}

async function doReset() {
  var willRevertAuto = autoChangedAgainstBaseline();
  var tip = '重置会丢弃尚未保存的修改，恢复成磁盘上的配置。';
  if (willRevertAuto) tip += '\n开机启动的改动也会一并撤销回打开窗口时的状态。';
  if ((dirty || willRevertAuto) && !confirm(tip + '\n继续吗？')) return;

  var r = await B.read();
  if (!r.ok) { setStatus(r.message, 'err'); return; }
  baseline = toUiValues(parseConfig(r.text));
  paint(baseline);
  markDirty();
  FIELDS.forEach(function (f) { if (f.type === 'path') { lastSent.delete(f.key); queueVerify(f, baseline[f.key]); } });

  var reverted = await revertAutostart();
  var msg = '已重置为磁盘内容';
  if (reverted && reverted.length) msg += '；开机启动已撤销：' + reverted.join('、');
  setStatus(msg, 'ok');
}

/* 自启当前状态与打开窗口时相比是否被改过（只比较 on/off 两种可撤销状态） */
function autoChangedAgainstBaseline() {
  if (!autoBaseline) return false;
  var cur = readAutoNow();
  var ids = ['daemon', 'tray'];
  for (var i = 0; i < ids.length; i++) {
    if (desiredOf(autoBaseline[ids[i]]) !== desiredOf(cur[ids[i]])) return true;
  }
  return false;
}

/* 从界面上的拨杆读当前意图状态（比再问一次 DshConfigHost 便宜，且与用户所见一致） */
function readAutoNow() {
  var out = {};
  ['daemon', 'tray'].forEach(function (id) {
    var el = $('main').querySelector('[data-auto="' + id + '"]');
    out[id] = (el && el.classList.contains('on')) ? 'on' : 'off';
  });
  return out;
}

/* error 状态不可撤销（我们不知道该恢复成什么），保持原样 */
function desiredOf(state) {
  if (!state) return null;
  var s = (typeof state === 'string') ? state : (state.state || null);
  return (s === 'on' || s === 'off') ? s : null;
}

async function revertAutostart() {
  if (!autoBaseline) return null;
  var want = { daemon: desiredOf(autoBaseline.daemon), tray: desiredOf(autoBaseline.tray) };
  var now = readAutoNow();
  var done = [];
  var ids = ['daemon', 'tray'];
  for (var i = 0; i < ids.length; i++) {
    var id = ids[i];
    if (!want[id] || want[id] === now[id]) continue;
    var r = await B.setAutostart(want[id] === 'on' ? 'enable' : 'disable', id);
    if (r && r.states) repaintAuto(r.states);
    done.push(autoLabel(id) + (want[id] === 'on' ? '启用' : '停用'));
  }
  return done;
}

function autoLabel(id) {
  return id === 'daemon' ? '后台守护进程' : '托盘程序';
}

/* ---------------- 启动 ---------------- */

async function init() {
  renderNav();
  renderForm();

  $('btnSave').disabled = true;
  $('btnSaveRestart').disabled = true;

  var r = await B.read();
  if (!r.ok) {
    window.__ERRMSG = r.message || '';
    setStatus('读取失败', 'err');
    $('main').innerHTML = '<section><h2>无法继续</h2><div class="row-msg err" style="grid-column:1/-1">'
                        + esc(r.message) + '</div></section>';
    return;
  }
  baseline = toUiValues(parseConfig(r.text));
  paint(baseline);
  setStatus('已保存', 'ok');

  var env = await B.getEnv();
  window.__startupDir = env.startupDir || '';
  var auto = await B.autostart();
  autoBaseline = JSON.parse(JSON.stringify(auto.states || {}));
  renderStartup(auto.states);
  renderEnv(env);

  // 「打开方式」来自 DshLauncher.json（不是 ps1），单独渲染 + 读当前值
  renderOpenMode();
  try {
    var om = await B.getOpenMode();
    if (om && om.ok) {
      openModeState.mode = (om.mode === 'app') ? 'app' : 'browser';
      openModeState.appId = om.appId || '';
      paintOpenMode();
    }
  } catch (e) { /* 读不到就保持默认 browser，不影响其它功能 */ }

  FIELDS.forEach(function (f) { if (f.type === 'path') queueVerify(f, baseline[f.key]); });

  $('main').addEventListener('input', function (e) {
    if (!e.target.matches || !e.target.matches('input')) return;
    var f = FIELDS.filter(function (x) { return 'f_' + x.key === e.target.id; })[0];
    refreshValidation();
    markDirty();
    if (f) queueVerify(f, e.target.value);
  });
  /* 只绑字段开关：自启拨杆也是 .switch，宽选择器会覆盖 bindAuto 装的处理器 */
  Array.prototype.forEach.call($('main').querySelectorAll('.switch[id^="f_"]'), function (el) {
    el.onclick = function () { el.classList.toggle('on'); markDirty(); };
  });
  $('main').addEventListener('scroll', spyNav);
  bindHelp();

  /* 「打开所在文件夹」用事件委托：环境信息区块是异步插入的，逐元素绑会漏。
     而且它必须排在 .switch 的绑定之后，否则点图标会连带触发开关。 */
  $('main').addEventListener('click', function (e) {
    var btn = e.target && e.target.closest ? e.target.closest('.ico[data-open]') : null;
    if (!btn) return;
    openFolderOf(btn);
  });

  /* 「打开方式」的胶囊点击。它走 DshLauncher.json、切换即写盘，
     所以先写盘成功、再由 applyOpenMode 点亮选项 —— 写失败不留错误的视觉状态。
     故意不调 markDirty：这一项与 ps1 的脏标记无关。 */
  $('main').addEventListener('click', function (e) {
    var opt = e.target && e.target.closest ? e.target.closest('.opt') : null;
    if (!opt) return;
    var key = opt.getAttribute('data-key');
    if (key !== 'OPEN_MODE') return;
    var v = opt.getAttribute('data-v');
    if (v === openModeState.mode && !(v === 'app' && !openModeState.appId)) return;  // 已选中就不重复请求
    onAppModePick(v).catch(function (err) { setStatus('操作失败：' + err.message, 'err'); });
  });

  $('btnSave').onclick = function () { doSave(false).catch(function (e) { setStatus(e.message, 'err'); }); };
  $('btnSaveRestart').onclick = function () {
    (dirty ? doSave(true) : Promise.resolve())
      .then(doRestart)
      .catch(function (e) { setStatus(e.message, 'err'); });
  };
  $('btnReset').onclick = function () { doReset().catch(function (e) { setStatus(e.message, 'err'); }); };
  $('btnDefaults').onclick = function () { doDefaults().catch(function (e) { setStatus(e.message, 'err'); }); };

  window.addEventListener('beforeunload', function (e) {
    if (dirty) { e.preventDefault(); e.returnValue = ''; }
  });

  spyNav();
  /* 供 --page-test 读取的真实「可交互」时刻：从 navigationStart 算起 */
  window.__READY_MS = Math.round(performance.now());
}

init();