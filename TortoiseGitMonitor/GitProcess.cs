using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace TortoiseGitMonitor
{
    /// <summary>统一 Git 调用；写入前等待锁释放，手动任务可清理确认失效的 index.lock。</summary>
    internal static class GitProcess
    {
        public static ProcessResult Read(string path, int timeoutSec, params string[] args) =>
            ProcessRunner.Run("git", "--no-optional-locks " + ProcessUtil.BuildArguments(args), path, timeoutSec);

        public static ProcessResult Write(string path, string action, Action<string> log,
            CancellationToken cancel, int timeoutSec, params string[] args) =>
            Write(path, action, log, cancel, timeoutSec, null, args);

        public static ProcessResult Write(string path, string action, Action<string> log,
            CancellationToken cancel, int timeoutSec, Func<bool> stopRequested, params string[] args)
        {
            ProcessResult location = Read(path, 10, "rev-parse", "--git-path", "index.lock");
            if (!location.Ok || string.IsNullOrWhiteSpace(location.Stdout))
                return new ProcessResult { Stderr = "无法定位 index.lock，停止写入: " + location.Stderr };
            string indexLock = Path.GetFullPath(Path.Combine(path, location.Stdout.Trim()));
            // 后台 fetch 只跳过占锁仓库，不等待、不清理；手动写操作最多等 30 秒。
            ProcessResult ready = IndexLockPolicy.Prepare(indexLock, action, log, cancel,
                timeoutSec == 0 ? IndexLockPolicy.WaitSeconds : 0, stopRequested);
            if (!ready.Ok) return ready;
            if (cancel.IsCancellationRequested || (stopRequested != null && stopRequested()))
                return new ProcessResult { Cancelled = true, Stderr = "已停止；未启动 " + action };

            ProcessResult result = ProcessRunner.Run("git", "--no-optional-locks " + ProcessUtil.BuildArguments(args), path, timeoutSec,
                msg => log?.Invoke("  " + action + ": " + msg), cancel);
            string after = DescribeLock(indexLock);
            if (after.Length > 0)
            {
                result.Code = -1;
                result.Stderr += "\n" + after;
            }
            else if (result.Cancelled || result.TimedOut)
                result.Stderr += "\n已确认本次进程树退出，index.lock 不存在。请检查 git status、git stash list 及其他 Git 锁后恢复。";
            return result;
        }

        /// <summary>用独占打开检测占用；检测结果只代表当前时刻，绝不据此自动删锁。</summary>
        internal static string DescribeLock(string path)
        {
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                return "存在 index.lock（可能是活动锁或残留锁）: " + path + "；当前可独占打开，但不能据此确认已失效";
            }
            catch (FileNotFoundException) { return ""; }
            catch (DirectoryNotFoundException) { return ""; }
            catch (Exception ex)
            {
                return "锁仍被占用或无法确认占用状态: " + path + "；已停止后续写入，未删除锁（" + ex.Message + "）";
            }
        }
    }

    /// <summary>按 Git 公共目录互斥；覆盖子目录、worktree 以及同一登录会话的多个 Monitor 进程。</summary>
    internal sealed class RepositoryOperation : IDisposable
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _gate;
        private readonly Mutex _mutex;
        private int _disposed;
        private RepositoryOperation(SemaphoreSlim gate, Mutex mutex) { _gate = gate; _mutex = mutex; }
        public static RepositoryOperation TryEnter(string path)
        {
            string key = Path.GetFullPath(path).TrimEnd('\\', '/');
            ProcessResult common = GitProcess.Read(path, 10, "rev-parse", "--git-common-dir");
            if (common.Ok && !string.IsNullOrWhiteSpace(common.Stdout))
                key = Path.GetFullPath(Path.Combine(path, common.Stdout.Trim())).TrimEnd('\\', '/');
            var gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            if (!gate.Wait(0)) return null;
            Mutex mutex = null;
            bool acquired = false;
            try
            {
                string id;
                using (var hash = SHA256.Create())
                    id = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(key.ToUpperInvariant()))).Replace("-", "");
                mutex = new Mutex(false, @"Local\TortoiseGitMonitor.Repository." + id);
                try { acquired = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                return acquired ? new RepositoryOperation(gate, mutex) : null;
            }
            finally
            {
                if (!acquired)
                {
                    mutex?.Dispose();
                    gate.Release();
                }
            }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _mutex.ReleaseMutex(); }
            finally { _mutex.Dispose(); _gate.Release(); }
        }
    }
}
