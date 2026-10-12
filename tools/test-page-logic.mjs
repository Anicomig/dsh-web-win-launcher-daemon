/* 配置页纯逻辑单测。
 *
 * 为什么用 Node 而不是 xUnit：这些函数活在 app.js 里，是页面的真源；
 * 用 dotnet 测就得把它们复制一份到 C#，复制出来的东西测不出原件的错。
 * app.js 里有一条分界标记把纯逻辑和碰 DOM 的代码隔开，所以这里可以直接
 * 加载前半段 —— 不需要任何"正则剥离"的脆弱夹具。
 *
 * 跑法：node --test tools\test-page-logic.mjs
 */
import fs from 'node:fs';
import path from 'node:path';
import { test } from 'node:test';
import assert from 'node:assert/strict';

const ROOT = path.resolve(new URL('.', import.meta.url).pathname.replace(/^\/(\w:)/, '$1'), '..');
const APP = path.join(ROOT, 'ConfigHost', 'wwwroot', 'app.js');

const src = fs.readFileSync(APP, 'utf8');
const MARK = '/* ===== 以下是 UI =====';
const cut = src.indexOf(MARK);
assert.ok(cut > 0, 'app.js 缺少分界标记 ' + MARK);

const pure = src.slice(0, cut);
assert.ok(!/\bwindow\.|\bdocument\./.test(pure), '分界标记之前的代码不应碰 window/document');

const P = new Function(pure + '\nreturn { GROUPS, FIELDS, CROSS_NOTE, parseConfig, validateField, validateCross, buildEdits, toUiValues, esc };')();
const { GROUPS, FIELDS, parseConfig, validateField, validateCross, buildEdits, toUiValues } = P;

const CFG = [
  '# ===== 配置 =====',
  '$CONFIG_PORT                       = 3080              # dsh web 默认端口',
  '$CONFIG_HOST                       = "127.0.0.1"',
  '$CONFIG_NO_OPEN                    = $true',
  '$CONFIG_GATEWAY_LOG_ENABLED        = $false',
  '$CONFIG_DSH_PROFILE                = "web"',
  '$CONFIG_DSH_HOME                   = "C:\\Users\\example\\.dsh"',
  '$CONFIG_WORKSPACE_DIR              = "D:\\work\\projects"',
  '$CONFIG_NODE_EXE                   = "C:\\Program Files\\nodejs\\node.exe"',
  '$CONFIG_DSH_CLI                    = "C:\\Users\\example\\AppData\\Roaming\\npm\\node_modules\\@deepseek-ai\\dsh\\lib\\bin.js"',
  '$CONFIG_PORT_CHECK_INTERVAL        = 2',
  '$CONFIG_PROC_CHECK_INTERVAL        = 3',
  '$CONFIG_DAEMON_PORT_ONLY_INTERVAL  = 15',
  '$CONFIG_LAUNCH_GRACE_SEC           = 120',
  '$CONFIG_STARTUP_TIMEOUT_SEC        = 180',
  '$CONFIG_RESTART_BACKOFF_MAX_SEC    = 300',
].join('\n');

test('T1 字段表规模与分组归属', () => {
  assert.equal(FIELDS.length, 15);
  assert.equal(GROUPS.length, 5);
  const ids = new Set(GROUPS.map(g => g.id));
  FIELDS.forEach(f => assert.ok(ids.has(f.group), f.key + ' 的 group 不在 GROUPS 里'));
  const byGroup = {};
  FIELDS.forEach(f => { byGroup[f.group] = (byGroup[f.group] || 0) + 1; });
  assert.deepEqual(byGroup, { runtime: 5, paths: 4, timing: 6 });
});

test('T1 varName 唯一且符合宿主侧白名单', () => {
  const names = FIELDS.map(f => f.varName);
  assert.equal(new Set(names).size, FIELDS.length);
  names.forEach(n => assert.match(n, /^CONFIG_[A-Z0-9_]{1,40}$/));
});

test('T1 parseConfig 覆盖全部字段', () => {
  const v = parseConfig(CFG);
  FIELDS.forEach(f => assert.ok(v[f.key] !== undefined, '漏解析 ' + f.key));
  assert.equal(v.PORT, 3080);
  assert.equal(v.HOST, '127.0.0.1');
  assert.equal(v.NO_OPEN, true);
  assert.equal(v.GATEWAY_LOG_ENABLED, false);
});

test('★ 带空格的路径必须被完整解析（\S+ 会把这类值截断）', () => {
  const v = parseConfig(CFG);
  assert.equal(v.NODE_EXE, 'C:\\Program Files\\nodejs\\node.exe');
  assert.ok(v.WORKSPACE_DIR.includes('work'), 'WORKSPACE_DIR=' + v.WORKSPACE_DIR);
  assert.ok(v.DSH_CLI.includes('@deepseek-ai\\dsh\\lib\\bin.js'));
});

test('T7 数值范围与格式', () => {
  const port = FIELDS.find(f => f.key === 'PORT');
  assert.equal(validateField(port, 3080).level, 'ok');
  assert.equal(validateField(port, 80).level, 'warn');       // warnUnder=1024
  assert.equal(validateField(port, 0).level, 'err');
  assert.equal(validateField(port, 70000).level, 'err');
  assert.equal(validateField(port, 'abc').level, 'err');
  assert.equal(validateField(port, '').level, 'err');
  assert.equal(validateField(port, 3080.5).level, 'err');    // 非整数

  const only = FIELDS.find(f => f.key === 'DAEMON_PORT_ONLY_INTERVAL');
  assert.equal(validateField(only, 1).level, 'err');         // min 是 2 不是 1
  assert.equal(validateField(only, 2).level, 'ok');
});

test('T7 路径类字段的格式规则', () => {
  const cli = FIELDS.find(f => f.key === 'DSH_CLI');
  assert.equal(validateField(cli, 'C:\\a\\bin.js').level, 'err');
  assert.equal(validateField(cli, 'C:\\x\\@deepseek-ai\\dsh\\lib\\bin.js').level, 'ok');

  const node = FIELDS.find(f => f.key === 'NODE_EXE');
  assert.equal(validateField(node, 'C:\\n.exe').level, 'err');
  assert.equal(validateField(node, 'D:\\Program Files\\nodejs\\node.exe').level, 'ok');
  assert.equal(validateField(node, 'D:\\nvm\\node.exe').level, 'warn');   // 没有 nodejs

  const home = FIELDS.find(f => f.key === 'DSH_HOME');
  assert.equal(validateField(home, 'C:\\Users\\me\\dsh').level, 'err');
  assert.equal(validateField(home, 'C:\\Users\\me\\.dsh').level, 'ok');

  const prof = FIELDS.find(f => f.key === 'DSH_PROFILE');
  assert.equal(validateField(prof, 'desktop').level, 'err');
  assert.equal(validateField(prof, 'web').level, 'ok');
  assert.equal(validateField(prof, 'a b').level, 'err');
});

test('T7 跨字段：宽限≥超时、轮询为 0', () => {
  const bad = validateCross({ LAUNCH_GRACE_SEC: 200, STARTUP_TIMEOUT_SEC: 180,
                              PORT_CHECK_INTERVAL: 2, PROC_CHECK_INTERVAL: 3 });
  assert.equal(bad.length, 1);
  assert.equal(bad[0].key, 'LAUNCH_GRACE_SEC');

  const zero = validateCross({ LAUNCH_GRACE_SEC: 120, STARTUP_TIMEOUT_SEC: 180,
                               PORT_CHECK_INTERVAL: 0, PROC_CHECK_INTERVAL: 0 });
  assert.equal(zero.length, 2);
  zero.forEach(e => assert.ok(e.msg.includes('不得为 0')));

  assert.equal(validateCross({ LAUNCH_GRACE_SEC: 120, STARTUP_TIMEOUT_SEC: 180,
                               PORT_CHECK_INTERVAL: 2, PROC_CHECK_INTERVAL: 3 }).length, 0);
  // 相等也算违规（须"小于"）
  assert.equal(validateCross({ LAUNCH_GRACE_SEC: 180, STARTUP_TIMEOUT_SEC: 180,
                               PORT_CHECK_INTERVAL: 2, PROC_CHECK_INTERVAL: 3 }).length, 1);
});

test('T5 NO_OPEN 取反：界面 on -> 存 false；界面 off -> 存 true', () => {
  const stored = parseConfig(CFG);                 // 磁盘上 NO_OPEN = $true（不弹）
  assert.equal(stored.NO_OPEN, true);

  const ui = toUiValues(stored);
  assert.equal(ui.NO_OPEN, false, '磁盘不弹 -> 界面应显示"不打开"');

  const e1 = buildEdits({ ...ui, NO_OPEN: true }).find(x => x.name === 'CONFIG_NO_OPEN');
  assert.equal(e1.value, false, '界面打开 -> NO_OPEN 必须 false');
  assert.equal(e1.kind, 'bool');

  const e2 = buildEdits(ui).find(x => x.name === 'CONFIG_NO_OPEN');
  assert.equal(e2.value, true, '界面关闭 -> NO_OPEN 必须 true');
});

test('T5 往返闭合：磁盘 -> 界面 -> edits 必须与磁盘原值一致', () => {
  const stored = parseConfig(CFG);
  const round = {};
  buildEdits(toUiValues(stored)).forEach(e => {
    round[e.name.replace(/^CONFIG_/, '')] = e.value;
  });
  FIELDS.filter(f => !f.readonly).forEach(f => {
    assert.deepEqual(round[f.key], stored[f.key], f.key + ' 往返后变了');
  });
});

test('GATEWAY_LOG_ENABLED 是正向语义，不取反', () => {
  const e = buildEdits({ GATEWAY_LOG_ENABLED: true }).find(x => x.name === 'CONFIG_GATEWAY_LOG_ENABLED');
  assert.equal(e.value, true);
});

test('readonly 字段不进 edits（HOST 不可写）', () => {
  const edits = buildEdits({ HOST: '0.0.0.0', PORT: 3080 });
  assert.ok(!edits.some(e => e.name === 'CONFIG_HOST'));
  const readonlyCount = FIELDS.filter(f => f.readonly).length;
  assert.equal(edits.length, FIELDS.length - readonlyCount);
});

test('path 字段的 kind 是 str，且宿主白名单接受', () => {
  const e = buildEdits({ NODE_EXE: 'D:\\Program Files\\nodejs\\node.exe' })
                    .find(x => x.name === 'CONFIG_NODE_EXE');
  assert.equal(e.kind, 'str');
  assert.match(e.name, /^CONFIG_[A-Z0-9_]{1,40}$/);
});

test('int 值以数字而非字符串送出去', () => {
  const e = buildEdits({ PORT: '3080' }).find(x => x.name === 'CONFIG_PORT');
  assert.equal(typeof e.value, 'number');
  assert.equal(e.value, 3080);
});

test('提示文案：非空、无换行、无冗余前缀', () => {
  const withHelp = FIELDS.filter(f => f.help);
  assert.ok(withHelp.length >= 13, '绝大多数可编辑字段都该有说明');
  withHelp.forEach(f => {
    assert.ok(!/[\r\n]/.test(f.help), f.key + ' 的说明含换行');
    assert.ok(!/^(提示|注意)：/.test(f.help), f.key + ' 的说明有冗余前缀');
    assert.ok(f.help.length <= 120, f.key + ' 的说明过长（' + f.help.length + '），应精简');
  });
});

test('建议值已按评审要求写进说明', () => {
  const find = k => FIELDS.find(f => f.key === k).help;
  assert.match(find('PORT'), /建议 3000[–-]3999/);
  assert.match(find('DSH_PROFILE'), /建议(使用)? web/);
  assert.match(find('DSH_HOME'), /用户目录/);
  assert.match(find('PORT_CHECK_INTERVAL'), /建议 2 秒/);
  assert.match(find('PROC_CHECK_INTERVAL'), /建议 3 秒/);
  assert.match(find('DAEMON_PORT_ONLY_INTERVAL'), /建议 15 秒/);
  assert.match(find('LAUNCH_GRACE_SEC'), /建议 120 秒/);
  assert.match(find('STARTUP_TIMEOUT_SEC'), /建议 180 秒/);
  assert.match(find('RESTART_BACKOFF_MAX_SEC'), /建议 300 秒/);
});

test('默认值与字段定义自洽（def 必须落在 min..max 内）', () => {
  FIELDS.filter(f => f.type === 'int').forEach(f => {
    assert.ok(f.def >= f.min && f.def <= f.max, f.key + ' 的 def 超出范围');
  });
});