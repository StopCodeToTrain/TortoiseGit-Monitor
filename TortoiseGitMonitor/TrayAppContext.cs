/**
 * TortoiseGit-Monitor - 托盘应用。
 * 无主窗口，所有交互通过系统托盘完成。
 * 左键/右键均弹出菜单：仓库快捷入口 + 设置 + 刷新 + 退出。
 */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TortoiseGitMonitor
{
    // ═══════════════════════════
    //  浅色主题配色（Catppuccin Latte）
    // ═══════════════════════════

    internal static class ThemeColors
    {
        public static readonly Color Bg = ColorTranslator.FromHtml("#eff1f5");       // 基础背景
        public static readonly Color Surface = ColorTranslator.FromHtml("#e6e9ef");    // 面板背景
        public static readonly Color Surface2 = ColorTranslator.FromHtml("#dce0e8");   // 悬停/交替行
        public static readonly Color Border = ColorTranslator.FromHtml("#bcc0cc");     // 边框
        public static readonly Color Text = ColorTranslator.FromHtml("#4c4f69");       // 主文字
        public static readonly Color Subtext = ColorTranslator.FromHtml("#6c6f85");    // 次要文字
        public static readonly Color Blue = ColorTranslator.FromHtml("#1e66f5");       // 链接/强调
        public static readonly Color Green = ColorTranslator.FromHtml("#40a02b");      // 正常/同步
        public static readonly Color Yellow = ColorTranslator.FromHtml("#df8e1d");     // 修改/警告
        public static readonly Color Red = ColorTranslator.FromHtml("#d20f39");        // 错误/落后
        public static readonly Color Overlay = ColorTranslator.FromHtml("#9ca0b0");    // 禁用/占位
    }

    // ═══════════════════════════
    //  图标（GDI+ 程序绘制，与 Python QPainter 版一致）
    // ═══════════════════════════

    internal static class StatusIcons
    {
        private static readonly Dictionary<string, Icon> IconCache = new Dictionary<string, Icon>();
        private static readonly Dictionary<string, Bitmap> BitmapCache = new Dictionary<string, Bitmap>();

        private static Color KeyToColor(string key)
        {
            switch (key)
            {
                case "clean": return ThemeColors.Green;
                case "dirty": return ThemeColors.Yellow;
                case "behind": return ThemeColors.Red;
                default: return ThemeColors.Overlay;
            }
        }

        /// <summary>绘制 16x16 圆形状态图标。</summary>
        private static Bitmap MakeBitmap(Color color)
        {
            const int size = 16;
            var bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var brush = new SolidBrush(color))
                    g.FillEllipse(brush, 2, 2, size - 4, size - 4);
            }
            return bmp;
        }

        /// <summary>获取托盘用 Icon（带缓存）。key: clean / dirty / behind / error</summary>
        public static Icon GetIcon(string key)
        {
            if (!IconCache.TryGetValue(key, out Icon icon))
            {
                using (Bitmap bmp = MakeBitmap(KeyToColor(key)))
                {
                    // GetHicon 产生的句柄由 Icon 持有，进程退出时统一释放（缓存数量有限）
                    icon = Icon.FromHandle(bmp.GetHicon());
                }
                IconCache[key] = icon;
            }
            return icon;
        }

        /// <summary>获取菜单项用 Bitmap（带缓存）。</summary>
        public static Bitmap GetBitmap(string key)
        {
            if (!BitmapCache.TryGetValue(key, out Bitmap bmp))
            {
                bmp = MakeBitmap(KeyToColor(key));
                BitmapCache[key] = bmp;
            }
            return bmp;
        }

        /// <summary>根据仓库状态返回对应颜色的菜单图标。</summary>
        public static Bitmap GetRepoStatusBitmap(GitStatus st)
        {
            if (st.Error.Length > 0)
                return GetBitmap("error");
            if (st.Behind > 0)
                return GetBitmap("behind");
            if (st.Dirty)
                return GetBitmap("dirty");
            return GetBitmap("clean");
        }

        /// <summary>
        /// 绘制三角形箭头图标。
        /// direction: "up" | "down"；enabled=false 时半透明（置灰状态）。
        /// </summary>
        public static Bitmap MakeArrowBitmap(string direction, Color color, bool enabled = true)
        {
            const int size = 16;
            var bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Color fill = enabled ? color : Color.FromArgb(80, color);
                using (var brush = new SolidBrush(fill))
                {
                    Point[] poly = direction == "up"
                        ? new[] { new Point(8, 2), new Point(2, 13), new Point(14, 13) }   // 朝上的三角形
                        : new[] { new Point(8, 14), new Point(2, 3), new Point(14, 3) };  // 朝下的三角形
                    g.FillPolygon(brush, poly);
                }
            }
            return bmp;
        }
    }

    // ═══════════════════════════
    //  TortoiseGit 查找
    // ═══════════════════════════

    internal static class TortoiseGitLauncher
    {
        private static readonly string[] Candidates =
        {
            @"C:\Program Files\TortoiseGit\bin\TortoiseGitProc.exe",
            @"C:\Program Files (x86)\TortoiseGit\bin\TortoiseGitProc.exe",
        };

        /// <summary>优先使用设置中显式指定的路径，其次自动查找常见安装位置。</summary>
        public static string Find()
        {
            string custom = (AppConfig.Load().Global.TortoiseGitPath ?? "").Trim();
            if (custom.Length > 0 && File.Exists(custom))
                return custom;
            foreach (string p in Candidates)
            {
                if (File.Exists(p))
                    return p;
            }
            return null;
        }

        /// <summary>启动 TortoiseGitProc.exe（默认 show log）。</summary>
        public static void Launch(string repoPath, string command = "log")
        {
            string tg = Find();
            if (tg == null)
                return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = tg,
                    Arguments = ProcessUtil.BuildArguments($"/command:{command}", $"/path:{repoPath}"),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            }
            catch (Exception)
            {
                // 启动失败不应导致程序崩溃
            }
        }
    }

    // ═══════════════════════════
    //  开机自启动
    // ═══════════════════════════

    internal static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "TortoiseGit-Monitor";

        /// <summary>写入/删除注册表 Run 键，实现开机自启动（仅当前用户，无需管理员权限）。</summary>
        public static void Set(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
                {
                    if (key == null)
                        return;
                    if (enabled)
                        key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
                    else
                        key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
            }
            catch (Exception)
            {
                // 注册表写入失败不应导致程序崩溃
            }
        }
    }

    // ═══════════════════════════
    //  托盘应用
    // ═══════════════════════════

    /// <summary>主应用：系统托盘 + 定时检测。</summary>
    internal sealed class TrayAppContext : ApplicationContext
    {
        private const int MaxVisibleRepos = 20;  // 一级菜单最多同时显示的仓库数
        // .NET Framework 的 NotifyIcon.Text 上限是 63 字符（超出抛 ArgumentOutOfRangeException），
        // 而不是文档中 Win32 新外壳的 127。截断时留一个字符余量。
        private const int MaxTooltipLength = 63;

        private readonly NotifyIcon _tray;
        private readonly ContextMenuStrip _menu;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly Control _invoker;       // 用于把后台结果封送回 UI 线程

        private List<GitStatus> _results = new List<GitStatus>();
        private readonly Dictionary<string, RepoScheduleState> _schedules =
            new Dictionary<string, RepoScheduleState>();
        private int _repoOffset;                 // 仓库列表滚动偏移
        private volatile bool _checking;
        private volatile bool _exiting;

        public TrayAppContext()
        {
            // 隐藏的 Invoke 载体（无窗口应用没有可 Invoke 的窗体）
            _invoker = new Control();
            _invoker.CreateControl();

            // 托盘
            _tray = new NotifyIcon
            {
                Icon = StatusIcons.GetIcon("error"),
                Text = "TortoiseGit-Monitor",
                Visible = true,
            };
            _tray.MouseClick += OnTrayMouseClick;

            // 菜单
            _menu = new ContextMenuStrip();
            RebuildMenu();
            _tray.ContextMenuStrip = _menu;

            // 定时器
            _timer = new System.Windows.Forms.Timer();
            _timer.Tick += (s, e) => Refresh();

            Refresh();
        }

        // ═══════════════════════════
        //  菜单构建
        // ═══════════════════════════

        /// <summary>完全重建菜单内容。</summary>
        private void RebuildMenu()
        {
            _menu.Items.Clear();

            List<GitStatus> repos = _results;
            int total = repos.Count;

            if (total > 0)
            {
                if (total > MaxVisibleRepos)
                {
                    // ── 上翻箭头 ──
                    if (_repoOffset > 0)
                    {
                        var up = new ToolStripMenuItem(
                            $"  上翻（{_repoOffset} 个）",
                            StatusIcons.MakeArrowBitmap("up", ThemeColors.Blue),
                            (s, e) => PageUp());
                        _menu.Items.Add(up);
                    }
                    else
                    {
                        var up = new ToolStripMenuItem(
                            "  ── 到顶 ──",
                            StatusIcons.MakeArrowBitmap("up", ThemeColors.Overlay, enabled: false))
                        { Enabled = false };
                        _menu.Items.Add(up);
                    }

                    // ── 可见仓库 ──
                    int end = Math.Min(_repoOffset + MaxVisibleRepos, total);
                    for (int i = _repoOffset; i < end; i++)
                        _menu.Items.Add(MakeRepoItem(repos[i]));

                    // ── 下翻箭头 ──
                    if (end < total)
                    {
                        int remaining = total - end;
                        var down = new ToolStripMenuItem(
                            $"  下翻（{remaining} 个）",
                            StatusIcons.MakeArrowBitmap("down", ThemeColors.Blue),
                            (s, e) => PageDown());
                        _menu.Items.Add(down);
                    }
                    else
                    {
                        var down = new ToolStripMenuItem(
                            "  ── 到底 ──",
                            StatusIcons.MakeArrowBitmap("down", ThemeColors.Overlay, enabled: false))
                        { Enabled = false };
                        _menu.Items.Add(down);
                    }
                }
                else
                {
                    // ── 不超过 20 个，全部显示 ──
                    foreach (GitStatus st in repos)
                        _menu.Items.Add(MakeRepoItem(st));
                }

                _menu.Items.Add(new ToolStripSeparator());
            }

            // ── 静态菜单项 ──
            _menu.Items.Add(new ToolStripMenuItem("⟳  刷新全部", null, (s, e) => Refresh()));
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(new ToolStripMenuItem("⚙  设置...", null, (s, e) => OpenSettings()));
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(new ToolStripMenuItem("✕  退出", null, (s, e) => Quit()));
        }

        /// <summary>构建单个仓库菜单项（状态图标 + 名称 + 摘要）。</summary>
        private ToolStripMenuItem MakeRepoItem(GitStatus st)
        {
            var parts = new List<string>();
            if (st.Dirty)
                parts.Add("修改");
            if (st.Behind > 0)
                parts.Add($"↓{st.Behind}");
            if (st.Ahead > 0)
                parts.Add($"↑{st.Ahead}");
            if (parts.Count == 0 && st.Error.Length == 0)
                parts.Add("同步");
            if (st.Error.Length > 0)
                parts = new List<string> { "⚠" };

            string label = $"  {st.RepoName}  ({string.Join(" ", parts)})";
            string path = st.RepoPath;
            return new ToolStripMenuItem(label, StatusIcons.GetRepoStatusBitmap(st),
                (s, e) => TortoiseGitLauncher.Launch(path));
        }

        /// <summary>数据更新时重置偏移并重建菜单。</summary>
        private void UpdateRepoMenu()
        {
            _repoOffset = 0;
            RebuildMenu();
        }

        /// <summary>上翻一页。</summary>
        private void PageUp()
        {
            _repoOffset = Math.Max(0, _repoOffset - MaxVisibleRepos);
            RebuildMenu();
        }

        /// <summary>下翻一页。</summary>
        private void PageDown()
        {
            int maxOffset = Math.Max(0, _results.Count - MaxVisibleRepos);
            _repoOffset = Math.Min(maxOffset, _repoOffset + MaxVisibleRepos);
            RebuildMenu();
        }

        // ═══════════════════════════
        //  检测
        // ═══════════════════════════

        /// <summary>触发一轮后台检测（UI 线程调用，立即返回）。</summary>
        public void Refresh()
        {
            if (_checking || _exiting)
                return;
            _checking = true;

            AppConfig cfg = AppConfig.Load();
            GlobalSettings glb = cfg.Global;

            SetTrayText("TortoiseGit-Monitor - 检测中...");
            bool checkBranches = cfg.Projects.Any(p => p.MonitorAllBranches);
            int logCount = glb.LogCount;

            Task.Run(() =>
            {
                List<GitStatus> results;
                try
                {
                    // 在工作线程中解析仓库路径（避免 UI 线程阻塞）
                    List<string> repos = AppConfig.ResolveRepoPaths(cfg);
                    results = repos.Count == 0
                        ? new List<GitStatus>()
                        : GitWatcher.CheckAllParallel(repos, logCount, _schedules,
                            checkBranchesFlag: checkBranches);
                }
                catch (Exception ex)
                {
                    // 异常时构造错误结果，确保 UI 不会卡在"检测中..."
                    var keys = _schedules.Keys.ToList();
                    results = keys.Count > 0
                        ? keys.Select(p => new GitStatus
                        {
                            RepoPath = p,
                            RepoName = Path.GetFileName(p.TrimEnd('\\', '/')),
                            Error = $"检测异常: {ex.Message}",
                        }).ToList()
                        : new List<GitStatus>
                        {
                            new GitStatus { RepoPath = ".", RepoName = "错误", Error = $"检测异常: {ex.Message}" },
                        };
                }

                if (_exiting)
                    return;
                _invoker.BeginInvoke((Action)(() => OnResults(results, glb)));
            });

            // 更新定时器
            int interval = Math.Max(60, glb.IntervalSec) * 1000;
            if (_timer.Interval != interval || !_timer.Enabled)
            {
                _timer.Interval = interval;
                _timer.Start();
            }
        }

        /// <summary>检测结果回到 UI 线程：更新托盘、菜单，并按需弹通知。</summary>
        private void OnResults(List<GitStatus> results, GlobalSettings glb)
        {
            _checking = false;
            if (_exiting)
                return;

            // 发现新的远程提交时弹出通知（behind 数较上一轮增加的仓库）
            if (glb.ShowNotifications)
            {
                foreach (GitStatus r in results)
                {
                    GitStatus prev = _results.FirstOrDefault(x => x.RepoPath == r.RepoPath);
                    if (prev != null && r.Behind > prev.Behind && r.Error.Length == 0)
                    {
                        string name = r.RepoName;
                        int count = r.Behind;
                        bool sound = glb.NotificationSound;
                        Task.Run(() => Notifier.NotifyNewCommits(name, count, sound));
                    }
                }
            }

            _results = results;
            UpdateTray();
            UpdateRepoMenu();
        }

        private void UpdateTray()
        {
            if (_results.Count == 0)
            {
                _tray.Icon = StatusIcons.GetIcon("error");
                return;
            }

            bool hasBehind = _results.Any(r => r.Behind > 0);
            bool hasDirty = _results.Any(r => r.Dirty);
            bool hasError = _results.Any(r => r.Error.Length > 0);

            string iconKey;
            if (hasError || hasBehind)
                iconKey = "behind";
            else if (hasDirty)
                iconKey = "dirty";
            else
                iconKey = "clean";
            _tray.Icon = StatusIcons.GetIcon(iconKey);

            var lines = new List<string> { "TortoiseGit-Monitor" };
            foreach (GitStatus r in _results)
                lines.Add($"  {GitWatcher.StatusIcon(r)} {r.RepoName}: {GitWatcher.StatusSummary(r)}");
            SetTrayText(string.Join("\n", lines));
        }

        /// <summary>设置托盘提示文本（超长截断到 Win32 上限）。</summary>
        private void SetTrayText(string text)
        {
            if (text.Length > MaxTooltipLength)
                text = text.Substring(0, MaxTooltipLength);
            _tray.Text = text;
        }

        // ═══════════════════════════
        //  交互
        // ═══════════════════════════

        private void OnTrayMouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                // 左键：显示右键菜单（NotifyIcon 内部方法，官方推荐做法）
                typeof(NotifyIcon)
                    .GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(_tray, null);
            }
        }

        private void OpenSettings()
        {
            using (var dlg = new SettingsForm())
            {
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    _timer.Stop();
                    Refresh();
                }
            }
        }

        private void Quit()
        {
            _exiting = true;
            _timer.Stop();
            _tray.Visible = false;
            _tray.Dispose();
            _invoker.Dispose();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
                _menu.Dispose();
                _tray.Visible = false;
                _tray.Dispose();   // Dispose 幂等，Quit 已调用过时不会重复出错
                if (!_invoker.IsDisposed)
                    _invoker.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
