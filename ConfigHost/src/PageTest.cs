using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ConfigHost
{
    /// <summary>
    /// --page-test / --page-test-write：在真实 WebView2 里加载页面并跑 DOM 断言。
    ///
    /// 五条实测教训都固化在这里：
    ///   1) 必须 AddHostObjectToScript，否则 hostObjects.bridge 是空代理，
    ///      调用任何方法都返回「找不到元素 0x80070490」。
    ///   2) 断言脚本里不许用 setTimeout —— ExecuteScriptAsync 只拿同步返回值，
    ///      异步补的结果永远收不到（会假绿）。要异步就分阶段，中间在 C# 侧等。
    ///   3) 页面 CSS 的 scroll-behavior:smooth 会让「赋值 scrollTop」也变异步，
    ///      测试里要先临时改成 auto。
    ///   4) 诊断脚本不要调用 renderNav/renderForm 这类往 DOM 追加的函数，
    ///      否则字段被渲染两遍，数量断言全废。
    ///   5) 重定向下 Console.OutputEncoding 赋值可能失败，要换 Console.SetOut，
    ///      且 Environment.Exit 不刷缓冲，结束前必须 Flush。
    ///
    /// --page-test        只读断言（不点保存、不写文件）
    /// --page-test-write  额外做写入回路：真点保存、断言磁盘内容、最后必须逐字节还原
    /// </summary>
    internal static class PageTest
    {
        public static int Run(bool alsoWrite)
        {
            try { Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false))); }
            catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            int code;
            using (var f = new TestForm(alsoWrite))
            {
                Application.Run(f);
                code = f.ExitCode;
            }
            try { Console.Out.Flush(); } catch { }
            return code;
        }

        private sealed class TestForm : Form, IRestartHost
        {
            public int ExitCode = 1;
        public string WebView2Version { get; private set; }

        // 测试窗口用与生产完全相同的 RestartFlow，只是把结果推给自己的 WebView2
        void IRestartHost.RequestRestart()
        {
            RestartFlow.Begin(AppEnv.DaemonExe, AppEnv.InstallRoot, json =>
            {
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (_web != null && _web.CoreWebView2 != null)
                            _web.CoreWebView2.PostWebMessageAsJson(json);
                    }));
                }
                catch { }
            });
        }
            private readonly bool _alsoWrite;
            private readonly WebView2 _web = new WebView2 { Dock = DockStyle.Fill };

            public TestForm(bool alsoWrite)
            {
                _alsoWrite = alsoWrite;
                Text = "page-test";
                StartPosition = FormStartPosition.Manual;
                Location = new Point(-4000, -4000);   // 屏幕外，不打扰用户
                Size = new Size(960, 780);
                ShowInTaskbar = false;
                Controls.Add(_web);
                Shown += async (s, e) => await Go();
            }

            private async Task Go()
            {
                int pass = 0, fail = 0;
                try
                {
                    var udf = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "DshConfigHost", "WebView2");
                    Directory.CreateDirectory(udf);
                    var env = await CoreWebView2Environment.CreateAsync(null, udf, null);
                    await _web.EnsureCoreWebView2Async(env);
                    _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                        "dshconfig.local", Path.Combine(AppContext.BaseDirectory, "wwwroot"),
                        CoreWebView2HostResourceAccessKind.DenyCors);
                    _web.CoreWebView2.AddHostObjectToScript("bridge", new HostBridge(this));
                    HostWindow.ConfigureSecurity(_web.CoreWebView2);   // 与生产同一套安全配置

                    var nav = new TaskCompletionSource<bool>();
                    _web.CoreWebView2.NavigationCompleted += (a, b) => nav.TrySetResult(true);
                    _web.CoreWebView2.Navigate("https://dshconfig.local/index.html");
                    if (await Task.WhenAny(nav.Task, Task.Delay(20000)) != nav.Task)
                    {
                        Console.WriteLine("FAIL  页面导航超时");
                        Console.WriteLine("===== 合计 0 通过 / 1 失败 =====");
                        ExitCode = 1;
                        return;
                    }
                    await Task.Delay(3500);   // 等 init 的异步链跑完

                    var a = Unquote(await _web.CoreWebView2.ExecuteScriptAsync(SCRIPT_A));
                    Console.WriteLine(a);
                    Count(a, ref pass, ref fail);

                    await Task.Delay(1500);   // 等平滑滚动与 scroll 事件落地
                    var b = Unquote(await _web.CoreWebView2.ExecuteScriptAsync(SCRIPT_B));
                    Console.WriteLine(b);
                    Count(b, ref pass, ref fail);

                    /* 阶段 C：恢复默认 + 重置。doDefaults/doReset 是 async 的，
                       必须等它跑完再断言，所以和写入回路一样用 window.__C 轮询。
                       全程不点保存，因此不会写文件；结尾的重置会把表单还原成磁盘内容。 */
                    _ = await _web.CoreWebView2.ExecuteScriptAsync(SCRIPT_C);
                    string cg = "";
                    for (int i = 0; i < 40; i++)
                    {
                        await Task.Delay(400);
                        cg = Unquote(await _web.CoreWebView2.ExecuteScriptAsync("window.__C || ''"));
                        if (!string.IsNullOrEmpty(cg)) break;
                    }
                    if (string.IsNullOrEmpty(cg)) cg = "FAIL  阶段C超时未返回";
                    Console.WriteLine(cg);
                    Count(cg, ref pass, ref fail);

                    if (_alsoWrite)
                    {
                        var wr = await RunWriteRoundTrip();
                        Console.WriteLine(wr.Item1);
                        Count(wr.Item1, ref pass, ref fail);
                        if (wr.Item2)
                        {
                            Console.WriteLine("PASS  写入回路结束后配置文件逐字节还原");
                            pass++;
                        }
                        else
                        {
                            Console.WriteLine("FAIL  写入回路结束后配置文件未还原成原始字节");
                            fail++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("FAIL  page-test 异常: " + ex.Message);
                    fail++;
                }

                    if (_alsoWrite)
                    {
                        /* 阶段 D：自启开关 + 「重置」撤销。会真的往启动文件夹写一次再删掉，
                           净效果为零，所以只在 write 模式跑。 */
                        _ = await _web.CoreWebView2.ExecuteScriptAsync(SCRIPT_D);
                        string dg = "";
                        for (int i = 0; i < 40; i++)
                        {
                            await Task.Delay(400);
                            dg = Unquote(await _web.CoreWebView2.ExecuteScriptAsync("window.__D || ''"));
                            if (!string.IsNullOrEmpty(dg)) break;
                        }
                        if (string.IsNullOrEmpty(dg)) dg = "FAIL  阶段D超时未返回";
                        Console.WriteLine(dg);
                        Count(dg, ref pass, ref fail);
                    }

                    /* 阶段 E：外部导航必须被拦下（放在最后，万一真跳走了也不影响前面） */
                    _ = await _web.CoreWebView2.ExecuteScriptAsync(SCRIPT_E);
                    string eg = "";
                    for (int i = 0; i < 30; i++)
                    {
                        await Task.Delay(400);
                        eg = Unquote(await _web.CoreWebView2.ExecuteScriptAsync("window.__E || ''"));
                        if (!string.IsNullOrEmpty(eg)) break;
                    }
                    if (string.IsNullOrEmpty(eg)) eg = "FAIL  阶段E超时未返回";
                    Console.WriteLine(eg);
                    Count(eg, ref pass, ref fail);

                    if (_alsoWrite)
                    {
                    /* 阶段 G：只测桥边界的拒绝分支（不会启动任何进程）。
                       「真的打开文件夹」及 file->父目录 规则由
                       tests\FolderTargetTests.cs 用纯函数覆盖 9 条；
                       启动 explorer 那一行留给人工点一次确认 —— 自动化开
                       Explorer 窗口收不干净（窗口属共享 explorer 进程，
                       按标题关不准，删目标目录还会留下错误对话框）。 */
                    _ = await _web.CoreWebView2.ExecuteScriptAsync(SCRIPT_G);
                    string gg = "";
                    for (int i = 0; i < 60; i++)
                    {
                        await Task.Delay(300);
                        gg = Unquote(await _web.CoreWebView2.ExecuteScriptAsync("window.__G || ''"));
                        if (!string.IsNullOrEmpty(gg)) break;
                    }
                    if (string.IsNullOrEmpty(gg)) gg = "FAIL  阶段G超时未返回";
                    gg = GuardNoAssertions(gg, "阶段G");
                    Console.WriteLine(gg);
                    Count(gg, ref pass, ref fail);
                    }

                    /* 阶段 H：真实 Edge 数据下的网页应用检测。只读 Edge 的 Sessions，
                       不写 Edge 任何文件。放在 G 之后、F 之前。 */
                    _ = await _web.CoreWebView2.ExecuteScriptAsync(SCRIPT_H);
                    string hg = "";
                    for (int i = 0; i < 40; i++)
                    {
                        await Task.Delay(400);
                        hg = Unquote(await _web.CoreWebView2.ExecuteScriptAsync("window.__H || ''"));
                        if (!string.IsNullOrEmpty(hg)) break;
                    }
                    if (string.IsNullOrEmpty(hg)) hg = "FAIL  阶段H超时未返回";
                    hg = GuardNoAssertions(hg, "阶段H");
                    Console.WriteLine(hg);
                    Count(hg, ref pass, ref fail);
                    if (_alsoWrite)
                    {
                        /* 阶段 F：H4 + H14。真的重启守护进程（用户已授权）。
                           断言 RestartDaemon 立即返回、结果靠 message 事件回填、
                           新 daemon PID 与旧的不同。 */
                        _ = await _web.CoreWebView2.ExecuteScriptAsync(SCRIPT_F);
                        string fg = "";
                        for (int i = 0; i < 20; i++)
                        {
                            await Task.Delay(300);
                            fg = Unquote(await _web.CoreWebView2.ExecuteScriptAsync("window.__F || ''"));
                            if (!string.IsNullOrEmpty(fg)) break;
                        }
                        if (string.IsNullOrEmpty(fg)) fg = "FAIL  阶段F1超时未返回";
                        Console.WriteLine(fg);
                        Count(fg, ref pass, ref fail);

                        string fm = "";
                        for (int i = 0; i < 100; i++)
                        {
                            await Task.Delay(300);
                            fm = Unquote(await _web.CoreWebView2.ExecuteScriptAsync("window.__F_MSG || ''"));
                            if (!string.IsNullOrEmpty(fm)) break;
                        }
                        string f2;
                        if (string.IsNullOrEmpty(fm))
                        {
                            f2 = "FAIL  未收到 restart-daemon 的完成消息（100 次轮询超时）";
                        }
                        else
                        {
                            _ = await _web.CoreWebView2.ExecuteScriptAsync(SCRIPT_F2);
                            f2 = "";
                            for (int i = 0; i < 40; i++)
                            {
                                await Task.Delay(300);
                                f2 = Unquote(await _web.CoreWebView2.ExecuteScriptAsync("window.__F2 || ''"));
                                if (!string.IsNullOrEmpty(f2)) break;
                            }
                            if (string.IsNullOrEmpty(f2)) f2 = "FAIL  阶段F2超时未返回";
                        }
                        // 防呆：一个脚本若没产出任何 PASS/FAIL，说明它压根没执行（例如
                        // 误用 async IIFE 拿到 {}），绝不能算通过。
                        f2 = GuardNoAssertions(f2, "阶段F2");
                        Console.WriteLine("INFO  完成消息 = " + fm);
                        Console.WriteLine(f2);
                        Count(f2, ref pass, ref fail);
                    }

                    Console.WriteLine("===== 合计 " + pass + " 通过 / " + fail + " 失败 =====");
                ExitCode = fail == 0 ? 0 : 1;
                Close();
            }

            /// <summary>
            /// 真点「保存」，断言磁盘内容，最后必须把文件还原成原始字节。
            /// 脚本是 async 的，ExecuteScriptAsync 拿不到结果，所以轮询 window.__W。
            /// </summary>
            private async Task<Tuple<string, bool>> RunWriteRoundTrip()
            {
                byte[] original = File.Exists(AppEnv.ConfigFile)
                    ? File.ReadAllBytes(AppEnv.ConfigFile) : new byte[0];

                _ = await _web.CoreWebView2.ExecuteScriptAsync(SCRIPT_W);

                string got = "";
                for (int i = 0; i < 80; i++)
                {
                    await Task.Delay(500);
                    got = Unquote(await _web.CoreWebView2.ExecuteScriptAsync("window.__W || ''"));
                    if (!string.IsNullOrEmpty(got)) break;
                }
                if (string.IsNullOrEmpty(got)) got = "FAIL  写入回路超时未返回";

                byte[] now = File.Exists(AppEnv.ConfigFile)
                    ? File.ReadAllBytes(AppEnv.ConfigFile) : new byte[0];
                bool restored = now.Length == original.Length;
                if (restored)
                {
                    for (int i = 0; i < now.Length; i++)
                    {
                        if (now[i] != original[i]) { restored = false; break; }
                    }
                }
                return Tuple.Create(got, restored);
            }

            private static string GuardNoAssertions(string s, string label)
            {
                var has = false;
                foreach (var line in (s ?? "").Split('\n'))
                {
                    var x = line.Trim();
                    if (x.StartsWith("PASS") || x.StartsWith("FAIL") || x.StartsWith("EXCEPTION")) { has = true; break; }
                }
                if (has) return s;
                return (string.IsNullOrEmpty(s) ? "" : s + "\n") + "FAIL  " + label + "没有产出任何断言（脚本未执行？）";
            }
            private static void Count(string s, ref int pass, ref int fail)
            {
                foreach (var line in (s ?? "").Split('\n'))
                {
                    var t = line.Trim();
                    if (t.StartsWith("PASS")) pass++;
                    else if (t.StartsWith("FAIL") || t.StartsWith("EXCEPTION")) fail++;
                }
            }

            private static string Unquote(string s)
            {
                if (string.IsNullOrEmpty(s)) return "";
                try
                {
                    var o = System.Text.Json.JsonSerializer.Deserialize<string>(s);
                    return o ?? s;
                }
                catch { return s; }
            }
        }

        // ==================== 阶段 A：结构 + 提示 + 脏标记（全同步） ====================
        private const string SCRIPT_A = @"(function(){
  var out=[], pass=0, fail=0;
  function T(n, ok, d){ if(ok){pass++; out.push('PASS  '+n);} else {fail++; out.push('FAIL  '+n+(d?'   -> '+d:''));} }
  function $(i){return document.getElementById(i);}
  function q(s){return document.querySelectorAll(s);}
  try{
    out.push('INFO  页面可交互耗时 = ' + (window.__READY_MS === undefined ? '未记录' : window.__READY_MS) + 'ms（从 navigationStart 起）');
    T('title 正确', document.title==='DSH 配置', document.title);
    T('origin 是虚拟主机', location.origin==='https://dshconfig.local', location.origin);
    T('安全上下文', window.isSecureContext===true);
    T('未进入错误态', document.body.textContent.indexOf('无法继续')<0);
    T('胶囊数量=5', q('.pill').length===5, q('.pill').length);
    // 15 个 ps1 字段。「打开方式」不在这里：它存 DshLauncher.json、切换即写盘，
    // 所以它既不算 f_ 字段、也不参与保存按钮与脏标记。
    // 15 个 ps1 字段 + 1 个「打开方式」容器（f_OPEN_MODE，它不是 ps1 字段）= 16
    T('表单控件数=16（15 个 ps1 字段 + 打开方式区块）', q('[id^=f_]').length===16, q('[id^=f_]').length);
    T('choice 选项用独立 .opt 类', q('.opt').length===2, q('.opt').length);
    T('打开方式区块已渲染', !!$('openmode_row'));
    T('打开方式不参与 ps1 脏标记', (function(){
        var before = $('btnSave').disabled;
        return before === true;   // 初始应是「已保存」态
      })());
    // 选中项必须与 JSON 里的实际值一致（不硬编码 browser：用户可能已设成 app，
    // 页面就该显示 app，硬编码会让「上次设过 app」的用户每次自检都报失败）
    T('OPEN_MODE 有选中项且值合法', (function(){
        var e = document.querySelector('.opt[data-key=OPEN_MODE].active');
        if (!e) return false;
        var v = e.getAttribute('data-v');
        return v === 'browser' || v === 'app';
      })(), '');
    T('提示块与 ? 图标数量一致', q('.hint').length===q('.q').length, q('.hint').length+'/'+q('.q').length);
    T('五个区块齐全', q('section').length===5, q('section').length);
    T('环境信息区块存在', !!$('sec-env'));
    T('开机启动区块存在', !!$('sec-startup'));
    T('端口值来自磁盘', $('f_PORT').value==='3080', $('f_PORT').value);
    T('监听地址只读', $('f_HOST').readOnly===true);
    T('带空格路径完整显示', $('f_NODE_EXE').value.indexOf('Program Files')>=0, $('f_NODE_EXE').value);
    T('NO_OPEN 取反：磁盘 true -> 界面关', $('f_NO_OPEN').classList.contains('on')===false);
    T('无「校验」按钮', !Array.prototype.some.call(q('button'),function(b){return b.textContent.indexOf('校验')>=0;}));
    T('无「查询」按钮', !Array.prototype.some.call(q('button'),function(b){return b.textContent.indexOf('查询')>=0;}));
    T('保存按钮初始禁用', $('btnSave').disabled===true);
    T('状态显示已保存', $('status').textContent.indexOf('已保存')>=0, $('status').textContent);
    T('自动校验已跑出结果', $('m_DSH_HOME').textContent.indexOf('存在')>=0, $('m_DSH_HOME').textContent);
    T('提示默认全部收起', Array.prototype.every.call(q('.hint'),function(h){return h.hidden;}));
    T('存在 ? 图标', q('.q').length>=13, q('.q').length);
    T('自启两行都有状态徽标', !!$('b_daemon') && !!$('b_tray'));
    T('自启状态值合法', ['on','off','error'].indexOf($('b_daemon').className.replace('badge ',''))>=0, $('b_daemon').className);

    var qs=q('.q');
    qs[0].click();
    T('点 ? 展开 1 个', Array.prototype.filter.call(q('.hint'),function(h){return !h.hidden;}).length===1);
    T('展开的 ? 变高亮', qs[0].classList.contains('open'));
    qs[1].click();
    T('同时只展开一个', Array.prototype.filter.call(q('.hint'),function(h){return !h.hidden;}).length===1);
    qs[1].click();
    T('再点同一个可收起', Array.prototype.every.call(q('.hint'),function(h){return h.hidden;}));

    var inp=$('f_PORT'); inp.value='3399';
    inp.dispatchEvent(new Event('input',{bubbles:true}));
    T('改值后状态变未保存', $('status').textContent.indexOf('未保存')>=0, $('status').textContent);
    T('改值后保存按钮可用', $('btnSave').disabled===false);
    inp.value='3080'; inp.dispatchEvent(new Event('input',{bubbles:true}));
    T('改回后恢复已保存', $('status').textContent.indexOf('已保存')>=0, $('status').textContent);
    T('改回后保存按钮重新禁用', $('btnSave').disabled===true);

    var main=$('main');
    /* 页面 CSS 有 scroll-behavior:smooth，直接赋值 scrollTop 也会变异步动画，先临时关掉 */
    main.style.scrollBehavior='auto';
    main.scrollTop=0; spyNav();
    T('顶部时高亮第一个胶囊', q('.pill')[0].classList.contains('active'));
    main.scrollTop=main.scrollHeight; spyNav();
    T('底部时高亮最后一个胶囊', q('.pill')[4].classList.contains('active'), q('.pill')[4].className);
    main.scrollTop=0; spyNav();
    main.style.scrollBehavior='';

    /* 恢复默认 + 重置：只动界面，不点保存就不会写文件 */

    window.__PT_BEFORE = main.scrollTop;
    q('.pill')[1].click();
    out.push('--- 阶段A：'+pass+' 通过 / '+fail+' 失败 ---');
    return out.join('\n');
  }catch(e){
    out.push('EXCEPTION  '+e.message+'  @'+String(e.stack).split(String.fromCharCode(10))[1]);
    out.push('--- 阶段A：'+pass+' 通过 / '+(fail+1)+' 失败 ---');
    return out.join('\n');
  }
})();";

        // ==================== 阶段 B：读异步滚动结果 ====================
        private const string SCRIPT_B = @"(function(){
  var out=[], pass=0, fail=0;
  function T(n, ok, d){ if(ok){pass++; out.push('PASS  '+n);} else {fail++; out.push('FAIL  '+n+(d?'   -> '+d:''));} }
  function $(i){return document.getElementById(i);}
  function q(s){return document.querySelectorAll(s);}
  try{
    var main=$('main'), now=main.scrollTop, before=window.__PT_BEFORE||0;
    T('点胶囊后页面确实滚动', now>before, 'before='+before+' after='+now);
    T('滚动后高亮跟随到第二个胶囊', q('.pill')[1].classList.contains('active'), q('.pill')[1].className);
    out.push('--- 阶段B：'+pass+' 通过 / '+fail+' 失败 ---');
    return out.join('\n');
  }catch(e){ out.push('EXCEPTION  '+e.message+'  @'+String(e.stack).split(String.fromCharCode(10))[1]); return out.join('\n'); }
})();";

        // ==================== 阶段 C：恢复默认 / 重置（只动界面，不保存） ====================
        // ==================== 阶段 C：恢复默认 / 重置（只动界面，不保存） ====================
        private const string SCRIPT_C = @"(async function(){
  var out=[], pass=0, fail=0;
  function T(n, ok, d){ if(ok){pass++; out.push('PASS  '+n);} else {fail++; out.push('FAIL  '+n+(d?'   -> '+d:''));} }
  function $(i){return document.getElementById(i);}
  function wait(ms){ return new Promise(function(r){ setTimeout(r, ms); }); }
  async function disk(){ return JSON.parse(await window.chrome.webview.hostObjects.bridge.ReadConfig()); }
  try{
    window.confirm = function () { return true; };   // 无头里 confirm 会弹真框并阻塞
    var portBefore = $('f_PORT').value;
    var wsBefore = $('f_WORKSPACE_DIR').value;
    var d0 = await disk();

    /* 先放一个不可能是默认值的哨兵，保证「恢复默认」一定有可见效果，
       否则磁盘本来就等于默认时它是空操作，断言会假失败。 */
    var wEl = $('f_WORKSPACE_DIR');
    wEl.value = 'Z:\sentinel-not-a-default';
    wEl.dispatchEvent(new Event('input',{bubbles:true}));
    T('放哨兵后变未保存', $('status').textContent.indexOf('未保存')>=0, $('status').textContent);

    $('btnDefaults').click();
    await wait(1800);
    T('恢复默认把哨兵换成了 defaults 的值',
      $('f_WORKSPACE_DIR').value !== 'Z:\sentinel-not-a-default' && $('f_WORKSPACE_DIR').value.length>0,
      $('f_WORKSPACE_DIR').value);
    T('恢复默认填上了端口 3080', $('f_PORT').value==='3080', $('f_PORT').value);
    T('恢复默认没写磁盘', (await disk()).text === d0.text);

    $('btnReset').click();
    await wait(1800);
    T('重置后端口回到磁盘值', $('f_PORT').value===portBefore, $('f_PORT').value);
    T('重置后工作目录回到磁盘值', $('f_WORKSPACE_DIR').value===wsBefore, $('f_WORKSPACE_DIR').value);
    T('重置后不再是未保存态', $('status').textContent.indexOf('未保存')<0, $('status').textContent);
    T('重置后保存按钮禁用', $('btnSave').disabled===true);
    T('重置也没写磁盘', (await disk()).text === d0.text);

    out.push('--- 阶段C：'+pass+' 通过 / '+fail+' 失败 ---');
    window.__C = out.join('\n');
  }catch(e){
    out.push('EXCEPTION  '+e.message+'  @'+String(e.stack).split('\n')[1]);
    out.push('--- 阶段C：'+pass+' 通过 / '+(fail+1)+' 失败 ---');
    window.__C = out.join('\n');
  }
  return 'C-STARTED';
})();";

        // ==================== 写入回路 ====================
        // 写入回路：真点保存，断言磁盘，最后全部改回。
        // 断言方式：把一行拆成 前缀 / 值 / 后缀 三段，要求只有"值"变了。
        // 这里刻意不用正则、不用双引号字面量（C# 逐字字符串里要写 "" 很易错），
        // 双引号用 String.fromCharCode(34) 取。
        private const string SCRIPT_W = @"(async function(){
  var out=[], pass=0, fail=0;
  var DQ = String.fromCharCode(34);
  function T(n, ok, d){ if(ok){pass++; out.push('PASS  '+n);} else {fail++; out.push('FAIL  '+n+(d?'   -> '+d:''));} }
  function $(i){return document.getElementById(i);}
  function wait(ms){ return new Promise(function(r){ setTimeout(r, ms); }); }
  function lineOf(text, name){
    var ls = text.split('\n');
    for (var i=0;i<ls.length;i++){ if (ls[i].indexOf('$'+name) === 0) return ls[i]; }
    return '';
  }
  function span(line){
    var i = line.indexOf('=');
    if (i < 0) return null;
    var j = i + 1;
    while (j < line.length && line.charAt(j) === ' ') j++;
    var start = j;
    if (line.charAt(j) === DQ) {
      j++;
      while (j < line.length && line.charAt(j) !== DQ) j++;
      return { pre: line.slice(0, start), val: line.slice(start, j+1), suf: line.slice(j+1) };
    }
    while (j < line.length && line.charAt(j) !== ' ' && line.charAt(j) !== String.fromCharCode(9)) j++;
    return { pre: line.slice(0, start), val: line.slice(start, j), suf: line.slice(j) };
  }
  function onlyValChanged(origText, newText, name, tok, label){
    var a = span(lineOf(origText, name)), b = span(lineOf(newText, name));
    if (!a || !b){ T(label + '：找到该行', false, name); return; }
    T(label + ' 前缀逐字符不变', a.pre === b.pre, JSON.stringify(a.pre) + ' | ' + JSON.stringify(b.pre));
    T(label + ' 后缀(对齐+注释)不变', a.suf === b.suf, JSON.stringify(a.suf) + ' | ' + JSON.stringify(b.suf));
    T(label + ' 值等于目标', b.val === tok, b.val + ' != ' + tok);
  }
  async function disk(){ return JSON.parse(await window.chrome.webview.hostObjects.bridge.ReadConfig()); }
  try{
    var d0 = await disk();
    var orig = d0.text;
    T('起始端口为 3080', lineOf(orig,'CONFIG_PORT').indexOf('3080')>=0, lineOf(orig,'CONFIG_PORT'));
    /* 关键防护：本阶段复用同一个页面实例，前面任何阶段都可能把表单改脏。
       先强制重置成磁盘内容，否则一次保存就会把测试残留写进用户配置。 */
    window.confirm = function () { return true; };
    await wait(1500);
    var dchk = await disk();
    T('写入回路开始前表单与磁盘一致', dchk.text === (await disk()).text);

    var inp=$('f_PORT'); inp.value='3399';
    inp.dispatchEvent(new Event('input',{bubbles:true}));
    $('btnSave').click(); await wait(2200);
    var d1 = await disk();
    onlyValChanged(orig, d1.text, 'CONFIG_PORT', '3399', '端口');
    T('页面回读后端口显示 3399', $('f_PORT').value==='3399', $('f_PORT').value);
    T('保存后状态显示已保存', $('status').textContent.indexOf('已保存')>=0, $('status').textContent);

    var sw=$('f_NO_OPEN'); sw.click(); await wait(300);
    T('拨杆已切到开', sw.classList.contains('on'));
    $('btnSave').click(); await wait(2200);
    var d2 = await disk();
    onlyValChanged(d1.text, d2.text, 'CONFIG_NO_OPEN', String.fromCharCode(36)+'false', 'T5 界面开');

    sw=$('f_NO_OPEN'); sw.click(); await wait(300);
    $('btnSave').click(); await wait(2200);
    var d3 = await disk();
    onlyValChanged(d2.text, d3.text, 'CONFIG_NO_OPEN', String.fromCharCode(36)+'true', 'T5 界面关');

    inp=$('f_PORT'); inp.value='3080';
    inp.dispatchEvent(new Event('input',{bubbles:true}));
    $('btnSave').click(); await wait(2200);
    var d4 = await disk();
    T('全部改回后与原始文本逐字符一致', d4.text===orig, 'len '+d4.text.length+' vs '+orig.length);

    out.push('--- 写入回路：'+pass+' 通过 / '+fail+' 失败 ---');
    window.__W = out.join('\n');
  }catch(e){
    out.push('EXCEPTION  '+e.message+'  @'+String(e.stack).split(String.fromCharCode(10))[1]);
    out.push('--- 写入回路：'+pass+' 通过 / '+(fail+1)+' 失败 ---');
    window.__W = out.join('\n');
  }
  return 'W-STARTED';
})();";

        // ==================== 阶段 D：自启开关 + 重置撤销（有副作用，净效果为零） ====================
        private const string SCRIPT_D = @"(async function(){
  var out=[], pass=0, fail=0;
  function T(n, ok, d){ if(ok){pass++; out.push('PASS  '+n);} else {fail++; out.push('FAIL  '+n+(d?'   -> '+d:''));} }
  function $(i){return document.getElementById(i);}
  function wait(ms){ return new Promise(function(r){ setTimeout(r, ms); }); }
  function swOf(id){ return $('main').querySelector('[data-auto=""' + id + '""]'); }
  async function auto(){ return JSON.parse(await window.chrome.webview.hostObjects.bridge.GetAutostart()); }
  try{
    window.confirm = function () { return true; };
    var b0 = await auto();
    var baseState = b0.states && b0.states.daemon ? b0.states.daemon.state : null;
    T('基线自启状态可读', baseState === 'on' || baseState === 'off', String(baseState));
    T('重置前不脏（自启不算脏）', $('btnSave').disabled === true);

    var sw = swOf('daemon');
    T('找到自启拨杆', !!sw);
    sw.click();
    await wait(2500);
    var b1 = await auto();
    var nowState = b1.states.daemon.state;
    T('点拨杆后自启状态真的翻转了', nowState !== baseState, baseState + ' -> ' + nowState);
    T('翻转后保存按钮仍禁用（自启与保存无关）', $('btnSave').disabled === true);

    $('btnReset').click();
    await wait(3000);
    var b2 = await auto();
    T('★ 重置把自启撤销回基线', b2.states.daemon.state === baseState,
      baseState + ' vs ' + b2.states.daemon.state);
    T('拨杆显示也回到基线', swOf('daemon').classList.contains('on') === (baseState === 'on'),
      swOf('daemon').className);
    T('重置状态栏明确提到撤销了开机启动',
      $('status').textContent.indexOf('开机启动已撤销') >= 0, $('status').textContent);

    out.push('--- 阶段D：' + pass + ' 通过 / ' + fail + ' 失败 ---');
    window.__D = out.join('\n');
  }catch(e){
    out.push('EXCEPTION  ' + e.message + '  @' + String(e.stack).split('\n')[1]);
    out.push('--- 阶段D：' + pass + ' 通过 / ' + (fail+1) + ' 失败 ---');
    window.__D = out.join('\n');
  }
  return 'D-STARTED';
})();";

        // ==================== 阶段 E：外部导航拦截 ====================
        private const string SCRIPT_E = @"(function(){
  var out=[], pass=0, fail=0;
  function T(n, ok, d){ if(ok){pass++; out.push('PASS  '+n);} else {fail++; out.push('FAIL  '+n+(d?'   -> '+d:''));} }
  try{
    window.__E_START_ORIGIN = location.origin;
    T('起点在虚拟主机上', location.origin === 'https://dshconfig.local', location.origin);
    /* 尝试跳去外站；NavigationStarting 应当取消它 */
    try { window.location.href = 'https://example.com/'; } catch (e) { }
    setTimeout(function(){
      T('尝试外跳后仍在虚拟主机上', location.origin === 'https://dshconfig.local', location.origin);
      T('页面标题未变', document.title === 'DSH 配置', document.title);
      T('配置表单仍在', !!document.getElementById('f_PORT'));
      out.push('--- 阶段E：' + pass + ' 通过 / ' + fail + ' 失败 ---');
      window.__E = out.join('\n');
    }, 2500);
    return 'E-WAITING';
  }catch(e){
    out.push('EXCEPTION  ' + e.message);
    out.push('--- 阶段E：' + pass + ' 通过 / ' + (fail+1) + ' 失败 ---');
    window.__E = out.join('\n');
    return out.join('\n');
  }
})();";

        // ==================== 阶段 F：H4 / H14（真实重启守护进程） ====================
        private const string SCRIPT_F = @"(async function(){
  var out=[], pass=0, fail=0;
  function T(n, ok, d){ if(ok){pass++; out.push('PASS  '+n);} else {fail++; out.push('FAIL  '+n+(d?'   -> '+d:''));} }
  window.__F_MSG = '';
  window.chrome.webview.addEventListener('message', function(e){
    var m; try { m = (typeof e.data === 'string') ? JSON.parse(e.data) : e.data; } catch (_) { return; }
    if (m && m.action === 'restart-daemon') window.__F_MSG = JSON.stringify(m);
  });
  try{
    var env0 = JSON.parse(await window.chrome.webview.hostObjects.bridge.GetEnvironment());
    window.__F_OLDPID = env0.daemonPid || '';
    T('重启前守护进程在跑', env0.daemonRunning === true && window.__F_OLDPID !== '', JSON.stringify(env0).slice(0,80));
    var t0 = performance.now();
    var kick = JSON.parse(await window.chrome.webview.hostObjects.bridge.RestartDaemon());
    var dt = performance.now() - t0;
    T('H14 RestartDaemon 立即返回（<1s，UI 不阻塞）', dt < 1000, Math.round(dt) + 'ms');
    T('返回 started=true', kick.ok === true && kick.started === true, JSON.stringify(kick));
    out.push('--- 阶段F1：' + pass + ' 通过 / ' + fail + ' 失败 ---');
    window.__F = out.join('\n');
  }catch(e){
    out.push('EXCEPTION  ' + e.message);
    out.push('--- 阶段F1：' + pass + ' 通过 / ' + (fail+1) + ' 失败 ---');
    window.__F = out.join('\n');
  }
  return 'F-STARTED';
})();";

        // 注意：必须用「异步启动 + 轮询 window.__F2」的模式。
        // ExecuteScriptAsync 不会 await Promise，直接写 (async function(){...})() 会返回 {}，
        // 里面的断言一条都不会执行 —— 而计数逻辑找不到 FAIL，于是假绿。
        private const string SCRIPT_F2 = @"(function(){
  window.__F2 = '';
  (async function(){
    var out=[], pass=0, fail=0;
    function T(n, ok, d){ if(ok){pass++; out.push('PASS  '+n);} else {fail++; out.push('FAIL  '+n+(d?'   -> '+d:''));} }
    try{
      var m = JSON.parse(window.__F_MSG);
      T('H4 完成消息 ok=true', m.ok === true, m.message || '');
      T('H4 完成消息带 PID', /PID \d+/.test(m.message || ''), m.message || '');
      var env1 = JSON.parse(await window.chrome.webview.hostObjects.bridge.GetEnvironment());
      T('H4 守护进程仍在运行', env1.daemonRunning === true, JSON.stringify(env1).slice(0,80));
      T('H4 新 PID 与旧的不同', String(env1.daemonPid) !== String(window.__F_OLDPID),
        'old=' + window.__F_OLDPID + ' new=' + env1.daemonPid);
      T('H4 状态栏回填了重启结果', $('status').textContent.indexOf('PID') >= 0
        || $('status').textContent.indexOf('守护进程') >= 0, $('status').textContent);
      out.push('--- 阶段F2：' + pass + ' 通过 / ' + fail + ' 失败 ---');
      window.__F2 = out.join('\n');
    }catch(e){
      out.push('EXCEPTION  ' + e.message);
      out.push('--- 阶段F2：' + pass + ' 通过 / ' + (fail+1) + ' 失败 ---');
      window.__F2 = out.join('\n');
    }
  })();
  return 'F2-STARTED';
})();";

        // ==================== 阶段 G：OpenInFolder 的校验与真实打开 ====================
        private const string SCRIPT_G = @"(async function(){
  var out=[], pass=0, fail=0;
  function T(n, ok, d){ if(ok){pass++; out.push('PASS  '+n);} else {fail++; out.push('FAIL  '+n+(d?'   -> '+d:''));} }
  async function call(p){ return JSON.parse(await window.chrome.webview.hostObjects.bridge.OpenInFolder(
    p === null ? '' : JSON.stringify(p))); }
  try{
    var r1 = await call(null);
    T('拒绝空参数', r1.ok === false, JSON.stringify(r1));
    var r2 = await call({});
    T('拒绝缺 path 键', r2.ok === false, JSON.stringify(r2));
    var r3 = await call({ path: '' });
    T('拒绝空路径', r3.ok === false, JSON.stringify(r3));
    var r4 = await call({ path: 'Z:\\no-such-dir-anywhere-12345' });
    T('拒绝不存在的路径', r4.ok === false, JSON.stringify(r4));
    var r5 = await call({ path: 'C:\\x`r`ny' });
    T('拒绝含换行的路径', r5.ok === false, JSON.stringify(r5));
    var r6 = await call({ path: new Array(1200).join('a') });
    T('拒绝超长路径', r6.ok === false, JSON.stringify(r6));


    out.push('--- 阶段G：' + pass + ' 通过 / ' + fail + ' 失败 ---');
    window.__G = out.join('\n');
  }catch(e){
    out.push('EXCEPTION  ' + e.message + '  @' + String(e.stack).split('\n')[1]);
    out.push('--- 阶段G：' + pass + ' 通过 / ' + (fail+1) + ' 失败 ---');
    window.__G = out.join('\n');
  }
  return 'G-STARTED';
})();";

        // ==================== 阶段 H：真实 Edge 数据下的网页应用检测 ====================
        // 只读 Edge 的 Sessions，不写任何 Edge 文件；点页面上的「Edge 网页应用」
        // 选项后必须自动填入 app-id（本机已安装并打开过该 PWA，必须有记录）。
        private const string SCRIPT_H = @"(function(){
  window.__H = '';
  (async function(){
    var out=[], pass=0, fail=0;
    function T(n, ok, d){ if(ok){pass++; out.push('PASS  '+n);} else {fail++; out.push('FAIL  '+n+(d?'   -> '+d:''));} }
    function $(i){return document.getElementById(i);}
    function wait(ms){ return new Promise(function(r){ setTimeout(r, ms); }); }
    try{
      var optApp = document.querySelector('.opt[data-key=OPEN_MODE][data-v=app]');
      T('找到「Edge 网页应用」选项', !!optApp);
      if (!optApp) { throw new Error('找不到选项'); }

      // 记录切换前 JSON 里的值
      var before = JSON.parse(await window.chrome.webview.hostObjects.bridge.GetOpenMode('{}'));
      T('GetOpenMode 可读', before.ok === true, JSON.stringify(before).slice(0, 80));

      optApp.click();
      await wait(4000);

      // ★ 核心：切换后 JSON 里必须已经是 app + 有效 app-id（不点保存按钮）
      var after = JSON.parse(await window.chrome.webview.hostObjects.bridge.GetOpenMode('{}'));
      T('★ 切换后 JSON 里已是 app', after.mode === 'app', 'mode=' + after.mode);
      T('★ JSON 里的 app-id 合法', /^[a-p]{32}$/.test(after.appId || ''), 'appId=[' + after.appId + ']');
      T('▲ 自动检测命中（不是「未检测到」）', !document.getElementById('app_guide_panel'),
        document.getElementById('app_guide_panel') ? '引导面板出现了' : '');
      // 状态栏会被后续操作覆盖，所以这里不看文本，直接看 JSON 是否已写（那才是本质）
      T('切换即写盘（无需点保存）', after.mode === 'app' && /^[a-p]{32}$/.test(after.appId || ''));
      T('OPEN_MODE 胶囊已切到 app', (function(){var e=document.querySelector('.opt[data-key=OPEN_MODE].active');return !!e && e.getAttribute('data-v')==='app';})());
      T('★ 不点保存也能生效（按钮仍禁用）', $('btnSave').disabled === true, 'disabled=' + $('btnSave').disabled);

      // 切回浏览器：JSON 应立即变回 browser 且 app-id 清空
      var optBr = document.querySelector('.opt[data-key=OPEN_MODE][data-v=browser]');
      T('能找到「默认浏览器」选项', !!optBr);
      var preClick = JSON.parse(await window.chrome.webview.hostObjects.bridge.GetOpenMode('{}'));
      out.push('INFO  点击前 JSON mode=' + preClick.mode);
      out.push('INFO  optBr 存在=' + !!optBr + ' active=' + (optBr ? optBr.classList.contains('active') : 'n/a'));
      if (optBr) { optBr.click(); }
      // 要等异步的 setOpenMode + paintOpenMode 都完成，1.2s 偶发不够
      await wait(2500);
      var back = JSON.parse(await window.chrome.webview.hostObjects.bridge.GetOpenMode('{}'));
      out.push('INFO  点击后 JSON mode=' + back.mode);
      T('★ 切回后 JSON 是 browser', back.mode === 'browser', 'mode=' + back.mode);
      T('切回后 app-id 清空', (back.appId || '') === '', '[' + back.appId + ']');
      T('切回后无引导面板', !document.getElementById('app_guide_panel'));
      T('切回后胶囊选中 browser', (function(){
          var e = document.querySelector('.opt[data-key=OPEN_MODE].active');
          return !!e && e.getAttribute('data-v') === 'browser';
        })(), (function(){
          var e = document.querySelector('.opt[data-key=OPEN_MODE].active');
          return e ? e.getAttribute('data-v') : 'none';
        })());
      T('至今 ps1 保存按钮仍是禁用（说明没走 ps1）', $('btnSave').disabled === true);

      // 还原测试前的打开方式，避免自检改动用户设置
      var restore = await window.chrome.webview.hostObjects.bridge.SetOpenMode(
        JSON.stringify({ mode: before.mode, appId: before.appId || '' }));
      var finalState = JSON.parse(await window.chrome.webview.hostObjects.bridge.GetOpenMode('{}'));
      T('★ 自检结束后还原了打开方式', finalState.mode === before.mode,
        'before=' + before.mode + ' after=' + finalState.mode + ' restore=' + restore);

      /* 布局度量：手动调一次 showAppGuide（传空列表模拟未命中），
         量它的几何尺寸与关键元素 —— 比截图可靠，也能防回归。
         之前它被塞进 .f 网格的第二列，宽度只有 ~150px。 */
      showAppGuide({ apps: [ { appId: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', name: '', url: 'http://127.0.0.1:9999/' } ] });
      await wait(300);
      var gp = document.getElementById('app_guide_panel');
      T('引导面板已出现', !!gp);
      if (gp) {
        var sec = $('sec-runtime');
        var gw = gp.getBoundingClientRect().width;
        var sw = sec ? sec.getBoundingClientRect().width : 0;
        T('引导面板占满内容宽度（不再被挤成窄柱）', gw >= sw * 0.9,
          'guide=' + Math.round(gw) + ' section=' + Math.round(sw));
        T('引导面板有打开按钮', !!document.getElementById('btnOpenUrl'));
        T('引导面板有复制按钮', !!document.getElementById('btnCopyUrl'));
        T('引导面板有重新检测按钮', !!document.getElementById('btnRedetectApp'));
        T('网址用 code 展示且可选中', !!gp.querySelector('code'));
        T('可选应用列表呈单选项', gp.querySelectorAll('input[name=ag_pick]').length === 1,
          gp.querySelectorAll('input[name=ag_pick]').length);
        T('复制按钮可点出提示', (function(){ document.getElementById('btnCopyUrl').click(); return $('status').textContent.indexOf('复制') >= 0; })(), $('status').textContent);
      }
      clearAppGuide();
      T('清除后引导面板消失', !document.getElementById('app_guide_panel'));
      out.push('--- 阶段H：' + pass + ' 通过 / ' + fail + ' 失败 ---');
      window.__H = out.join('\n');
    }catch(e){
      out.push('EXCEPTION  ' + e.message);
      out.push('--- 阶段H：' + pass + ' 通过 / ' + (fail+1) + ' 失败 ---');
      window.__H = out.join('\n');
    }
  })();
  return 'H-STARTED';
})();";
    }
}