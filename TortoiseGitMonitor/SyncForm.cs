/**
 * 一键 Pull 更新：进度窗口 + 交互对话框。
 *
 * - 进度窗口：只读日志框实时显示同步过程，后台线程执行 GitSyncer。
 * - 交互对话框（AskDialog）：替代 git-sync 终端版的 input() 询问，
 *   所有需要用户决策的场景（merge 进行中、冲突文件 LLM 对话选项等）
 *   都弹出模态对话框，按钮即选项。
 *
 * 线程模型：同步在后台线程执行；日志经 BeginInvoke 追加，询问经 Invoke
 * 阻塞后台线程直到用户做出选择。运行期间禁止关闭窗口（避免半同步状态）。
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TortoiseGitMonitor
{
    /// <summary>统一显示并激活进度窗口，供首次打开和再次点击菜单使用。</summary>
    internal static class ProgressWindow
    {
        private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        internal static void ShowAndActivate(Form form)
        {
            form.Show();
            if (form.WindowState == FormWindowState.Minimized)
                form.WindowState = FormWindowState.Normal;
            // 隐藏启动可能影响首次 ShowWindow；再次显式显示，不能仅依赖 Activate。
            ShowWindow(form.Handle, 5); // SW_SHOW，保留正常/最大化窗口状态。
            form.BringToFront();
            form.Activate();
        }

        /// <summary>恢复旧实例已经隐藏的同步窗口；不重启程序，不启动或中断任何 Git 操作。</summary>
        internal static string RestoreRunningWindow(int processId)
        {
            using (var process = Process.GetProcessById(processId))
            {
                if (!string.Equals(process.ProcessName, "TortoiseGitMonitor", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("目标进程不是 TortoiseGitMonitor");
                var candidates = new List<IntPtr>();
                EnumWindows((window, parameter) =>
                {
                    uint owner;
                    GetWindowThreadProcessId(window, out owner);
                    if (owner != processId) return true;
                    var title = new StringBuilder(256);
                    GetWindowText(window, title, title.Capacity);
                    if (title.ToString() == "一键 Pull 更新") candidates.Add(window);
                    return true;
                }, IntPtr.Zero);
                if (candidates.Count != 1)
                    throw new InvalidOperationException("需要恰好一个现有同步窗口，实际找到 " + candidates.Count);
                IntPtr target = candidates[0];
                uint targetOwner;
                GetWindowThreadProcessId(target, out targetOwner);
                if (targetOwner != processId || process.HasExited)
                    throw new InvalidOperationException("目标进程或窗口已经变化");
                bool wasVisible = IsWindowVisible(target);
                ShowWindowAsync(target, 9); // SW_RESTORE，不依赖旧实例的 Activate 逻辑。
                SetForegroundWindow(target);
                for (int i = 0; i < 20 && !IsWindowVisible(target); i++) Thread.Sleep(50);
                if (!IsWindowVisible(target)) throw new InvalidOperationException("窗口未能显示，未改动同步任务");
                return "process_id=" + processId + "\nwas_visible=" + wasVisible + "\nis_visible=True\n";
            }
        }
    }

    /// <summary>选项对话框：prompt + 一组选项按钮，返回 1-based 选项序号。</summary>
    internal sealed class AskDialog : Form
    {
        private int _choice;

        private AskDialog(string prompt, IList<string> options)
        {
            Text = "需要确认";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            MinimumSize = new Size(460, 0);
            BackColor = ThemeColors.Bg;
            Font = new Font("Microsoft YaHei UI", 9F);
            TopMost = true;   // 托盘应用无任务栏窗口，置顶确保用户能看到询问

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                Padding = new Padding(12),
                AutoSize = true,
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            // prompt 可能较长（含 LLM 理由），用只读文本框便于滚动查看
            var txtPrompt = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = ThemeColors.Bg,
                ForeColor = ThemeColors.Text,
                Dock = DockStyle.Top,
                ScrollBars = ScrollBars.Vertical,
                Text = prompt,
                TabStop = false,
            };
            // 按内容行数估算高度，限制在 60~260 像素之间
            int lines = prompt.Split('\n').Length + prompt.Length / 55;
            txtPrompt.Height = Math.Max(60, Math.Min(260, lines * 20));
            root.Controls.Add(txtPrompt, 0, 0);

            var panel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                AutoSize = true,
                Dock = DockStyle.Top,
                WrapContents = false,
                Margin = new Padding(0, 10, 0, 0),
            };
            for (int i = 0; i < options.Count; i++)
            {
                int index = i + 1;   // 1-based，与 git-sync 终端版一致
                var btn = new Button
                {
                    Text = $"{index}. {options[i]}",
                    AutoSize = true,
                    MinimumSize = new Size(320, 30),
                    TextAlign = ContentAlignment.MiddleLeft,
                    Padding = new Padding(8, 2, 8, 2),
                };
                if (i == 0)
                {
                    btn.FlatStyle = FlatStyle.Flat;
                    btn.BackColor = ThemeColors.Blue;
                    btn.ForeColor = Color.White;
                }
                btn.Click += (s, e) =>
                {
                    _choice = index;
                    DialogResult = DialogResult.OK;
                    Close();
                };
                panel.Controls.Add(btn);
            }
            root.Controls.Add(panel, 0, 1);
            Controls.Add(root);
        }

        /// <summary>弹出模态对话框并返回 1-based 选项序号；直接关闭窗口视为选第 1 项（默认项）。</summary>
        public static int Ask(IWin32Window owner, string prompt, IList<string> options)
        {
            using (var dlg = new AskDialog(prompt, options))
            {
                dlg.ShowDialog(owner);
                return dlg._choice >= 1 ? dlg._choice : 1;
            }
        }
    }

    /// <summary>一键 Pull 更新的进度窗口（单例）。</summary>
    internal sealed class SyncForm : Form
    {
        private static SyncForm _instance;

        private readonly List<string> _repos;
        private readonly Action _onComplete;
        private readonly TextBox _log;
        private readonly Label _status;
        private readonly Button _btnClose;
        private volatile bool _running;
        internal static bool IsRunning => _instance != null && _instance._running;
        private GitSyncer _syncer;
        private readonly CancellationTokenSource _forceStop = new CancellationTokenSource();
        private readonly Button _btnStop;
        private readonly Button _btnForce;

        private SyncForm(List<string> repos, Action onComplete)
        {
            _repos = repos;
            _onComplete = onComplete;

            Text = "一键 Pull 更新";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(760, 520);
            MinimumSize = new Size(560, 360);
            BackColor = ThemeColors.Bg;
            Font = new Font("Microsoft YaHei UI", 9F);

            _log = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                ForeColor = ThemeColors.Text,
                Font = new Font("Consolas", 9F),
                WordWrap = false,
            };

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 40 };
            _status = new Label
            {
                Text = "正在同步...",
                AutoSize = false,
                AutoEllipsis = true,
                ForeColor = ThemeColors.Subtext,
                Location = new Point(10, 12),
            };
            _btnClose = new Button
            {
                Text = "关闭",
                Width = 80,
                Height = 28,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Enabled = false,
            };
            _btnClose.Click += (s, e) => Close();
            _btnStop = new Button { Text = "安全停止", Width = 90, Height = 28 };
            _btnStop.Click += (s, e) => RequestStop();
            _btnForce = new Button { Text = "强制停止…", Width = 90, Height = 28, Enabled = false };
            _btnForce.Click += (s, e) =>
            {
                if (MessageBox.Show(this, "将终止当前命令的整个进程树，并等待退出。可能留下部分工作区改动或残留锁，需要人工检查恢复。确定强制停止？",
                    "强制停止同步", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    return;
                _forceStop.Cancel();
                _btnForce.Enabled = false;
                _status.Text = "正在等待进程树退出…";
            };
            bottom.Controls.Add(_btnStop);
            bottom.Controls.Add(_btnForce);
            bottom.Controls.Add(_status);
            bottom.Controls.Add(_btnClose);
            bottom.Resize += (s, e) =>
            {
                _btnClose.Location = new Point(bottom.Width - _btnClose.Width - 10, 6);
                _btnForce.Location = new Point(_btnClose.Left - _btnForce.Width - 8, 6);
                _btnStop.Location = new Point(_btnForce.Left - _btnStop.Width - 8, 6);
                _status.Width = Math.Max(100, _btnStop.Left - 20);
            };

            Controls.Add(_log);
            Controls.Add(bottom);
        }

        /// <summary>打开进度窗口并开始同步（已有窗口则直接激活）。onComplete 在 UI 线程回调。</summary>
        public static void ShowFor(List<string> repos, Action onComplete)
        {
            if (TryActivateExisting()) return;
            _instance = new SyncForm(repos, onComplete);
            ProgressWindow.ShowAndActivate(_instance);
        }

        internal static bool TryActivateExisting()
        {
            if (_instance == null || _instance.IsDisposed) return false;
            ProgressWindow.ShowAndActivate(_instance);
            return true;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _running = true;
            Task.Run(() => RunSync());
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_running)
            {
                RequestStop();
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
        }

        // ═══════════════════════════
        //  同步执行（后台线程）
        // ═══════════════════════════

        private void RunSync()
        {
            var results = new List<SyncRepoResult>();
            try
            {
                _syncer = new GitSyncer(AppConfig.Load().Llm)
                {
                    Log = AppendLog,
                    Ask = AskOnUiThread,
                    ForceCancellation = _forceStop.Token,
                };

                foreach (string repo in _repos)
                {
                    try
                    {
                        if (_syncer.StopRequested) break;
                        results.Add(_syncer.SyncRepo(repo));
                        if (_syncer.StopRequested) break;
                    }
                    catch (OperationCanceledException)
                    {
                        AppendLog("\n用户中止全部同步");
                        results.Add(new SyncRepoResult { Repo = repo, Status = "skipped", Reason = "用户中止" });
                        break;
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"✗ 仓库异常: {ex.Message}");
                        results.Add(new SyncRepoResult { Repo = repo, Status = "error", Reason = ex.Message });
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog("✗ 同步初始化失败: " + ex.Message);
                results.Add(new SyncRepoResult { Repo = "同步初始化", Status = "error", Reason = ex.Message });
            }

            PrintSummary(results);

            _running = false;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    _status.Text = _syncer != null && _syncer.StopRequested ? "同步已停止" : results.Any(r => r.Status != "ok") ? "同步结束，请检查摘要" : "同步完成";
                    _btnStop.Enabled = false;
                    _btnForce.Enabled = false;
                    _btnClose.Enabled = true;
                    _onComplete?.Invoke();
                }));
            }
            catch (Exception)
            {
                // 窗口可能已被销毁
            }
        }

        private void RequestStop()
        {
            if (!_running || _syncer == null || _syncer.StopRequested) return;
            _syncer.StopRequested = true;
            _btnStop.Enabled = false;
            _btnForce.Enabled = true;
            _status.Text = "等待当前命令完成后停止…";
            AppendLog("已请求安全停止：等待当前命令完成，不再启动后续写入；stash 和当前状态会保留。若命令一直无响应，可选择强制停止。");
        }

        /// <summary>在 UI 线程弹选项对话框，阻塞后台线程直到用户选择。</summary>
        private int AskOnUiThread(string prompt, IList<string> options)
        {
            try
            {
                if (_syncer.StopRequested) throw new OperationCanceledException();
                return (int)Invoke((Func<int>)(() => AskDialog.Ask(this, prompt, options)));
            }
            catch (Exception)
            {
                throw new OperationCanceledException("询问已取消或窗口不可用");
            }
        }

        private void AppendLog(string msg)
        {
            try
            {
                BeginInvoke((Action)(() =>
                {
                    _log.AppendText(msg + Environment.NewLine);
                }));
            }
            catch (Exception)
            {
                // 窗口可能已被销毁
            }
        }

        // ═══════════════════════════
        //  摘要（移植自 git_sync._print_summary）
        // ═══════════════════════════

        private void PrintSummary(List<SyncRepoResult> results)
        {
            AppendLog("");
            AppendLog(new string('#', 60));
            AppendLog("同步摘要");
            AppendLog(new string('#', 60));
            foreach (SyncRepoResult r in results)
            {
                if (r.Status != "ok")
                {
                    AppendLog($"  [{r.Repo}] {r.Status}: {r.Reason}");
                }
                AppendLog($"  [{r.Repo}] merge={r.Merge} stash_pop={r.StashPop}");
                if (r.UntrackedBackupDirectory.Length > 0)
                    AppendLog($"    未跟踪阻塞文件已备份 {r.UntrackedBackedUp} 个: {r.UntrackedBackupDirectory}");
                if (r.UnmergedRemaining.Count > 0)
                    AppendLog("    未解决: " + string.Join(", ", r.UnmergedRemaining));
                if (r.StatusKnown)
                {
                    AppendLog($"    已暂存(staged): {r.Staged}；未暂存(unstaged): {r.Unstaged}；未跟踪(untracked): {r.Untracked}；未合并: {r.Unmerged}");
                    if (r.Staged > 0) AppendLog("    暂存区有改动，请检查后手动提交。");
                }
                else
                    AppendLog("    工作区状态未知，请手动检查 git status。");
            }
            AppendLog("");
            AppendLog("提示: 工具不自动 commit/push，请检查后手动提交。");
        }
    }
}
