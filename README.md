# TortoiseGit-Monitor

Windows 系统托盘应用，定时检测本地 Git 仓库状态，支持 TortoiseGit 快捷操作。

C# / WinForms 实现，目标框架 .NET Framework 4.8（Windows 10/11 系统自带，无需安装任何运行时），单文件 exe 仅几十 KB。

## 功能

- **自动 fetch** — 按设定的时间间隔自动 `git fetch --prune`，及时感知远程变更
- **状态指示** — 托盘图标用不同颜色标识仓库整体状态（绿/黄/红/灰）
- **右键菜单** — 点击托盘直接查看所有仓库状态，单击打开 TortoiseGit Show Log
- **仓库发现** — 配置扫描目录，自动递归搜索 Git 仓库（最多 3 层）
- **桌面通知** — 发现新提交时弹出 Windows Toast 通知，可选声音提示
- **分支监控** — 支持查看各仓库全部分支的 ahead/behind 状态
- **开机自启** — 设置中一键开启/关闭（注册表 Run 键，仅当前用户）

## 界面

```
  ▲  上翻（20 个）              ← 超过 20 个仓库时分页
  🟢  my-project    (同步)      ← 绿色：完全同步
  🟡  backend       (修改 ↓2)   ← 黄色：有本地未提交修改
  🔴  frontend      (↓5 ↑1)     ← 红色：落后远程
  ⚪  offline-repo  (⚠ err)     ← 灰色：检测异常
  ▼  下翻（15 个）
──────────────────────────
  ⟳  刷新全部
  ⚙  设置...
  ✕  退出
```

### 设置对话框

| 配置项 | 说明 |
|---|---|
| 检测间隔 | 60 ~ 86400 秒（默认 300 秒） |
| 日志条数 | 获取最近 N 条提交记录（默认 10） |
| 弹出通知 | 发现新提交时弹出 Windows Toast |
| 通知声音 | 通知时播放系统提示音 |
| 随 Windows 启动 | 登录后自动运行（注册表 Run 键） |
| TortoiseGit 程序路径 | 指定 TortoiseGitProc.exe，留空自动查找 |
| 扫描目录 | 在这些目录下递归搜索 Git 仓库 |

## 构建

需要 Visual Studio（含 MSBuild）：

```bat
MSBuild TortoiseGitMonitor\TortoiseGitMonitor.csproj /p:Configuration=Release
```

产物：`TortoiseGitMonitor\bin\Release\TortoiseGitMonitor.exe`，可直接拷贝到任意位置运行。

## 依赖

- Windows 10 / 11（自带 .NET Framework 4.8）
- Git（需在 PATH 中）
- [TortoiseGit](https://tortoisegit.org/)（可选，用于 `Show Log` 快捷操作）

## 配置文件

配置保存在 `%USERPROFILE%\.config\tortoisegit_monitor\settings.json`。

首次启动会自动生成默认配置，也可通过托盘菜单 → 设置手动编辑。

## 项目结构

```
tortoisegit-monitor/
└── TortoiseGitMonitor/
    ├── TortoiseGitMonitor.csproj  # 项目文件（.NET Framework 4.8）
    ├── Program.cs                 # 应用入口
    ├── TrayAppContext.cs          # 托盘图标、菜单、定时检测、自启动
    ├── SettingsForm.cs            # 设置对话框
    ├── GitWatcher.cs              # Git 操作（状态检测、并行 fetch）
    ├── AppConfig.cs               # 配置管理（JSON 持久化）
    ├── Notifier.cs                # Windows Toast 通知
    └── ProcessUtil.cs             # 进程/命令行工具
```

## License

MIT
