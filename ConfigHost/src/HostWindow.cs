using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ConfigHost
{
    internal sealed class HostWindow : Form, IRestartHost
    {
        private WebView2 _web;

        public string WebView2Version { get; private set; }

        // 第二个实例叫我醒来的事件；后台线程等它，收到后在自己的 UI 线程上激活窗口
        private EventWaitHandle _activateSignal;
        private volatile bool _stopWatch;

        public HostWindow()
        {
            Text = "DSH 配置";
            ClientSize = new Size(900, 720);
            MinimumSize = new Size(720, 560);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(0x1F, 0x1F, 0x1F);

            _web = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_web);
            Shown += async (s, e) => await InitAsync();
            StartActivateWatcher();
        }


        /// <summary>
        /// 监听"被再次启动"的信号。收到后把窗口恢复到正常状态并提到最前。
        /// 用 TopMost 开关法：跨进程的前台权限不可靠，但改自己的 TopMost 一定有效。
        /// </summary>
        private void StartActivateWatcher()
        {
            try
            {
                _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ActivateEventName);
            }
            catch { return; }

            var t = new System.Threading.Thread(WatchLoop) { IsBackground = true };
            t.Start();
        }

        private void WatchLoop()
        {
            while (!_stopWatch)
            {
                try
                {
                    if (!_activateSignal.WaitOne(500)) continue;
                    if (_stopWatch) break;
                    BeginInvoke(new Action(ActivateSelf));
                }
                catch { return; }
            }
        }

        private void ActivateSelf()
        {
            try
            {
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                TopMost = true;
                TopMost = false;
                Activate();
            }
            catch { }
        }
        private async Task InitAsync()
        {
            try
            {
                var udf = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DshConfigHost", "WebView2");
                Directory.CreateDirectory(udf);

                var env = await CoreWebView2Environment.CreateAsync(null, udf, null);
                await _web.EnsureCoreWebView2Async(env);
                WebView2Version = env.BrowserVersionString;

                var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
                _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "dshconfig.local", wwwroot, CoreWebView2HostResourceAccessKind.DenyCors);

                _web.CoreWebView2.AddHostObjectToScript("bridge", new HostBridge(this));

                // 安全设置（设计第十三节）。与 --page-test 共用同一个方法，
                // 否则测试会跑在一个比生产更弱的配置上，测不出真实拦截行为。
                ConfigureSecurity(_web.CoreWebView2);

                _web.CoreWebView2.Navigate("https://dshconfig.local/index.html");
            }
            catch (Exception ex)
            {
                // 缺 WebView2 Runtime 等情况：不崩，给可读提示（设计第十二节）
                MessageBox.Show("界面初始化失败：" + ex.Message +
                    "\n\n若本机没有 Microsoft Edge WebView2 Runtime，请安装：\n" +
                    "https://developer.microsoft.com/microsoft-edge/webview2/",
                    "DSH 配置", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ---- 深色标题栏 ----
        // WinForms 的标题栏由 Windows 自己画，默认跟随系统浅色主题，
        // 与页面深色背景不搭。DWMWA_USE_IMMERSIVE_DARK_MODE 在 Win10 2004+/11
        // 是 20，Win10 1809~1909 是 19 —— 两个都试，失败就保持原样（无害）。
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private void ApplyDarkTitleBar()
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)) != 0)
                {
                    on = 1;
                    DwmSetWindowAttribute(Handle, 19, ref on, sizeof(int));
                }
            }
            catch { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDarkTitleBar();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) ApplyDarkTitleBar();   // 系统主题中途切换也能纠正回来
        }
        /// <summary>发起重启；实际逻辑在 RestartFlow，与 --page-test 共用同一份。</summary>
        void IRestartHost.RequestRestart()
        {
            RestartFlow.Begin(AppEnv.DaemonExe, AppEnv.InstallRoot, json =>
            {
                try
                {
                    // WebView2 的 API 必须在 UI 线程调用
                    BeginInvoke(new Action(() =>
                    {
                        if (_web != null && _web.CoreWebView2 != null)
                            _web.CoreWebView2.PostWebMessageAsJson(json);
                    }));
                }
                catch { }
            });
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _stopWatch = true;
            try { if (_activateSignal != null) _activateSignal.Set(); } catch { }
            try { if (_activateSignal != null) _activateSignal.Close(); } catch { }
            base.OnFormClosed(e);
        }

        /// <summary>
        /// DshConfigHost 对 WebView2 的全部安全约束。生产窗口与 --page-test 都调它，
        /// 保证测试验证的就是真实配置（复制一份到测试里迟早会漂移）。
        /// </summary>
        internal static void ConfigureSecurity(Microsoft.Web.WebView2.Core.CoreWebView2 c)
        {
            c.Settings.AreDefaultContextMenusEnabled = false;
            c.Settings.AreDevToolsEnabled = false;
            c.Settings.IsStatusBarEnabled = false;

            // 拦截外部导航：配置页不该跳走
            c.NavigationStarting += (s, e) =>
            {
                if (!e.Uri.StartsWith("https://dshconfig.local/", StringComparison.OrdinalIgnoreCase))
                    e.Cancel = true;
            };
            c.NewWindowRequested += (s, e) => { e.Handled = true; };
        }
    }
}