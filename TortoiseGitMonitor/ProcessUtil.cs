// 进程命令行工具：参数引号转义 + XML 转义。
// 所有外部进程一律使用固定参数列表，绝不经过 shell，避免命令注入。
using System.Text;

namespace TortoiseGitMonitor
{
    internal static class ProcessUtil
    {
        /// <summary>
        /// 将单个参数转为 Windows 命令行安全形式（与 Python subprocess.list2cmdline 行为一致）。
        /// 仅当参数含空白或引号时才加引号；引号内的反斜杠和引号按规则转义。
        /// </summary>
        public static string QuoteArg(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"', '\n', '\r' }) < 0)
                return arg;

            var sb = new StringBuilder();
            sb.Append('"');
            for (int i = 0; i < arg.Length; i++)
            {
                char c = arg[i];
                if (c == '\\')
                {
                    // 连续反斜杠：若后面紧跟引号或位于末尾，需要翻倍
                    int backslashes = 1;
                    while (i + 1 < arg.Length && arg[i + 1] == '\\')
                    {
                        backslashes++;
                        i++;
                    }
                    bool followedByQuote = (i + 1 < arg.Length && arg[i + 1] == '"') || i + 1 == arg.Length;
                    sb.Append('\\', followedByQuote ? backslashes * 2 : backslashes);
                }
                else if (c == '"')
                {
                    sb.Append("\\\"");
                }
                else
                {
                    sb.Append(c);
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>拼接参数列表为命令行字符串。</summary>
        public static string BuildArguments(params string[] args)
        {
            var sb = new StringBuilder();
            foreach (string arg in args)
            {
                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(QuoteArg(arg));
            }
            return sb.ToString();
        }

        /// <summary>XML 转义（与 Python xml.sax.saxutils.escape 默认行为一致：&amp; &lt; &gt;）。</summary>
        public static string XmlEscape(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            return text
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }
    }
}
