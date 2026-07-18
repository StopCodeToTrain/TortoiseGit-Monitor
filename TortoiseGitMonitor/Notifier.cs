/**
 * Windows Toast 通知模块。
 * 使用 PowerShell 调用 COM 接口发送原生通知，零外部依赖。
 * （移植自 Python 版 notify.py，保持相同实现方式与转义纪律）
 */
using System;
using System.Diagnostics;

namespace TortoiseGitMonitor
{
    internal static class Notifier
    {
        /// <summary>
        /// 发送 Windows 10/11 原生 Toast 通知。
        /// 通过 PowerShell 创建 AppUserModelID 绑定并弹出通知。
        /// title/body 会进行 XML 转义防止注入和解析失败。
        /// </summary>
        public static void ShowToast(string title, string body, bool sound = false)
        {
            // XML 转义，防止 < > & 等字符破坏 Toast XML 解析
            string safeTitle = ProcessUtil.XmlEscape(title);
            string safeBody = ProcessUtil.XmlEscape(body);

            string ps = @"
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom, ContentType = WindowsRuntime] > $null

$template = @'
<toast>
    <visual>
        <binding template=""ToastText02"">
            <text id=""1"">" + safeTitle + @"</text>
            <text id=""2"">" + safeBody + @"</text>
        </binding>
    </visual>
</toast>
'@

$xml = New-Object Windows.Data.Xml.Dom.XmlDocument
$xml.LoadXml($template)

$notifier = [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier(""GitMonitor"")
$notifier.Show($xml)
";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = "-NoProfile -Command " + ProcessUtil.QuoteArg(ps),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (Process process = Process.Start(psi))
                {
                    // 最多等待 10 秒，通知失败不应影响主流程
                    if (!process.WaitForExit(10 * 1000))
                    {
                        try { process.Kill(); } catch (Exception) { /* 忽略 */ }
                    }
                }
            }
            catch (Exception)
            {
                // 通知失败不应影响主流程
            }
        }

        /// <summary>有新提交时的通知。</summary>
        public static void NotifyNewCommits(string repoName, int count, bool sound = false)
        {
            ShowToast(
                title: $"📥 {repoName}",
                body: $"发现 {count} 个新的远程提交",
                sound: sound);
        }

        /// <summary>错误通知。</summary>
        public static void NotifyError(string repoName, string message)
        {
            ShowToast(
                title: $"⚠️ {repoName} 检测失败",
                body: message.Length > 200 ? message.Substring(0, 200) : message,
                sound: true);
        }
    }
}
