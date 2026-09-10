/**
 * 全部仓库分支切换：将监控目录下的所有 git 仓库切换到指定分支。
 *
 * 交互流程：托盘菜单 -> 输入目标分支 -> 进度窗口逐仓库切换。
 * 每仓库流程：预检 -> fetch -> (脏工作区询问/stash) -> checkout -> pop stash -> 摘要。
 *
 * 切换策略：
 * - 本地已存在该分支 -> git checkout <branch>
 * - 本地不存在但 origin/<branch> 存在 -> git checkout -b <branch> --track origin/<branch>
 * - 两边都不存在 -> 标记失败，跳过该仓库
 *
 * 安全约束（与一键 Pull 更新一致）：
 * - merge/rebase 进行中状态先询问，绝不贸然操作
 * - 脏工作区先询问，可选 stash 后切换并恢复
 * - 单仓库失败隔离，继续处理下一个
 * - 写操作记录到配置文件目录的 branch_switch.log，可审计
 *
 * 线程模型与 SyncForm 相同：后台线程执行，日志 BeginInvoke 追加，
 * 询问经 Invoke 弹 AskDialog（复用 SyncForm.cs 中的 AskDialog）。
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TortoiseGitMonitor
{
    // ═══════════════════════════
    //  引擎
    // ═══════════════════════════

    /// <summary>单个仓库的分支切换结果。</summary>
    internal sealed class BranchSwitchResult
    {
        public string Repo = "";
        public string Status = "";    // switched | already | skipped | error
        public string From = "";      // 切换前分支
        public string Detail = "";
    }

    /// <summary>分支切换引擎：串行处理多个仓库，交互通过回调完成。</summary>
    internal sealed class BranchSwitcher
    {
        /// <summary>日志输出（追加到进度窗口）。</summary>
        public Action<string> Log;

        /// <summary>询问用户：prompt + 选项列表 -> 1-based 选项序号。</summary>
        public Func<string, IList<string>, int> Ask;

        private readonly string _logFile;
        private readonly string _branch;

        public BranchSwitcher(string branch)
        {
            _branch = branch;
            _logFile = Path.Combine(AppConfig.ConfigDir, "branch_switch.log");
            Log = msg => { };
            Ask = (prompt, options) => 1;
        }

        private void FileLog(string msg)
        {
            try
            {
                Directory.CreateDirectory(AppConfig.ConfigDir);
                File.AppendAllText(_logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n", new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // 日志失败不影响主流程
            }
        }

        // ── 底层 git 调用：统一进程树托管 ──

        private static ProcessResult Run(string cwd, int timeoutSec, params string[] args) =>
            GitProcess.Read(cwd, timeoutSec, args);

        /// <summary>执行写操作并记审计日志。</summary>
        private ProcessResult RunWrite(string cwd, string action, params string[] args)
        {
            ProcessResult res = GitProcess.Write(cwd, action, Log, CancellationToken.None, 0, args);
            FileLog($"{action}: git {string.Join(" ", args)} -> rc={res.Code} (cwd={cwd})");
            // checkout 的明确未跟踪文件阻塞允许用户选择备份重试，其他写入失败立即停止。
            bool blockers = action.StartsWith("checkout") && res.Stderr.Contains("would be overwritten by checkout");
            if (!res.Ok && !blockers)
                throw new IOException(action + " 失败，已停止后续写入；stash 保留，请检查 git status / git stash list。\n" + res.Stderr);
            return res;
        }

        // ── 状态判断 ──

        private static bool IsRepo(string path) =>
            Run(path, 10, "rev-parse", "--is-inside-work-tree").Ok;

        private static string CurrentBranch(string path)
        {
            ProcessResult res = Run(path, 10, "branch", "--show-current");
            return res.Ok ? res.Stdout.Trim() : "";
        }

        /// <summary>是否有未提交的 tracked 改动（不含未跟踪文件）。</summary>
        private static bool IsDirty(string path)
        {
            ProcessResult res = Run(path, 120, "status", "--short");
            if (!res.Ok) throw new IOException("无法确认工作区状态: " + res.Stderr);
            foreach (string line in res.Stdout.Split('\n'))
            {
                if (line.Length < 2 || line.StartsWith("??"))
                    continue;
                if (line.Trim().Length > 0)
                    return true;
            }
            return false;
        }

        /// <summary>检测进行中的 merge/rebase/cherry-pick/revert，返回类型名或 null。</summary>
        private static string InProgress(string path)
        {
            ProcessResult res = Run(path, 10, "rev-parse", "--git-dir");
            if (!res.Ok) throw new IOException("无法确认仓库进行中状态: " + res.Stderr);
            string gd = Path.GetFullPath(Path.Combine(path, res.Stdout.Trim()));
            if (File.Exists(Path.Combine(gd, "MERGE_HEAD")))
                return "merge";
            if (Directory.Exists(Path.Combine(gd, "rebase-merge")) || Directory.Exists(Path.Combine(gd, "rebase-apply")))
                return "rebase";
            if (File.Exists(Path.Combine(gd, "CHERRY_PICK_HEAD")))
                return "cherry-pick";
            if (File.Exists(Path.Combine(gd, "REVERT_HEAD")))
                return "revert";
            return null;
        }

        private static bool LocalBranchExists(string path, string branch) =>
            Run(path, 10, "rev-parse", "--verify", "refs/heads/" + branch).Ok;

        private static bool RemoteBranchExists(string path, string branch) =>
            Run(path, 10, "rev-parse", "--verify", "refs/remotes/origin/" + branch).Ok;

        // ── 单仓库切换 ──

        public BranchSwitchResult SwitchRepo(string path)
        {
            using (var operation = RepositoryOperation.TryEnter(path))
            {
                if (operation == null)
                    return new BranchSwitchResult { Repo = Path.GetFileName(path), Status = "skipped", Detail = "仓库正在执行其他操作，请稍后重试" };
                return SwitchRepoCore(path);
            }
        }

        private BranchSwitchResult SwitchRepoCore(string path)
        {
            string name = Path.GetFileName(path.TrimEnd('\\', '/'));
            var result = new BranchSwitchResult { Repo = name };
            Log("");
            Log(new string('=', 60));
            Log($"仓库: {name} ({path})");
            Log(new string('=', 60));

            if (!IsRepo(path))
            {
                Log("✗ 不是 git 仓库，跳过");
                result.Status = "skipped";
                result.Detail = "不是 git 仓库";
                return result;
            }

            // 预检：进行中状态
            string prog = InProgress(path);
            if (prog != null)
            {
                int choice = Ask($"⚠ 仓库 {name} 处于 {prog} 进行中状态，如何处理？",
                    new[] { "abort 后继续切换", "跳过此仓库（保留现状）", "中止全部切换" });
                if (choice == 1)
                {
                    RunWrite(path, "abort-" + prog, prog, "--abort");
                    Log($"已 abort {prog}");
                }
                else if (choice == 2)
                {
                    result.Status = "skipped";
                    result.Detail = $"用户跳过({prog}进行中)";
                    return result;
                }
                else
                {
                    throw new OperationCanceledException("用户中止全部切换");
                }
            }

            string cur = CurrentBranch(path);
            result.From = cur;
            if (cur == _branch)
            {
                Log($"• 已在目标分支 {_branch}，无需切换");
                result.Status = "already";
                return result;
            }
            if (cur.Length == 0)
            {
                Log("✗ 处于 detached HEAD 状态，跳过");
                result.Status = "skipped";
                result.Detail = "detached HEAD";
                return result;
            }

            // fetch（确保远程分支信息最新）
            Log("▶ fetch --all --prune ...");
            RunWrite(path, "fetch", "fetch", "--all", "--prune");

            // 脏工作区处理
            bool stashed = false;
            if (IsDirty(path))
            {
                int choice = Ask($"⚠ 仓库 {name} 有未提交的本地改动，如何处理？",
                    new[] { "stash 后切换并恢复", "跳过此仓库（保留现状）", "中止全部切换" });
                if (choice == 1)
                {
                    Log("▶ stash 本地改动(tracked)...");
                    ProcessResult sr = RunWrite(path, "stash-push", "stash", "push", "-m", "branch-switch:" + name);
                    stashed = sr.Ok && !sr.Stdout.Contains("No local changes to save");
                    Log("  stash " + (stashed ? "完成" : "未产生(无改动)"));
                }
                else if (choice == 2)
                {
                    result.Status = "skipped";
                    result.Detail = "用户跳过(有本地改动)";
                    return result;
                }
                else
                {
                    throw new OperationCanceledException("用户中止全部切换");
                }
            }

            // 切换分支
            ProcessResult cr = DoCheckout(path);
            if (cr != null && !cr.Ok && cr.Stderr.Contains("would be overwritten by checkout"))
            {
                // 未跟踪文件在目标分支中已被跟踪，阻塞 checkout（stash 只暂存 tracked，管不到它们）
                List<string> blockers = ParseUntrackedBlockers(cr.Stderr);
                int bc = Ask(
                    $"⚠ 仓库 {name} 有 {blockers.Count} 个未跟踪文件在目标分支 {_branch} 中已被跟踪，阻塞切换：\n" +
                    string.Join("\n", blockers.Select(b => "  " + b)) + "\n如何处理？",
                    new[] { "备份到 .git/branch-switch-backup 并移除后继续切换", "跳过此仓库（保留 stash）", "中止全部切换" });
                if (bc == 1)
                {
                    string backupDir = MoveToBackup(path, blockers);
                    Log("  已备份阻塞文件到: " + backupDir);
                    cr = DoCheckout(path);   // 移除后重试
                }
                else if (bc == 2)
                {
                    Log("已保留 stash，请检查后手动恢复。");
                    result.Status = "skipped";
                    result.Detail = "用户跳过(未跟踪文件阻塞)";
                    return result;
                }
                else
                {
                    Log("已保留 stash，请检查后手动恢复。");
                    throw new OperationCanceledException("用户中止全部切换");
                }
            }

            bool ok = cr != null && cr.Ok;
            string err = cr == null ? "分支不存在" : cr.Stderr;
            if (cr == null)
                Log($"✗ 本地与远程均不存在分支 {_branch}，跳过");

            if (!ok)
            {
                if (err != "分支不存在")
                    Log("  ✗ 切换失败: " + Trim200(err));
                result.Status = "error";
                result.Detail = (err == "分支不存在" ? "分支不存在" : Trim200(err)) + "；stash 保留，请检查后手动恢复";
                return result;
            }
            else
            {
                Log($"  ✓ 已切换到 {_branch}");
                result.Status = "switched";
            }

            // 仅切换成功后恢复 stash；失败不再叠加写入
            if (stashed)
            {
                Log("▶ pop stash...");
                ProcessResult pr = RunWrite(path, "stash-pop", "stash", "pop");
                string output = pr.Stdout + pr.Stderr;
                if (output.Contains("CONFLICT"))
                {
                    Log("  ⚠ stash pop 产生冲突，请手动解决（不自动处理）");
                    result.Detail = (result.Detail.Length > 0 ? result.Detail + "；" : "") + "stash pop 冲突待手动解决";
                }
                else if (!pr.Ok)
                {
                    Log("  ⚠ stash pop 失败（stash 保留，可手动 git stash pop）: " + Trim200(output));
                    result.Detail = (result.Detail.Length > 0 ? result.Detail + "；" : "") + "stash pop 失败";
                }
            }

            return result;
        }

        /// <summary>执行分支切换：本地分支直接 checkout，否则基于 origin 创建跟踪分支；两边都不存在返回 null。</summary>
        private ProcessResult DoCheckout(string path)
        {
            if (LocalBranchExists(path, _branch))
            {
                Log($"▶ 切换到本地分支 {_branch} ...");
                return RunWrite(path, "checkout", "checkout", _branch);
            }
            if (RemoteBranchExists(path, _branch))
            {
                Log($"▶ 本地无该分支，基于 origin/{_branch} 创建并切换 ...");
                return RunWrite(path, "checkout-track",
                    "checkout", "-b", _branch, "--track", "origin/" + _branch);
            }
            return null;
        }

        /// <summary>从 checkout 失败输出中解析"untracked ... would be overwritten"的阻塞文件列表。</summary>
        private static List<string> ParseUntrackedBlockers(string stderr)
        {
            var files = new List<string>();
            bool inList = false;
            foreach (string raw in stderr.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Contains("would be overwritten by checkout"))
                {
                    inList = true;
                    continue;
                }
                if (!inList)
                    continue;
                // 文件列表以缩进行列出，遇到 "Please move or remove" 等非缩进行结束
                if (line.StartsWith("\t") || line.StartsWith("    "))
                    files.Add(line.Trim());
                else
                    break;
            }
            return files;
        }

        /// <summary>把阻塞文件移动到 .git/branch-switch-backup/&lt;时间戳&gt;/ 下，返回备份目录。</summary>
        private string MoveToBackup(string repoPath, List<string> files)
        {
            string backupRoot = Path.Combine(repoPath, ".git", "branch-switch-backup",
                DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            foreach (string rel in files)
            {
                string src = Path.Combine(repoPath, rel);
                if (!File.Exists(src))
                    continue;
                string dst = Path.Combine(backupRoot, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Move(src, dst);
                FileLog($"backup: {rel} -> {dst}");
            }
            return backupRoot;
        }

        private static string Trim200(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            text = text.Trim();
            return text.Length > 200 ? text.Substring(0, 200) : text;
        }
    }

    // ═══════════════════════════
    //  分支名输入对话框
    // ═══════════════════════════

    /// <summary>目标分支输入框，确定返回分支名，取消返回 null。</summary>
    internal sealed class BranchInputDialog : Form
    {
        private readonly TextBox _txtBranch;

        private BranchInputDialog()
        {
            Text = "全部切换分支";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            MinimumSize = new Size(420, 0);
            BackColor = ThemeColors.Bg;
            Font = new Font("Microsoft YaHei UI", 9F);
            TopMost = true;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                Padding = new Padding(12),
                AutoSize = true,
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var hint = new Label
            {
                Text = "将监控目录下的所有 git 仓库切换到指定分支：\n本地有该分支直接切换；没有则基于 origin 同名分支创建并跟踪。",
                AutoSize = true,
                ForeColor = ThemeColors.Subtext,
            };
            root.Controls.Add(hint, 0, 0);

            var row = new TableLayoutPanel
            {
                ColumnCount = 2,
                AutoSize = true,
                Dock = DockStyle.Top,
                Margin = new Padding(0, 8, 0, 0),
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var lbl = new Label
            {
                Text = "目标分支:",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 6, 3, 3),
            };
            _txtBranch = new TextBox { Dock = DockStyle.Fill };
            row.Controls.Add(lbl, 0, 0);
            row.Controls.Add(_txtBranch, 1, 0);
            root.Controls.Add(row, 0, 1);

            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Dock = DockStyle.Top,
                WrapContents = false,
                Margin = new Padding(0, 10, 0, 0),
            };
            var btnOk = new Button { Text = "开始切换", Width = 90, DialogResult = DialogResult.OK };
            var btnCancel = new Button { Text = "取消", Width = 80, DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(btnOk);
            buttons.Controls.Add(btnCancel);
            root.Controls.Add(buttons, 0, 2);

            AcceptButton = btnOk;
            CancelButton = btnCancel;
            Controls.Add(root);
        }

        /// <summary>弹出输入框；返回 trimmed 分支名，取消或为空返回 null。</summary>
        public static string Prompt(IWin32Window owner)
        {
            using (var dlg = new BranchInputDialog())
            {
                if (dlg.ShowDialog(owner) != DialogResult.OK)
                    return null;
                string branch = dlg._txtBranch.Text.Trim();
                return branch.Length > 0 ? branch : null;
            }
        }
    }

    // ═══════════════════════════
    //  进度窗口（与 SyncForm 同模式，单例）
    // ═══════════════════════════

    /// <summary>全部切换分支的进度窗口（单例）。</summary>
    internal sealed class BranchSwitchForm : Form
    {
        private static BranchSwitchForm _instance;

        private readonly List<string> _repos;
        private readonly string _branch;
        private readonly Action _onComplete;
        private readonly TextBox _log;
        private readonly Label _status;
        private readonly Button _btnClose;
        private volatile bool _running;
        internal static bool IsRunning => _instance != null && _instance._running;

        private BranchSwitchForm(List<string> repos, string branch, Action onComplete)
        {
            _repos = repos;
            _branch = branch;
            _onComplete = onComplete;

            Text = "全部切换分支 - " + branch;
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
                Text = "正在切换...",
                AutoSize = true,
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
            bottom.Controls.Add(_status);
            bottom.Controls.Add(_btnClose);
            bottom.Resize += (s, e) => _btnClose.Location = new Point(bottom.Width - _btnClose.Width - 10, 6);

            Controls.Add(_log);
            Controls.Add(bottom);
        }

        /// <summary>打开进度窗口并开始切换（已有窗口则直接激活）。onComplete 在 UI 线程回调。</summary>
        public static void ShowFor(List<string> repos, string branch, Action onComplete)
        {
            if (_instance != null && !_instance.IsDisposed)
            {
                _instance.Activate();
                return;
            }
            _instance = new BranchSwitchForm(repos, branch, onComplete);
            _instance.Show();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _running = true;
            Task.Run(() => RunSwitch());
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_running)
            {
                // 切换进行中禁止关闭，避免留下半完成状态
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
        }

        // ── 执行（后台线程） ──

        private void RunSwitch()
        {
            var results = new List<BranchSwitchResult>();
            try
            {
                var switcher = new BranchSwitcher(_branch)
                {
                    Log = AppendLog,
                    Ask = AskOnUiThread,
                };
                foreach (string repo in _repos)
                {
                    try
                    {
                        results.Add(switcher.SwitchRepo(repo));
                    }
                    catch (OperationCanceledException)
                    {
                        AppendLog("\n用户中止全部切换");
                        results.Add(new BranchSwitchResult { Repo = repo, Status = "skipped", Detail = "用户中止" });
                        break;
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"✗ 仓库异常: {ex.Message}");
                        results.Add(new BranchSwitchResult { Repo = repo, Status = "error", Detail = ex.Message });
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog("✗ 初始化失败: " + ex.Message);
            }

            PrintSummary(results);
            if (results.Count == _repos.Count && results.All(r => r.Status == "switched" || r.Status == "already"))
                RunPostScripts();
            else
                AppendLog("存在失败或跳过的仓库，已停止后置脚本，请检查后再执行。");

            _running = false;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    _status.Text = "切换完成";
                    _btnClose.Enabled = true;
                    _onComplete?.Invoke();
                }));
            }
            catch (Exception)
            {
                // 窗口可能已被销毁
            }
        }

        private int AskOnUiThread(string prompt, IList<string> options)
        {
            try
            {
                return (int)Invoke((Func<int>)(() => AskDialog.Ask(this, prompt, options)));
            }
            catch (Exception)
            {
                return 1;   // 窗口已销毁等异常时取默认项
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

        // ── 摘要 ──

        /// <summary>全部切换完成后依次执行配置的后置脚本列表（如 dbfs 同步），输出追加到日志窗口。</summary>
        private void RunPostScripts()
        {
            List<string> scripts = AppConfig.Load().Global.PostSwitchScripts;
            if (scripts == null || scripts.Count == 0)
                return;
            foreach (string raw in scripts)
            {
                string script = (raw ?? "").Trim();
                if (script.Length == 0)
                    continue;
                RunOnePostScript(script);
            }
        }

        /// <summary>执行单个后置脚本（cmd /c，stdin 闭合防 pause 阻塞，GBK 解码输出）。</summary>
        private void RunOnePostScript(string script)
        {
            AppendLog("");
            AppendLog(new string('=', 60));
            AppendLog("后置脚本: " + script);
            AppendLog(new string('=', 60));
            if (!File.Exists(script))
            {
                AppendLog("✗ 脚本不存在，跳过");
                return;
            }
            try
            {
                ProcessResult execution = ProcessRunner.Run("cmd.exe", "/c " + ProcessUtil.QuoteArg(script),
                    Path.GetDirectoryName(script), 300, AppendLog, CancellationToken.None, Encoding.Default);
                foreach (string line in execution.Stdout.Split('\n')) AppendLog(line.TrimEnd('\r'));
                if (execution.Stderr.Length > 0) AppendLog(execution.Stderr);
                AppendLog(execution.Ok ? "✓ 脚本执行完成" : $"✗ 脚本未完成，退出码 {execution.Code}；整个进程树已退出");
            }
            catch (Exception ex)
            {
                AppendLog("✗ 脚本执行异常: " + ex.Message);
            }
        }

        private void PrintSummary(List<BranchSwitchResult> results)
        {
            AppendLog("");
            AppendLog(new string('#', 60));
            AppendLog("切换摘要");
            AppendLog(new string('#', 60));
            foreach (BranchSwitchResult r in results)
            {
                switch (r.Status)
                {
                    case "switched":
                        AppendLog($"  [{r.Repo}] {r.From} -> {_branch}" +
                                  (r.Detail.Length > 0 ? $"（{r.Detail}）" : ""));
                        break;
                    case "already":
                        AppendLog($"  [{r.Repo}] 已在 {_branch}");
                        break;
                    default:
                        AppendLog($"  [{r.Repo}] {r.Status}: {r.Detail}");
                        break;
                }
            }
        }
    }
}
