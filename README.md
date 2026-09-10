# TortoiseGit-Monitor

Windows 系统托盘应用，定时检测本地 Git 仓库状态，支持 TortoiseGit 快捷操作。

C# / WinForms 实现，目标框架 .NET Framework 4.8（Windows 10/11 系统自带，无需安装任何运行时），单文件 exe 仅几十 KB。

## 功能

- **自动 fetch** — 按设定的时间间隔自动 `git fetch --prune`，及时感知远程变更
- **一键 Pull 更新** — 托盘菜单一键同步所有仓库：自动 stash 本地改动 → merge → 校验 → pop stash；写入失败立即停止并保留现场，不自动 commit/push
- **全部切换分支** — 将所有仓库批量切换到指定分支：本地有直接切换，没有则基于 origin 同名分支创建并跟踪；脏工作区可 stash 后切换再恢复
- **冲突保留** — merge 或 stash pop 产生冲突时立即停止后续写入，保留现场供人工检查；本次同步不继续调用冲突解决器
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
  ⇣  一键 Pull 更新
  ⑂  全部切换分支...
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
| 后置脚本 | 全部切换分支完成后依次执行的脚本列表（如 dbfs 同步），留空不执行 |
| 扫描目录 | 在这些目录下递归搜索 Git 仓库 |
| LLM 服务商 | anthropic / openai（一键 Pull 更新的冲突合并用） |
| LLM API Key | 留空则读环境变量 `GIT_SYNC_LLM_KEY` |
| LLM Base URL | 可选，代理 / 自部署 / OpenAI 兼容服务（如 DeepSeek） |
| LLM 模型 | 模型名（如 claude-sonnet-5、gpt-4o） |
| LLM 超时 | 单次请求超时秒数（默认 60） |
| CSV 解决脚本 | 可选，csv_conflict_resolver.py 路径，用于 CSV/plist 冲突三方合并 |

### 一键 Pull 更新

移植自 git-sync 的同步流程（每仓库独立，单仓库失败不影响其他；取消停止全部）：

1. **预检** — 检测到 merge/rebase 进行中状态时先弹窗询问，绝不贸然操作
2. **fetch** — `git fetch --all --prune`
3. **stash** — 有本地改动时自动 stash（仅 tracked 文件）
4. **合并** — 已最新/仅领先则不产生 stash；落后则快进到本次 fetch 后记录的目标提交；分叉合并使用 `--no-commit --no-ff`，等待手动提交
5. **校验与失败处理** — 等整个 Git 进程树退出，校验 HEAD、固定目标、当前远端与 ahead/behind；任何写操作失败、取消或冲突都立即停止该仓库后续写入，不再自动解决冲突或 pop stash。命令异常但目标实际已到达时会明确说明，仍停止后续写入
6. **pop stash** — 仅在合并和校验成功、没有进行中状态时恢复本次 stash；用 `--index` 恢复原有暂存状态，stash 栈被其他操作改变时停止。pop 失败保留现场并提示恢复
7. **摘要** — **不自动 commit、不 push**；分别显示 staged、unstaged、untracked 和未合并文件数，检查失败显示状态未知

同步和分支切换的 Git 写命令持续等待，每 15 秒显示运行时间，不再按 60 秒误判失败。一键 Pull 更新窗口提供“安全停止”（等当前命令完成后停止）和“强制停止”（二次确认后终止整个进程树并等待退出）。关闭运行中的同步窗口会请求安全停止；任务结束前不能从托盘退出应用。

所有受超时控制的外部命令使用 Windows Job Object 管理完整进程树，进程创建时即加入 Job。后台 fetch 保留 30 秒预算，超时后终止整个进程树并确认退出；只读 Git 调用禁用可选写锁。写操作前后检查 `git rev-parse --git-path index.lock`（支持 worktree），发现活动锁或残留锁就停止，**绝不自动删除锁**。同一仓库的后台检测、同步和分支切换互斥。

停止后先检查 `git status`、`git stash list` 和摘要中的 stash 提交。可能存在部分工作区改动；确认没有其他 Git 操作、处理冲突/合并状态及残留锁后，再选择对应 stash 恢复，不要盲目重复 pop。

所有 git 写操作记录到 `%USERPROFILE%\.config\tortoisegit_monitor\git_sync.log`，可审计。

### 全部切换分支

将监控目录下的所有仓库批量切换到指定分支（每仓库独立，单仓库失败不影响其他）：

1. **预检** — merge/rebase 进行中状态先弹窗询问
2. **fetch** — `git fetch --all --prune`，确保远程分支信息最新
3. **脏工作区** — 有未提交改动时弹窗询问，可选 stash 后切换再恢复
4. **切换** — 本地有该分支直接 `checkout`；没有则基于 `origin/<branch>` 创建并跟踪；两边都没有则标记失败跳过。若 checkout 被未跟踪文件阻塞（这些文件在目标分支中已被跟踪，stash 管不到），弹窗列出阻塞文件，可选择备份到 `.git/branch-switch-backup/` 后移除并重试
5. **恢复** — 仅切换成功后 pop；失败或跳过保留 stash（冲突不自动处理，提示手动解决）
6. **后置脚本** — 全部切换完成后依次执行设置中配置的脚本列表（如 dbfs 同步、清理编译产物的 `.bat`），完成后输出显示在进度窗口，每 15 秒报告运行时间；仅全部仓库成功切换时执行；脚本经 `cmd /c` 运行，stdin 闭合避免 `pause` 阻塞

写操作记录到 `%USERPROFILE%\.config\tortoisegit_monitor\branch_switch.log`。

## 构建

需要 Visual Studio（含 MSBuild）：

```bat
MSBuild TortoiseGitMonitor\TortoiseGitMonitor.csproj /p:Configuration=Release
```

产物：`TortoiseGitMonitor\bin\Release\TortoiseGitMonitor.exe`，可直接拷贝到任意位置运行。

## 验证

无需第三方测试框架，验证程序只在 `TortoiseGitMonitor/obj/RegressionTests/` 下新建隔离仓库，测试产物不入库：

```bat
MSBuild tests\RegressionTests.csproj /p:Configuration=Release
TortoiseGitMonitor\obj\RegressionTests\RegressionTests.exe
```

覆盖长快进、进程树超时/取消、锁占用、失败后停止、stash 恢复、后置校验、摘要与分支切换。执行结果见 [验证记录](tests/VERIFICATION.md)。

## 依赖

- Windows 10 / 11（自带 .NET Framework 4.8）
- Git（需在 PATH 中）
- [TortoiseGit](https://tortoisegit.org/)（可选，用于 `Show Log` 快捷操作）
- Python（可选，仅在配置了 CSV 冲突解决脚本时需要）

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
    ├── ProcessRunner.cs          # Windows Job 进程树执行器
    ├── GitProcess.cs             # Git 锁检查与仓库操作互斥
    ├── GitSyncer.cs               # 一键 Pull 更新（stash/merge/冲突解决，移植自 git-sync）
    ├── BranchSwitchForm.cs        # 全部切换分支（引擎 + 分支输入框 + 进度窗口）
    ├── LlmClient.cs               # LLM 调用（OpenAI / Anthropic 冲突合并）
    ├── SyncForm.cs                # 同步进度窗口 + 选项对话框
    ├── AppConfig.cs               # 配置管理（JSON 持久化）
    ├── Notifier.cs                # Windows Toast 通知
    └── ProcessUtil.cs             # 进程/命令行工具
```

## License

MIT
