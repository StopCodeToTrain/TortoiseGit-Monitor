using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace TortoiseGitMonitor
{
    /// <summary>统一 Git 调用与锁检查；不删除任何 Git 锁文件。</summary>
    internal static class GitProcess
    {
        public static ProcessResult Read(string path, int timeoutSec, params string[] args) =>
            ProcessRunner.Run("git", "--no-optional-locks " + ProcessUtil.BuildArguments(args), path, timeoutSec);

        public static ProcessResult Write(string path, string action, Action<string> log,
            CancellationToken cancel, int timeoutSec, params string[] args)
        {
            ProcessResult location = Read(path, 10, "rev-parse", "--git-path", "index.lock");
            if (!location.Ok || string.IsNullOrWhiteSpace(location.Stdout))
                return new ProcessResult { Stderr = "无法定位 index.lock，停止写入: " + location.Stderr };
            string indexLock = Path.GetFullPath(Path.Combine(path, location.Stdout.Trim()));
            string before = DescribeLock(indexLock);
            if (before.Length > 0)
                return new ProcessResult { Stderr = before + "；未启动 " + action };

            ProcessResult result = ProcessRunner.Run("git", ProcessUtil.BuildArguments(args), path, timeoutSec,
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
                return "存在残留锁 " + path + "（当前可独占打开），已保留；请确认其他 Git 操作结束并检查仓库后再人工处理";
            }
            catch (FileNotFoundException) { return ""; }
            catch (DirectoryNotFoundException) { return ""; }
            catch (Exception ex)
            {
                return "锁仍被占用或无法确认占用状态: " + path + "；禁止后续写入，未删除锁（" + ex.Message + "）";
            }
        }
    }

    /// <summary>避免本应用后台 fetch、同步和分支切换交叉写入同一个仓库。</summary>
    internal sealed class RepositoryOperation : IDisposable
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _gate;
        private RepositoryOperation(SemaphoreSlim gate) { _gate = gate; }
        public static RepositoryOperation TryEnter(string path)
        {
            string key = Path.GetFullPath(path).TrimEnd('\\', '/');
            var gate = Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            return gate.Wait(0) ? new RepositoryOperation(gate) : null;
        }
        public void Dispose() { _gate.Release(); }
    }
}
