using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TortoiseGitMonitor
{
    /// <summary>只移动与快进新增路径冲突的未跟踪文件，保留目录结构和全部内容。</summary>
    internal static class UntrackedBackup
    {
        internal static List<string> Find(IEnumerable<string> untracked, IEnumerable<string> added)
        {
            var incoming = new HashSet<string>(added, StringComparer.OrdinalIgnoreCase);
            var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in incoming)
                foreach (string parent in Parents(path)) parents.Add(parent);
            return untracked.Where(path => incoming.Contains(path.TrimEnd('/')) || parents.Contains(path.TrimEnd('/')) ||
                Parents(path).Any(incoming.Contains)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static IEnumerable<string> Parents(string path)
        {
            int slash = path.LastIndexOf('/');
            while (slash > 0)
            {
                yield return path.Substring(0, slash);
                slash = path.LastIndexOf('/', slash - 1);
            }
        }

        internal static string Resolve(string root, string relative)
        {
            if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) ||
                relative.IndexOfAny(new[] { '\\', ':' }) >= 0 ||
                relative.Split('/').Any(p => p.Length == 0 || p == "." || p == ".." || p.Equals(".git", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("无法安全备份路径: " + relative);
            string prefix = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string absolute = Path.GetFullPath(Path.Combine(prefix, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!absolute.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new IOException("备份路径越出工作目录: " + relative);
            return absolute;
        }

        internal static string IoPath(string path)
        {
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
            return full.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + full.Substring(2) : @"\\?\" + full;
        }

        private static void CheckComponents(string root, string path)
        {
            string boundary = Path.GetFullPath(root).TrimEnd('\\', '/');
            string current = path;
            while (!string.Equals(current, boundary, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(current)) throw new IOException("备份路径无有效边界");
                if ((File.Exists(IoPath(current)) || Directory.Exists(IoPath(current))) &&
                    (File.GetAttributes(IoPath(current)) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("路径含符号链接或重解析点，停止自动备份: " + current);
                current = Path.GetDirectoryName(current);
            }
        }

        internal static void Move(string repo, string gitDir, string backup, IList<string> files,
            Action checkStop, Action<string> log, string manifest)
        {
            var paths = files.Select(relative => new
            {
                Relative = relative,
                Source = Resolve(repo, relative),
                Destination = Resolve(Path.Combine(backup, "files"), relative)
            }).ToList();
            // 全部验证后才开始移动；目录/嵌套仓库和链接不会被当作普通文件移走。
            foreach (var path in paths)
            {
                CheckComponents(repo, path.Source);
                if (!File.Exists(IoPath(path.Source)) || Directory.Exists(IoPath(path.Source)))
                    throw new IOException("阻塞路径不是普通文件，停止自动备份: " + path.Source);
                CheckComponents(gitDir, path.Destination);
            }
            checkStop();
            Directory.CreateDirectory(IoPath(backup));
            File.WriteAllText(IoPath(Path.Combine(backup, "manifest.json")), manifest, new System.Text.UTF8Encoding(false));
            var moved = new List<int>();
            try
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    checkStop();
                    var path = paths[i];
                    CheckComponents(repo, path.Source);
                    Directory.CreateDirectory(IoPath(Path.GetDirectoryName(path.Destination)));
                    File.Move(IoPath(path.Source), IoPath(path.Destination));
                    moved.Add(i);
                    log("  已备份未跟踪阻塞文件: " + path.Relative + " -> " + path.Destination);
                }
            }
            catch (Exception ex)
            {
                foreach (int i in moved.AsEnumerable().Reverse())
                {
                    var path = paths[i];
                    try
                    {
                        CheckComponents(repo, path.Source);
                        if (File.Exists(IoPath(path.Source)) || Directory.Exists(IoPath(path.Source)))
                            throw new IOException("原路径已被其他操作创建，保留备份");
                        File.Move(IoPath(path.Destination), IoPath(path.Source));
                    }
                    catch (Exception rollback) { log("  备份保留于 " + path.Destination + ": " + rollback.Message); }
                }
                if (ex is OperationCanceledException) throw;
                throw new IOException("未跟踪文件备份未完成，未继续合并: " + ex.Message + "；备份目录: " + backup, ex);
            }
        }
    }
}
