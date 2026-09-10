// Windows 10/11 进程树执行器：进程创建时即加入 Job，避免 Git 包装进程先退出后留下写入子进程。
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace TortoiseGitMonitor
{
    internal sealed class ProcessResult
    {
        public string Stdout = "";
        public string Stderr = "";
        public int Code = -1;
        public bool TimedOut;
        public bool Cancelled;
        public bool Ok => Code == 0 && !TimedOut && !Cancelled;
    }

    internal static class ProcessRunner
    {
        /// <summary>
        /// timeoutSec=0 表示持续等待。返回前必须确认整个 Job 无活动进程并读完双路输出。
        /// cancel 只用于明确要求的强制终止；正常停止应在调用方的命令边界处理。
        /// </summary>
        public static ProcessResult Run(string file, string arguments, string cwd, int timeoutSec,
            Action<string> progress = null, CancellationToken cancel = default(CancellationToken), Encoding encoding = null)
        {
            var result = new ProcessResult();
            var clock = Stopwatch.StartNew();
            SafeFileHandle outRead = null, outWrite = null, errRead = null, errWrite = null, input = null;
            NativeHandle job = null;
            var pi = new ProcessInformation();
            IntPtr attributes = IntPtr.Zero, handles = IntPtr.Zero, jobs = IntPtr.Zero;
            bool attributesReady = false, drained = false;
            Task<string> stdout = null, stderr = null;
            try
            {
                if (cancel.IsCancellationRequested)
                    return new ProcessResult { Cancelled = true, Stderr = "已取消，未启动进程" };

                job = CreateJobObject(IntPtr.Zero, null);
                Check(!job.IsInvalid);
                var limits = new ExtendedLimits();
                limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE，不允许子进程脱离 Job
                Check(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(limits)));

                var sa = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), InheritHandle = true };
                Check(CreatePipe(out outRead, out outWrite, ref sa, 0));
                Check(CreatePipe(out errRead, out errWrite, ref sa, 0));
                Check(SetHandleInformation(outRead, 1, 0));
                Check(SetHandleInformation(errRead, 1, 0));
                input = CreateFile("NUL", 0x80000000, 3, ref sa, 3, 0, IntPtr.Zero);
                Check(!input.IsInvalid); // stdin 为 EOF，避免不可见的终端输入或脚本 pause 卡住

                IntPtr size = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
                attributes = Marshal.AllocHGlobal(size);
                Check(InitializeProcThreadAttributeList(attributes, 2, 0, ref size));
                attributesReady = true;
                handles = Marshal.AllocHGlobal(IntPtr.Size * 3);
                Marshal.Copy(new[] { input.DangerousGetHandle(), outWrite.DangerousGetHandle(), errWrite.DangerousGetHandle() }, 0, handles, 3);
                Check(UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20002, handles, (IntPtr)(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero));
                jobs = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(jobs, job.DangerousGetHandle());
                // JOB_LIST 原子加入 Job，连“挂起创建再绑定”之间的崩溃窗口也没有。
                Check(UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x2000D, jobs, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero));
                var si = new StartupInfoEx { Attributes = attributes };
                si.Info.Size = Marshal.SizeOf(si);
                si.Info.Flags = 0x100; // STARTF_USESTDHANDLES
                si.Info.Input = input.DangerousGetHandle();
                si.Info.Output = outWrite.DangerousGetHandle();
                si.Info.Error = errWrite.DangerousGetHandle();
                string executable = FindExecutable(file);
                var command = new StringBuilder(ProcessUtil.QuoteArg(executable) + " " + arguments);
                Check(CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, true,
                    0x08080000, IntPtr.Zero, cwd, ref si, out pi)); // NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT
                outWrite.Dispose();
                errWrite.Dispose();
                input.Dispose();

                // 同时排空 stdout/stderr；管道使用同步句柄，在后台线程读取，避免缓冲区塞满死锁。
                encoding = encoding ?? Encoding.UTF8;
                stdout = ReadPipe(outRead, encoding);
                stderr = ReadPipe(errRead, encoding);
                double nextProgress = 15;
                while (ActiveProcesses(job) != 0)
                {
                    bool timedOut = timeoutSec > 0 && clock.Elapsed.TotalSeconds >= timeoutSec;
                    if (cancel.IsCancellationRequested || timedOut)
                    {
                        result.Cancelled = cancel.IsCancellationRequested;
                        result.TimedOut = !result.Cancelled;
                        Report(progress, (result.Cancelled ? "收到强制停止请求" : "操作超时") + "，正在终止整个进程树并等待退出...");
                        TerminateAndWait(job, progress);
                        break;
                    }
                    if (clock.Elapsed.TotalSeconds >= nextProgress)
                    {
                        Report(progress, $"仍在运行，已用时 {(int)clock.Elapsed.TotalSeconds} 秒；正在等待进程及其子进程完成，请勿重复操作。");
                        nextProgress = clock.Elapsed.TotalSeconds + 15;
                    }
                    Thread.Sleep(100);
                }
                drained = true;
                Check(WaitForSingleObject(pi.Process, uint.MaxValue) == 0);
                uint code;
                Check(GetExitCodeProcess(pi.Process, out code));
                result.Code = unchecked((int)code);
                result.Stdout = stdout.GetAwaiter().GetResult();
                result.Stderr = stderr.GetAwaiter().GetResult();
                if (result.Cancelled || result.TimedOut)
                    result.Stderr += "\n" + (result.Cancelled ? "已强制停止" : "操作超时") + "；整个进程树已退出。现场可能有部分改动或残留锁，请检查后恢复。";
            }
            catch (Exception ex)
            {
                result.Code = -1;
                result.Stderr += "\n进程执行失败: " + ex.Message;
            }
            finally
            {
                // 任意异常同样不能放任子进程继续写入；终止失败时保持等待，不谎报已经退出。
                if (pi.Process != IntPtr.Zero && !drained)
                    TerminateAndWait(job, progress);
                outWrite?.Dispose();
                errWrite?.Dispose();
                input?.Dispose();
                Observe(stdout);
                Observe(stderr);
                outRead?.Dispose();
                errRead?.Dispose();
                if (pi.Thread != IntPtr.Zero) CloseHandle(pi.Thread);
                if (pi.Process != IntPtr.Zero) CloseHandle(pi.Process);
                if (attributesReady) DeleteProcThreadAttributeList(attributes);
                if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
                if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
                if (jobs != IntPtr.Zero) Marshal.FreeHGlobal(jobs);
                job?.Dispose();
            }
            return result;
        }

        private static Task<string> ReadPipe(SafeFileHandle handle, Encoding encoding) => Task.Run(() =>
        {
            using (var stream = new FileStream(handle, FileAccess.Read))
            using (var reader = new StreamReader(stream, encoding))
                return reader.ReadToEnd();
        });

        private static void Observe(Task<string> task)
        {
            if (task == null) return;
            try { task.GetAwaiter().GetResult(); }
            catch (Exception) { /* 读取异常已由主流程报告，仍须回收管道 */ }
        }

        private static void Report(Action<string> progress, string message)
        {
            try { progress?.Invoke(message); }
            catch (Exception) { /* 日志窗口关闭不应影响进程树清理 */ }
        }

        private static uint ActiveProcesses(NativeHandle job)
        {
            BasicAccounting info;
            Check(QueryInformationJobObject(job, 1, out info, (uint)Marshal.SizeOf(typeof(BasicAccounting)), IntPtr.Zero));
            return info.ActiveProcesses;
        }

        private static void TerminateAndWait(NativeHandle job, Action<string> progress)
        {
            if (!TerminateJobObject(job, 1))
                Report(progress, "终止请求未成功，继续等待进程树退出，禁止后续写入。");
            var clock = Stopwatch.StartNew();
            while (ActiveProcesses(job) != 0)
            {
                if (clock.Elapsed.TotalSeconds >= 15)
                {
                    Report(progress, "仍在等待整个进程树退出，请保持窗口开启；不会删除锁或继续写入。");
                    clock.Restart();
                }
                Thread.Sleep(100);
            }
        }

        private static string FindExecutable(string file)
        {
            if (Path.IsPathRooted(file)) return file;
            string name = Path.HasExtension(file) ? file : file + ".exe";
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string candidate = Path.Combine(dir.Trim().Trim('"'), name);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            throw new FileNotFoundException("找不到 " + file + "，请确认已安装且在 PATH 中");
        }

        private static void Check(bool ok)
        {
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        private sealed class NativeHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public NativeHandle() : base(true) { }
            protected override bool ReleaseHandle() => CloseHandle(handle);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfo
        {
            public int Size;
            public IntPtr Reserved, Desktop, Title;
            public uint X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
            public ushort ShowWindow, ReservedSize;
            public IntPtr ReservedData, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx { public StartupInfo Info; public IntPtr Attributes; }
        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long ProcessTime, JobTime;
            public uint LimitFlags;
            public UIntPtr MinWorkingSet, MaxWorkingSet;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            public BasicLimits Basic;
            public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct BasicAccounting
        {
            public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
            public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern NativeHandle CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(NativeHandle job, int infoClass, ref ExtendedLimits info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool QueryInformationJobObject(NativeHandle job, int infoClass, out BasicAccounting info, uint length, IntPtr returned);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(NativeHandle job, uint code);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, ref SecurityAttributes attributes, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")]
        private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
            bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
