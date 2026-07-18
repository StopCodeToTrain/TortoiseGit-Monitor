"""
TortoiseGit-Monitor - 托盘应用。
无主窗口，所有交互通过系统托盘完成。
左键/右键均弹出菜单：仓库快捷入口 + 设置 + 刷新 + 退出。
"""

from __future__ import annotations

import subprocess
from pathlib import Path

from PyQt6.QtCore import Qt, QEvent, QObject, QPoint, QThread, QTimer, pyqtSignal
from PyQt6.QtGui import QColor, QCursor, QIcon, QPainter, QPixmap, QPolygon
from PyQt6.QtWidgets import (
    QApplication, QCheckBox, QDialog, QDialogButtonBox, QFileDialog,
    QFormLayout, QGroupBox, QHBoxLayout, QLabel, QLineEdit, QListWidget,
    QMenu, QPushButton, QSpinBox, QSystemTrayIcon,
    QVBoxLayout,
)

from config import (
    get_global, load, resolve_repo_paths, save, update_global,
)
from git_watcher import (
    GitStatus, RepoScheduleState,
    check_all_parallel, status_icon, status_summary,
)


# ═══════════════════════════
#  浅色主题配色（Catppuccin Latte）
# ═══════════════════════════

COLORS = {
    "bg":          "#eff1f5",   # 基础背景
    "surface":     "#e6e9ef",   # 面板背景
    "surface2":    "#dce0e8",   # 悬停/交替行
    "border":      "#bcc0cc",   # 边框
    "text":        "#4c4f69",   # 主文字
    "subtext":     "#6c6f85",   # 次要文字
    "blue":        "#1e66f5",   # 链接/强调
    "green":       "#40a02b",   # 正常/同步
    "yellow":      "#df8e1d",   # 修改/警告
    "red":         "#d20f39",   # 错误/落后
    "overlay":     "#9ca0b0",   # 禁用/占位
}

LIGHT_STYLE = f"""
    QWidget {{ background-color: {COLORS["bg"]}; color: {COLORS["text"]}; font-size: 12px; }}
    QLabel {{ background: transparent; border: none; }}
    QPushButton {{
        background-color: {COLORS["surface"]}; color: {COLORS["text"]};
        border: 1px solid {COLORS["border"]}; border-radius: 4px;
        padding: 5px 12px;
    }}
    QPushButton:hover {{ background-color: {COLORS["surface2"]}; border-color: {COLORS["blue"]}; }}
    QPushButton:pressed {{ background-color: {COLORS["border"]}; }}
    QLineEdit, QSpinBox {{
        background-color: {COLORS["surface"]}; color: {COLORS["text"]};
        border: 1px solid {COLORS["border"]}; border-radius: 4px;
        padding: 3px 6px;
    }}
    QLineEdit:focus, QSpinBox:focus {{
        border: 1px solid {COLORS["blue"]};
    }}
    QListWidget {{
        background-color: {COLORS["surface"]}; color: {COLORS["text"]};
        border: 1px solid {COLORS["border"]}; border-radius: 4px;
    }}
    QListWidget::item {{ padding: 4px 8px; border-bottom: 1px solid {COLORS["border"]}; }}
    QListWidget::item:hover {{ background-color: {COLORS["surface2"]}; }}
    QListWidget::item:selected {{
        background-color: {COLORS["blue"]}; color: white;
    }}
    QGroupBox {{
        color: {COLORS["subtext"]}; border: 1px solid {COLORS["border"]};
        border-radius: 6px; margin-top: 10px; padding-top: 8px; font-weight: bold;
    }}
    QGroupBox::title {{ subcontrol-origin: margin; left: 10px; padding: 0 4px; }}
    QCheckBox {{ color: {COLORS["text"]}; }}
    QCheckBox::indicator {{
        width: 14px; height: 14px; border-radius: 3px;
        border: 1px solid {COLORS["border"]}; background-color: {COLORS["surface"]};
    }}
    QCheckBox::indicator:checked {{
        background-color: {COLORS["blue"]}; border: 1px solid {COLORS["blue"]};
    }}
    QMenu {{
        background-color: {COLORS["surface"]}; color: {COLORS["text"]};
        border: 1px solid {COLORS["border"]};
    }}
    QMenu::item {{ padding: 5px 20px; }}
    QMenu::item:selected {{ background-color: {COLORS["blue"]}; color: white; }}
    QMenu::separator {{ height: 1px; background-color: {COLORS["border"]}; margin: 4px 8px; }}
    QScrollBar:vertical {{ background: {COLORS["surface"]}; width: 8px; border: none; }}
    QScrollBar::handle:vertical {{
        background: {COLORS["border"]}; border-radius: 4px; min-height: 20px;
    }}
    QScrollBar::handle:vertical:hover {{ background: {COLORS["overlay"]}; }}
    QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical {{ height: 0; }}
"""


# ═══════════════════════════
#  图标
# ═══════════════════════════

_icon_cache: dict[str, QIcon] = {}

def _make_icon(color: str) -> QIcon:
    size = 16
    pixmap = QPixmap(size, size)
    pixmap.fill(Qt.GlobalColor.transparent)
    p = QPainter(pixmap)
    p.setRenderHint(QPainter.RenderHint.Antialiasing)
    p.setBrush(QColor(color))
    p.setPen(Qt.PenStyle.NoPen)
    p.drawEllipse(2, 2, size - 4, size - 4)
    p.end()
    return QIcon(pixmap)

def _get_icon(key: str) -> QIcon:
    if key not in _icon_cache:
        color_map = {
            "clean": COLORS["green"],
            "dirty": COLORS["yellow"],
            "behind": COLORS["red"],
            "error": COLORS["overlay"],
        }
        _icon_cache[key] = _make_icon(color_map.get(key, COLORS["overlay"]))
    return _icon_cache[key]

def _get_repo_status_icon(st) -> QIcon:
    """根据仓库状态返回对应颜色的圆形图标。"""
    if st.error:
        return _get_icon("error")
    if st.behind > 0:
        return _get_icon("behind")
    if st.dirty:
        return _get_icon("dirty")
    return _get_icon("clean")

def _make_arrow_icon(direction: str, color: str, enabled: bool = True) -> QIcon:
    """绘制三角形箭头图标。
    Args:
        direction: "up" | "down"
        color: 箭头填充色
        enabled: True=实心, False=空心（置灰状态）
    """
    size = 16
    pixmap = QPixmap(size, size)
    pixmap.fill(Qt.GlobalColor.transparent)
    p = QPainter(pixmap)
    p.setRenderHint(QPainter.RenderHint.Antialiasing)
    fill = QColor(color)
    if not enabled:
        fill.setAlpha(80)
    p.setBrush(fill)
    p.setPen(Qt.PenStyle.NoPen)
    if direction == "up":
        # 朝上的三角形
        poly = QPolygon([QPoint(8, 2), QPoint(2, 13), QPoint(14, 13)])
    else:
        # 朝下的三角形
        poly = QPolygon([QPoint(8, 14), QPoint(2, 3), QPoint(14, 3)])
    p.drawPolygon(poly)
    p.end()
    return QIcon(pixmap)


# ═══════════════════════════
#  TortoiseGit 查找
# ═══════════════════════════

TG_CANDIDATES = [
    r"C:\Program Files\TortoiseGit\bin\TortoiseGitProc.exe",
    r"C:\Program Files (x86)\TortoiseGit\bin\TortoiseGitProc.exe",
]

def find_tortoisegit() -> str | None:
    # 优先使用设置中显式指定的路径
    custom = get_global().get("tortoisegit_path", "").strip()
    if custom and Path(custom).exists():
        return custom
    for p in TG_CANDIDATES:
        if Path(p).exists():
            return p
    return None

def launch_tortoisegit(repo_path: str, command: str = "log") -> None:
    tg = find_tortoisegit()
    if not tg:
        return
    try:
        subprocess.Popen(
            [tg, f"/command:{command}", f"/path:{repo_path}"],
            creationflags=subprocess.CREATE_NO_WINDOW if hasattr(subprocess, "CREATE_NO_WINDOW") else 0,
        )
    except Exception:
        pass


# ═══════════════════════════
#  后台检测线程
# ═══════════════════════════

class CheckWorker(QThread):
    """后台检测线程：解析仓库路径 + 批量检测。"""
    results_ready = pyqtSignal(list)

    def __init__(self, log_count=10, schedules=None, check_branches_flag=False, parent=None):
        super().__init__(parent)
        self._log_count = log_count
        self._schedules = schedules or {}
        self._check_branches_flag = check_branches_flag
        self._cancel = False

    def cancel(self):
        self._cancel = True

    def run(self):
        try:
            if self._cancel:
                return
            # 在工作线程中解析仓库路径（避免主线程阻塞）
            repos = resolve_repo_paths()
            if not repos:
                self.results_ready.emit([])
                return
            results = check_all_parallel(
                repos, log_count=self._log_count,
                schedules=self._schedules,
                check_branches_flag=self._check_branches_flag,
            )
            if not self._cancel:
                self.results_ready.emit(results)
        except Exception as e:
            if not self._cancel:
                # 异常时构造错误结果，确保 UI 不会卡在"检测中..."
                repos = self._schedules.keys()
                error_results = [
                    GitStatus(
                        repo_path=Path(str(p)),
                        repo_name=Path(str(p)).name if isinstance(p, str) else str(p),
                        error=f"检测异常: {e}",
                    )
                    for p in repos
                ] if repos else [
                    GitStatus(repo_path=Path("."), repo_name="错误", error=f"检测异常: {e}")
                ]
                self.results_ready.emit(error_results)


# ═══════════════════════════
#  设置对话框
# ═══════════════════════════

class SettingsDialog(QDialog):
    """检测设置 + 扫描目录。"""

    def __init__(self, parent=None):
        super().__init__(parent)
        self.setWindowTitle("设置")
        self.setMinimumWidth(450)
        self.setStyleSheet(LIGHT_STYLE)
        self._init_ui()
        self._load()

    def _init_ui(self):
        layout = QVBoxLayout(self)

        # ── 检测设置 ──
        gb_poll = QGroupBox("检测设置")
        gl = QFormLayout(gb_poll)
        self.spin_interval = QSpinBox()
        self.spin_interval.setRange(60, 86400)
        self.spin_interval.setSuffix(" 秒")
        gl.addRow("检测间隔:", self.spin_interval)
        self.spin_log_count = QSpinBox()
        self.spin_log_count.setRange(1, 100)
        gl.addRow("日志条数:", self.spin_log_count)
        self.chk_notify = QCheckBox("发现新提交时弹出通知")
        gl.addRow(self.chk_notify)
        self.chk_sound = QCheckBox("通知时播放声音")
        gl.addRow(self.chk_sound)
        layout.addWidget(gb_poll)

        # ── TortoiseGit ──
        gb_tg = QGroupBox("TortoiseGit")
        tl = QFormLayout(gb_tg)
        row = QHBoxLayout()
        self.edit_tg = QLineEdit()
        self.edit_tg.setPlaceholderText("留空则自动查找（Show Log 快捷操作使用）")
        row.addWidget(self.edit_tg, 1)
        btn_browse = QPushButton("浏览...")
        btn_browse.clicked.connect(self._browse_tg)
        row.addWidget(btn_browse)
        btn_clear = QPushButton("清除")
        btn_clear.clicked.connect(self.edit_tg.clear)
        row.addWidget(btn_clear)
        tl.addRow("程序路径:", row)
        layout.addWidget(gb_tg)

        # ── 扫描目录 ──
        gb_scan = QGroupBox("扫描目录")
        sl = QVBoxLayout(gb_scan)
        hint = QLabel("在这些目录下递归搜索 git 仓库（最多 3 层）")
        hint.setStyleSheet(f"color: {COLORS['subtext']}; font-size: 11px;")
        sl.addWidget(hint)
        self.scan_list = QListWidget()
        self.scan_list.setMaximumHeight(80)
        sl.addWidget(self.scan_list)
        row = QHBoxLayout()
        self.edit_scan = QLineEdit()
        self.edit_scan.setPlaceholderText("输入目录路径...")
        row.addWidget(self.edit_scan, 1)
        btn_add = QPushButton("添加")
        btn_add.clicked.connect(self._add_scan)
        row.addWidget(btn_add)
        btn_del = QPushButton("移除")
        btn_del.clicked.connect(self._del_scan)
        row.addWidget(btn_del)
        sl.addLayout(row)
        layout.addWidget(gb_scan)

        # ── 按钮 ──
        btns = QDialogButtonBox(
            QDialogButtonBox.StandardButton.Ok | QDialogButtonBox.StandardButton.Cancel
        )
        btns.accepted.connect(self._save)
        btns.rejected.connect(self.reject)
        layout.addWidget(btns)

    def _load(self):
        glb = get_global()
        data = load()
        self.spin_interval.setValue(glb.get("interval_sec", 300))
        self.spin_log_count.setValue(glb.get("log_count", 10))
        self.chk_notify.setChecked(glb.get("show_notifications", True))
        self.chk_sound.setChecked(glb.get("notification_sound", False))
        self.edit_tg.setText(glb.get("tortoisegit_path", ""))
        for p in data.get("scan_paths", []):
            self.scan_list.addItem(p)

    def _save(self):
        update_global({
            "interval_sec": self.spin_interval.value(),
            "log_count": self.spin_log_count.value(),
            "show_notifications": self.chk_notify.isChecked(),
            "notification_sound": self.chk_sound.isChecked(),
            "tortoisegit_path": self.edit_tg.text().strip(),
        })
        data = load()
        data["scan_paths"] = [self.scan_list.item(i).text() for i in range(self.scan_list.count())]
        save(data)
        self.accept()

    def _browse_tg(self):
        path, _ = QFileDialog.getOpenFileName(
            self, "选择 TortoiseGitProc.exe", "",
            "TortoiseGitProc (TortoiseGitProc.exe);;所有文件 (*)",
        )
        if path:
            self.edit_tg.setText(path)

    def _add_scan(self):
        path = self.edit_scan.text().strip()
        if not path:
            return
        abs_path = str(Path(path).expanduser().resolve())
        existing = {self.scan_list.item(i).text() for i in range(self.scan_list.count())}
        if abs_path not in existing:
            self.scan_list.addItem(abs_path)
        self.edit_scan.clear()

    def _del_scan(self):
        for item in self.scan_list.selectedItems():
            self.scan_list.takeItem(self.scan_list.row(item))


# ═══════════════════════════
#  托盘应用
# ═══════════════════════════

class TrayApp(QObject):
    """主应用：系统托盘 + 定时检测。"""

    MAX_VISIBLE_REPOS = 20  # 一级菜单最多同时显示的仓库数

    def __init__(self):
        super().__init__()
        self._results: list[GitStatus] = []
        self._worker: CheckWorker | None = None
        self._schedules: dict[str, RepoScheduleState] = {}
        self._timer = QTimer()
        self._timer.timeout.connect(self.refresh)
        self._repo_offset = 0  # 仓库列表滚动偏移
        self._menu: QMenu | None = None

        # 托盘
        self.tray = QSystemTrayIcon()
        self.tray.setIcon(_get_icon("error"))
        self.tray.setToolTip("TortoiseGit-Monitor")
        self.tray.activated.connect(self._on_activated)

        # 菜单
        self._build_menu()
        self.tray.show()
        self.refresh()

    def _build_menu(self):
        """初次构建菜单（空仓库状态）。"""
        self._menu = QMenu()
        self._menu.setStyleSheet(LIGHT_STYLE)
        self._menu.installEventFilter(self)
        self._rebuild_menu()
        self.tray.setContextMenu(self._menu)

    def _rebuild_menu(self):
        """完全重建菜单内容。"""
        menu = self._menu
        if menu is None:
            return
        menu.clear()

        repos = self._results
        total = len(repos)
        max_n = self.MAX_VISIBLE_REPOS

        if total > 0:
            if total > max_n:
                # ── 上翻箭头 ──
                if self._repo_offset > 0:
                    act_up = menu.addAction(f"  上翻（{self._repo_offset} 个）")
                    act_up.setIcon(_make_arrow_icon("up", COLORS["blue"]))
                    act_up.triggered.connect(self._page_up)
                else:
                    act_up = menu.addAction("  ── 到顶 ──")
                    act_up.setIcon(_make_arrow_icon("up", COLORS["overlay"], enabled=False))
                    act_up.setEnabled(False)

                # ── 可见仓库 ──
                end = min(self._repo_offset + max_n, total)
                for i in range(self._repo_offset, end):
                    st = repos[i]
                    parts = []
                    if st.dirty:
                        parts.append("修改")
                    if st.behind:
                        parts.append(f"↓{st.behind}")
                    if st.ahead:
                        parts.append(f"↑{st.ahead}")
                    if not parts and not st.error:
                        parts.append("同步")
                    if st.error:
                        parts = ["⚠"]
                    label = f"  {st.repo_name}  ({' '.join(parts)})"
                    action = menu.addAction(label)
                    action.setIcon(_get_repo_status_icon(st))
                    action.triggered.connect(
                        lambda checked, p=str(st.repo_path): self._open_log(p)
                    )

                # ── 下翻箭头 ──
                if end < total:
                    remaining = total - end
                    act_down = menu.addAction(f"  下翻（{remaining} 个）")
                    act_down.setIcon(_make_arrow_icon("down", COLORS["blue"]))
                    act_down.triggered.connect(self._page_down)
                else:
                    act_down = menu.addAction("  ── 到底 ──")
                    act_down.setIcon(_make_arrow_icon("down", COLORS["overlay"], enabled=False))
                    act_down.setEnabled(False)
            else:
                # ── 不超过 20 个，全部显示 ──
                for st in repos:
                    parts = []
                    if st.dirty:
                        parts.append("修改")
                    if st.behind:
                        parts.append(f"↓{st.behind}")
                    if st.ahead:
                        parts.append(f"↑{st.ahead}")
                    if not parts and not st.error:
                        parts.append("同步")
                    if st.error:
                        parts = ["⚠"]
                    label = f"  {st.repo_name}  ({' '.join(parts)})"
                    action = menu.addAction(label)
                    action.setIcon(_get_repo_status_icon(st))
                    action.triggered.connect(
                        lambda checked, p=str(st.repo_path): self._open_log(p)
                    )

            menu.addSeparator()

        # ── 静态菜单项 ──
        act_refresh = menu.addAction("⟳  刷新全部")
        act_refresh.triggered.connect(self.refresh)
        menu.addSeparator()
        act_settings = menu.addAction("⚙  设置...")
        act_settings.triggered.connect(self._open_settings)
        menu.addSeparator()
        act_quit = menu.addAction("✕  退出")
        act_quit.triggered.connect(self._quit)

        self.tray.setContextMenu(menu)

    def _update_repo_menu(self):
        """数据更新时重置偏移并重建菜单。"""
        self._repo_offset = 0
        self._rebuild_menu()

    def _page_up(self):
        """上翻一页。"""
        self._repo_offset = max(0, self._repo_offset - self.MAX_VISIBLE_REPOS)
        self._rebuild_menu()

    def _page_down(self):
        """下翻一页。"""
        max_offset = max(0, len(self._results) - self.MAX_VISIBLE_REPOS)
        self._repo_offset = min(max_offset, self._repo_offset + self.MAX_VISIBLE_REPOS)
        self._rebuild_menu()

    def eventFilter(self, obj, event):
        """拦截菜单滚轮事件，实现滚动翻页。"""
        if obj is self._menu and event.type() == QEvent.Type.Wheel:
            delta = event.angleDelta().y()
            if delta > 0:
                self._page_up()
            elif delta < 0:
                self._page_down()
            return True
        return super().eventFilter(obj, event)

    # ═══════════════════════════
    #  检测
    # ═══════════════════════════

    def refresh(self):
        if self._worker and self._worker.isRunning():
            return
        cfg = load()
        glb = cfg.get("global", {})

        self.tray.setToolTip("TortoiseGit-Monitor - 检测中...")
        check_branches = any(p.get("monitor_all_branches", False) for p in cfg.get("projects", []))
        self._worker = CheckWorker(
            glb.get("log_count", 10), self._schedules, check_branches
        )
        self._worker.results_ready.connect(self._on_results)
        self._worker.start()

        # 更新定时器
        interval = max(60, glb.get("interval_sec", 300)) * 1000
        if self._timer.interval() != interval or not self._timer.isActive():
            self._timer.start(interval)

    def _on_results(self, results: list[GitStatus]):
        self._results = results
        self._update_tray()
        self._update_repo_menu()

    def _update_tray(self):
        if not self._results:
            self.tray.setIcon(_get_icon("error"))
            return
        has_behind = any(r.behind > 0 for r in self._results)
        has_dirty = any(r.dirty for r in self._results)
        has_error = any(r.error for r in self._results)

        if has_error or has_behind:
            icon_key = "behind"
        elif has_dirty:
            icon_key = "dirty"
        else:
            icon_key = "clean"
        self.tray.setIcon(_get_icon(icon_key))

        lines = ["TortoiseGit-Monitor"]
        for r in self._results:
            lines.append(f"  {status_icon(r)} {r.repo_name}: {status_summary(r)}")
        self.tray.setToolTip("\n".join(lines))

    # ═══════════════════════════
    #  交互
    # ═══════════════════════════

    def _on_activated(self, reason: QSystemTrayIcon.ActivationReason):
        if reason == QSystemTrayIcon.ActivationReason.Trigger:
            # 左键：显示右键菜单
            self.tray.contextMenu().popup(QCursor.pos())

    def _open_log(self, repo_path: str):
        """打开 TortoiseGit showLog。"""
        launch_tortoisegit(repo_path, "log")

    def _open_settings(self):
        dlg = SettingsDialog()
        if dlg.exec() == QDialog.DialogCode.Accepted:
            self._timer.stop()
            self.refresh()

    def _quit(self):
        self._timer.stop()
        if self._worker and self._worker.isRunning():
            self._worker.cancel()
            self._worker.wait(3000)
        self.tray.hide()
        QApplication.instance().quit()
