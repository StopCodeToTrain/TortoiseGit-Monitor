/**
 * TortoiseGit-Monitor - 入口。
 * 托盘应用，无主窗口。定时检测 git 仓库状态。
 *
 * 附加参数:
 *   --selftest [输出文件]  仅加载配置并输出解析结果（用于验证配置兼容性），不启动托盘。
 */
using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace TortoiseGitMonitor
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length >= 1 && args[0] == "--selftest")
                return RunSelfTest(args.Length > 1 ? args[1] : null);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayAppContext());
            return 0;
        }

        /// <summary>配置兼容性自检：加载真实 settings.json 并输出解析结果。</summary>
        private static int RunSelfTest(string outputPath)
        {
            if (string.IsNullOrEmpty(outputPath))
                outputPath = Path.Combine(Path.GetTempPath(), "tgm_selftest.txt");
            try
            {
                AppConfig cfg = AppConfig.Load();
                var sb = new StringBuilder();
                sb.AppendLine("config_file=" + AppConfig.ConfigFile);
                sb.AppendLine("interval_sec=" + cfg.Global.IntervalSec);
                sb.AppendLine("log_count=" + cfg.Global.LogCount);
                sb.AppendLine("start_with_windows=" + cfg.Global.StartWithWindows.ToString().ToLowerInvariant());
                sb.AppendLine("show_notifications=" + cfg.Global.ShowNotifications.ToString().ToLowerInvariant());
                sb.AppendLine("notification_sound=" + cfg.Global.NotificationSound.ToString().ToLowerInvariant());
                sb.AppendLine("tortoisegit_path=" + cfg.Global.TortoiseGitPath);
                sb.AppendLine("projects=" + cfg.Projects.Count);
                foreach (ProjectEntry p in cfg.Projects)
                    sb.AppendLine($"  project: name={p.Name} path={p.Path} enabled={p.Enabled} monitor_all_branches={p.MonitorAllBranches}");
                sb.AppendLine("scan_paths=" + cfg.ScanPaths.Count);
                foreach (string p in cfg.ScanPaths)
                    sb.AppendLine("  scan: " + p);
                sb.AppendLine("SELFTEST_OK");
                // 序列化回写测试：将内存中的配置重新序列化到临时文件，验证 schema 兼容
                string roundTrip = Path.Combine(Path.GetTempPath(), "tgm_roundtrip.json");
                cfg.SaveTo(roundTrip);
                sb.AppendLine("roundtrip_file=" + roundTrip);
                File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(outputPath, "SELFTEST_FAIL: " + ex, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                return 1;
            }
        }
    }
}
