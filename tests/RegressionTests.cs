// 无第三方测试框架；所有 Git 写入仅发生在本次创建的临时仓库，绝不接触用户仓库。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
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
            try
            {
                Console.OutputEncoding = new UTF8Encoding(false);
                if (args.Length > 0 && args[0] == "--worker") return Worker(args);
                if (args.Length > 0 && args[0] == "--summary-only")
                {
                    TestStatus();
                    TestUi();
                    return 0;
                }
                if (Path.GetFileName(Self).Equals("git.exe", StringComparison.OrdinalIgnoreCase)) return GitShim(args);
                Root = Path.Combine(Path.GetDirectoryName(Self), "monitor-regression-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Root);
                RealGit = @"C:\Program Files\Git\cmd\git.exe";
                Environment.SetEnvironmentVariable("MONITOR_TEST_REAL_GIT", RealGit);
                // 防止测试继承用户的提交钩子或签名要求，也不使用任何远程凭据。
                Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", "NUL");
                Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
                Console.WriteLine("测试目录: " + Root);
                TestProcessTree();
                TestStatus();
                TestUi();
                string shim = Path.Combine(Root, "shim");
                Directory.CreateDirectory(shim);
                File.Copy(Self, Path.Combine(shim, "git.exe"));
                Environment.SetEnvironmentVariable("PATH", shim + ";" + Environment.GetEnvironmentVariable("PATH"));
                TestFastForward();
                TestFailures();
                TestStop();
                TestDivergence();
                TestVerificationMismatch();
                TestWrapperFailure();
                TestConflictAndAbort();
                TestRepositoryGate();
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

        private static int Worker(string[] args)
        {
            string mode = args[1], dir = args[2];
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

        private static GitSyncer Syncer(List<string> logs = null) => new GitSyncer(new LlmSettings())
        {
            Log = msg => { logs?.Add(msg); if (msg.Contains("秒")) Console.WriteLine(msg); },
            Ask = (prompt, options) => { throw new Exception("测试出现意外询问: " + prompt); }
        };
        private static void Mode(string path, string mode) => Write(Path.Combine(path, ".git", "monitor-test-mode"), mode);
        private static string Trace(string path) => File.ReadAllText(Path.Combine(path, ".git", "monitor-test-trace"));

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
                Assert(new BranchSwitcher("main").SwitchRepo(path).Status == "skipped", "分支切换不得交叉");
            }
            Pass("同仓库同步、后台检测和分支切换互斥");
        }

        private static void TestBranchFailure()
        {
            string path = Fixture("branch-failure");
            Mode(path, "fail-fetch");
            bool failed = false;
            try { new BranchSwitcher("other").SwitchRepo(path); }
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
            string command = string.Join(" ", args);
            bool write = args.Length > 0 && new[] { "fetch", "stash", "merge", "add", "checkout" }.Contains(args[0])
                && !(args[0] == "stash" && args.Length > 1 && args[1] == "list");
            if (Directory.Exists(gitDir)) File.AppendAllText(trace, (write ? "WRITE " : "READ ") + command + "\n");
            bool fail = (mode == "fail-fetch" && command.StartsWith("fetch ")) ||
                (mode == "fail-stash" && command.StartsWith("stash push")) ||
                (mode == "fail-merge" && command.StartsWith("merge ")) ||
                (mode == "fail-pop" && command.StartsWith("stash pop")) ||
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
                return mode == "wrapper-failure" && command.StartsWith("merge --ff-only") && child.ExitCode == 0 ? 1 : child.ExitCode;
            }
        }
    }
}
