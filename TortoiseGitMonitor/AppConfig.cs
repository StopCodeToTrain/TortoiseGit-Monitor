/**
 * 配置管理：全局设置、项目列表、JSON 持久化。
 *
 * 与 Python 版共用同一配置文件：
 *   %USERPROFILE%\.config\tortoisegit_monitor\settings.json
 *
 * 数据结构（保持与 Python 版完全一致）:
 *   {
 *     "global": {
 *       "interval_sec": 300,         // 默认检测间隔（秒）
 *       "log_count": 10,             // 提交日志条数
 *       "start_with_windows": false, // 随系统启动
 *       "show_notifications": true,  // 显示弹出通知
 *       "notification_sound": false, // 通知时播放声音
 *       "tortoisegit_path": "",      // TortoiseGitProc.exe 路径，留空自动查找
 *     },
 *     "projects": [
 *       {
 *         "name": "my-project",      // 显示名称
 *         "path": "D:/repo",         // 本地路径
 *         "monitor_all_branches": false,
 *         "interval": 0,             // 0=使用全局默认
 *         "ignore_authors": "",
 *         "regex_filter": "",
 *         "enabled": true,
 *       },
 *     ],
 *     "scan_paths": ["~", "D:/GitProject"],  // 自动扫描目录
 *   }
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace TortoiseGitMonitor
{
    /// <summary>全局设置（对应 JSON 的 "global" 节点）。</summary>
    [DataContract]
    internal sealed class GlobalSettings
    {
        [DataMember(Name = "interval_sec")]
        public int IntervalSec;

        [DataMember(Name = "log_count")]
        public int LogCount;

        [DataMember(Name = "start_with_windows")]
        public bool StartWithWindows;

        [DataMember(Name = "show_notifications")]
        public bool ShowNotifications;

        [DataMember(Name = "notification_sound")]
        public bool NotificationSound;

        [DataMember(Name = "tortoisegit_path")]
        public string TortoiseGitPath = "";

        /// <summary>反序列化前填充默认值（缺失的键回退到默认值）。</summary>
        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            IntervalSec = 300;
            LogCount = 10;
            StartWithWindows = false;
            ShowNotifications = true;
            NotificationSound = false;
            TortoiseGitPath = "";
        }
    }

    /// <summary>单个监控项目（对应 JSON "projects" 数组元素）。</summary>
    [DataContract]
    internal sealed class ProjectEntry
    {
        [DataMember(Name = "name")]
        public string Name = "";

        [DataMember(Name = "path")]
        public string Path = "";

        [DataMember(Name = "monitor_all_branches")]
        public bool MonitorAllBranches;

        [DataMember(Name = "interval")]
        public int Interval;

        [DataMember(Name = "ignore_authors")]
        public string IgnoreAuthors = "";

        [DataMember(Name = "regex_filter")]
        public string RegexFilter = "";

        [DataMember(Name = "enabled")]
        public bool Enabled;

        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            Name = "";
            Path = "";
            MonitorAllBranches = false;
            Interval = 0;
            IgnoreAuthors = "";
            RegexFilter = "";
            Enabled = true;
        }
    }

    /// <summary>配置文件根节点。</summary>
    [DataContract]
    internal sealed class AppConfig
    {
        private const string AppDirName = "tortoisegit_monitor";
        private const string LegacyDirName = "git_monitor";

        [DataMember(Name = "global")]
        public GlobalSettings Global = new GlobalSettings();

        [DataMember(Name = "projects")]
        public List<ProjectEntry> Projects = new List<ProjectEntry>();

        [DataMember(Name = "scan_paths")]
        public List<string> ScanPaths = new List<string>();

        /// <summary>配置目录：%USERPROFILE%\.config\tortoisegit_monitor</summary>
        public static string ConfigDir =>
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config", AppDirName);

        /// <summary>配置文件完整路径。</summary>
        public static string ConfigFile => System.IO.Path.Combine(ConfigDir, "settings.json");

        private static string HomeDir =>
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        /// <summary>默认扫描目录：用户主目录。</summary>
        private static List<string> DefaultScanPaths() => new List<string> { HomeDir };

        /// <summary>生成一份全新默认配置。</summary>
        public static AppConfig Defaults()
        {
            return new AppConfig
            {
                Global = new GlobalSettings
                {
                    IntervalSec = 300,
                    LogCount = 10,
                    StartWithWindows = false,
                    ShowNotifications = true,
                    NotificationSound = false,
                    TortoiseGitPath = "",
                },
                Projects = new List<ProjectEntry>(),
                ScanPaths = DefaultScanPaths(),
            };
        }

        /// <summary>从旧目录名 git_monitor 迁移配置到 tortoisegit_monitor。</summary>
        private static void MigrateOldConfigDir()
        {
            string oldDir = System.IO.Path.Combine(HomeDir, ".config", LegacyDirName);
            try
            {
                if (Directory.Exists(oldDir) && !Directory.Exists(ConfigDir))
                    CopyDirectory(oldDir, ConfigDir);
            }
            catch (Exception)
            {
                // 迁移失败不应影响主流程
            }
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source))
                File.Copy(file, System.IO.Path.Combine(target, System.IO.Path.GetFileName(file)), overwrite: true);
            foreach (string dir in Directory.GetDirectories(source))
                CopyDirectory(dir, System.IO.Path.Combine(target, System.IO.Path.GetFileName(dir)));
        }

        /// <summary>加载配置；文件缺失/损坏时返回默认值，并兼容旧版 flat 格式。</summary>
        public static AppConfig Load()
        {
            Directory.CreateDirectory(ConfigDir);
            MigrateOldConfigDir();

            AppConfig data = null;
            try
            {
                if (File.Exists(ConfigFile))
                {
                    byte[] raw = File.ReadAllBytes(ConfigFile);
                    data = Deserialize<AppConfig>(raw);
                    // 旧格式没有 "global" 节点，按 flat 结构迁移
                    if (data == null || data.Global == null)
                        data = MigrateLegacyFlat(raw);
                }
            }
            catch (Exception)
            {
                data = null;
            }

            if (data == null)
                return Defaults();

            // 补全缺失字段
            if (data.Global == null)
                data.Global = Defaults().Global;
            if (data.Projects == null)
                data.Projects = new List<ProjectEntry>();
            if (data.ScanPaths == null)
                data.ScanPaths = DefaultScanPaths();
            foreach (ProjectEntry p in data.Projects)
            {
                if (string.IsNullOrEmpty(p.Name))
                    p.Name = p.Path ?? "";
            }
            return data;
        }

        /// <summary>保存配置（UTF-8 无 BOM，缩进格式，与 Python 版输出一致）。</summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                SaveTo(ConfigFile);
            }
            catch (Exception)
            {
                // 配置写入失败不应导致程序崩溃
            }
        }

        /// <summary>将配置序列化到指定文件（UTF-8 无 BOM，缩进格式）。</summary>
        public void SaveTo(string path)
        {
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(AppConfig));
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var writer = JsonReaderWriterFactory.CreateJsonWriter(fs, new UTF8Encoding(false), ownsStream: false, indent: true))
            {
                serializer.WriteObject(writer, this);
            }
        }

        private static T Deserialize<T>(byte[] raw) where T : class
        {
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
            using (var ms = new MemoryStream(raw))
                return serializer.ReadObject(ms) as T;
        }

        /// <summary>旧版 flat 配置格式的 DTO（仅用于迁移）。</summary>
        [DataContract]
        private sealed class LegacyFlatConfig
        {
            [DataMember(Name = "interval_sec")] public int IntervalSec = 300;
            [DataMember(Name = "log_count")] public int LogCount = 10;
            [DataMember(Name = "start_with_windows")] public bool StartWithWindows;
            [DataMember(Name = "show_notifications")] public bool ShowNotifications = true;
            [DataMember(Name = "notification_sound")] public bool NotificationSound;
            [DataMember(Name = "tortoisegit_path")] public string TortoiseGitPath = "";
            [DataMember(Name = "monitor_all_branches")] public bool MonitorAllBranches;
            [DataMember(Name = "scan_paths")] public List<string> ScanPaths;
            [DataMember(Name = "extra_paths")] public List<string> ExtraPaths;

            [OnDeserializing]
            private void OnDeserializing(StreamingContext context)
            {
                IntervalSec = 300;
                LogCount = 10;
                StartWithWindows = false;
                ShowNotifications = true;
                NotificationSound = false;
                TortoiseGitPath = "";
                MonitorAllBranches = false;
                ScanPaths = null;
                ExtraPaths = null;
            }
        }

        /// <summary>将旧版 flat 配置迁移到 projects + global 结构。</summary>
        private static AppConfig MigrateLegacyFlat(byte[] raw)
        {
            LegacyFlatConfig legacy;
            try
            {
                legacy = Deserialize<LegacyFlatConfig>(raw);
            }
            catch (Exception)
            {
                return Defaults();
            }
            if (legacy == null)
                return Defaults();

            AppConfig migrated = Defaults();
            migrated.Global.IntervalSec = legacy.IntervalSec;
            migrated.Global.LogCount = legacy.LogCount;
            migrated.Global.StartWithWindows = legacy.StartWithWindows;
            migrated.Global.ShowNotifications = legacy.ShowNotifications;
            migrated.Global.NotificationSound = legacy.NotificationSound;
            migrated.Global.TortoiseGitPath = legacy.TortoiseGitPath ?? "";
            if (legacy.ScanPaths != null)
                migrated.ScanPaths = legacy.ScanPaths;

            if (legacy.ExtraPaths != null)
            {
                foreach (string path in legacy.ExtraPaths)
                {
                    migrated.Projects.Add(new ProjectEntry
                    {
                        Name = System.IO.Path.GetFileName((path ?? "").TrimEnd('\\', '/')),
                        Path = path,
                        MonitorAllBranches = legacy.MonitorAllBranches,
                        Interval = 0,
                        IgnoreAuthors = "",
                        RegexFilter = "",
                        Enabled = true,
                    });
                }
            }
            return migrated;
        }

        // ── 仓库路径解析 ──

        /// <summary>展开路径开头的 "~" 为用户主目录，并转为绝对路径。</summary>
        private static string ExpandAndResolve(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            string path = raw.Trim();
            if (path == "~")
                path = HomeDir;
            else if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
                path = HomeDir + path.Substring(1);
            try
            {
                return System.IO.Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>返回所有需要监控的 git 仓库路径（扫描目录自动发现 + 项目列表）。</summary>
        public static List<string> ResolveRepoPaths(AppConfig data)
        {
            var extra = new List<string>();
            foreach (ProjectEntry proj in data.Projects)
            {
                if (proj == null || !proj.Enabled)
                    continue;
                string path = ExpandAndResolve(proj.Path);
                if (path != null)
                    extra.Add(path);
            }

            var scan = new List<string>();
            foreach (string raw in data.ScanPaths ?? DefaultScanPaths())
            {
                string path = ExpandAndResolve(raw);
                if (path != null)
                    scan.Add(path);
            }

            return Discover(scan, extra);
        }

        /// <summary>扫描时需要跳过的大型/无关目录。</summary>
        private static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.Ordinal)
        {
            ".git", "node_modules", ".venv", "venv", "__pycache__",
            "AppData", "Library", "Application Data",
        };

        /// <summary>从扫描目录发现 git 仓库（最多 3 层），合并额外路径，去重排序。</summary>
        private static List<string> Discover(List<string> scanPaths, List<string> extraPaths, int maxDepth = 3)
        {
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string basePath in scanPaths)
            {
                if (!Directory.Exists(basePath))
                    continue;
                Walk(new DirectoryInfo(basePath), 0, maxDepth, found);
            }

            foreach (string p in extraPaths)
            {
                // 额外路径允许 .git 为文件（worktree/submodule）
                string dotGit = System.IO.Path.Combine(p, ".git");
                if (Directory.Exists(dotGit) || File.Exists(dotGit))
                    found[p] = p;
            }

            var result = new List<string>(found.Values);
            result.Sort((a, b) => string.Compare(
                System.IO.Path.GetFileName(a.TrimEnd('\\', '/')),
                System.IO.Path.GetFileName(b.TrimEnd('\\', '/')),
                StringComparison.OrdinalIgnoreCase));
            return result;
        }

        private static void Walk(DirectoryInfo dir, int depth, int maxDepth, IDictionary<string, string> found)
        {
            if (depth >= maxDepth)
                return;
            try
            {
                // 当前目录是 git 仓库则记录，不再深入其内部
                if (Directory.Exists(System.IO.Path.Combine(dir.FullName, ".git")))
                {
                    found[dir.FullName] = dir.FullName;
                    return;
                }
                foreach (DirectoryInfo sub in dir.EnumerateDirectories())
                {
                    if (SkipDirs.Contains(sub.Name))
                        continue;
                    Walk(sub, depth + 1, maxDepth, found);
                }
            }
            catch (Exception)
            {
                // 无权限 / IO 错误的目录直接跳过
            }
        }
    }
}
