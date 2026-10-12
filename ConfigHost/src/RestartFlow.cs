using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ConfigHost
{
    /// <summary>
    /// DshConfigHost 窗口需要向桥暴露的能力：报告 WebView2 版本、发起一次后台重启。
    /// 生产窗口和 --page-test 的测试窗口都实现它，于是测试走的就是真实重启逻辑。
    /// </summary>
    internal interface IRestartHost
    {
        string WebView2Version { get; }

        /// <summary>立即返回，完成后把 JSON 结果推给页面（message 事件）。</summary>
        void RequestRestart();
    }

    /// <summary>
    /// 重启守护进程的唯一实现：后台线程执行，结果交给调用方提供的推送回调。
    /// 抽出来的原因：如果生产与测试各写一份，测试通过不代表生产正确。
    /// </summary>
    internal static class RestartFlow
    {
        public static void Begin(string exePath, string workingDir, Action<string> postJson)
        {
            Task.Run(() =>
            {
                string json;
                try
                {
                    var r = DaemonController.Restart(exePath, workingDir);
                    json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        { "action", "restart-daemon" },
                        { "ok", r.Ok },
                        { "message", r.Message }
                    });
                }
                catch (Exception ex)
                {
                    json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        { "action", "restart-daemon" },
                        { "ok", false },
                        { "message", "重启过程异常：" + ex.Message }
                    });
                }
                try { postJson(json); } catch { }
            });
        }
    }
}