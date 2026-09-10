# Git 写操作与进程树验证记录

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
