// 只在新写操作启动前清理失效锁；不重试已经失败的 merge/stash/checkout。
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace TortoiseGitMonitor
{
    internal static class IndexLockPolicy
    {
        internal const int WaitSeconds = 30;
        internal static readonly TimeSpan StaleAge = TimeSpan.FromMinutes(2);
        private const int StableSeconds = 2;

        internal static ProcessResult Prepare(string path, string action, Action<string> log,
            CancellationToken cancel, int waitSeconds, Func<bool> stopRequested = null)
        {
            var clock = Stopwatch.StartNew();
            Snapshot initial = null;
            bool observed = false, unchanged = true;
            bool waited = false;
            double nextLog = 5;
            double nextCleanup = StableSeconds;
            string cleanupFailure = "";
            while (true)
            {
                if (cancel.IsCancellationRequested || (stopRequested != null && stopRequested()))
                    return new ProcessResult { Cancelled = true, Stderr = "等待 index.lock 时已停止；未启动 " + action };
                string error;
                Snapshot current = Inspect(path, out error);
                if (current == null && error.Length == 0)
                {
                    if (waited) log?.Invoke("  index.lock 已释放，继续 " + action);
                    return new ProcessResult { Code = 0 };
                }
                if (!observed) initial = current;
                else if (current == null || !current.Equals(initial)) unchanged = false;
                observed = true;
                // 旧锁连续稳定至少 2 秒后即可检查清理；短暂 Git 活动在 30 秒窗口内继续等待。
                if (waitSeconds > 0 && current != null && unchanged && initial != null && current.IsOld &&
                    clock.Elapsed.TotalSeconds >= StableSeconds &&
                    (clock.Elapsed.TotalSeconds >= nextCleanup || clock.Elapsed.TotalSeconds >= waitSeconds))
                {
                    cleanupFailure = DeleteStale(path, current, cancel, stopRequested);
                    nextCleanup = clock.Elapsed.TotalSeconds + 2;
                    if (cleanupFailure.Length == 0)
                    {
                        log?.Invoke("  已直接删除失效 index.lock: " + path +
                            $"（{current.Length} 字节，最后修改 {current.LastWriteUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}）");
                        string remaining = GitProcess.DescribeLock(path);
                        if (remaining.Length == 0) return new ProcessResult { Code = 0 };
                        return new ProcessResult { Stderr = "清理后其他操作又创建了锁，停止本次写入。" + remaining };
                    }
                }
                if (clock.Elapsed.TotalSeconds >= waitSeconds)
                {
                    string reason = cleanupFailure.Length > 0 ? cleanupFailure : error;
                    // waitSeconds=0 是后台模式：只报告占锁，绝不删除。
                    if (waitSeconds == 0)
                        reason = "后台检测跳过占锁仓库，不等待、不清理";
                    else if (reason.Length == 0 && current != null)
                        reason = "锁尚未满足清理条件（至少 2 分钟未更新，且观察期间保持不变）";
                    if (cancel.IsCancellationRequested || (stopRequested != null && stopRequested()))
                        return new ProcessResult { Cancelled = true, Stderr = "等待 index.lock 时已停止；未启动 " + action };
                    return new ProcessResult { Stderr = "index.lock 未释放: " + path + "；" + reason + "；未启动 " + action };
                }
                if (!waited)
                {
                    log?.Invoke("  检测到 index.lock，最多等待 " + waitSeconds + " 秒: " + path);
                    waited = true;
                }
                if (clock.Elapsed.TotalSeconds >= nextLog)
                {
                    log?.Invoke($"  等待 index.lock，已用时 {(int)clock.Elapsed.TotalSeconds} 秒；未启动 {action}");
                    nextLog = clock.Elapsed.TotalSeconds + 5;
                }
                cancel.WaitHandle.WaitOne(200);
            }
        }

        private static Snapshot Inspect(string path, out string error)
        {
            error = "";
            try
            {
                // 观察时只读属性并允许共享，避免反复独占探测干扰 Git 正常重新打开锁。
                using (SafeFileHandle file = CreateFile(path, 0x80, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
                {
                    if (file.IsInvalid)
                    {
                        int code = Marshal.GetLastWin32Error();
                        if (code == 2 || code == 3) return null;
                        throw new Win32Exception(code);
                    }
                    return ReadSnapshot(file);
                }
            }
            catch (Exception ex) { error = "锁被占用或状态无法确认，未删除（" + ex.Message + "）"; return null; }
        }

        private static string DeleteStale(string path, Snapshot expected, CancellationToken cancel, Func<bool> stopRequested)
        {
            try
            {
                // DELETE | FILE_READ_ATTRIBUTES，独占现有文件，禁止跟随锁文件上的重解析点。
                using (SafeFileHandle file = CreateFile(path, 0x00010080, 0, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
                {
                    if (file.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                    Snapshot current = ReadSnapshot(file);
                    if (!current.Equals(expected) || !current.IsOld)
                        return "锁在等待期间被替换或更新，未删除";
                    string active = ActiveGitProcesses();
                    if (active.Length > 0) return active + "；无法确认锁已失效，未删除";
                    if (cancel.IsCancellationRequested || (stopRequested != null && stopRequested()))
                        return "已收到停止请求，未删除锁";
                    // 按独占句柄删除同一个文件，避免检查后路径被替换而误删新锁。
                    var disposition = new FileDisposition { DeleteFile = true };
                    if (!SetFileInformationByHandle(file, 4, ref disposition, (uint)Marshal.SizeOf(disposition)))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                return "";
            }
            catch (Exception ex) { return "清理失败，未继续写入（" + ex.Message + "）"; }
        }

        private static string ActiveGitProcesses()
        {
            var active = new List<string>();
            Process[] processes;
            try { processes = Process.GetProcesses(); }
            catch (Exception ex) { return "无法检查 Git 进程: " + ex.Message; }
            foreach (Process process in processes)
            {
                using (process)
                {
                    try
                    {
                        string name = process.ProcessName;
                        if (name.Equals("git", StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith("git-", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("TortoiseGitProc", StringComparison.OrdinalIgnoreCase))
                            active.Add(name + "(PID=" + process.Id + ")");
                    }
                    catch (InvalidOperationException) { /* 已退出的进程没有锁所有权 */ }
                    catch (Exception ex) { active.Add("进程状态无法确认: " + ex.Message); }
                }
            }
            return active.Count == 0 ? "" : "检测到活动进程或未知状态: " + string.Join(", ", active);
        }

        private static Snapshot ReadSnapshot(SafeFileHandle file)
        {
            FileInformation info;
            if (!GetFileInformationByHandle(file, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((info.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException("锁文件是目录或重解析点，不自动处理");
            return new Snapshot
            {
                Volume = info.Volume, IndexHigh = info.IndexHigh, IndexLow = info.IndexLow,
                CreationUtc = FileTime(info.Creation), LastWriteUtc = FileTime(info.LastWrite),
                Length = ((long)info.SizeHigh << 32) | info.SizeLow
            };
        }

        private static DateTime FileTime(NativeFileTime time) =>
            DateTime.FromFileTimeUtc(((long)time.High << 32) | time.Low);

        private sealed class Snapshot
        {
            public uint Volume, IndexHigh, IndexLow;
            public DateTime CreationUtc, LastWriteUtc;
            public long Length;
            public bool IsOld => DateTime.UtcNow - CreationUtc >= StaleAge && DateTime.UtcNow - LastWriteUtc >= StaleAge;
            public bool Equals(Snapshot other) => other != null && Volume == other.Volume &&
                IndexHigh == other.IndexHigh && IndexLow == other.IndexLow && CreationUtc == other.CreationUtc &&
                LastWriteUtc == other.LastWriteUtc && Length == other.Length;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeFileTime { public uint Low, High; }
        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            public uint Attributes;
            public NativeFileTime Creation, Access, LastWrite;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct FileDisposition { [MarshalAs(UnmanagedType.Bool)] public bool DeleteFile; }
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass,
            ref FileDisposition info, uint length);
    }
}
