"""
配置管理：项目列表、全局设置、JSON 持久化。

数据结构:
  {
    "global": {
      "interval_sec": 300,         // 默认检测间隔（秒）
      "log_count": 10,             // 提交日志条数
      "start_with_windows": false, // 随系统启动
      "show_notifications": true,  // 显示弹出通知
      "notification_sound": false, // 通知时播放声音
    },
    "projects": [
      {
        "name": "my-project",      // 显示名称
        "path": "D:/repo",         // 本地路径
        "monitor_all_branches": false,
        "interval": 0,             // 0=使用全局默认
        "ignore_authors": "",
        "regex_filter": "",
        "enabled": true,
      },
      ...
    ],
    "scan_paths": ["~", "D:/GitProject"],  // 自动扫描目录
  }
"""

import json
from pathlib import Path
from typing import Any


APP_NAME = "tortoisegit_monitor"
CONFIG_DIR = Path.home() / ".config" / APP_NAME
CONFIG_FILE = CONFIG_DIR / "settings.json"

# ── 默认值 ──

DEFAULT_GLOBAL: dict[str, Any] = {
    "interval_sec": 300,
    "log_count": 10,
    "start_with_windows": False,
    "show_notifications": True,
    "notification_sound": False,
}

DEFAULT_SCAN_PATHS = [
    str(Path.home()),
]


# ── IO ──

def _ensure_dir() -> None:
    CONFIG_DIR.mkdir(parents=True, exist_ok=True)


def _migrate_old_config() -> None:
    """从旧目录名 git_monitor 迁移配置到 tortoisegit_monitor。"""
    import shutil
    old_dir = Path.home() / ".config" / "git_monitor"
    if old_dir.is_dir() and not CONFIG_DIR.is_dir():
        try:
            shutil.copytree(str(old_dir), str(CONFIG_DIR))
        except OSError:
            pass


def load() -> dict:
    _ensure_dir()
    _migrate_old_config()
    try:
        with open(CONFIG_FILE, "r", encoding="utf-8") as f:
            data = json.load(f)
    except (FileNotFoundError, json.JSONDecodeError, PermissionError, OSError):
        return _defaults()
    if not isinstance(data, dict):
        return _defaults()
    return _migrate(data)


def save(data: dict) -> None:
    _ensure_dir()
    try:
        with open(CONFIG_FILE, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2, ensure_ascii=False)
    except OSError:
        pass  # 配置写入失败不应导致程序崩溃


def _defaults() -> dict:
    return {
        "global": dict(DEFAULT_GLOBAL),
        "projects": [],
        "scan_paths": list(DEFAULT_SCAN_PATHS),
    }


def _migrate(data: dict) -> dict:
    """兼容旧格式：将 flat 配置迁移到新的 projects + global 结构。"""
    if not isinstance(data, dict):
        return _defaults()

    # 如果已经是新格式
    if isinstance(data.get("global"), dict):
        for k, v in DEFAULT_GLOBAL.items():
            data["global"].setdefault(k, v)
        for p in data.get("projects", []):
            p.setdefault("name", p.get("path", ""))
            p.setdefault("monitor_all_branches", False)
            p.setdefault("interval", 0)
            p.setdefault("ignore_authors", "")
            p.setdefault("regex_filter", "")
            p.setdefault("enabled", True)
        data.setdefault("scan_paths", list(DEFAULT_SCAN_PATHS))
        return data

    # 旧格式迁移
    migrated = {
        "global": {
            "interval_sec": data.get("interval_sec", DEFAULT_GLOBAL["interval_sec"]),
            "log_count": data.get("log_count", DEFAULT_GLOBAL["log_count"]),
            "start_with_windows": data.get("start_with_windows", False),
            "show_notifications": data.get("show_notifications", True),
            "notification_sound": data.get("notification_sound", False),
        },
        "projects": [],
        "scan_paths": data.get("scan_paths", list(DEFAULT_SCAN_PATHS)),
    }
    for path in data.get("extra_paths", []):
        migrated["projects"].append({
            "name": Path(path).name,
            "path": path,
            "monitor_all_branches": data.get("monitor_all_branches", False),
            "interval": 0,
            "ignore_authors": "",
            "regex_filter": "",
            "enabled": True,
        })
    return migrated


# ── 项目管理 ──

def get_projects() -> list[dict]:
    """返回所有项目（不包括禁用的）。"""
    data = load()
    return [p for p in data["projects"] if p.get("enabled", True)]


def add_project(project: dict) -> None:
    data = load()
    path = project.get("path", "")
    project.setdefault("name", Path(path).name if path else "未命名")
    project.setdefault("monitor_all_branches", False)
    project.setdefault("interval", 0)
    project.setdefault("ignore_authors", "")
    project.setdefault("regex_filter", "")
    project.setdefault("enabled", True)
    data["projects"].append(project)
    save(data)


def update_project(index: int, project: dict) -> None:
    data = load()
    if 0 <= index < len(data["projects"]):
        data["projects"][index] = project
        save(data)


def remove_project(index: int) -> None:
    data = load()
    if 0 <= index < len(data["projects"]):
        del data["projects"][index]
        save(data)


def get_global() -> dict:
    return load()["global"]


def update_global(settings: dict) -> None:
    data = load()
    data["global"].update(settings)
    save(data)


# ── 仓库路径解析 ──

def resolve_repo_paths() -> list[Path]:
    """返回所有需要监控的 git 仓库路径。"""
    data = load()
    extra: list[Path] = []
    for proj in data.get("projects", []):
        if not isinstance(proj, dict):
            continue
        if not proj.get("enabled", True):
            continue
        path = proj.get("path", "")
        if not path:
            continue
        try:
            extra.append(Path(path).expanduser().resolve())
        except (OSError, ValueError):
            continue

    scan: list[Path] = []
    for raw in data.get("scan_paths", DEFAULT_SCAN_PATHS):
        scan.append(Path(raw).expanduser().resolve())

    return _discover(scan, extra)


def _discover(scan_paths: list[Path], extra_paths: list[Path], max_depth: int = 3) -> list[Path]:
    """从扫描目录发现 git 仓库，合并额外指定路径，去重排序。

    使用 os.walk 限制深度，跳过 .git/node_modules 等大型目录加速扫描。
    """
    import os
    found: dict[str, Path] = {}

    skip_dirs = {".git", "node_modules", ".venv", "venv", "__pycache__",
                 "AppData", "Library", "Application Data"}

    for base in scan_paths:
        if not base.is_dir():
            continue
        base_str = str(base)
        for root, dirs, _files in os.walk(base_str):
            # 计算当前深度
            rel = os.path.relpath(root, base_str)
            depth = 0 if rel == "." else rel.count(os.sep) + 1
            if depth >= max_depth:
                dirs.clear()  # 不再深入
                continue
            # 跳过无关目录
            dirs[:] = [d for d in dirs if d not in skip_dirs]
            # 检查当前目录是否为 git 仓库
            if os.path.isdir(os.path.join(root, ".git")):
                p = Path(root).resolve()
                found[str(p)] = p
                dirs.clear()  # git 仓库内部不再搜索

    for p in extra_paths:
        if (p / ".git").exists():
            found[str(p)] = p

    return sorted(found.values(), key=lambda p: p.name.lower())
