/**
 * Git 操作封装：状态检测、日志获取、diff 查看。
 * 通过子进程调用 git 命令，避免额外依赖。
 *
 * 并发模型：Parallel.For 并行检测多个仓库（最多 6 个并发）。
 * 节流策略：同一仓库 2 分钟内不重复 fetch，减少网络开销。
 */
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TortoiseGitMonitor
{
    /// <summary>单个仓库的 git 状态快照。</summary>
    internal sealed class GitStatus
    {
        public string RepoPath = "";
        public string RepoName = "";
        public string Branch = "";
        public bool HasRemote;

        // 本地状态
        public bool Dirty;
        public int Staged;
        public int Unstaged;
        public int Untracked;

        // 远程状态
        public int Behind;
        public int Ahead;
        public string FetchError = "";

        // 最近提交
        public List<string> RecentCommits = new List<string>();
        public string DiffStat = "";

        // 全部分支（仅在 monitor_all_branches 开启时填充）
        public List<BranchStatus> Branches = new List<BranchStatus>();

        // 时间
        public double CheckedAt;
        public string Error = "";
    }

    /// <summary>每个仓库的调度状态（不对外暴露），用于节流控制。</summary>
    internal sealed class RepoScheduleState
    {
        public string RepoPath = "";
        public double LastFetchTime;
    }

    /// <summary>单个分支的状态。</summary>
    internal sealed class BranchStatus
    {
        public string Name = "";
        public bool IsCurrent;
        public string Tracking = "";    // 跟踪的远程分支，如 "origin/main"
        public int Ahead;
        public int Behind;
        public string LastHash = "";    // 最新提交的短 hash
        public string LastMessage = ""; // 最新提交消息
    }

    /// <summary>单个文件的变更信息。</summary>
    internal sealed class ChangedFile
    {
        public string Path = "";
        public int Added;            // 新增行数
        public int Deleted;          // 删除行数
        public string Status = "";   // A=新增, M=修改, D=删除, R=重命名
    }

    /// <summary>单条提交的详细信息。</summary>
    internal sealed class CommitDetail
    {
        public string Hash = "";
        public string ShortHash = "";
        public string Author = "";
        public string Date = "";
        public string Message = "";
        public List<ChangedFile> ChangedFiles = new List<ChangedFile>();
    }

    internal static class GitWatcher
    {
        public const int FetchTimeoutSec = 30;    // 单次 fetch 超时（秒）
        public const int FetchCooldownSec = 120;  // 同一仓库两次 fetch 最小间隔（秒）
        public const int MaxConcurrent = 6;       // 最大并行检测数

        // ═══════════════════════════
        //  底层 git 调用
        // ═══════════════════════════

        private sealed class GitResult
        {
            public string Stdout = "";
            public string Stderr = "";
            public int Code = 1;
        }

        private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

        /// <summary>执行 git 命令，返回 (stdout, stderr, returncode)。</summary>
        private static GitResult Run(string cwd, int timeoutSec, params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = ProcessUtil.BuildArguments(args),
                    WorkingDirectory = cwd,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    CreateNoWindow = true,
                };
                using (Process process = Process.Start(psi))
                {
                    Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                    Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(timeoutSec * 1000))
                    {
                        try { process.Kill(); } catch (Exception) { /* 进程可能已退出 */ }
                        return new GitResult { Stderr = "操作超时" };
                    }
                    process.WaitForExit(); // 等待异步输出读取完成
                    return new GitResult
                    {
                        Stdout = stdoutTask.Result.Trim(),
                        Stderr = stderrTask.Result.Trim(),
                        Code = process.ExitCode,
                    };
                }
            }
            catch (Win32Exception)
            {
                return new GitResult { Stderr = "git 未找到，请确认 git 已安装且在 PATH 中" };
            }
            catch (Exception ex)
            {
                return new GitResult { Stderr = ex.Message };
            }
        }

        /// <summary>检查是否为有效的 git 仓库且 git 可用。</summary>
        private static bool CheckVersion(string cwd)
        {
            return Run(cwd, 10, "rev-parse", "--is-inside-work-tree").Code == 0;
        }

        // ═══════════════════════════
        //  单仓库检测
        // ═══════════════════════════

        /// <summary>检测单个仓库的完整状态。</summary>
        public static GitStatus CheckRepo(
            string repoPath,
            int logCount = 10,
            RepoScheduleState schedule = null,
            int fetchCooldown = FetchCooldownSec,
            bool checkBranchesFlag = false)
        {
            var status = new GitStatus
            {
                RepoPath = repoPath,
                RepoName = Path.GetFileName(repoPath.TrimEnd('\\', '/')),
                CheckedAt = Now(),
            };

            // ── 基础检查 ──
            if (!CheckVersion(repoPath))
            {
                status.Error = "不是有效的 git 仓库";
                return status;
            }

            // ── 分支名 ──
            GitResult branchResult = Run(repoPath, 10, "rev-parse", "--abbrev-ref", "HEAD");
            status.Branch = branchResult.Stdout;
            bool isDetached = branchResult.Stdout == "HEAD";

            // ── fetch 远程（带节流）──
            GitResult remoteResult = Run(repoPath, 5, "remote");
            var remotes = remoteResult.Stdout
                .Split('\n')
                .Where(r => r.Trim().Length > 0)
                .ToList();
            status.HasRemote = remotes.Count > 0;

            bool shouldFetch = false;
            if (status.HasRemote && !isDetached)
            {
                if (schedule == null)
                    shouldFetch = true;
                else if (Now() - schedule.LastFetchTime >= fetchCooldown)
                    shouldFetch = true;
            }

            if (shouldFetch)
            {
                GitResult fetchResult = Run(repoPath, FetchTimeoutSec, "fetch", "--prune", "--quiet");
                if (schedule != null)
                    schedule.LastFetchTime = Now();
                // 检查 fetch 错误：返回码非0 或 stderr 含错误关键词
                string lowerErr = fetchResult.Stderr.ToLowerInvariant();
                if (fetchResult.Code != 0 || (fetchResult.Stderr.Length > 0 &&
                    (lowerErr.Contains("error") || lowerErr.Contains("fatal") ||
                     lowerErr.Contains("failed") || lowerErr.Contains("denied"))))
                {
                    status.FetchError = fetchResult.Stderr.Length > 0
                        ? fetchResult.Stderr.Substring(0, Math.Min(200, fetchResult.Stderr.Length))
                        : "fetch 失败";
                }
            }

            // 计算 ahead / behind：使用 @{u} 自动识别上游分支（避免硬编码 origin）
            if (status.HasRemote && !isDetached)
            {
                GitResult revResult = Run(repoPath, 10, "rev-list", "--left-right", "--count", "@{u}...HEAD");
                if (revResult.Code == 0 && revResult.Stdout.Length > 0)
                {
                    string[] parts = revResult.Stdout.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2)
                    {
                        int.TryParse(parts[0], out int behind);
                        int.TryParse(parts[1], out int ahead);
                        status.Behind = behind;
                        status.Ahead = ahead;
                    }
                }
            }

            // ── 本地状态 ──
            GitResult porcelain = Run(repoPath, 10, "status", "--porcelain");
            if (porcelain.Stdout.Length > 0)
            {
                status.Dirty = true;
                foreach (string rawLine in porcelain.Stdout.Split('\n'))
                {
                    string line = rawLine.TrimEnd('\r');
                    if (line.Trim().Length == 0)
                        continue;
                    char stagedChar = line.Length > 0 ? line[0] : ' ';
                    char unstagedChar = line.Length > 1 ? line[1] : ' ';
                    // untracked: ??
                    if (stagedChar == '?' && unstagedChar == '?')
                    {
                        status.Untracked++;
                        continue;
                    }
                    if (stagedChar != ' ')
                        status.Staged++;
                    if (unstagedChar != ' ')
                        status.Unstaged++;
                }
            }

            // ── 最近提交 ──
            // 格式: hash||date||author||message  （用 || 分隔，便于解析）
            GitResult logResult = Run(repoPath, 5,
                "log", "-n", logCount.ToString(), "--date=iso", "--format=%h||%ad||%an||%s");
            if (logResult.Stdout.Length > 0)
                status.RecentCommits = new List<string>(logResult.Stdout.Split('\n'));
            else if (logResult.Code != 0 && logResult.Stderr.ToLowerInvariant().Contains("does not have any commits"))
                status.Error = "空仓库（无提交历史）";

            // ── diff stat ──
            GitResult diffResult = Run(repoPath, 10, "diff", "--stat");
            if (diffResult.Stdout.Length > 0)
                status.DiffStat = diffResult.Stdout;

            // ── 全部分支（可选）──
            if (checkBranchesFlag)
                status.Branches = CheckBranches(repoPath);

            return status;
        }

        // ═══════════════════════════
        //  并行批量检测
        // ═══════════════════════════

        /// <summary>获取仓库所有本地分支的状态。</summary>
        public static List<BranchStatus> CheckBranches(string repoPath)
        {
            var branches = new List<BranchStatus>();

            // git branch -vv: 列出所有本地分支及其跟踪关系
            GitResult result = Run(repoPath, 10,
                "branch", "-vv",
                "--format=%(refname:short)|%(upstream:short)|%(upstream:track,nobracket)|%(objectname:short)");
            if (result.Code != 0 || result.Stdout.Length == 0)
                return branches;

            foreach (string rawLine in result.Stdout.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                    continue;
                // 格式: main|origin/main|[ahead 1, behind 2]|abc1234
                string[] parts = line.Split(new[] { '|' }, 4);
                if (parts.Length < 4)
                    continue;

                string name = parts[0];
                string tracking = parts[1];
                string trackInfo = parts[2];   // e.g. "ahead 1, behind 2" or "" or "gone"
                string lastHash = parts[3];

                int ahead = 0;
                int behind = 0;
                if (trackInfo.Length > 0 && trackInfo != "gone")
                {
                    foreach (string seg in trackInfo.Split(','))
                    {
                        string trimmed = seg.Trim();
                        if (trimmed.StartsWith("ahead ", StringComparison.Ordinal))
                        {
                            string[] words = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            if (words.Length > 1)
                                int.TryParse(words[1], out ahead);
                        }
                        else if (trimmed.StartsWith("behind ", StringComparison.Ordinal))
                        {
                            string[] words = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            if (words.Length > 1)
                                int.TryParse(words[1], out behind);
                        }
                    }
                }

                branches.Add(new BranchStatus
                {
                    Name = name,
                    Tracking = tracking,
                    Ahead = ahead,
                    Behind = behind,
                    LastHash = lastHash,
                });
            }

            // 标记当前分支
            GitResult current = Run(repoPath, 10, "rev-parse", "--abbrev-ref", "HEAD");
            foreach (BranchStatus b in branches)
            {
                if (b.Name == current.Stdout.Trim())
                    b.IsCurrent = true;
            }

            return branches;
        }

        /// <summary>并行检测所有仓库（最多 6 个并发），结果顺序与输入路径一致。</summary>
        public static List<GitStatus> CheckAllParallel(
            IList<string> repoPaths,
            int logCount = 10,
            IDictionary<string, RepoScheduleState> schedules = null,
            int maxWorkers = MaxConcurrent,
            int fetchCooldown = FetchCooldownSec,
            bool checkBranchesFlag = false)
        {
            if (schedules == null)
                schedules = new Dictionary<string, RepoScheduleState>();

            // 确保每个 repo 有对应的 schedule
            var scheduleLock = new object();
            foreach (string p in repoPaths)
            {
                if (!schedules.ContainsKey(p))
                    schedules[p] = new RepoScheduleState { RepoPath = p };
            }

            var results = new GitStatus[repoPaths.Count];
            int actualWorkers = Math.Min(maxWorkers, repoPaths.Count);
            if (actualWorkers <= 0)
                return new List<GitStatus>();

            var options = new ParallelOptions { MaxDegreeOfParallelism = actualWorkers };
            Parallel.For(0, repoPaths.Count, options, i =>
            {
                string key = repoPaths[i];
                RepoScheduleState sched;
                lock (scheduleLock)
                    sched = schedules[key];
                try
                {
                    results[i] = CheckRepo(key, logCount, sched, fetchCooldown, checkBranchesFlag);
                }
                catch (Exception ex)
                {
                    results[i] = new GitStatus
                    {
                        RepoPath = key,
                        RepoName = Path.GetFileName(key.TrimEnd('\\', '/')),
                        Error = $"检测异常: {ex.Message}",
                    };
                }
            });

            return results.Where(r => r != null).ToList();
        }

        // ═══════════════════════════
        //  提交详情 & 文件对比
        // ═══════════════════════════

        /// <summary>获取单条提交的详细信息（作者、日期、变更文件列表）。</summary>
        public static CommitDetail GetCommitDetail(string repoPath, string commitHash)
        {
            // ── 基本信息 ──
            GitResult result = Run(repoPath, 5,
                "show", "--no-patch", "--format=%H||%h||%an||%ad||%s",
                "--date=relative", commitHash);
            if (result.Code != 0 || result.Stdout.Length == 0)
                return null;

            string[] parts = result.Stdout.Split(new[] { "||" }, 5, StringSplitOptions.None);
            if (parts.Length < 5)
                return null;

            var detail = new CommitDetail
            {
                Hash = parts[0],
                ShortHash = parts[1],
                Author = parts[2],
                Date = parts[3],
                Message = parts[4],
            };

            // ── 变更文件列表 ──
            GitResult statResult = Run(repoPath, 10, "show", "--stat", "--format=", commitHash);
            if (statResult.Stdout.Length > 0)
                detail.ChangedFiles = ParseChangedFiles(statResult.Stdout);

            return detail;
        }

        /// <summary>获取某个文件在某次提交中的 diff 内容。</summary>
        public static string GetCommitFileDiff(string repoPath, string commitHash, string filePath, int maxLines = 200)
        {
            GitResult result = Run(repoPath, 10, "show", commitHash, "--", filePath);
            if (result.Code != 0)
                return result.Stderr.Length > 0 ? result.Stderr : "无法获取 diff";
            string[] lines = result.Stdout.Split('\n');
            if (lines.Length > maxLines)
                return string.Join("\n", lines.Take(maxLines)) + $"\n\n... (截断，共 {lines.Length} 行)";
            return result.Stdout;
        }

        /// <summary>获取当前未暂存变更中某个文件的 diff。</summary>
        public static string GetUnstagedFileDiff(string repoPath, string filePath, int maxLines = 200)
        {
            GitResult result = Run(repoPath, 10, "diff", "--", filePath);
            if (result.Code != 0)
                return result.Stderr.Length > 0 ? result.Stderr : "无变更";
            if (result.Stdout.Length == 0)
            {
                // 可能已暂存，尝试 --cached
                result = Run(repoPath, 10, "diff", "--cached", "--", filePath);
                if (result.Stdout.Length == 0)
                    return "无变更";
            }
            string[] lines = result.Stdout.Split('\n');
            if (lines.Length > maxLines)
                return string.Join("\n", lines.Take(maxLines)) + $"\n\n... (截断，共 {lines.Length} 行)";
            return result.Stdout;
        }

        /// <summary>解析 git show --stat 输出中的文件列表。</summary>
        private static List<ChangedFile> ParseChangedFiles(string statOutput)
        {
            var files = new List<ChangedFile>();
            foreach (string rawLine in statOutput.Trim().Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                    continue;
                // 格式: "path.py | 3 ++-"
                // 最后一行是 "N files changed, ..." 的汇总，跳过
                int sepIndex = line.LastIndexOf('|');
                if (sepIndex < 0)
                    continue;
                string filePath = line.Substring(0, sepIndex).Trim();
                string changes = line.Substring(sepIndex + 1).Trim();

                int added = 0;
                int deleted = 0;
                // 解析变更统计，如 "3 +++---" 或 "5 +-" 或 "Bin 0 -> 123 bytes"
                if (!changes.Contains("Bin"))
                {
                    foreach (char ch in changes)
                    {
                        if (ch == '+')
                            added++;
                        else if (ch == '-')
                            deleted++;
                    }
                }

                string fileStatus = "M";
                if (filePath.Contains("=>"))
                    fileStatus = "R";

                files.Add(new ChangedFile
                {
                    Path = filePath,
                    Added = added,
                    Deleted = deleted,
                    Status = fileStatus,
                });
            }
            return files;
        }

        // ═══════════════════════════
        //  展示辅助
        // ═══════════════════════════

        /// <summary>生成状态摘要文本（用于 tooltip / 菜单）。</summary>
        public static string StatusSummary(GitStatus st)
        {
            if (st.Error.Length > 0)
                return $"✗ {st.Error}";

            string branch = st.Branch.Length > 0 ? st.Branch : "???";
            var flags = new List<string>();

            if (st.Dirty)
            {
                var partsDirty = new List<string>();
                if (st.Staged > 0)
                    partsDirty.Add($"暂存{st.Staged}");
                if (st.Unstaged > 0)
                    partsDirty.Add($"修改{st.Unstaged}");
                if (st.Untracked > 0)
                    partsDirty.Add($"新增{st.Untracked}");
                flags.Add(string.Join(" ", partsDirty));
            }
            if (st.Ahead > 0)
                flags.Add($"↑{st.Ahead}");
            if (st.Behind > 0)
                flags.Add($"↓{st.Behind}");
            if (st.HasRemote && !st.Dirty && st.Ahead == 0 && st.Behind == 0)
                flags.Add("同步");

            if (flags.Count > 0)
                return branch + " | " + string.Join(", ", flags);
            return branch;
        }

        /// <summary>返回状态对应的 emoji 图标。</summary>
        public static string StatusIcon(GitStatus st)
        {
            if (st.Error.Length > 0)
                return "❌";
            if (st.Behind > 0 && st.Ahead > 0)
                return "🔀";
            if (st.Behind > 0)
                return "⬇";
            if (st.Ahead > 0)
                return "⬆";
            if (st.Dirty)
                return "✏";
            return "✓";
        }
    }
}
