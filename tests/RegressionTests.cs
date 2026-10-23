// 无第三方测试框架；所有 Git 写入仅发生在本次创建的临时仓库，绝不接触用户仓库。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TortoiseGitMonitor
{
    internal static class RegressionTests
    {
        private static string Self => Assembly.GetExecutingAssembly().Location;
        private static string Root;
        private static string RealGit;
        private static int Passed;
        private static readonly Encoding Utf8 = new UTF8Encoding(false);

        [STAThread]
        private static int Main(string[] args)
        {
            ProcessUtil.EnableLongPaths();
            try
            {
                Console.OutputEncoding = new UTF8Encoding(false);
                if (args.Length > 0 && args[0] == "--print-image-path") { Console.Write(Self); return 0; }
                if (args.Length > 0 && args[0] == "--worker") return Worker(args);
                if (args.Length > 0 && args[0] == "--gate-worker")
                {
                    using (var operation = RepositoryOperation.TryEnter(args[1]))
                        Console.Write(operation == null ? "BUSY" : "ACQUIRED");
                    return 0;
                }
                if (args.Length > 0 && args[0] == "--window-visibility-worker") return WindowVisibilityWorker();
                if (args.Length > 0 && args[0] == "--window-only")
                {
                    TestWindowVisibility();
                    return 0;
                }
                if (args.Length > 0 && args[0] == "--summary-only")
                {
                    TestStatus();
                    TestUi();
                    return 0;
                }
                if (Path.GetFileName(Self).Equals("git.exe", StringComparison.OrdinalIgnoreCase)) return GitShim(args);
                Root = Path.Combine(Path.GetDirectoryName(Self), "monitor-regression-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Root);
                TestGitExecutablePath();
                if (args.Length > 0 && args[0] == "--git-path-only") return 0;
                RealGit = @"C:\Program Files\Git\cmd\git.exe";
                Environment.SetEnvironmentVariable("MONITOR_TEST_REAL_GIT", RealGit);
                // 防止测试继承用户的提交钩子或签名要求，也不使用任何远程凭据。
                Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", "NUL");
                Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
                Console.WriteLine("测试目录: " + Root);
                bool pendingOnly = args.Length > 0 && args[0] == "--pending-only";
                if (!pendingOnly)
                {
                    TestProcessTree();
                    TestStatus();
                    TestUi();
                    TestWindowVisibility();
                }
                string shim = Path.Combine(Root, "shim");
                Directory.CreateDirectory(shim);
                File.Copy(Self, Path.Combine(shim, "git.exe"));
                Environment.SetEnvironmentVariable("PATH", shim + ";" + Environment.GetEnvironmentVariable("PATH"));
                if (args.Length > 0 && args[0] == "--lock-regression-only")
                {
                    TestIndexLockPolicy();
                    TestTortoiseGitLockOwnership();
                    TestRepositoryGate();
                    TestStaleLockSync();
                    Console.WriteLine($"锁策略回归通过: {Passed} 组检查");
                    return 0;
                }
                if (!pendingOnly)
                {
                    TestUntrackedFastForward();
                    TestUntrackedPathTransitions();
                    TestBackupFailure();
                    TestPendingRecovery();
                    TestRecoveryGuards();
                }
                TestPendingNewChanges();
                TestPendingMultipleRetries();
                TestPendingRestoreFailure();
                TestPendingFetchFailure();
                TestPendingLateChanges();
                TestPendingBytes();
                if (pendingOnly)
                {
                    Console.WriteLine($"多批改动恢复通过: {Passed} 组检查");
                    return 0;
                }
                if (args.Length > 0 && args[0] == "--untracked-only")
                {
                    Console.WriteLine($"未跟踪文件与恢复策略通过: {Passed} 组检查");
                    return 0;
                }
                TestIndexLockPolicy();
                TestTortoiseGitLockOwnership();
                TestRepositoryGate();
                if (args.Length > 0 && args[0] == "--lock-policy-only")
                {
                    TestStaleLockSync();
                    Console.WriteLine($"锁策略通过: {Passed} 组检查");
                    return 0;
                }
                TestFastForward();
                TestStaleLockSync();
                TestFailures();
                TestStop();
                TestDivergence();
                TestVerificationMismatch();
                TestWrapperFailure();
                TestConflictAndAbort();
                TestBranchFailure();
                Console.WriteLine($"全部通过: {Passed} 组检查");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        private static void Assert(bool ok, string message)
        {
            if (!ok) throw new Exception("断言失败: " + message);
        }
        private static void Pass(string message) { Passed++; Console.WriteLine("通过: " + message); }
        private static void Write(string path, string value) { File.WriteAllText(path, value, Utf8); }

        private static string ResolveExecutable(string path) => (string)typeof(ProcessRunner)
            .GetMethod("FindExecutable", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { path });

        private static string FakeExecutable(string relativePath)
        {
            string path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            Write(path, "not an executable");
            return path;
        }

        private static void TestGitExecutablePath()
        {
            string bin = FakeExecutable(@"Git with spaces\bin\git.exe");
            string cmd = FakeExecutable(@"Git with spaces\cmd\git.exe");
            string native = FakeExecutable(@"Git with spaces\mingw64\bin\git.exe");
            Assert(ResolveExecutable(bin) == native && ResolveExecutable(cmd) == native, "Git for Windows 的 bin/cmd 包装入口必须直接解析到真实 Git");
            Assert(ResolveExecutable(native) == native, "已经指定的真实 Git 路径保持不变");
            string legacy = FakeExecutable(@"Legacy Git\cmd\git.exe");
            Assert(ResolveExecutable(legacy) == legacy, "真实 Git 不存在时保留原入口");
            string git32 = FakeExecutable(@"Git32\mingw32\bin\git.exe");
            Assert(ResolveExecutable(FakeExecutable(@"Git32\bin\git.exe")) == git32, "兼容 32 位 Git for Windows 布局");
            string standalone = FakeExecutable(@"custom-shim\git.exe");
            Assert(ResolveExecutable(standalone) == standalone, "自定义 Git shim 保持不变");
            string other = FakeExecutable(@"Git with spaces\cmd\python.exe");
            Assert(ResolveExecutable(other) == other, "非 Git 程序不得被重定向");

            string previousPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("PATH", "\"" + Path.GetDirectoryName(cmd) + "\";" + previousPath);
                Assert(ResolveExecutable("git") == native && ResolveExecutable("git.exe") == native, "PATH 入口与绝对路径使用同一解析规则");
                File.Copy(Self, native, true);
                ProcessResult result = ProcessRunner.Run("git", "--print-image-path", Root, 10);
                Assert(result.Ok && result.Stdout == native, "实际 CreateProcess 必须启动真实路径，不经过包装入口: " + result.Stderr);
            }
            finally { Environment.SetEnvironmentVariable("PATH", previousPath); }
            Pass("Git 真实入口解析、空格路径、32 位兼容、缺失回退、非 Git 隔离及实际进程路径");
        }

        private static int Worker(string[] args)
        {
            string mode = args[1], dir = args[2];
            if (mode == "idle")
            {
                Write(Path.Combine(dir, "idle-started"), "started");
                Thread.Sleep(int.Parse(args[3]));
                return 0;
            }
            if (mode == "parent")
            {
                Console.Write(new string('O', 100000));
                Console.Error.Write(new string('E', 100000));
                using (Process.Start(new ProcessStartInfo(Self, ProcessUtil.BuildArguments("--worker", "lock", dir, args[3]))
                { UseShellExecute = false, CreateNoWindow = true })) { }
                return 0; // 父进程先退出；子进程仍持有锁和继承的输出句柄
            }
            using (var held = new FileStream(Path.Combine(dir, "index.lock"), FileMode.Create, FileAccess.Write, FileShare.None))
            {
                Write(Path.Combine(dir, "pid"), Process.GetCurrentProcess().Id.ToString());
                Console.Write(new string('O', 100000));
                Console.Error.Write(new string('E', 100000));
                Thread.Sleep(int.Parse(args[3]));
                Write(Path.Combine(dir, "done"), "done");
            }
            return 0;
        }

        private static void TestProcessTree()
        {
            string dir = Path.Combine(Root, "process-normal");
            Directory.CreateDirectory(dir);
            var clock = Stopwatch.StartNew();
            ProcessResult result = ProcessRunner.Run(Self, ProcessUtil.BuildArguments("--worker", "parent", dir, "1600"), dir, 10);
            Assert(result.Ok && File.Exists(Path.Combine(dir, "done")) && clock.ElapsedMilliseconds >= 1500, "必须等提前退出父进程的子树完成: rc=" + result.Code + " " + result.Stderr);
            Assert(result.Stdout.Length == 100000 && result.Stderr.Length == 100000, "双路大量输出必须完整排空: stdout=" + result.Stdout.Length + ", stderr=" + result.Stderr.Length + ", stderr前缀=" + result.Stderr.Substring(0, Math.Min(200, result.Stderr.Length)));
            Pass("父进程先退出，等待写锁子进程完成；stdout/stderr 各 100 KB 无死锁");

            foreach (bool cancel in new[] { false, true })
            {
                dir = Path.Combine(Root, cancel ? "process-cancel" : "process-timeout");
                Directory.CreateDirectory(dir);
                using (var source = new CancellationTokenSource())
                {
                    if (cancel) source.CancelAfter(1200);
                    result = ProcessRunner.Run(Self, ProcessUtil.BuildArguments("--worker", "parent", dir, "60000"), dir,
                        cancel ? 0 : 2, Console.WriteLine, source.Token);
                }
                Assert(!result.Ok && (cancel ? result.Cancelled : result.TimedOut), "正确区分取消和超时");
                Assert(!File.Exists(Path.Combine(dir, "done")), "子进程不得继续写入");
                int pid = int.Parse(File.ReadAllText(Path.Combine(dir, "pid")));
                bool exited;
                try { using (var p = Process.GetProcessById(pid)) exited = p.HasExited; }
                catch (ArgumentException) { exited = true; }
                Assert(exited, "子进程必须退出");
                Assert(File.Exists(Path.Combine(dir, "index.lock")), "保留残留锁");
                using (new FileStream(Path.Combine(dir, "index.lock"), FileMode.Open, FileAccess.Read, FileShare.None)) { }
                Pass((cancel ? "取消" : "超时") + "杀完整子树并等待，锁句柄已释放、锁文件保留");
            }
        }

        private static void TestStatus()
        {
            var r = new SyncRepoResult();
            GitSyncer.ParseStatus(" M unstaged\0?? untracked\0M  staged\0MM both\0R  new\0old\nname\0UU conflict\0", r);
            Assert(r.Staged == 3 && r.Unstaged == 2 && r.Untracked == 1 && r.Unmerged == 1 && r.UnmergedRemaining.SequenceEqual(new[] { "conflict" }), "XY、重命名和冲突计数");
            GitSyncer.ParseStatus(" M only\0", r);
            Assert(r.Staged == 0 && r.Unstaged == 1, "首列空格不得丢失");
            Pass("staged/unstaged/untracked、重命名换行文件和未合并状态计数");
        }

        private static void TestUi()
        {
            using (var form = (Form)Activator.CreateInstance(typeof(SyncForm), BindingFlags.NonPublic | BindingFlags.Instance,
                null, new object[] { new List<string>(), null }, null))
            {
                var handle = form.Handle;
                var r = new SyncRepoResult { Repo = "摘要测试", StatusKnown = true, Unstaged = 2, Untracked = 1, Dirty = true };
                typeof(SyncForm).GetMethod("PrintSummary", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form, new object[] { new List<SyncRepoResult> { r } });
                Application.DoEvents();
                var log = (TextBox)typeof(SyncForm).GetField("_log", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                Assert(log.Text.Contains("已暂存(staged): 0") && !log.Text.Contains("已 git add"), "实际 WinForms 摘要文案");
            }
            Pass("WinForms 摘要实际输出：staged=0 不声称已 git add");
        }

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        private static int WindowVisibilityWorker()
        {
            // 独立进程的第一个窗口用于复现 STARTUPINFO 的隐藏启动；绝不启动 Git 同步。
            using (var form = new Form { Text = "Monitor 窗口显示回归测试", ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-30000, -30000) })
            {
                ProgressWindow.ShowAndActivate(form);
                Application.DoEvents();
                Assert(IsWindowVisible(form.Handle), "隐藏启动后首次进度窗口必须实际可见");
                ShowWindow(form.Handle, 0); // 模拟启动器或系统在原生层隐藏窗口。
                ProgressWindow.ShowAndActivate(form);
                Application.DoEvents();
                Assert(IsWindowVisible(form.Handle), "原生隐藏后再次点击必须实际显示窗口");
                form.WindowState = FormWindowState.Minimized;
                ProgressWindow.ShowAndActivate(form);
                Application.DoEvents();
                Assert(form.WindowState != FormWindowState.Minimized && IsWindowVisible(form.Handle), "再次点击必须还原最小化窗口");
                form.Hide();
                ProgressWindow.ShowAndActivate(form);
                Application.DoEvents();
                Assert(IsWindowVisible(form.Handle), "再次点击必须重新显示隐藏窗口");
            }
            return 0;
        }

        private static void TestWindowVisibility()
        {
            using (var process = Process.Start(new ProcessStartInfo(Self, "--window-visibility-worker")
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            }))
            {
                Task<string> errors = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(15000))
                {
                    process.Kill(); // 仅终止本测试创建、绝不操作仓库的窗口测试子进程。
                    process.WaitForExit();
                    throw new Exception("窗口可见性测试超时");
                }
                Assert(process.ExitCode == 0, "窗口可见性子进程: " + errors.GetAwaiter().GetResult());
            }
            Pass("隐藏启动首次显示、最小化还原和隐藏窗口重新显示");
        }

        private static string Git(string path, params string[] args)
        {
            ProcessResult r = ProcessRunner.Run(RealGit, ProcessUtil.BuildArguments(args), path, 30);
            if (!r.Ok) throw new Exception("测试 Git 命令失败: " + string.Join(" ", args) + "\n" + r.Stderr);
            return r.Stdout.Trim();
        }

        private static string Fixture(string name, bool dirty = true)
        {
            string folder = Path.Combine(Root, name);
            Directory.CreateDirectory(folder);
            string remote = Path.Combine(folder, "remote"), local = Path.Combine(folder, "local");
            Directory.CreateDirectory(remote);
            Git(remote, "init", "-b", "main");
            Git(remote, "config", "user.name", "Regression Test");
            Git(remote, "config", "user.email", "test@example.invalid");
            Write(Path.Combine(remote, "base.txt"), "base\n");
            Write(Path.Combine(remote, "stage.txt"), "base\n");
            foreach (string file in LaterFiles) Write(Path.Combine(remote, file), "base\n");
            Git(remote, "add", ".");
            Git(remote, "commit", "-m", "fixture base");
            Git(folder, "clone", "--quiet", remote, local);
            Git(local, "config", "user.name", "Regression Test");
            Git(local, "config", "user.email", "test@example.invalid");
            Write(Path.Combine(remote, "remote.txt"), "remote\n");
            Git(remote, "add", ".");
            Git(remote, "commit", "-m", "fixture remote");
            if (dirty)
            {
                Write(Path.Combine(local, "base.txt"), "local unstaged\n");
                Write(Path.Combine(local, "stage.txt"), "local staged\n");
                Git(local, "add", "stage.txt");
                Write(Path.Combine(local, "untracked.txt"), "untracked\n");
            }
            return local;
        }

        private static GitSyncer Syncer(List<string> logs = null)
        {
            var syncer = new GitSyncer(new LlmSettings())
            {
                Log = msg => { logs?.Add(msg); if (msg.Contains("秒")) Console.WriteLine(msg); },
                Ask = (prompt, options) => { throw new Exception("测试出现意外询问: " + prompt); }
            };
            // 测试审计日志也隔离到本次目录，绝不追加真实用户日志。
            typeof(GitSyncer).GetField("_logFile", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(syncer, Path.Combine(Root, "git_sync.log"));
            return syncer;
        }
        private static BranchSwitcher Switcher(string branch)
        {
            var switcher = new BranchSwitcher(branch);
            typeof(BranchSwitcher).GetField("_logFile", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(switcher, Path.Combine(Root, "branch_switch.log"));
            return switcher;
        }
        private static void Mode(string path, string mode) => Write(Path.Combine(path, ".git", "monitor-test-mode"), mode);
        private static string Trace(string path) => File.ReadAllText(Path.Combine(path, ".git", "monitor-test-trace"));

        private static void AddRemoteFiles(string local, params string[] paths)
        {
            string remote = Path.Combine(Path.GetDirectoryName(local), "remote");
            foreach (string relative in paths)
            {
                string file = Path.Combine(remote, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                Write(file, "remote: " + relative + "\n");
            }
            Git(remote, "add", ".");
            Git(remote, "commit", "-m", "fixture incoming files");
        }

        private static void TestUntrackedFastForward()
        {
            string path = Fixture("untracked-fast-forward");
            string[] files = { "ai/docs/review/config-review/FOGD-4005.md", "ai/docs/review/config-review/配置 [4023].md" };
            AddRemoteFiles(path, files);
            foreach (string relative in files)
            {
                string file = Path.Combine(path, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                Write(file, "local: " + relative + "\r\n本地原文\n");
            }
            SyncRepoResult result = Syncer().SyncRepo(path);
            Assert(result.Status == "ok" && result.Merge == "fast_forward" && result.StashPop == "ok", "未跟踪阻塞自动处理后同步成功: " + result.Reason);
            Assert(result.UntrackedBackedUp == 2 && result.Staged == 1 && result.Unstaged == 1 && result.Untracked == 1, "只备份阻塞文件，原暂存与其他未跟踪文件保留");
            foreach (string relative in files)
            {
                Assert(File.ReadAllText(Path.Combine(path, relative)) == "remote: " + relative + "\n", "工作区采用远端文件");
                Assert(File.ReadAllText(UntrackedBackup.IoPath(Path.Combine(result.UntrackedBackupDirectory, "files", relative))) == "local: " + relative + "\r\n本地原文\n", "备份保留完整本地原文与目录结构");
            }
            Assert(File.Exists(UntrackedBackup.IoPath(Path.Combine(result.UntrackedBackupDirectory, "manifest.json"))), "备份清单可查");
            Assert(!File.Exists(Path.Combine(path, ".git", "monitor-sync-pending.json")), "成功恢复后清理恢复记录");
            Pass("真实快进自动备份两份未跟踪阻塞文件，支持中文/空格/方括号，正确恢复原暂存状态");
        }

        private static void TestUntrackedPathTransitions()
        {
            string path = Fixture("untracked-file-to-directory", false);
            Write(Path.Combine(path, "blocker"), "local file\n");
            AddRemoteFiles(path, "blocker/remote.txt");
            SyncRepoResult result = Syncer().SyncRepo(path);
            Assert(result.Status == "ok" && result.UntrackedBackedUp == 1 && Directory.Exists(Path.Combine(path, "blocker")), "未跟踪文件阻塞远端目录时自动处理: " + result.Reason);
            Assert(File.ReadAllText(UntrackedBackup.IoPath(Path.Combine(result.UntrackedBackupDirectory, "files", "blocker"))) == "local file\n", "原文件保留");

            path = Fixture("untracked-directory-to-file", false);
            Directory.CreateDirectory(Path.Combine(path, "blocker"));
            Write(Path.Combine(path, "blocker", "first.txt"), "first\n");
            Write(Path.Combine(path, "blocker", "second.txt"), "second\n");
            AddRemoteFiles(path, "blocker");
            result = Syncer().SyncRepo(path);
            Assert(result.Status == "ok" && result.UntrackedBackedUp == 2 && File.Exists(Path.Combine(path, "blocker")), "未跟踪目录内容阻塞远端文件时自动处理: " + result.Reason);
            Assert(File.ReadAllText(UntrackedBackup.IoPath(Path.Combine(result.UntrackedBackupDirectory, "files", "blocker", "first.txt"))) == "first\n", "目录内原文保留");
            Pass("未跟踪文件/目录与远端路径发生类型冲突时逐文件备份并快进");
        }

        private static void TestBackupFailure()
        {
            string path = Fixture("untracked-backup-failure");
            AddRemoteFiles(path, "first.md", "second.md");
            Write(Path.Combine(path, "first.md"), "first local\n");
            Write(Path.Combine(path, "second.md"), "second local\n");
            using (var held = new FileStream(Path.Combine(path, "second.md"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                SyncRepoResult result = Syncer().SyncRepo(path);
                Assert(result.Status == "error" && !Trace(path).Contains("WRITE stash push") && !Trace(path).Contains("WRITE merge"), "备份失败前不 stash、不 merge");
                Assert(File.ReadAllText(Path.Combine(path, "first.md")) == "first local\n", "部分备份失败后还原已经移动的文件");
            }
            string outside = Path.Combine(Path.GetDirectoryName(path), "outside.txt");
            Write(outside, "outside\n");
            foreach (string bad in new[] { "../outside.txt", "C:/outside.txt", "folder/../../outside.txt", "file:stream", ".git/index" })
            {
                bool rejected = false;
                try { UntrackedBackup.Resolve(path, bad); }
                catch (IOException) { rejected = true; }
                Assert(rejected && File.ReadAllText(outside) == "outside\n", "越界/元数据路径不得进入备份计划");
            }
            Pass("备份失败回滚已移动文件，保留 tracked 改动，拒绝越界与 Git 元数据路径");
        }

        private static void TestPendingRecovery()
        {
            foreach (string mode in new[] { "fail-merge", "wrapper-failure" })
            {
                string path = Fixture("pending-" + mode);
                if (mode == "fail-merge")
                {
                    AddRemoteFiles(path, "review.md");
                    Write(Path.Combine(path, "review.md"), "local review\n");
                }
                Mode(path, mode);
                SyncRepoResult failed = Syncer().SyncRepo(path);
                Assert(failed.Status == "error" && failed.StashCommit.Length > 0 && File.Exists(Path.Combine(path, ".git", "monitor-sync-pending.json")), "失败时记录准确 stash");
                Mode(path, "");
                SyncRepoResult result = Syncer().SyncRepo(path); // 新实例模拟重启后重试。
                Assert(result.Status == "ok" && result.ResumedStash && result.StashPop == "ok" && result.StashCommit == failed.StashCommit,
                    "新实例恢复上次 stash，包括 HEAD 已快进的情况: " + result.Reason);
                Assert(result.Staged == 1 && result.Unstaged == 1 && result.Untracked == 1 && Git(path, "stash", "list") == "", "恢复原暂存与工作区改动");
                Assert(Trace(path).Split('\n').Count(l => l.StartsWith("WRITE stash push")) == 1, "重试不再生成第二份 stash");
                if (mode == "fail-merge")
                    Assert(result.UntrackedBackupDirectory == failed.UntrackedBackupDirectory &&
                        File.ReadAllText(UntrackedBackup.IoPath(Path.Combine(result.UntrackedBackupDirectory, "files", "review.md"))) == "local review\n", "重试保留备份位置与内容");
            }
            Pass("重启后继续失败的同步并恢复记录的 stash；HEAD 已到目标时也正确恢复");
        }

        private static void TestRecoveryGuards()
        {
            foreach (string guard in new[] { "new-stash", "restore-started" })
            {
                string path = Fixture("recovery-guard-" + guard);
                Mode(path, guard == "restore-started" ? "fail-pop" : "fail-merge");
                Syncer().SyncRepo(path);
                Mode(path, "");
                if (guard != "restore-started") Write(Path.Combine(path, "base.txt"), "new user changes\n");
                if (guard == "new-stash") Git(path, "stash", "push", "-m", "user-owned");
                int before = Trace(path).Split('\n').Count(l => l.StartsWith("WRITE "));
                SyncRepoResult result = Syncer().SyncRepo(path);
                Assert(result.Status == "error" && before == Trace(path).Split('\n').Count(l => l.StartsWith("WRITE ")),
                    "恢复保护触发后不执行新写命令: " + guard);
            }
            string user = Fixture("unrelated-user-stash", false);
            Write(Path.Combine(user, "base.txt"), "user stash\n");
            Git(user, "stash", "push", "-m", "user-owned");
            string userStash = Git(user, "rev-parse", "stash@{0}");
            SyncRepoResult synced = Syncer().SyncRepo(user);
            Assert(synced.Status == "ok" && synced.StashPop == "none" && Git(user, "rev-parse", "stash@{0}") == userStash, "不自动恢复没有记录的用户 stash");

            Pass("stash 栈变动及已启动过恢复均停止；普通用户 stash 不受影响");
        }

        private static readonly string[] LaterFiles = { "later-1.txt", "later-2.txt", "later-3.txt", "later-4.txt", "later-stage.txt", "later-binary.dat", "later-encoded.txt" };

        private static void NewTrackedChanges(string path, bool staged)
        {
            foreach (string file in LaterFiles.Take(4)) Write(Path.Combine(path, file), "new: " + file + "\n");
            if (staged)
            {
                Write(Path.Combine(path, "later-stage.txt"), "new staged\n");
                Git(path, "add", "later-stage.txt");
                Write(Path.Combine(path, "later-stage.txt"), "new staged plus unstaged\n");
            }
        }

        private static void CheckRecoveredChanges(string path, SyncRepoResult result, bool staged)
        {
            Assert(result.Status == "ok" && result.ResumedStash && result.StashPop == "ok", "恢复旧/新增两批改动成功: " + result.Reason);
            Assert(File.ReadAllText(Path.Combine(path, "base.txt")) == "local unstaged\n" &&
                File.ReadAllText(Path.Combine(path, "stage.txt")) == "local staged\n" &&
                Git(path, "show", ":stage.txt") == "local staged", "旧 stash 的暂存与未暂存原文恢复");
            foreach (string file in LaterFiles.Take(4))
                Assert(File.ReadAllText(Path.Combine(path, file)) == "new: " + file + "\n", "新增原文恢复: " + file);
            Assert(result.Staged == (staged ? 2 : 1) && result.Unstaged == (staged ? 6 : 5) && result.Untracked == 1 && result.Unmerged == 0,
                "两批改动状态与无关未跟踪文件保留");
            if (staged)
                Assert(Git(path, "show", ":later-stage.txt") == "new staged" &&
                    File.ReadAllText(Path.Combine(path, "later-stage.txt")) == "new staged plus unstaged\n", "新增同一文件的暂存与未暂存内容均保留");
            Assert(!File.Exists(Path.Combine(path, ".git", "monitor-sync-pending.json")), "全部恢复完成后清理记录");
        }

        private static void TestPendingNewChanges()
        {
            foreach (string mode in new[] { "fail-merge", "wrapper-failure" })
            {
                string path = Fixture("pending-new-changes-" + mode);
                Mode(path, mode);
                SyncRepoResult failed = Syncer().SyncRepo(path);
                Assert(failed.Status == "error" && failed.StashCommit.Length > 0, "第一次失败保留旧 stash");
                if (mode == "fail-merge")
                {
                    string file = Path.Combine(path, ".git", "monitor-sync-pending.json");
                    var json = new System.Web.Script.Serialization.JavaScriptSerializer();
                    var legacy = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(file));
                    legacy.Remove("AdditionalStashes");
                    Write(file, json.Serialize(legacy)); // 模拟用户已有的单份 stash 旧记录。
                }
                NewTrackedChanges(path, mode == "wrapper-failure");
                Mode(path, "");
                SyncRepoResult result = Syncer().SyncRepo(path);
                CheckRecoveredChanges(path, result, mode == "wrapper-failure");
                Assert(result.StashCommit == failed.StashCommit && result.StashCommits.Count == 2 && Git(path, "stash", "list") == "", "原记录未被新 stash 覆盖；成功后仅清理两份记录");
                Assert(Git(path, "rev-parse", "HEAD") == Git(path, "rev-parse", "origin/main"), "重试更新到远端目标");
                string trace = Trace(path);
                Assert(trace.Split('\n').Count(l => l.StartsWith("WRITE stash push")) == 2 &&
                    trace.Split('\n').Count(l => l.StartsWith("WRITE stash apply")) == 1 &&
                    trace.LastIndexOf("WRITE apply", StringComparison.Ordinal) < trace.IndexOf("WRITE stash drop", StringComparison.Ordinal), "全部恢复成功前不删除原 stash");
            }
            Pass("旧 stash 未恢复时自动保存新增 4 项改动，支持已快进及新增文件同时 staged/unstaged");
        }

        private static void TestPendingMultipleRetries()
        {
            string path = Fixture("pending-multiple-retries", false);
            Write(Path.Combine(path, "later-stage.txt"), "user-owned stash\n");
            Git(path, "stash", "push", "-m", "user-owned");
            string userStash = Git(path, "rev-parse", "stash@{0}");
            Write(Path.Combine(path, "base.txt"), "local unstaged\n");
            Write(Path.Combine(path, "stage.txt"), "local staged\n");
            Git(path, "add", "stage.txt");
            Write(Path.Combine(path, "untracked.txt"), "untracked\n");
            Mode(path, "fail-merge");
            SyncRepoResult first = Syncer().SyncRepo(path);
            NewTrackedChanges(path, false);
            SyncRepoResult second = Syncer().SyncRepo(path);
            Assert(second.Status == "error" && second.StashCommit == first.StashCommit && second.StashCommits.Count == 2 && second.Staged + second.Unstaged == 0,
                "二次失败保留旧与新两份记录，工作区 tracked 已保存");
            string marker = File.ReadAllText(Path.Combine(path, ".git", "monitor-sync-pending.json"));
            Assert(second.StashCommits.All(marker.Contains), "全部准确提交持久化，支持重启");
            Write(Path.Combine(path, "later-stage.txt"), "new staged\n");
            Git(path, "add", "later-stage.txt");
            Write(Path.Combine(path, "later-stage.txt"), "new staged plus unstaged\n");
            Mode(path, "");
            SyncRepoResult result = Syncer().SyncRepo(path);
            CheckRecoveredChanges(path, result, true);
            Assert(result.StashCommits.Count == 3 && Git(path, "stash", "list", "--format=%H") == userStash, "三批恢复后不动此前普通用户 stash");
            Pass("连续失败与重启可累计三批本地改动，精确恢复并保留无关用户 stash");
        }

        private static void TestPendingRestoreFailure()
        {
            foreach (string mode in new[] { "overlap", "fail-second-apply", "apply-wrapper-failure" })
            {
                string path = Fixture("restore-" + mode); // 原生 Git 会把长 revision 临时当作路径探测，用例目录保持短路径。
                Mode(path, "fail-merge");
                Syncer().SyncRepo(path);
                if (mode == "overlap") Write(Path.Combine(path, "base.txt"), "different new user changes\n");
                else NewTrackedChanges(path, false);
                Mode(path, mode == "overlap" ? "" : mode);
                SyncRepoResult result = Syncer().SyncRepo(path);
                Assert(result.Status == "error" && result.StashPop == "failed" && result.StashCommits.Count == 2, "重叠/注入恢复失败时停止: " + mode);
                string list = Git(path, "stash", "list", "--format=%H");
                Assert(result.StashCommits.All(list.Contains) && !Trace(path).Contains("WRITE stash drop"), "恢复未全部成功时保留每份原始 stash");
                Assert(File.ReadAllText(Path.Combine(path, "base.txt")) == "local unstaged\n", "已恢复的旧改动不被新改动覆盖: " + mode + ": " + result.Reason);
                Mode(path, "");
                int writes = Trace(path).Split('\n').Count(l => l.StartsWith("WRITE "));
                SyncRepoResult retry = Syncer().SyncRepo(path);
                Assert(retry.Status == "error" && writes == Trace(path).Split('\n').Count(l => l.StartsWith("WRITE ")), "部分恢复后不重复应用或继续写入");
            }
            Pass("多批恢复遇到重叠、第二批失败或包装异常时保留全部 stash，重试不重复应用");
        }

        private static void TestPendingFetchFailure()
        {
            string path = Fixture("pending-new-changes-fetch-failure");
            Mode(path, "fail-merge");
            SyncRepoResult first = Syncer().SyncRepo(path);
            NewTrackedChanges(path, false);
            Mode(path, "fail-fetch");
            SyncRepoResult failed = Syncer().SyncRepo(path);
            Assert(failed.Status == "error" && failed.StashCommits.Count == 1 && Git(path, "stash", "list", "--format=%H") == first.StashCommit,
                "fetch 失败不再执行新 stash 或恢复");
            foreach (string file in LaterFiles.Take(4)) Assert(File.ReadAllText(Path.Combine(path, file)) == "new: " + file + "\n", "fetch 失败新增改动原位保留");
            Mode(path, "");
            CheckRecoveredChanges(path, Syncer().SyncRepo(path), false);
            Pass("fetch 失败保留原位新改动与旧 stash，之后重试正确恢复两批");
        }

        private static void TestPendingLateChanges()
        {
            foreach (string mode in new[] { "new-tracked-on-fetch", "new-tracked-on-merge" })
            {
                string path = Fixture("late-" + mode);
                Mode(path, mode == "new-tracked-on-fetch" ? "wrapper-failure" : "fail-merge");
                Syncer().SyncRepo(path);
                Mode(path, mode);
                SyncRepoResult result = Syncer().SyncRepo(path);
                Assert(result.Status == "ok" && result.StashPop == "ok" && result.StashCommits.Count == 2 && result.Staged == 1 && result.Unstaged == 2,
                    "fetch/merge 期间新增改动也单独保存并恢复: " + result.Reason);
                Assert(File.ReadAllText(Path.Combine(path, "later-1.txt")) == "new changes during " + (mode == "new-tracked-on-fetch" ? "fetch" : "merge") + "\n" &&
                    Git(path, "stash", "list") == "", "晚出现改动原文保留，完成后清理记录");
            }
            Pass("fetch 与快进期间新增 tracked 改动在恢复前自动保存并恢复");
        }

        private static void TestPendingBytes()
        {
            string path = Fixture("pending-raw-bytes");
            Mode(path, "fail-merge");
            Syncer().SyncRepo(path);
            byte[] binary = { 0, 255, 1, 128, 13, 10, 0, 42 };
            byte[] encodedIndex = { 0xD6, 0xD0, 0xCE, 0xC4, 13, 10 };
            byte[] encodedWorktree = encodedIndex.Concat(new byte[] { 0xB8, 0xC4, 13, 10 }).ToArray();
            File.WriteAllBytes(Path.Combine(path, "later-binary.dat"), binary);
            File.WriteAllBytes(Path.Combine(path, "later-encoded.txt"), encodedIndex);
            Git(path, "add", "later-encoded.txt");
            File.WriteAllBytes(Path.Combine(path, "later-encoded.txt"), encodedWorktree);
            string expected = Path.Combine(Path.GetDirectoryName(path), "expected-index.bin");
            File.WriteAllBytes(expected, encodedIndex);
            string indexBlob = Git(path, "hash-object", "--no-filters", expected);
            Mode(path, "");
            SyncRepoResult result = Syncer().SyncRepo(path);
            Assert(result.Status == "ok" && result.StashPop == "ok" && result.Staged == 2 && result.Unstaged == 3, "新增二进制与非 UTF-8 文件状态恢复: " + result.Reason);
            Assert(File.ReadAllBytes(Path.Combine(path, "later-binary.dat")).SequenceEqual(binary) &&
                File.ReadAllBytes(Path.Combine(path, "later-encoded.txt")).SequenceEqual(encodedWorktree) &&
                Git(path, "rev-parse", ":later-encoded.txt") == indexBlob, "二进制、非 UTF-8 文本、CRLF 与独立暂存内容逐字节保留");
            Assert(Git(path, "stash", "list") == "" && Directory.GetFiles(Path.Combine(path, ".git"), "monitor-replay-*.patch").Length == 0,
                "成功后清理对应 stash 与本次补丁");
            Pass("后续批次二进制、非 UTF-8 文本与 CRLF 原字节保留，暂存内容独立正确");
        }

        private static void OldLock(string path)
        {
            Write(path, "fixture lock data");
            // 模拟早于所有当前 GUI 进程的遗留文件，不要求关闭开发机上已打开的日志窗口。
            DateTime old = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetCreationTimeUtc(path, old);
            File.SetLastWriteTimeUtc(path, old);
        }

        private static void TestTortoiseGitLockOwnership()
        {
            DateTime now = DateTime.UtcNow;
            DateTime old = now.AddHours(-2);
            Assert(!IndexLockPolicy.TortoiseGitCouldOwnLock(now, old, old), "晚于锁两项时间的新进程不可能是原锁创建者");
            Assert(IndexLockPolicy.TortoiseGitCouldOwnLock(old, now, now), "进程先于锁启动时仍拦截");
            Assert(IndexLockPolicy.TortoiseGitCouldOwnLock(old, old.AddHours(-1), now), "创建时间继承旧值，但写入晚于进程时仍拦截");
            Assert(IndexLockPolicy.TortoiseGitCouldOwnLock(old, now, old.AddHours(-1)), "任一文件时间晚于进程时仍拦截");
            Assert(IndexLockPolicy.TortoiseGitCouldOwnLock(now, now.AddMilliseconds(-500), now.AddMilliseconds(-500)), "一秒时间余量内不误判");
            Pass("TortoiseGit 持锁时间关系：旧进程、旧创建时间与近期写入、时间余量均保持保护");

            string dir = Path.Combine(Root, "tortoise-log-lock");
            Directory.CreateDirectory(dir);
            string fakeTortoise = Path.Combine(dir, "TortoiseGitProc.exe");
            File.Copy(Self, fakeTortoise);
            string path = Path.Combine(dir, "index.lock");
            OldLock(path);
            using (var process = Process.Start(new ProcessStartInfo(fakeTortoise,
                ProcessUtil.BuildArguments("--worker", "idle", dir, "15000"))
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }))
            {
                try
                {
                    WaitFile(Path.Combine(dir, "idle-started"));
                    ProcessResult ready = IndexLockPolicy.Prepare(path, "日志窗口已打开", null, CancellationToken.None, 4);
                    Assert(ready.Ok && !File.Exists(path) && !process.HasExited, "新开的 TortoiseGit GUI 不应阻塞稳定旧锁: " + ready.Stderr);
                    Pass("TortoiseGit 进程仍运行时可清理其启动前的稳定旧锁，无需关闭日志窗口");
                    OldLock(path);
                    using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        ready = IndexLockPolicy.Prepare(path, "仍有句柄占用", null, CancellationToken.None, 3);
                        Assert(!ready.Ok && File.Exists(path), "即使 GUI 晚于锁启动，锁仍被占用也不得删除");
                    }
                    Pass("时间关系排除新 GUI 后，实际锁句柄保护仍然有效");
                }
                finally
                {
                    if (!process.HasExited) process.Kill(); // 只停止本测试创建的 idle 进程。
                    process.WaitForExit();
                    File.Delete(path);
                }
            }
        }

        private static void TestIndexLockPolicy()
        {
            string dir = Path.Combine(Root, "index-lock-policy");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "index.lock");
            var logs = new List<string>();
            using (var started = new ManualResetEventSlim())
            {
                var owner = Task.Run(() =>
                {
                    using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        started.Set();
                        Thread.Sleep(800);
                    }
                    File.Delete(path); // 仅模拟本测试创建的外部锁拥有者正常释放。
                });
                started.Wait();
                var clock = Stopwatch.StartNew();
                ProcessResult ready = IndexLockPolicy.Prepare(path, "测试写入", logs.Add, CancellationToken.None, 3);
                owner.GetAwaiter().GetResult();
                Assert(ready.Ok && clock.ElapsedMilliseconds >= 700 && logs.Any(l => l.Contains("已释放")), "短暂外部锁必须等待释放后再允许写入");
            }
            Pass("短暂 index.lock 占用等待后继续");

            OldLock(path);
            logs.Clear();
            ProcessResult result = IndexLockPolicy.Prepare(path, "测试写入", logs.Add, CancellationToken.None, IndexLockPolicy.WaitSeconds);
            Assert(result.Ok && !File.Exists(path) && logs.Any(l => l.Contains("直接删除")), "稳定失效锁直接删除: " + result.Stderr);
            Pass("稳定、至少两分钟未更新且没有 Git 进程的锁直接删除");

            OldLock(path);
            result = IndexLockPolicy.Prepare(path, "后台 fetch", null, CancellationToken.None, 0);
            Assert(!result.Ok && File.Exists(path), "后台检测不得清理锁");
            Pass("后台占锁立即跳过，不清理锁");

            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                result = IndexLockPolicy.Prepare(path, "测试写入", null, CancellationToken.None, 3);
                Assert(!result.Ok && File.Exists(path), "仍被句柄占用的旧锁不得删除");
            }
            Pass("正在使用的旧锁保持完整");

            File.SetCreationTimeUtc(path, DateTime.UtcNow);
            result = IndexLockPolicy.Prepare(path, "测试写入", null, CancellationToken.None, 3);
            Assert(!result.Ok && File.Exists(path), "新创建但继承旧修改时间的锁不得清理");
            OldLock(path);
            Task changed = Task.Run(() => { Thread.Sleep(1000); Write(path, "changed lock data"); });
            result = IndexLockPolicy.Prepare(path, "测试写入", null, CancellationToken.None, 3);
            changed.GetAwaiter().GetResult();
            Assert(!result.Ok && File.ReadAllText(path) == "changed lock data", "观察期间更新的锁不得删除");
            OldLock(path);
            DateTime creation = File.GetCreationTimeUtc(path), modified = File.GetLastWriteTimeUtc(path);
            Task replaced = Task.Run(() =>
            {
                Thread.Sleep(1000);
                File.Delete(path);
                OldLock(path);
                File.SetCreationTimeUtc(path, creation);
                File.SetLastWriteTimeUtc(path, modified);
            });
            result = IndexLockPolicy.Prepare(path, "测试写入", null, CancellationToken.None, 3);
            replaced.GetAwaiter().GetResult();
            Assert(!result.Ok && File.Exists(path), "大小与时间相同但文件身份变化的锁不得删除");
            Pass("新锁、观察期间更新及被替换的新锁均不清理");

            OldLock(path);
            using (var source = new CancellationTokenSource())
            {
                source.CancelAfter(100);
                result = IndexLockPolicy.Prepare(path, "测试写入", null, source.Token, 3);
                Assert(result.Cancelled && File.Exists(path), "强制停止等待不得清理锁或启动写入");
            }
            bool stop = false;
            result = IndexLockPolicy.Prepare(path, "测试写入", message => stop = true, CancellationToken.None, 3, () => stop);
            Assert(result.Cancelled && File.Exists(path), "安全停止等待不得清理锁或启动写入");
            Pass("锁等待响应安全停止与强制停止");

            string busy = Path.Combine(dir, "busy-git");
            Directory.CreateDirectory(busy);
            string fakeGit = Path.Combine(busy, "git.exe");
            File.Copy(Self, fakeGit);
            using (var process = Process.Start(new ProcessStartInfo(fakeGit, ProcessUtil.BuildArguments("--worker", "idle", busy, "15000"))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }))
            {
                try
                {
                    WaitFile(Path.Combine(busy, "idle-started"));
                    result = IndexLockPolicy.Prepare(path, "测试写入", null, CancellationToken.None, 3);
                    Assert(!result.Ok && result.Stderr.Contains("活动进程") && File.Exists(path), "Git 即便不持有文件句柄也不得清理其逻辑锁");
                }
                finally
                {
                    if (!process.HasExited) process.Kill(); // 只清理本测试创建、不访问仓库的 idle 子进程。
                    process.WaitForExit();
                }
            }
            File.Delete(path);
            Pass("Git 进程关闭锁句柄后继续运行时仍禁止删除逻辑锁");
        }

        private static void TestStaleLockSync()
        {
            string path = Fixture("stale-lock-sync");
            OldLock(Path.Combine(path, ".git", "index.lock"));
            var logs = new List<string>();
            SyncRepoResult result = Syncer(logs).SyncRepo(path);
            Assert(result.Status == "ok" && result.Merge == "fast_forward" && result.StashPop == "ok", "失效锁自动清理后同步成功: " + result.Reason);
            Assert(logs.Any(l => l.Contains("直接删除")) && result.Staged == 1 && result.Unstaged == 1 && result.Untracked == 1,
                "旧锁清理后保留原本 staged/unstaged/untracked 改动");
            Pass("真实同步自动清理旧锁、快进并恢复 stash 与原暂存状态");
        }

        private static void TestFastForward()
        {
            string path = Fixture("slow-fast-forward");
            Mode(path, "slow-merge");
            var logs = new List<string>();
            var clock = Stopwatch.StartNew();
            SyncRepoResult r = Syncer(logs).SyncRepo(path);
            Assert(clock.Elapsed.TotalSeconds > 60 && r.Status == "ok" && r.Merge == "fast_forward", "超过旧 60 秒仍快进成功: " + r.Reason);
            Assert(r.Head == r.Target && r.Ahead == 0 && r.Behind == 0 && Git(path, "rev-parse", "HEAD") == Git(path, "rev-parse", "origin/main"), "快进后置校验");
            Assert(r.StashPop == "ok" && r.Staged == 1 && r.Unstaged == 1 && r.Untracked == 1 && Git(path, "stash", "list") == "", "恢复本地改动及原暂存状态");
            Assert(logs.Count(l => l.Contains("仍在运行")) >= 4, "长操作持续报告状态");
            Pass("真实快进延迟 65 秒成功，4 次运行状态、HEAD/远端一致，stash 恢复 staged/unstaged/untracked");
        }

        private static void TestFailures()
        {
            foreach (string mode in new[] { "fail-fetch", "fail-stash", "fail-merge", "fail-pop" })
            {
                string path = Fixture(mode);
                Mode(path, mode);
                SyncRepoResult r = Syncer().SyncRepo(path);
                Assert(r.Status == "error", mode + " 必须报错");
                string trace = Trace(path);
                int failure = trace.IndexOf("FAILED", StringComparison.Ordinal);
                Assert(failure >= 0 && !trace.Substring(failure + 6).Contains("WRITE "), "失败后无后续写入");
                if (mode == "fail-merge" || mode == "fail-pop") Assert(Git(path, "stash", "list").Length > 0, "stash 必须保留");
                Pass(mode + " 后无后续写入且保留恢复状态");
            }
            string locked = Fixture("existing-lock");
            using (var held = new FileStream(Path.Combine(locked, ".git", "index.lock"), FileMode.Create, FileAccess.Write, FileShare.None))
            {
                SyncRepoResult r = Syncer().SyncRepo(locked);
                Assert(r.Status == "error" && !Trace(locked).Contains("WRITE "), "活动锁阻止写入");
            }
            Assert(File.Exists(Path.Combine(locked, ".git", "index.lock")), "活动锁未删除");
            Pass("外部持有 index.lock 时不启动写入、不删锁");
        }

        private static void TestStop()
        {
            string path = Fixture("safe-stop");
            Mode(path, "short-merge");
            var syncer = Syncer();
            Task<SyncRepoResult> pending = Task.Run(() => syncer.SyncRepo(path));
            WaitFile(Path.Combine(path, ".git", "merge-started"));
            syncer.StopRequested = true;
            SyncRepoResult r = pending.Result;
            Assert(r.Status == "cancelled" && r.Merge == "fast_forward" && !Trace(path).Contains("WRITE stash pop"), "安全停止等待当前快进，禁止 pop");
            Assert(Git(path, "stash", "list").Length > 0, "安全停止保留 stash");
            Pass("安全停止等待快进完成并校验，保留 stash，无 pop");

            path = Fixture("force-stop");
            Mode(path, "locked-merge");
            using (var source = new CancellationTokenSource())
            {
                syncer = Syncer();
                syncer.ForceCancellation = source.Token;
                string testPath = path;
                pending = Task.Run(() => syncer.SyncRepo(testPath));
                WaitFile(Path.Combine(path, ".git", "pid"));
                source.Cancel();
                r = pending.Result;
            }
            Assert(r.Status == "cancelled" && !Trace(path).Contains("WRITE stash pop"), "强制停止不能 pop");
            using (new FileStream(Path.Combine(path, ".git", "index.lock"), FileMode.Open, FileAccess.Read, FileShare.None)) { }
            Assert(Git(path, "stash", "list").Length > 0 && r.Reason.Contains("残留锁"), "强制停止记录残留锁并保留 stash");
            Pass("同步中强制停止：写锁子树退出、锁保留并明确提示、stash 不弹出");
        }

        private static void TestDivergence()
        {
            string path = Fixture("diverged", false);
            Write(Path.Combine(path, "local.txt"), "local\n");
            Git(path, "add", ".");
            Git(path, "commit", "-m", "fixture local");
            string before = Git(path, "rev-parse", "HEAD");
            Write(Path.Combine(path, "base.txt"), "uncommitted\n");
            SyncRepoResult r = Syncer().SyncRepo(path);
            Assert(r.Status == "needs_commit" && Git(path, "rev-parse", "HEAD") == before, "分叉不自动 commit");
            Assert(File.Exists(Path.Combine(path, ".git", "MERGE_HEAD")) && Git(path, "stash", "list").Length > 0, "提交前保留 merge 状态和 stash");
            Assert(!Trace(path).Contains("WRITE stash pop"), "待提交时无 stash pop");
            Pass("分叉合并不自动提交，保留 MERGE_HEAD 和 stash，等待人工恢复");
        }

        private static void TestVerificationMismatch()
        {
            string path = Fixture("false-success");
            Mode(path, "fake-merge-success");
            SyncRepoResult r = Syncer().SyncRepo(path);
            Assert(r.Status == "error" && r.Reason.Contains("后置校验") && !Trace(path).Contains("WRITE stash pop"), "rc=0 但 HEAD 不符必须失败");
            Pass("包装进程 rc=0 但 HEAD 未更新时，后置校验阻止误报和 stash pop");
        }

        private static void TestWrapperFailure()
        {
            string path = Fixture("wrapper-failure");
            Mode(path, "wrapper-failure");
            SyncRepoResult r = Syncer().SyncRepo(path);
            Assert(r.Status == "error" && r.Merge == "fast_forward" && r.Reason.Contains("实际上已到达"), "命令失败也须报告实际快进结果");
            Assert(!Trace(path).Contains("WRITE stash pop") && Git(path, "stash", "list").Length > 0, "校验成功也不恢复失败后的写流程");
            Pass("真实快进成功但包装进程失败：准确报告实际结果，仍保留 stash 并停止");
        }

        private static void TestConflictAndAbort()
        {
            string path = Fixture("merge-conflict", false);
            string remote = Path.Combine(Path.GetDirectoryName(path), "remote");
            Write(Path.Combine(remote, "base.txt"), "remote conflict\n");
            Git(remote, "add", ".");
            Git(remote, "commit", "-m", "fixture remote conflict");
            Write(Path.Combine(path, "base.txt"), "local conflict\n");
            Git(path, "add", ".");
            Git(path, "commit", "-m", "fixture local conflict");
            Write(Path.Combine(path, "stage.txt"), "uncommitted\n");
            SyncRepoResult r = Syncer().SyncRepo(path);
            Assert(r.Status == "error" && r.Unmerged == 1 && !Trace(path).Contains("WRITE stash pop"), "merge 冲突失败后必须停止");
            Assert(Git(path, "stash", "list").Length > 0, "merge 冲突保留 stash");
            Mode(path, "fail-abort");
            var syncer = Syncer();
            syncer.Ask = (prompt, choices) => 2;
            r = syncer.SyncRepo(path);
            string trace = Trace(path);
            Assert(r.Status == "error" && !trace.Substring(trace.IndexOf("FAILED") + 6).Contains("WRITE "), "abort 失败后不执行 fetch/stash");
            Pass("真实 merge 冲突和 abort 失败均停止后续写入，冲突及 stash 保留");
        }

        private static void TestRepositoryGate()
        {
            string path = Fixture("operation-gate");
            using (RepositoryOperation.TryEnter(path))
            {
                Assert(Syncer().SyncRepo(path).Status == "skipped", "同步不得和其他操作交叉");
                Assert(GitWatcher.CheckRepo(path).Error.Contains("正在"), "后台 fetch 不得交叉");
                Assert(Switcher("main").SwitchRepo(path).Status == "skipped", "分支切换不得交叉");
                string sub = Path.Combine(path, "sub");
                Directory.CreateDirectory(sub);
                Assert(RepositoryOperation.TryEnter(sub) == null, "仓库子目录必须共享互斥键");
                ProcessResult child = ProcessRunner.Run(Self, ProcessUtil.BuildArguments("--gate-worker", path), path, 10);
                Assert(child.Ok && child.Stdout == "BUSY", "另一个 Monitor 进程不得进入同仓库: " + child.Stderr);
            }
            ProcessResult released = ProcessRunner.Run(Self, ProcessUtil.BuildArguments("--gate-worker", path), path, 10);
            Assert(released.Ok && released.Stdout == "ACQUIRED", "释放后其他进程可进入仓库");
            string linked = Path.Combine(Root, "gate-linked-worktree");
            Git(path, "worktree", "add", "-b", "gate-linked", linked);
            using (RepositoryOperation.TryEnter(path))
                Assert(RepositoryOperation.TryEnter(linked) == null, "共享 refs 的 worktree 必须互斥");
            Pass("同仓库跨进程互斥，子目录/worktree 共用互斥键，释放后可重新进入");
        }

        private static void TestBranchFailure()
        {
            string path = Fixture("branch-failure");
            Mode(path, "fail-fetch");
            bool failed = false;
            try { Switcher("other").SwitchRepo(path); }
            catch (IOException) { failed = true; }
            string trace = Trace(path);
            Assert(failed && !trace.Substring(trace.IndexOf("FAILED") + 6).Contains("WRITE "), "分支切换 fetch 失败后停止");
            Pass("分支切换使用统一进程执行器，fetch 失败后不 stash/checkout/pop");
        }

        private static void WaitFile(string path)
        {
            var clock = Stopwatch.StartNew();
            while (!File.Exists(path))
            {
                if (clock.Elapsed.TotalSeconds > 25) throw new Exception("等待测试进程超时: " + path);
                Thread.Sleep(50);
            }
        }

        private static int GitShim(string[] args)
        {
            string gitDir = Path.Combine(Environment.CurrentDirectory, ".git");
            string trace = Path.Combine(gitDir, "monitor-test-trace");
            string modePath = Path.Combine(gitDir, "monitor-test-mode");
            string mode = File.Exists(modePath) ? File.ReadAllText(modePath) : "";
            string[] commandArgs = args.Length > 0 && args[0] == "--no-optional-locks" ? args.Skip(1).ToArray() : args;
            string command = string.Join(" ", commandArgs);
            bool write = commandArgs.Length > 0 && new[] { "fetch", "stash", "merge", "add", "checkout", "apply" }.Contains(commandArgs[0])
                && !(commandArgs[0] == "stash" && commandArgs.Length > 1 && commandArgs[1] == "list");
            if (Directory.Exists(gitDir)) File.AppendAllText(trace, (write ? "WRITE " : "READ ") + command + "\n");
            bool fail = (mode == "fail-fetch" && command.StartsWith("fetch ")) ||
                (mode == "fail-stash" && command.StartsWith("stash push")) ||
                (mode == "fail-merge" && command.StartsWith("merge ")) ||
                (mode == "fail-pop" && command.StartsWith("stash pop")) ||
                (mode == "fail-second-apply" && command.StartsWith("apply ") && !commandArgs.Contains("--check")) ||
                (mode == "fail-abort" && command == "merge --abort");
            if (fail)
            {
                File.AppendAllText(trace, "FAILED\n");
                Console.Error.WriteLine("测试注入写入失败");
                return 1;
            }
            if (command.StartsWith("merge --ff-only"))
            {
                Write(Path.Combine(gitDir, "merge-started"), "started");
                if (mode == "locked-merge")
                {
                    using (Process.Start(new ProcessStartInfo(Self, ProcessUtil.BuildArguments("--worker", "lock", gitDir, "60000"))
                    { UseShellExecute = false, CreateNoWindow = true })) { }
                    return 0;
                }
                if (mode == "slow-merge") Thread.Sleep(65000);
                if (mode == "short-merge") Thread.Sleep(1800);
                if (mode == "fake-merge-success") return 0;
            }
            using (var child = Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("MONITOR_TEST_REAL_GIT"), ProcessUtil.BuildArguments(args))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 }))
            {
                var stdout = child.StandardOutput.ReadToEndAsync();
                var stderr = child.StandardError.ReadToEndAsync();
                child.WaitForExit();
                Console.Write(stdout.Result);
                Console.Error.Write(stderr.Result);
                if (mode == "new-tracked-on-fetch" && command.StartsWith("fetch ") && child.ExitCode == 0)
                    Write(Path.Combine(Environment.CurrentDirectory, "later-1.txt"), "new changes during fetch\n");
                if (mode == "new-tracked-on-merge" && command.StartsWith("merge --ff-only") && child.ExitCode == 0)
                    Write(Path.Combine(Environment.CurrentDirectory, "later-1.txt"), "new changes during merge\n");
                return ((mode == "wrapper-failure" && command.StartsWith("merge --ff-only")) ||
                    (mode == "apply-wrapper-failure" && command.StartsWith("stash apply"))) && child.ExitCode == 0 ? 1 : child.ExitCode;
            }
        }
    }
}
