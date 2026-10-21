/**
 * 一键 Pull 更新：移植自 git-sync 的 git_sync.py / git_wrapper.py / conflict_resolver.py。
 *
 * 流程（每仓库）：预检 -> fetch -> stash 本地改动 -> merge -> 后置校验 -> pop stash -> 摘要。
 * 任一写入失败立即停止，保留冲突/stash；不自动 commit、不 push。
 *
 * 与终端版的差异：所有"询问"通过 Ask 回调弹 GUI 对话框（由 SyncForm 提供），
 * 日志通过 Log 回调输出到进度窗口；写操作同时记录到配置文件目录的 git_sync.log。
 *
 * 安全约束（与 git-sync 一致）：
 * - 不 push、不 git reset --hard、不 git clean、不自动 commit
 * - stash 默认只 tracked（不带 -u）
 * - 破坏性操作（丢弃一方改动/删除行/表头变更）必须询问
 * - merge/rebase 进行中状态先询问
 * - LLM API 失败 -> 降级询问，绝不自动应用
 * - 单仓库失败隔离，继续处理下一个
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace TortoiseGitMonitor
{
    /// <summary>单个冲突文件的处理结果。</summary>
    internal sealed class ConflictResult
    {
        public string File = "";
        public string Status = "";    // resolved | skipped | manual | error
        public string Action = "";    // llm_auto | user_llm | user_ours | user_theirs | editor | csv_auto | ...
        public string Detail = "";
        public string Confidence = "";
        public bool Destructive;
    }

    /// <summary>单个仓库的同步结果。</summary>
    internal sealed class SyncRepoResult
    {
        public string Repo = "";
        public string Status = "ok";  // ok | skipped | error
        public string Reason = "";
        public string Branch = "";
        public string Merge = "";     // up_to_date | fast_forward | fast_forward_failed | ahead_only | merged | merged_with_conflicts | none
        public List<ConflictResult> Conflicts = new List<ConflictResult>();
        public string StashPop = "none"; // none | ok | resolved_with_conflicts | failed
        public List<string> UnmergedRemaining = new List<string>();
        public bool Dirty;
        public bool StatusKnown;
        public int Staged, Unstaged, Untracked, Unmerged;
        public string StashCommit = "";
        public List<string> StashCommits = new List<string>();
        public bool ResumedStash;
        public string UntrackedBackupDirectory = "";
        public int UntrackedBackedUp;
        public string Head = "", Target = "";
        public int Ahead, Behind;
    }

    /// <summary>一次"一键 Pull 更新"任务：串行同步多个仓库，交互通过回调完成。</summary>
    internal sealed class GitSyncer
    {
        private sealed class PendingStash
        {
            public PendingStash() { }
            public string Head = "", StashCommit = "", StashTag = "";
        }

        private sealed class PendingSync
        {
            public PendingSync() { }
            public string Branch = "", Head = "", Target = "", StashCommit = "", StashTag = "";
            public string UntrackedBackupDirectory = "";
            public int UntrackedBackedUp;
            public bool RestoreStarted;
            // 保留第一份 stash 的旧格式字段，兼容已安装版本留下的恢复记录。
            public List<PendingStash> AdditionalStashes = new List<PendingStash>();

            public List<PendingStash> SavedStashes() => new[] {
                new PendingStash { Head = Head, StashCommit = StashCommit, StashTag = StashTag }
            }.Concat(AdditionalStashes ?? new List<PendingStash>()).ToList();
        }
        /// <summary>日志输出（追加到进度窗口）。</summary>
        public Action<string> Log;

        /// <summary>询问用户：prompt + 选项列表 -> 1-based 选项序号（GUI 对话框实现）。</summary>
        public Func<string, IList<string>, int> Ask;

        public volatile bool StopRequested;
        public CancellationToken ForceCancellation;

        private void CheckStop()
        {
            if (StopRequested || ForceCancellation.IsCancellationRequested)
                throw new OperationCanceledException("用户已停止同步，保留当前状态");
        }

        private readonly LlmClient _llm;      // 可能为 null（未配置/初始化失败）
        private readonly LlmSettings _cfg;
        private readonly string _logFile;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        // csv-conflict 脚本处理的文件类型
        private static readonly HashSet<string> CsvLikeExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".csv", ".plist" };

        // 代码/文本文件（走 LLM 合并）
        private static readonly HashSet<string> CodeExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".c", ".h", ".lua", ".py", ".cpp", ".cc", ".cxx", ".go",
            ".js", ".ts", ".json", ".md", ".txt", ".bat", ".sh",
            ".cs", ".java", ".rb", ".php", ".rs", ".kt", ".swift",
            ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf",
        };

        public GitSyncer(LlmSettings cfg)
        {
            _cfg = cfg ?? new LlmSettings();
            _logFile = Path.Combine(AppConfig.ConfigDir, "git_sync.log");
            Log = msg => { };
            Ask = (prompt, options) => 1;

            try
            {
                _llm = new LlmClient(_cfg, FileLog);
            }
            catch (Exception ex)
            {
                Log("⚠ LLM 初始化失败: " + ex.Message);
                Log("  将以无 LLM 模式运行，冲突全部走人工询问。");
                _llm = null;
            }
        }

        // ═══════════════════════════
        //  日志
        // ═══════════════════════════

        /// <summary>只写审计日志文件（git 写操作、LLM 细节），不输出到界面。</summary>
        private void FileLog(string msg)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logFile));
                File.AppendAllText(_logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n", new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // 日志失败不影响主流程
            }
        }

        // ═══════════════════════════
        //  底层 git 调用
        // ═══════════════════════════

        private static ProcessResult Run(string cwd, int timeoutSec, params string[] args) =>
            GitProcess.Read(cwd, timeoutSec, args);

        /// <summary>写命令不设硬超时；任何失败均通过异常退出本仓库，不允许调用方忽略结果。</summary>
        private ProcessResult RunWrite(string cwd, string action, params string[] args)
        {
            CheckStop();
            ProcessResult res = GitProcess.Write(cwd, action,
                msg => { Log(msg); FileLog(msg + " (cwd=" + cwd + ")"); },
                ForceCancellation, 0, () => StopRequested, args);
            FileLog($"{action}: git {string.Join(" ", args)} -> rc={res.Code}, cancelled={res.Cancelled} (cwd={cwd})");
            if (!res.Ok)
            {
                string detail = action + " 未完成: " + res.Stderr + "\n" + res.Stdout;
                if (res.Cancelled) throw new OperationCanceledException(detail);
                throw new IOException(detail);
            }
            return res;
        }

        private ProcessResult RequiredRead(string path, params string[] args)
        {
            ProcessResult res = ProcessRunner.Run("git", "--no-optional-locks " + ProcessUtil.BuildArguments(args),
                path, 0, msg => Log("  状态检查: " + msg), ForceCancellation);
            if (res.Cancelled) throw new OperationCanceledException(res.Stderr);
            if (!res.Ok) throw new IOException("无法确认仓库状态: git " + string.Join(" ", args) + "\n" + res.Stderr);
            return res;
        }

        private string InProgress(string path)
        {
            string gd = Path.GetFullPath(Path.Combine(path, RequiredRead(path, "rev-parse", "--git-dir").Stdout.Trim()));
            if (File.Exists(Path.Combine(gd, "MERGE_HEAD"))) return "merge";
            if (Directory.Exists(Path.Combine(gd, "rebase-merge")) || Directory.Exists(Path.Combine(gd, "rebase-apply"))) return "rebase";
            if (File.Exists(Path.Combine(gd, "CHERRY_PICK_HEAD"))) return "cherry-pick";
            if (File.Exists(Path.Combine(gd, "REVERT_HEAD"))) return "revert";
            return null;
        }

        private Tuple<int, int> Divergence(string path, string target)
        {
            string[] parts = RequiredRead(path, "rev-list", "--left-right", "--count", target + "...HEAD")
                .Stdout.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            int behind, ahead;
            if (parts.Length != 2 || !int.TryParse(parts[0], out behind) || !int.TryParse(parts[1], out ahead))
                throw new IOException("无法解析 ahead/behind，停止同步");
            return Tuple.Create(ahead, behind);
        }

        private List<string> UnmergedFiles(string path) =>
            RequiredRead(path, "diff", "--name-only", "--diff-filter=U", "-z").Stdout.Split('\0').Where(f => f.Length > 0).ToList();

        private static string ShowStage(string path, int stage, string file)
        {
            ProcessResult res = Run(path, 30, "show", $":{stage}:{file}");
            return res.Ok ? res.Stdout : "";
        }

        /// <summary>按 porcelain v1 的 XY 两列计数，-z 确保换行/重命名文件不会误计。</summary>
        internal static void ParseStatus(string text, SyncRepoResult result)
        {
            result.Staged = result.Unstaged = result.Untracked = result.Unmerged = 0;
            result.UnmergedRemaining.Clear();
            string[] records = text.Split('\0');
            for (int i = 0; i < records.Length; i++)
            {
                string record = records[i];
                if (record.Length < 3) continue;
                string xy = record.Substring(0, 2);
                if (xy == "??") { result.Untracked++; continue; }
                if (xy == "!!") continue;
                if (xy == "DD" || xy == "AA" || xy.IndexOf('U') >= 0)
                {
                    result.Unmerged++;
                    result.UnmergedRemaining.Add(record.Substring(3));
                }
                else
                {
                    if (xy[0] != ' ') result.Staged++;
                    if (xy[1] != ' ') result.Unstaged++;
                }
                if (xy.IndexOf('R') >= 0 || xy.IndexOf('C') >= 0) i++; // 下一条是重命名/复制源路径
            }
            result.Dirty = result.Staged + result.Unstaged + result.Untracked + result.Unmerged > 0;
            result.StatusKnown = true;
        }

        private string StashHead(string path)
        {
            ProcessResult res = RequiredRead(path, "stash", "list", "-1", "--format=%H");
            return res.Stdout.Trim();
        }

        private string RecoveryFile(string path) => Path.GetFullPath(Path.Combine(path,
            RequiredRead(path, "rev-parse", "--git-path", "monitor-sync-pending.json").Stdout.Trim()));

        private void SavePending(string file, PendingSync pending)
        {
            string temporary = file + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, Json.Serialize(pending), new UTF8Encoding(false));
            try
            {
                if (File.Exists(file)) File.Replace(temporary, file, null);
                else File.Move(temporary, file);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private PendingSync LoadPending(string path, string file, SyncRepoResult result)
        {
            if (!File.Exists(file)) return null;
            if (new FileInfo(file).Length > 65536) throw new IOException("同步恢复记录异常，停止自动恢复: " + file);
            PendingSync pending = Json.Deserialize<PendingSync>(File.ReadAllText(file, Encoding.UTF8));
            if (pending == null || pending.Branch != result.Branch ||
                !IsCommitId(pending.Target) || (pending.AdditionalStashes != null && pending.AdditionalStashes.Count > 63))
                throw new IOException("同步恢复记录与当前仓库/分支不符，保留现场: " + file);
            List<PendingStash> saved = pending.SavedStashes();
            string prefix = "git-sync:" + result.Repo + ":";
            foreach (PendingStash stash in saved)
            {
                Guid tagId;
                if (stash == null || !IsCommitId(stash.StashCommit) || !IsCommitId(stash.Head) ||
                    string.IsNullOrEmpty(stash.StashTag) || !stash.StashTag.StartsWith(prefix, StringComparison.Ordinal) ||
                    !Guid.TryParseExact(stash.StashTag.Substring(prefix.Length), "N", out tagId))
                    throw new IOException("同步恢复记录中的 stash 信息异常，保留现场: " + file);
            }
            if (saved.Select(s => s.StashCommit).Distinct(StringComparer.Ordinal).Count() != saved.Count)
                throw new IOException("同步恢复记录包含重复 stash，停止自动恢复: " + file);
            result.StashCommit = pending.StashCommit;
            result.StashCommits = saved.Select(s => s.StashCommit).ToList();
            result.UntrackedBackupDirectory = pending.UntrackedBackupDirectory ?? "";
            result.UntrackedBackedUp = pending.UntrackedBackedUp;
            if (pending.RestoreStarted)
                throw new IOException("上次 stash 恢复已启动但未确认完成，不重复恢复；请检查 git status / git stash list: " + file);
            CheckStashStack(path, pending);
            foreach (PendingStash stash in saved)
            {
                string subject = RequiredRead(path, "show", "-s", "--format=%s", stash.StashCommit).Stdout.Trim();
                string parent = RequiredRead(path, "rev-parse", "--verify", stash.StashCommit + "^1").Stdout.Trim();
                if (!subject.EndsWith(": " + stash.StashTag, StringComparison.Ordinal) || parent != stash.Head ||
                    !Run(path, 30, "merge-base", "--is-ancestor", stash.Head, "HEAD").Ok)
                    throw new IOException("上次 stash 的来源与当前 HEAD 不符，停止自动恢复");
            }
            result.ResumedStash = true;
            Log("▶ 继续上次未完成的同步，保留 stash: " + string.Join(", ", result.StashCommits));
            return pending;
        }

        private static bool IsCommitId(string value) => value != null && (value.Length == 40 || value.Length == 64) && value.All(Uri.IsHexDigit);

        private void CheckStashStack(string path, PendingSync pending)
        {
            string[] expected = pending.SavedStashes().Select(s => s.StashCommit).Reverse().ToArray();
            string[] actual = RequiredRead(path, "stash", "list", "-" + expected.Length, "--format=%H").Stdout
                .Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (!actual.SequenceEqual(expected))
                throw new IOException("stash 栈已发生变化，保留所有记录，不自动恢复或删除其他改动");
        }

        private PendingSync StashTracked(string path, string recovery, PendingSync pending, SyncRepoResult result)
        {
            if (pending != null)
            {
                CheckStashStack(path, pending);
                if (pending.SavedStashes().Count >= 64) throw new IOException("待恢复 stash 数量过多，保留改动并停止同步");
            }
            string head = RequiredRead(path, "rev-parse", "HEAD").Stdout.Trim();
            if (RequiredRead(path, "branch", "--show-current").Stdout.Trim() != result.Branch)
                throw new IOException("分支在同步期间改变，保留改动并停止同步");
            string before = StashHead(path);
            string tag = "git-sync:" + result.Repo + ":" + Guid.NewGuid().ToString("N");
            Log(pending == null ? "▶ stash 本地改动（仅 tracked）..." : "▶ 单独 stash 新增本地改动（仅 tracked，保留此前记录）...");
            RunWrite(path, "stash-push", "stash", "push", "-m", tag);
            string commit = StashHead(path);
            if (commit.Length == 0 || commit == before)
                throw new IOException("stash 未生成新的记录，停止同步");
            string subject = RequiredRead(path, "show", "-s", "--format=%s", commit).Stdout.Trim();
            string parent = RequiredRead(path, "rev-parse", "--verify", commit + "^1").Stdout.Trim();
            if (!subject.EndsWith(": " + tag, StringComparison.Ordinal) || parent != head)
                throw new IOException("新 stash 的标记或来源不符，保留现场并停止同步");
            if (pending == null)
                pending = new PendingSync { Branch = result.Branch, Head = head, Target = result.Target, StashCommit = commit,
                    StashTag = tag, UntrackedBackupDirectory = result.UntrackedBackupDirectory, UntrackedBackedUp = result.UntrackedBackedUp };
            else
            {
                if (pending.AdditionalStashes == null) pending.AdditionalStashes = new List<PendingStash>();
                pending.AdditionalStashes.Add(new PendingStash { Head = head, StashCommit = commit, StashTag = tag });
            }
            result.StashCommit = pending.StashCommit;
            result.StashCommits = pending.SavedStashes().Select(s => s.StashCommit).ToList();
            SavePending(recovery, pending);
            Log("  已保存 stash: " + commit);
            CheckStashStack(path, pending);
            ParseStatus(RequiredRead(path, "status", "--porcelain=v1", "-z", "--untracked-files=all").Stdout, result);
            if (result.Staged + result.Unstaged + result.Unmerged > 0)
                throw new IOException("stash 后仍有 tracked 改动或冲突，停止同步");
            if (RequiredRead(path, "rev-parse", "HEAD").Stdout.Trim() != head)
                throw new IOException("HEAD 在保存改动期间改变，保留 stash 并停止同步");
            return pending;
        }

        private void RestoreStash(string path, string recovery, PendingSync pending, SyncRepoResult result)
        {
            CheckStop();
            if (InProgress(path) != null || UnmergedFiles(path).Count > 0)
                throw new IOException("仓库仍在合并或存在冲突，停止同步");
            if (pending == null) return;
            var current = new SyncRepoResult();
            ParseStatus(RequiredRead(path, "status", "--porcelain=v1", "-z", "--untracked-files=no").Stdout, current);
            if (current.Unmerged > 0) throw new IOException("恢复 stash 前存在冲突，保留 stash 并停止");
            if (current.Staged + current.Unstaged > 0)
                pending = StashTracked(path, recovery, pending, result);
            CheckStashStack(path, pending);
            pending.RestoreStarted = true;
            SavePending(recovery, pending); // 取消/冲突/崩溃后不得重复 pop 已部分应用的 stash。
            result.StashPop = "failed";
            List<PendingStash> saved = pending.SavedStashes();
            if (saved.Count == 1)
            {
                Log("▶ pop stash（恢复原有暂存状态）...");
                RunWrite(path, "stash-pop", "stash", "pop", "--index", "stash@{0}");
            }
            else
            {
                for (int i = 0; i < saved.Count; i++)
                {
                    CheckStashStack(path, pending);
                    Log($"▶ 恢复第 {i + 1}/{saved.Count} 批本地改动（旧到新，保留暂存状态）: {saved[i].StashCommit}");
                    // 使用固定提交 ID；全部 apply 成功前保留每一份原始 stash。
                    if (i == 0) RunWrite(path, "stash-apply", "stash", "apply", "--index", saved[i].StashCommit);
                    else RestoreAdditionalStash(path, recovery, saved[i]);
                    if (UnmergedFiles(path).Count > 0) throw new IOException("恢复产生冲突，保留所有 stash 并停止");
                }
                foreach (PendingStash stash in saved.AsEnumerable().Reverse())
                {
                    if (StashHead(path) != stash.StashCommit)
                        throw new IOException("恢复后 stash 栈改变，不删除其他改动");
                    RunWrite(path, "stash-drop", "stash", "drop", "stash@{0}");
                }
            }
            result.StashPop = "ok";
            File.Delete(recovery);
        }

        private void RestoreAdditionalStash(string path, string recovery, PendingStash stash)
        {
            string prefix = Path.Combine(Path.GetDirectoryName(recovery), "monitor-replay-" + Guid.NewGuid().ToString("N"));
            string worktree = prefix + "-w.patch", index = prefix + "-i.patch";
            try
            {
                // --output 由 Git 直接写原始字节，保留二进制及非 UTF-8 文件；禁用外部 diff/textconv。
                RunWrite(path, "stash-worktree-patch", "diff", "--binary", "--full-index", "--no-renames", "--no-ext-diff", "--no-textconv",
                    "--output=" + worktree, stash.Head, stash.StashCommit, "--");
                RunWrite(path, "stash-index-patch", "diff", "--binary", "--full-index", "--no-renames", "--no-ext-diff", "--no-textconv",
                    "--output=" + index, stash.Head, stash.StashCommit + "^2", "--");
                // 先检查两份补丁，再按各自范围恢复；不重置此前批次的 index。
                // apply 无法合并时直接停止，原始 stash 全部保留，禁止覆盖工作区。
                bool hasWorktree = new FileInfo(worktree).Length > 0, hasIndex = new FileInfo(index).Length > 0;
                if (hasWorktree) RunWrite(path, "stash-worktree-check", "apply", "--check", worktree);
                if (hasIndex) RunWrite(path, "stash-index-check", "apply", "--cached", "--check", index);
                if (hasWorktree) RunWrite(path, "stash-worktree-apply", "apply", worktree);
                if (hasIndex) RunWrite(path, "stash-index-apply", "apply", "--cached", index);
            }
            finally
            {
                // 这里只删除本次生成的两份临时补丁；原始内容仍在准确 stash 提交中。
                try { if (File.Exists(worktree)) File.Delete(worktree); } catch (IOException) { }
                try { if (File.Exists(index)) File.Delete(index); } catch (IOException) { }
            }
        }

        private void BackupUntracked(string path, string status, SyncRepoResult result)
        {
            CheckStop();
            var untracked = status.Split('\0').Where(r => r.StartsWith("?? ", StringComparison.Ordinal)).Select(r => r.Substring(3)).ToList();
            if (untracked.Count == 0) return;
            string added = RequiredRead(path, "diff", "--no-renames", "--name-only", "--diff-filter=A", "-z", result.Head, result.Target).Stdout;
            List<string> blockers = UntrackedBackup.Find(untracked, added.Split('\0').Where(f => f.Length > 0));
            if (blockers.Count == 0) return;
            // 重新确认文件仍未跟踪，防止把其他操作刚加入暂存区的文件移走。
            for (int i = 0; i < blockers.Count; i += 64)
            {
                string[] args = new[] { "--literal-pathspecs", "ls-files", "--cached", "-z", "--" }.Concat(blockers.Skip(i).Take(64)).ToArray();
                if (RequiredRead(path, args).Stdout.Length > 0)
                    throw new IOException("阻塞文件已被其他操作加入跟踪，停止自动备份");
            }
            if (RequiredRead(path, "rev-parse", "HEAD").Stdout.Trim() != result.Head)
                throw new IOException("HEAD 在备份前发生变化，停止自动合并");
            string gitDir = RequiredRead(path, "rev-parse", "--absolute-git-dir").Stdout.Trim();
            string directory = Path.Combine(gitDir, "monitor-untracked-backup", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            result.UntrackedBackupDirectory = directory;
            Log($"▶ 自动备份 {blockers.Count} 个未跟踪阻塞文件: {directory}");
            UntrackedBackup.Move(path, gitDir, directory, blockers, CheckStop,
                message => { Log(message); FileLog(message + " (cwd=" + path + ")"); },
                Json.Serialize(new { branch = result.Branch, head = result.Head, target = result.Target, files = blockers }));
            result.UntrackedBackedUp = blockers.Count;
        }

        public SyncRepoResult SyncRepo(string path)
        {
            var result = new SyncRepoResult { Repo = Path.GetFileName(path.TrimEnd('\\', '/')) };
            using (var operation = RepositoryOperation.TryEnter(path))
            {
                if (operation == null)
                {
                    result.Status = "skipped";
                    result.Reason = "此仓库正在执行其他检测或写操作，请稍后重试";
                    return result;
                }
                Log("\n" + new string('=', 60));
                Log($"仓库: {result.Repo} ({path})");
                try
                {
                    CheckStop();
                    SyncRepoCore(path, result);
                }
                catch (OperationCanceledException ex)
                {
                    StopRequested = true;
                    result.Status = "cancelled";
                    result.Reason = ex.Message;
                }
                catch (Exception ex)
                {
                    result.Status = "error";
                    result.Reason = ex.Message;
                }
                if (result.Merge == "fast_forward_failed")
                {
                    // 即使命令异常也只读核对实际结果，绝不因此恢复后续写流程。
                    try
                    {
                        if (VerifyFastForward(path, result))
                        {
                            result.Merge = "fast_forward";
                            result.Reason = "快进目标实际上已到达，但命令异常或取消，本仓库已停止。\n" + result.Reason;
                        }
                    }
                    catch (Exception ex) { result.Reason += "\n后置校验未完成: " + ex.Message; }
                }
                if (result.Status != "ok" && result.Status != "skipped")
                {
                    result.Reason += "\n已停止本仓库后续写入，不再执行 stash pop。请检查 git status 和 git stash list 后手动恢复。";
                    if (result.StashCommits.Count > 0)
                        result.Reason += "\n待恢复记录中的 stash 提交（旧到新）: " + string.Join(", ", result.StashCommits) + "（以 stash list 为准）。";
                    if (result.UntrackedBackupDirectory.Length > 0)
                        result.Reason += "\n未跟踪文件备份目录: " + result.UntrackedBackupDirectory;
                    Log("✗ " + result.Reason);
                }
                // 终止后的摘要只能只读；不得把读失败当成干净工作区。
                ProcessResult status = Run(path, 120, "status", "--porcelain=v1", "-z", "--untracked-files=all");
                if (status.Ok)
                    ParseStatus(status.Stdout, result);
                else
                {
                    result.StatusKnown = false;
                    result.Reason += "\n工作区状态检查未完成: " + status.Stderr;
                    if (result.Status == "ok") result.Status = "error";
                }
                return result;
            }
        }

        private bool VerifyFastForward(string path, SyncRepoResult result)
        {
            string remote = "refs/remotes/origin/" + result.Branch;
            ProcessResult head = Run(path, 30, "rev-parse", "--verify", "HEAD");
            ProcessResult target = Run(path, 30, "rev-parse", "--verify", remote + "^{commit}");
            if (!head.Ok || !target.Ok) throw new IOException("无法读取 HEAD 或远端目标");
            result.Head = head.Stdout.Trim();
            string currentRemote = target.Stdout.Trim();
            ProcessResult counts = Run(path, 30, "rev-list", "--left-right", "--count", currentRemote + "...HEAD");
            string[] parts = counts.Stdout.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (!counts.Ok || parts.Length != 2 || !int.TryParse(parts[0], out result.Behind) || !int.TryParse(parts[1], out result.Ahead))
                throw new IOException("无法读取 ahead/behind");
            string detail = $"快进校验: HEAD={result.Head}, 目标={result.Target}, 远端={currentRemote}, ahead={result.Ahead}, behind={result.Behind}";
            Log("  " + detail);
            FileLog(detail + " (cwd=" + path + ")");
            return result.Head == result.Target && currentRemote == result.Target && result.Ahead == 0 && result.Behind == 0;
        }

        private void SyncRepoCore(string path, SyncRepoResult result)
        {
            if (RequiredRead(path, "rev-parse", "--is-inside-work-tree").Stdout.Trim() != "true")
                throw new IOException("不是工作区仓库");
            string prog = InProgress(path);
            if (prog != null)
            {
                int choice = Ask($"⚠ 仓库 {result.Repo} 处于 {prog} 进行中状态，如何处理？",
                    new[] { "跳过此仓库（保留现状）", "abort 后继续同步", "中止全部同步" });
                if (choice == 1)
                {
                    result.Status = "skipped";
                    result.Reason = "用户保留 " + prog + " 进行中状态";
                    return;
                }
                if (choice != 2) throw new OperationCanceledException("用户中止全部同步");
                RunWrite(path, "abort-" + prog, prog, "--abort");
                if (InProgress(path) != null) throw new IOException("abort 后仓库仍处于进行中状态");
            }
            result.Branch = RequiredRead(path, "branch", "--show-current").Stdout.Trim();
            if (result.Branch.Length == 0) throw new IOException("detached HEAD，停止同步");
            string recovery = RecoveryFile(path);
            PendingSync pending = LoadPending(path, recovery, result);
            string status = RequiredRead(path, "status", "--porcelain=v1", "-z", "--untracked-files=all").Stdout;
            ParseStatus(status, result);
            if (result.Unmerged > 0) throw new IOException("存在未解决冲突，停止同步");

            Log("▶ fetch --all --prune ...（持续等待，每 15 秒报告状态）");
            RunWrite(path, "fetch", "fetch", "--all", "--prune");
            CheckStop();
            string remote = "refs/remotes/origin/" + result.Branch;
            result.Target = RequiredRead(path, "rev-parse", "--verify", remote + "^{commit}").Stdout.Trim();
            Tuple<int, int> div = Divergence(path, result.Target);
            result.Ahead = div.Item1;
            result.Behind = div.Item2;
            result.Head = RequiredRead(path, "rev-parse", "HEAD").Stdout.Trim();
            status = RequiredRead(path, "status", "--porcelain=v1", "-z", "--untracked-files=all").Stdout;
            ParseStatus(status, result);
            if (result.Unmerged > 0) throw new IOException("存在未解决冲突，停止同步");
            if (result.Behind > 0 && result.Ahead == 0) BackupUntracked(path, status, result);
            if (pending != null && result.UntrackedBackupDirectory.Length > 0)
            {
                pending.UntrackedBackupDirectory = result.UntrackedBackupDirectory;
                pending.UntrackedBackedUp = result.UntrackedBackedUp;
                SavePending(recovery, pending);
            }
            if (result.Staged + result.Unstaged > 0 && (pending != null || result.Behind > 0))
                pending = StashTracked(path, recovery, pending, result);
            if (result.Behind == 0)
            {
                result.Merge = result.Ahead == 0 ? "up_to_date" : "ahead_only";
                Log(result.Ahead == 0 ? "• 已是最新，无需合并" : $"• 领先 {result.Ahead} 个提交，无需 pull");
                RestoreStash(path, recovery, pending, result);
                return; // 没有恢复记录时，不折腾用户已有的 stash。
            }
            CheckStop();
            if (result.Ahead == 0)
            {
                Log($"▶ 落后 {result.Behind} 个提交，快进到 {result.Target} ...");
                result.Merge = "fast_forward_failed";
                RunWrite(path, "快进合并", "merge", "--ff-only", result.Target);
                // 必须等完整进程树退出后，再检查 HEAD、固定目标和当前远端，不能只相信父进程退出码。
                if (!VerifyFastForward(path, result))
                    throw new IOException("快进后置校验不一致，保留 stash，请检查远端/本地是否同时被其他操作改变");
                result.Merge = "fast_forward";
            }
            else
            {
                Log($"▶ 分叉（ahead={result.Ahead}, behind={result.Behind}），合并后等待手动提交...");
                result.Merge = "merge_pending";
                RunWrite(path, "合并", "merge", "--no-commit", "--no-ff", result.Target);
                // 正常退出也可能仍有 MERGE_HEAD；提交前不叠加 stash 中的本地改动。
                if (InProgress(path) != null)
                {
                    result.Status = "needs_commit";
                    result.Reason = "合并已准备好，请检查并手动提交后恢复 stash";
                    return;
                }
            }
            CheckStop();
            RestoreStash(path, recovery, pending, result);
            CheckStop();
        }

        // ═══════════════════════════
        //  冲突解决调度
        // ═══════════════════════════

        private List<ConflictResult> ResolveAll(string repoPath, string repoName)
        {
            var results = new List<ConflictResult>();
            foreach (string file in UnmergedFiles(repoPath))
            {
                CheckStop();
                results.Add(ResolveFile(repoPath, file, repoName));
            }
            return results;
        }

        private ConflictResult ResolveFile(string repoPath, string file, string repoName)
        {
            string ext = Path.GetExtension(file) ?? "";
            if (CsvLikeExts.Contains(ext))
                return ResolveCsv(repoPath, file);
            if (CodeExts.Contains(ext))
                return ResolveCode(repoPath, file, repoName);
            return AskChoice(repoPath, file, "未知文件类型，请手动选择",
                new[] { "保留ours（本地）", "保留theirs（远程）", "编辑器手动改", "跳过" }, null);
        }

        // ── CSV / plist：调用外部 csv_conflict_resolver.py（可选配置） ──

        private ConflictResult ResolveCsv(string repoPath, string file)
        {
            string resolver = (_cfg.CsvResolver ?? "").Trim();
            if (resolver.Length == 0 || !File.Exists(resolver))
            {
                return AskChoice(repoPath, file, "CSV resolver 未配置或不存在，请手动选择",
                    new[] { "保留ours（本地）", "保留theirs（远程）", "编辑器手动改", "跳过" }, null);
            }

            CheckStop();
            ProcessResult execution = ProcessRunner.Run("python", ProcessUtil.BuildArguments(resolver, file),
                repoPath, 0, msg => Log("  CSV 解决脚本: " + msg), ForceCancellation);
            if (execution.Cancelled) throw new OperationCanceledException(execution.Stderr);
            if (!execution.Ok) throw new IOException("CSV 解决脚本失败: " + execution.Stderr);
            string stdout = execution.Stdout.Trim(), stderr = execution.Stderr.Trim();

            Dictionary<string, object> parsed = ParseJsonLenient(stdout);
            if (parsed == null)
            {
                string err = stderr.Length > 0 ? Trim200(stderr) : Trim200(stdout);
                return new ConflictResult { File = file, Status = "error", Action = "csv_error", Detail = "输出无法解析: " + err };
            }
            object statusObj, msgObj;
            string status = parsed.TryGetValue("status", out statusObj) && statusObj != null ? statusObj.ToString() : "";
            string message = parsed.TryGetValue("message", out msgObj) && msgObj != null ? msgObj.ToString() : "";
            if (status == "AUTO_MERGED")
            {
                RunWrite(repoPath, "add", "add", "--", file);  // 幂等，csv 脚本可能已 add
                return new ConflictResult { File = file, Status = "resolved", Action = "csv_auto", Detail = message };
            }
            return AskChoice(repoPath, file, $"CSV resolver: {status} - {message}",
                new[] { "保留ours（本地）", "保留theirs（远程）", "编辑器手动改", "跳过" }, null);
        }

        // ── 代码文件：LLM 三方合并 ──

        private ConflictResult ResolveCode(string repoPath, string file, string repoName)
        {
            string baseContent = ShowStage(repoPath, 1, file);
            string ours = ShowStage(repoPath, 2, file);
            string theirs = ShowStage(repoPath, 3, file);

            LlmMergeResult llmResult = _llm?.ResolveConflict(file, baseContent, ours, theirs, repoName);
            if (llmResult == null)
            {
                return AskChoice(repoPath, file, "LLM 不可用或无方案，请手动选择",
                    new[] { "保留ours（本地）", "保留theirs（远程）", "编辑器手动改", "跳过" }, null);
            }

            // 高置信（可选含 medium）且非破坏性 -> 自动应用
            bool confidenceOk = llmResult.Confidence == "high"
                || (_cfg.AutoApplyMedium && llmResult.Confidence == "medium");
            bool destructiveBlocked = llmResult.IsDestructive && _cfg.DestructiveAsk;
            if (confidenceOk && !destructiveBlocked)
            {
                WriteMergedFile(Path.Combine(repoPath, file), llmResult.MergedContent);
                RunWrite(repoPath, "add", "add", "--", file);
                return new ConflictResult
                {
                    File = file,
                    Status = "resolved",
                    Action = "llm_auto",
                    Confidence = llmResult.Confidence,
                    Destructive = llmResult.IsDestructive,
                    Detail = llmResult.Reason,
                };
            }

            // 低置信或破坏性 -> 询问（LLM 对话选项）
            return AskChoice(
                repoPath, file,
                $"LLM 合并方案：confidence={llmResult.Confidence}, destructive={llmResult.IsDestructive}\n理由: {llmResult.Reason}",
                new[] { "采用LLM方案", "保留ours（本地）", "保留theirs（远程）", "编辑器手动改", "跳过" },
                llmResult);
        }

        // ── 询问交互（1-based 选项，含 LLM 方案时第 1 项为采用 LLM） ──

        private ConflictResult AskChoice(string repoPath, string file, string prompt, IList<string> options, LlmMergeResult llmResult)
        {
            string header = $"冲突文件: {file}\n{prompt}";
            int choice = Ask(header, options);
            if (llmResult != null && choice == 1)
            {
                WriteMergedFile(Path.Combine(repoPath, file), llmResult.MergedContent);
                RunWrite(repoPath, "add", "add", "--", file);
                return new ConflictResult { File = file, Status = "resolved", Action = "user_llm", Detail = "采用LLM方案" };
            }
            // 统一映射到 ours/theirs/editor/skip 的偏移
            int off = llmResult != null ? choice - 1 : choice;
            if (off == 1)
            {
                RunWrite(repoPath, "checkout-ours", "checkout", "--ours", "--", file);
                RunWrite(repoPath, "add", "add", "--", file);
                return new ConflictResult { File = file, Status = "resolved", Action = "user_ours" };
            }
            if (off == 2)
            {
                RunWrite(repoPath, "checkout-theirs", "checkout", "--theirs", "--", file);
                RunWrite(repoPath, "add", "add", "--", file);
                return new ConflictResult { File = file, Status = "resolved", Action = "user_theirs" };
            }
            if (off == 3)
            {
                OpenEditorAndWait(repoPath, file);
                return new ConflictResult { File = file, Status = "manual", Action = "editor" };
            }
            return new ConflictResult { File = file, Status = "skipped", Action = "skipped" };
        }

        /// <summary>用记事本打开冲突文件，等用户编辑完成后 git add（或选择跳过）。</summary>
        private void OpenEditorAndWait(string repoPath, string file)
        {
            string full = Path.Combine(repoPath, file);
            try
            {
                Process.Start("notepad.exe", ProcessUtil.QuoteArg(full));
            }
            catch (Exception ex)
            {
                FileLog("编辑器调用失败: " + ex.Message);
            }
            int done = Ask($"已用记事本打开: {full}\n编辑并保存后请选择：",
                new[] { "完成编辑，执行 git add", "跳过此文件" });
            if (done == 1)
                RunWrite(repoPath, "add", "add", "--", file);
        }

        // ═══════════════════════════
        //  工具
        // ═══════════════════════════

        /// <summary>写入 LLM 合并结果，保持原文件的 BOM 与换行风格（移植自 conflict_resolver._write_file）。</summary>
        private void WriteMergedFile(string fullPath, string content)
        {
            CheckStop();
            bool hasBom = false;
            string lineEnding = "\n";
            try
            {
                byte[] raw = File.ReadAllBytes(fullPath);
                if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
                    hasBom = true;
                int probe = Math.Min(raw.Length, 8192);
                for (int i = 0; i + 1 < probe; i++)
                {
                    if (raw[i] == 0x0D && raw[i + 1] == 0x0A)
                    {
                        lineEnding = "\r\n";
                        break;
                    }
                }
            }
            catch (Exception)
            {
                // 文件不存在（新增文件冲突）时按默认 LF 处理
            }

            string normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
            if (lineEnding == "\r\n")
                normalized = normalized.Replace("\n", "\r\n");
            var bytes = new List<byte>(new UTF8Encoding(false).GetBytes(normalized));
            if (hasBom)
                bytes.InsertRange(0, new byte[] { 0xEF, 0xBB, 0xBF });
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            File.WriteAllBytes(fullPath, bytes.ToArray());
        }

        private static Dictionary<string, object> ParseJsonLenient(string output)
        {
            if (string.IsNullOrEmpty(output))
                return null;
            try
            {
                return Json.DeserializeObject(output) as Dictionary<string, object>;
            }
            catch (Exception)
            {
                // 尝试截取最外层 {...}
            }
            int start = output.IndexOf('{');
            int end = output.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                try
                {
                    return Json.DeserializeObject(output.Substring(start, end - start + 1)) as Dictionary<string, object>;
                }
                catch (Exception)
                {
                    return null;
                }
            }
            return null;
        }

        private static string Trim200(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            text = text.Trim();
            return text.Length > 200 ? text.Substring(0, 200) : text;
        }
    }
}
