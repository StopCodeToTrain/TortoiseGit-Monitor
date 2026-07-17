"""
TortoiseGit-Monitor - 入口。
托盘应用，无主窗口。定时检测 git 仓库状态。
"""

import sys
from PyQt6.QtWidgets import QApplication

from gui import TrayApp


def main():
    app = QApplication(sys.argv)
    app.setQuitOnLastWindowClosed(False)
    tray = TrayApp()
    sys.exit(app.exec())


if __name__ == "__main__":
    main()
