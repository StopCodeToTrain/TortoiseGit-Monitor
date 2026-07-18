"""
Git 操作封装：状态检测、日志获取、diff 查看。
通过 subprocess 调用 git 命令，避免额外依赖。

并发模型：concurrent.futures.ThreadPoolExecutor 并行检测多个仓库。
节流策略：同一仓库 2 分钟内不重复 fetch，减少网络开销。
"""

from __future__ import annotations

import subprocess
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass, field
from pathlib import Path


GIT_BIN = "git"
FETCH_TIMEOUT = 30        # 单次 fetch 超时（秒）
FETCH_COOLDOWN_SEC = 120  # 同一仓库两次 fetch 最小间隔（秒）
MAX_CONCURRENT = 6        # 最大并行检测数


# ═══════════════════════════
#  数据结构
# ═══════════════════════════

@dataclass
class GitStatus:
    """单个仓库的 git 状态快照。"""

    repo_path: Path
    repo_name: str = ""
    branch: str = ""
    has_remote: bool = False

    # 本地状态
    dirty: bool = False
    staged: int = 0
    unstaged: int = 0
    untracked: int = 0

    # 远程状态
    behind: int = 0
    ahead: int = 0
    fetch_error: str = ""

    # 最近提交
    recent_commits: list[str] = field(default_factory=list)
    diff_stat: str = ""

    # 全部分支（仅在 monitor_all_branches 开启时填充）
    branches: list[BranchStatus] = field(default_factory=list)

    # 时间
    checked_at: float = 0.0
    error: str = ""


@dataclass
class RepoScheduleState:
    """每个仓库的调度状态（不对外暴露），用于节流控制。"""
    repo_path: Path
    last_fetch_time: float = 0.0


@dataclass
class BranchStatus:
    """单个分支的状态。"""
    name: str
    is_current: bool = False
    tracking: str = ""       # 跟踪的远程分支，如 "origin/main"
    ahead: int = 0
    behind: int = 0
    last_hash: str = ""      # 最新提交的短 hash
    last_message: str = ""   # 最新提交消息


@dataclass
class ChangedFile:
    """单个文件的变更信息。"""
    path: str
    added: int = 0       # 新增行数
    deleted: int = 0     # 删除行数
    status: str = ""     # A=新增, M=修改, D=删除, R=重命名


@dataclass
class CommitDetail:
    """单条提交的详细信息。"""
    hash: str
    short_hash: str
    author: str
    date: str
    message: str
    changed_files: list[ChangedFile] = field(default_factory=list)


# ═══════════════════════════
#  底层 git 调用
# ═══════════════════════════

def _run(cwd: Path, args: list[str], timeout: int = 10) -> tuple[str, str, int]:
    """执行 git 命令，返回 (stdout, stderr, returncode)。"""
    try:
        p = subprocess.run(
            [GIT_BIN] + args,
            cwd=str(cwd),
            capture_output=True,
            text=True,
            timeout=timeout,
            encoding="utf-8",
            errors="replace",
            creationflags=subprocess.CREATE_NO_WINDOW if hasattr(subprocess, "CREATE_NO_WINDOW") else 0,
        )
        return p.stdout.strip(), p.stderr.strip(), p.returncode
    except FileNotFoundError:
        return "", "git 未找到，请确认 git 已安装且在 PATH 中", 1
    except subprocess.TimeoutExpired:
        return "", "操作超时", 1
    except Exception as e:
        return "", str(e), 1


def _check_version(cwd: Path) -> bool:
    """检查是否为有效的 git 仓库且 git 可用。"""
    _, _, rc = _run(cwd, ["rev-parse", "--is-inside-work-tree"])
    return rc == 0


# ═══════════════════════════
#  单仓库检测
# ═══════════════════════════

def check_repo(
    repo_path: Path,
    log_count: int = 10,
    schedule: RepoScheduleState | None = None,
    fetch_cooldown: int = FETCH_COOLDOWN_SEC,
    check_branches_flag: bool = False,
) -> GitStatus:
    """检测单个仓库的完整状态。

    Args:
        repo_path: 仓库路径
        log_count: 获取最近 N 条提交
        schedule: 调度状态（用于 fetch 节流）
        fetch_cooldown: 两次 fetch 的最小间隔（秒）
    """
    status = GitStatus(repo_path=repo_path)
    status.repo_name = repo_path.name
    status.checked_at = time.time()

    # ── 基础检查 ──
    if not _check_version(repo_path):
        status.error = "不是有效的 git 仓库"
        return status

    # ── 分支名 ──
    out, _, _ = _run(repo_path, ["rev-parse", "--abbrev-ref", "HEAD"])
    status.branch = out
    is_detached = (out == "HEAD")

    # ── fetch 远程（带节流）──
    remote_out, _, _ = _run(repo_path, ["remote"], timeout=5)
    remotes = [r for r in remote_out.split("\n") if r.strip()]
    status.has_remote = len(remotes) > 0

    should_fetch = False
    if status.has_remote and not is_detached:
        if schedule is None:
            should_fetch = True
        else:
            elapsed = time.time() - schedule.last_fetch_time
            if elapsed >= fetch_cooldown:
                should_fetch = True

    if should_fetch:
        _, fetch_err, fetch_rc = _run(
            repo_path, ["fetch", "--prune", "--quiet"], timeout=FETCH_TIMEOUT
        )
        if schedule is not None:
            schedule.last_fetch_time = time.time()
        # 检查 fetch 错误：返回码非0 或 stderr 含错误关键词
        if fetch_rc != 0 or (fetch_err and any(
            kw in fetch_err.lower() for kw in ("error", "fatal", "failed", "denied")
        )):
            status.fetch_error = fetch_err[:200] if fetch_err else "fetch 失败"

    # 计算 ahead / behind：使用 @{u} 自动识别上游分支（避免硬编码 origin）
    if status.has_remote and not is_detached:
        out, _, rc = _run(repo_path, [
            "rev-list", "--left-right", "--count", "@{u}...HEAD"
        ], timeout=10)
        if rc == 0 and out:
            parts = out.split()
            if len(parts) == 2:
                try:
                    status.behind = int(parts[0])
                    status.ahead = int(parts[1])
                except ValueError:
                    pass

    # ── 本地状态 ──
    out, _, _ = _run(repo_path, ["status", "--porcelain"])
    if out:
        status.dirty = True
        for line in out.split("\n"):
            if not line.strip():
                continue
            idx = line[:2]
            staged_char = idx[0] if len(idx) > 0 else " "
            unstaged_char = idx[1] if len(idx) > 1 else " "
            # untracked: ??
            if staged_char == "?" and unstaged_char == "?":
                status.untracked += 1
                continue
            if staged_char != " ":
                status.staged += 1
            if unstaged_char != " ":
                status.unstaged += 1

    # ── 最近提交 ──
    # 格式: hash||date||author||message  （用 || 分隔，便于解析）
    out, err, rc = _run(repo_path, [
        "log", "-n", str(log_count), "--date=iso",
        "--format=%h||%ad||%an||%s"
    ], timeout=5)
    if out:
        status.recent_commits = out.split("\n")
    elif rc != 0 and "does not have any commits" in err.lower():
        status.error = "空仓库（无提交历史）"

    # ── diff stat ──
    diff_out, _, _ = _run(repo_path, ["diff", "--stat"], timeout=10)
    if diff_out:
        status.diff_stat = diff_out

    # ── 全部分支（可选）──
    if check_branches_flag:
        status.branches = check_branches(repo_path)

    return status


# ═══════════════════════════
#  并行批量检测
# ═══════════════════════════

def check_branches(repo_path: Path) -> list[BranchStatus]:
    """获取仓库所有本地分支的状态。"""
    branches: list[BranchStatus] = []

    # git branch -vv: 列出所有本地分支及其跟踪关系
    out, _, rc = _run(repo_path, [
        "branch", "-vv", "--format=%(refname:short)|%(upstream:short)|%(upstream:track,nobracket)|%(objectname:short)"
    ], timeout=10)
    if rc != 0 or not out:
        return branches

    for line in out.split("\n"):
        line = line.strip()
        if not line:
            continue
        # 格式: main|origin/main|[ahead 1, behind 2]|abc1234
        parts = line.split("|", 3)
        if len(parts) < 4:
            continue

        name = parts[0]
        tracking = parts[1]
        track_info = parts[2]       # e.g. "ahead 1, behind 2" or "" or "gone"
        last_hash = parts[3]

        ahead = 0
        behind = 0
        if track_info and track_info != "gone":
            for seg in track_info.split(","):
                seg = seg.strip()
                if seg.startswith("ahead "):
                    try:
                        ahead = int(seg.split()[1])
                    except ValueError:
                        pass
                elif seg.startswith("behind "):
                    try:
                        behind = int(seg.split()[1])
                    except ValueError:
                        pass

        branches.append(BranchStatus(
            name=name,
            tracking=tracking,
            ahead=ahead,
            behind=behind,
            last_hash=last_hash,
        ))

    # 标记当前分支
    _, current_branch, _ = _run(repo_path, ["rev-parse", "--abbrev-ref", "HEAD"])
    for b in branches:
        if b.name == current_branch.strip():
            b.is_current = True

    return branches


def check_all_parallel(
    repo_paths: list[Path],
    log_count: int = 10,
    schedules: dict[str, RepoScheduleState] | None = None,
    max_workers: int = MAX_CONCURRENT,
    fetch_cooldown: int = FETCH_COOLDOWN_SEC,
    check_branches_flag: bool = False,
) -> list[GitStatus]:
    """并行检测所有仓库。"""
    if schedules is None:
        schedules = {}

    # 确保每个 repo 有对应的 schedule
    for p in repo_paths:
        key = str(p)
        if key not in schedules:
            schedules[key] = RepoScheduleState(repo_path=p)

    # 按 repo_path 顺序收集结果
    path_order = {str(p): i for i, p in enumerate(repo_paths)}
    results: list[GitStatus | None] = [None] * len(repo_paths)

    # 限制并行数不超过 repo 数
    actual_workers = min(max_workers, len(repo_paths))
    if actual_workers <= 0:
        return []

    with ThreadPoolExecutor(max_workers=actual_workers) as executor:
        futures = {}
        for repo_path in repo_paths:
            key = str(repo_path)
            sched = schedules[key]
            future = executor.submit(
                check_repo, repo_path, log_count, sched, fetch_cooldown, check_branches_flag
            )
            futures[future] = key

        for future in as_completed(futures):
            key = futures[future]
            idx = path_order[key]
            try:
                results[idx] = future.result()
            except Exception:
                results[idx] = GitStatus(
                    repo_path=Path(key),
                    repo_name=Path(key).name,
                    error=f"检测异常: {future.exception()}",
                )

    return [r for r in results if r is not None]


# ═══════════════════════════
#  提交详情 & 文件对比
# ═══════════════════════════

def get_commit_detail(repo_path: Path, commit_hash: str) -> CommitDetail | None:
    """获取单条提交的详细信息（作者、日期、变更文件列表）。"""
    # ── 基本信息 ──
    out, _, rc = _run(repo_path, [
        "show", "--no-patch", "--format=%H||%h||%an||%ad||%s",
        "--date=relative", commit_hash,
    ], timeout=5)
    if rc != 0 or not out:
        return None

    parts = out.split("||", 4)
    if len(parts) < 5:
        return None

    detail = CommitDetail(
        hash=parts[0],
        short_hash=parts[1],
        author=parts[2],
        date=parts[3],
        message=parts[4],
    )

    # ── 变更文件列表 ──
    stat_out, _, _ = _run(repo_path, [
        "show", "--stat", "--format=", commit_hash,
    ], timeout=10)
    if stat_out:
        detail.changed_files = _parse_changed_files(stat_out)

    return detail


def get_commit_file_diff(
    repo_path: Path, commit_hash: str, file_path: str, max_lines: int = 200
) -> str:
    """获取某个文件在某次提交中的 diff 内容。"""
    out, err, rc = _run(repo_path, [
        "show", commit_hash, "--", file_path,
    ], timeout=10)
    if rc != 0:
        return err or "无法获取 diff"
    lines = out.split("\n")
    if len(lines) > max_lines:
        return "\n".join(lines[:max_lines]) + f"\n\n... (截断，共 {len(lines)} 行)"
    return out


def get_unstaged_file_diff(
    repo_path: Path, file_path: str, max_lines: int = 200
) -> str:
    """获取当前未暂存变更中某个文件的 diff。"""
    out, err, rc = _run(repo_path, [
        "diff", "--", file_path,
    ], timeout=10)
    if rc != 0:
        return err or "无变更"
    if not out:
        # 可能已暂存，尝试 --cached
        out, err, rc = _run(repo_path, [
            "diff", "--cached", "--", file_path,
        ], timeout=10)
        if not out:
            return "无变更"
    lines = out.split("\n")
    if len(lines) > max_lines:
        return "\n".join(lines[:max_lines]) + f"\n\n... (截断，共 {len(lines)} 行)"
    return out


def _parse_changed_files(stat_output: str) -> list[ChangedFile]:
    """解析 git show --stat 输出中的文件列表。"""
    files = []
    lines = stat_output.strip().split("\n")
    for line in lines:
        line = line.strip()
        if not line:
            continue
        # 格式: "path.py | 3 ++-"
        # 最后一行是 "N files changed, ..." 的汇总，跳过
        if "|" not in line:
            continue
        parts = line.rsplit("|", 1)
        if len(parts) != 2:
            continue
        file_path = parts[0].strip()
        changes = parts[1].strip()

        added = 0
        deleted = 0
        # 解析变更统计，如 "3 +++---" 或 "5 +-" 或 "Bin 0 -> 123 bytes"
        if "Bin" in changes:
            pass  # 二进制文件
        else:
            for ch in changes:
                if ch == "+":
                    added += 1
                elif ch == "-":
                    deleted += 1

        status = "M"
        if "=>" in file_path:
            status = "R"

        files.append(ChangedFile(
            path=file_path,
            added=added,
            deleted=deleted,
            status=status,
        ))
    return files


# ═══════════════════════════
#  展示辅助
# ═══════════════════════════

def status_summary(st: GitStatus) -> str:
    """生成状态摘要文本（用于 tooltip / 菜单）。"""
    if st.error:
        return f"✗ {st.error}"

    parts = [st.branch or "???"]
    flags = []

    if st.dirty:
        parts_dirty = []
        if st.staged:
            parts_dirty.append(f"暂存{st.staged}")
        if st.unstaged:
            parts_dirty.append(f"修改{st.unstaged}")
        if st.untracked:
            parts_dirty.append(f"新增{st.untracked}")
        flags.append(" ".join(parts_dirty))
    if st.ahead:
        flags.append(f"↑{st.ahead}")
    if st.behind:
        flags.append(f"↓{st.behind}")
    if st.has_remote and not st.dirty and st.ahead == 0 and st.behind == 0:
        flags.append("同步")

    if flags:
        parts.append(" | " + ", ".join(flags))

    return " ".join(parts)


def status_icon(st: GitStatus) -> str:
    """返回状态对应的 emoji 图标。"""
    if st.error:
        return "❌"
    if st.behind > 0 and st.ahead > 0:
        return "🔀"
    if st.behind > 0:
        return "⬇"
    if st.ahead > 0:
        return "⬆"
    if st.dirty:
        return "✏"
    return "✓"
