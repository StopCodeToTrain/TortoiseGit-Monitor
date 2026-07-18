# TortoiseGit-Monitor

Windows 系统托盘应用，定时检测本地 Git 仓库状态，支持 TortoiseGit 快捷操作。

## 功能

- **自动 fetch** — 按设定的时间间隔自动 `git fetch --prune`，及时感知远程变更
- **状态指示** — 托盘图标用不同颜色标识仓库整体状态（绿/黄/红/灰）
- **右键菜单** — 点击托盘直接查看所有仓库状态，单击打开 TortoiseGit Show Log
- **仓库发现** — 配置扫描目录，自动递归搜索 Git 仓库（最多 3 层）
- **桌面通知** — 发现新提交时弹出 Windows Toast 通知，可选声音提示
- **分支监控** — 支持查看各仓库全部分支的 ahead/behind 状态

## 界面

```
  ▲  上翻（20 个）              ← 超过 20 个仓库时分页，支持滚轮翻页
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
| 扫描目录 | 在这些目录下递归搜索 Git 仓库 |

## 安装

```bash
pip install -r requirements.txt
pythonw run.pyw
```

- `python main.py` — 调试模式，带控制台窗口
- `pythonw run.pyw` — 常驻后台，无控制台，关闭命令行窗口不会退出

建议将 `run.pyw` 放入 Windows 启动目录或配置为计划任务实现开机自启。

## 依赖

- Python >= 3.11
- [PyQt6](https://pypi.org/project/PyQt6/) >= 6.5
- Git（需在 PATH 中）
- [TortoiseGit](https://tortoisegit.org/)（可选，用于 `Show Log` 快捷操作）

## 配置文件

配置保存在 `%USERPROFILE%\.config\tortoisegit_monitor\settings.json`。

首次启动会自动生成默认配置，也可通过托盘菜单 → 设置手动编辑。

## 项目结构

```
tortoisegit-monitor/
├── main.py           # 应用入口
├── gui.py            # 托盘、菜单、设置对话框
├── config.py         # 配置管理（JSON 持久化）
├── git_watcher.py    # Git 操作（状态检测、并行 fetch）
├── notify.py         # Windows Toast 通知
└── requirements.txt
```

## License

MIT
