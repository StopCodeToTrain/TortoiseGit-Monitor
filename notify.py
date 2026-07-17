"""
Windows Toast 通知模块。
使用 PowerShell 调用 COM 接口发送原生通知，零外部依赖。
"""

import subprocess
from xml.sax.saxutils import escape as xml_escape


def show_toast(
    title: str,
    body: str,
    sound: bool = False,
) -> None:
    """发送 Windows 10/11 原生 Toast 通知。

    通过 PowerShell 创建 AppUserModelID 绑定并弹出通知。
    title/body 会进行 XML 转义防止注入和解析失败。
    """
    # XML 转义，防止 < > & 等字符破坏 Toast XML 解析
    safe_title = xml_escape(title)
    safe_body = xml_escape(body)

    ps = f'''
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom, ContentType = WindowsRuntime] > $null

$template = @'
<toast>
    <visual>
        <binding template="ToastText02">
            <text id="1">{safe_title}</text>
            <text id="2">{safe_body}</text>
        </binding>
    </visual>
</toast>
'@

$xml = New-Object Windows.Data.Xml.Dom.XmlDocument
$xml.LoadXml($template)

$notifier = [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier("GitMonitor")
$notifier.Show($xml)
'''
    try:
        subprocess.run(
            ["powershell", "-NoProfile", "-Command", ps],
            capture_output=True,
            timeout=10,
            creationflags=subprocess.CREATE_NO_WINDOW if hasattr(subprocess, "CREATE_NO_WINDOW") else 0,
        )
    except Exception:
        # 通知失败不应影响主流程
        pass


def notify_new_commits(
    repo_name: str,
    count: int,
    sound: bool = False,
) -> None:
    """有新提交时的通知。"""
    show_toast(
        title=f"📥 {repo_name}",
        body=f"发现 {count} 个新的远程提交",
        sound=sound,
    )


def notify_error(
    repo_name: str,
    message: str,
) -> None:
    """错误通知。"""
    show_toast(
        title=f"⚠️ {repo_name} 检测失败",
        body=message[:200],
        sound=True,
    )
