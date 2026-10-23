# Git 写操作、锁策略与进程树验证记录

## 2026-10-23：v1.0.1 修复新 TortoiseGit 窗口误阻塞旧锁

Release 和回归程序重新构建成功，无警告、无错误。`RegressionTests.exe --lock-regression-only` **19 组全部通过**（退出码 0），`git diff --check` 通过。此次针对锁判断修改验证相关路径，未重跑与该改动无关的多批 stash 全量用例；v1.0.0 的完整 40 组结果保留如下。

- 进程时间判断覆盖：TortoiseGit 明确晚于锁创建及修改时间、早于任一时间、创建时间旧但写入时间新，以及 1 秒余量。
- 新建名为 `TortoiseGitProc.exe` 的隔离 idle 测试进程，确认它仍运行时可以清理其启动前的稳定旧锁，无需终止进程；同样时间关系下，实际文件句柄占用仍阻止删除。
- 原有新锁、变动/替换文件、Git 进程逻辑锁、取消、后台不清锁、跨进程互斥保护继续通过。
- 真实隔离仓库在清锁后完成快进并恢复 stash，staged/unstaged/untracked 状态正确；进程树超时/取消和窗口显示检查也通过。

同名测试进程不执行实际 TortoiseGit 操作。旧锁测试文件设为早于当前 GUI 的固定历史时间，避免测试要求关闭用户已打开的日志窗口。所有测试写入均限于 obj 下的新建隔离目录。

## 2026-10-21：v1.0.0 发布验证

正式发布使用的 Release 和回归程序均重新构建成功，无警告、无错误。完整回归 **40 组全部通过**（退出码 0）。环境为 Windows 11 专业版 x64，Git for Windows 2.55.0.windows.5；目标框架 .NET Framework 4.8，已安装运行时注册表版本 4.8.10008（Release 534296）。

本次完整回归覆盖真实 Git 快进与分叉、进程树等待和停止、失效锁清理及活动锁保护、跨进程互斥、未跟踪文件备份/路径类型变化/回滚、多批 stash 恢复/原始字节与暂存状态、失败后停止、冲突和窗口显示恢复。测试仓库与审计日志均位于被忽略的 `TortoiseGitMonitor/obj/RegressionTests/` 中。

截图使用正式 Release 二进制中的真实设置和同步窗口，通过独立演示程序载入；设置在显示前替换为示例值且不保存。同步仅操作新建的隔离演示仓库：自动备份 1 个同名未跟踪文件，快进 1 个提交，stash 恢复成功；实际 `git status` 确认 staged/unstaged/untracked 各 1、冲突为 0，`HEAD...origin/main` 的 ahead/behind 均为 0，备份原文逐字节一致。具体过程见安装说明中的界面截图。

验证没有覆盖所有 Windows/Git 版本，也未对真实外网凭据交互进行穷举。发行包、SHA-256 与使用说明随 [v1.0.0 Release](https://github.com/StopCodeToTrain/TortoiseGit-Monitor/releases/tag/v1.0.0) 提供。

## 2026-10-14：旧 stash 未恢复时自动保存新增改动

Release 与回归程序构建成功；完整回归 **40 组全部通过**，`git diff --check` 通过。完整通过的测试目录：`TortoiseGitMonitor/obj/RegressionTests/monitor-regression-e3dc10e7934e4b1ea3f5a8bb0d832955/`。

此前恢复记录只支持一份 stash，重试时出现新的 tracked 改动就停止。现在保留旧记录，将新增改动单独 stash 并追加准确提交、唯一标记和原 HEAD；兼容缺少 `AdditionalStashes` 字段的旧记录。fetch 完成后及恢复前都检查新增改动，HEAD 已到目标时也会接续恢复。

多批按旧到新恢复。第一批使用固定提交执行 `stash apply --index`；后续批次由 Git 直接输出工作区和暂存区的原始字节补丁，分别预检和应用，避免连续 `stash apply --index` 重置前一批的暂存状态。全部恢复成功前保留每份原始 stash，之后仅清理记录中的 stash；冲突、失败或中断后不重复应用已部分恢复的内容。

新增六组隔离仓库验证覆盖：

- 上次失败后新增 4 项 tracked 改动；旧格式记录和 HEAD 已快进两种场景；新增文件同时 staged/unstaged。
- 连续失败、重启和三批改动恢复；普通用户 stash 保留。
- 两批改动重叠、第二批注入失败，以及第一批实际恢复后包装进程返回失败；所有原始 stash 保留，重试不继续写入。
- fetch 失败时新改动原位保留，之后重试正确恢复两批。
- fetch 和快进期间晚出现的新改动在恢复前自动保存。
- 二进制、非 UTF-8 文本、CRLF 和同一文件独立暂存内容逐字节保留；成功后清理本次临时补丁。

原生 Git 在较长用例目录下把完整 revision 临时当作路径探测，触发 `Filename too long`；已缩短相关隔离用例目录。测试写入均限于新建的 obj 仓库，不操作业务仓库。

此前还完成了本机升级验证：等待仓库互斥量并确认没有命令子进程后，成对替换 exe 与运行配置并校验 SHA-256；旧文件另行备份。

## 2026-10-14：未跟踪文件自动备份与同步续处理

Release 和回归程序构建成功。未跟踪文件与恢复策略专项 **12 组通过**；完整回归 **34 组全部通过**，`git diff --check` 通过。

完整通过的测试目录：`TortoiseGitMonitor/obj/RegressionTests/monitor-regression-0fc608a942df4d25a7db4b7495d11e8e/`。

- 使用真实 Git 仓库模拟两份本地未跟踪文档与远端新增文件同名，自动备份后快进成功；本地原文、CRLF/UTF-8 与目录结构完整保留，工作区使用远端文件，其他未跟踪文件保留，原 staged/unstaged 状态正确恢复。
- 中文、空格、方括号路径与超过 260 字符的备份路径通过；使用运行时配置及 Windows 扩展路径，不修改系统长路径注册表。
- 未跟踪文件阻塞远端目录、未跟踪目录内容阻塞远端文件的情况都通过逐文件备份后完成快进。
- 故意独占第二份阻塞文件导致移动失败，已移动的第一份文件自动还原；未执行 stash/merge，tracked 改动保留。越界、ADS 和 Git 元数据路径被拒绝。
- 新实例根据持久化记录续处理失败的同步，不重复创建 stash；HEAD 已到目标时仍正确恢复记录的 stash，并保留前次备份位置。
- stash 栈变动、新 tracked 改动、此前已经启动但未确认完成的 pop 均停止自动恢复；普通用户 stash 不自动恢复。fetch 期间新增 tracked 改动也会在恢复前再次检测。
- 既有长快进、锁等待与直接清理、跨进程互斥、取消、失败停止、真实 merge 冲突及分支切换测试继续通过。

仅复测新策略：在回归程序后追加 `--untracked-only`。

## 2026-10-14：直接清理失效 index.lock

Release 和回归程序构建成功，无警告、无错误。完整回归 **29 组全部通过**，覆盖此前的进程树、真实快进、stash、失败停止、冲突及摘要验证，并新增以下锁策略验证：

- 短暂锁占用等待释放后继续；后台检测立即跳过，不清理锁。
- 锁的创建和修改时间均超过 2 分钟，连续观察至少 2 秒且文件身份、大小和时间保持不变，无 Git/TortoiseGitProc 活动并可独占访问时，直接删除，不备份、不弹窗。
- 观察锁时仅共享读取属性，避免反复独占探测影响外部 Git；删除时使用独占句柄复核文件身份并删除同一文件。
- 新锁、仍被使用的旧锁、观察期间修改的锁以及大小和时间相同但文件身份改变的锁均不删除。
- Git 进程关闭文件句柄后仍运行时，不误判逻辑锁失效；短暂进程活动在 30 秒等待窗口内继续检查。
- 锁等待响应安全停止和强制停止；失败后的写命令仍不重试、不继续 pop stash。
- 按 Git 公共目录进行进程内和同一登录会话的跨进程互斥；仓库子目录及 worktree 共用互斥键，释放后其他进程可进入。
- 真实同步清理旧锁后完成快进，恢复原有 staged、unstaged、untracked 改动及暂存状态。

完整通过的测试目录：`TortoiseGitMonitor/obj/RegressionTests/monitor-regression-2ba3d8b5c8214025b395bb25a3625c63/`。测试审计日志也隔离在该目录，不追加真实用户日志。

首次隔离仓库 clone 受 Windows 沙箱的 MSYS 对象权限限制，完整 Git 测试改在授权环境中运行。外部 Git 进程曾触发清理保护；调整为在等待窗口内继续检查，并让清理成功用例使用正式 30 秒预算后，完整回归通过。

仅复测锁策略与旧锁真实同步：在回归程序后追加 `--lock-policy-only`。

## 2026-09-10：进程树与写入失败停止

日期：2026-09-10。环境：Windows，Visual Studio MSBuild，.NET Framework 4.8，Git for Windows。

## 根因与修复

旧实现只等待 Git 包装进程，60 秒后仅调用 `Process.Kill()`；实际写入子进程可能继续持有 index.lock。同步流程未统一检查写命令结果，失败后仍可能检查冲突、恢复 stash。状态摘要还把全部 dirty 状态描述成已暂存。

新增 Windows Job 进程树执行器，在创建进程时通过 JOB_LIST 原子加入 Job，只继承三条标准流句柄；同时读取 stdout/stderr，等待整个 Job 无活动进程。需要终止时处理整个 Job 并等待退出。同步和分支切换写命令不设硬超时，每 15 秒报告运行时间；后台检测与脚本仍保留各自的超时预算，统一清理进程树。

写操作前后检查 worktree 对应的 index.lock；对现有锁尝试独占打开区分可打开和占用/未知状态，不删除锁。失败或取消立即停止仓库后续写入。快进后读 HEAD、固定目标、当前远端和 ahead/behind，命令异常也会只读确认实际结果。stash 使用唯一标记和提交 ID，恢复前确认栈顶，成功时通过 `pop --index` 恢复原暂存状态。分叉合并等待人工提交。

进程管理依据：[Microsoft Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects)、[创建进程时加入 Job](https://devblogs.microsoft.com/oldnewthing/20230209-00/?p=107812)。

## 验证结果

Release 构建成功，无警告、无错误。`git diff --check` 通过。

独立回归程序 19 组检查全部通过：

- 父进程先退出时仍等待持锁子进程；stdout/stderr 各 100 KB 输出正常排空。
- 超时与强制取消均终止整个子树；返回前子进程已退出，锁可独占打开，锁文件保留。
- XY 状态、同时 staged/unstaged、untracked、重命名中带换行的源路径、未合并文件计数正确。
- 实际 WinForms 摘要输出 staged=0 时不宣称已 git add。
- 包装程序延迟 65 秒后执行真实快进：15/30/45/60 秒报告状态，最终 HEAD 等于远端，ahead/behind 为 0，原有 staged/unstaged/untracked 各 1 正确恢复。
- 分别注入 fetch、stash push、merge、stash pop 失败，命令轨迹确认失败后无后续写入，适用时 stash 保留。
- 外部持有 index.lock 时不启动写入、不删锁。
- 安全停止等待快进完成并校验后停止，stash 保留，不执行 pop。
- 同步中强制停止持锁子树，进程退出、残留锁保留、提示恢复、stash 不弹出。
- 分叉合并不自动创建提交，保留 MERGE_HEAD 和 stash。
- 包装进程错误返回成功但 HEAD 未更新，后置校验判失败。
- 真实快进成功但包装进程返回失败，报告实际快进完成，同时停止后续写入、保留 stash。
- 真实 merge 冲突和注入 abort 失败，均停止后续写入、保留冲突与 stash。
- 同仓库同步、后台检测、分支切换互斥。
- 分支切换 fetch 失败后不继续 stash/checkout/pop。

最后调整摘要中的未合并文件名单后，重新构建 Release，并单独复跑状态解析与 WinForms 摘要检查通过。

首次测试使用 Windows 短路径临时目录，沙箱拒绝子进程访问并触发测试程序错误弹窗；已将测试数据改为项目内被忽略的 obj 目录，并捕获测试子进程异常。该问题未发生在产品 Git 同步流程中。

## 范围与限制

所有验证写入均限于测试程序新建的隔离仓库。未在 snail_client 或其他用户业务仓库执行 merge/stash，也未替换正在运行的托盘程序。验证包括实际 Windows 进程树、Git 仓库和 WinForms 摘要，不包含手工点击完整托盘流程或真实外网长时间 fetch。强制终止不能保证事务回滚，因此现场和残留锁保留给用户检查。

完整复现：

```bat
MSBuild TortoiseGitMonitor\TortoiseGitMonitor.csproj /p:Configuration=Release
MSBuild tests\RegressionTests.csproj /p:Configuration=Release
TortoiseGitMonitor\obj\RegressionTests\RegressionTests.exe
```

仅复测摘要：在最后一条命令后追加 `--summary-only`。
