/**
 * 设置对话框：检测设置 + TortoiseGit 路径 + 扫描目录。
 * 手写 WinForms 布局（无设计器），与 Python 版 SettingsDialog 行为一致。
 */
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace TortoiseGitMonitor
{
    internal sealed class SettingsForm : Form
    {
        private NumericUpDown _numInterval;
        private NumericUpDown _numLogCount;
        private CheckBox _chkNotify;
        private CheckBox _chkSound;
        private CheckBox _chkAutostart;
        private TextBox _txtTg;
        private TextBox _txtScan;
        private ListBox _lstScan;

        public SettingsForm()
        {
            Text = "设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            // 高度随内容自适应，避免内容超出时底部按钮被裁剪
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            MinimumSize = new Size(540, 0);
            BackColor = ThemeColors.Bg;
            Font = new Font("Microsoft YaHei UI", 9F);

            InitUi();
            LoadConfig();
        }

        // ═══════════════════════════
        //  界面构建
        // ═══════════════════════════

        private void InitUi()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                Padding = new Padding(10),
                AutoSize = true,
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            root.Controls.Add(BuildPollGroup(), 0, 0);
            root.Controls.Add(BuildTortoiseGitGroup(), 0, 1);
            root.Controls.Add(BuildScanGroup(), 0, 2);
            root.Controls.Add(BuildButtons(), 0, 3);

            Controls.Add(root);
        }

        /// <summary>检测设置分组。</summary>
        private Control BuildPollGroup()
        {
            var group = new GroupBox
            {
                Text = "检测设置",
                Dock = DockStyle.Top,
                AutoSize = true,
                ForeColor = ThemeColors.Subtext,
                Padding = new Padding(10),
            };

            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 2,
                AutoSize = true,
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _numInterval = new NumericUpDown
            {
                Minimum = 60,
                Maximum = 86400,
                Width = 120,
                Anchor = AnchorStyles.Left,
            };
            _numLogCount = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 100,
                Width = 120,
                Anchor = AnchorStyles.Left,
            };
            _chkNotify = new CheckBox { Text = "发现新提交时弹出通知", AutoSize = true };
            _chkSound = new CheckBox { Text = "通知时播放声音", AutoSize = true };
            _chkAutostart = new CheckBox { Text = "随 Windows 启动（登录后自动运行）", AutoSize = true };

            int row = 0;
            AddRow(grid, row++, "检测间隔（秒）:", _numInterval);
            AddRow(grid, row++, "日志条数:", _numLogCount);
            grid.Controls.Add(_chkNotify, 0, row);
            grid.SetColumnSpan(_chkNotify, 2);
            row++;
            grid.Controls.Add(_chkSound, 0, row);
            grid.SetColumnSpan(_chkSound, 2);
            row++;
            grid.Controls.Add(_chkAutostart, 0, row);
            grid.SetColumnSpan(_chkAutostart, 2);

            group.Controls.Add(grid);
            return group;
        }

        /// <summary>TortoiseGit 程序路径分组。</summary>
        private Control BuildTortoiseGitGroup()
        {
            var group = new GroupBox
            {
                Text = "TortoiseGit",
                Dock = DockStyle.Top,
                AutoSize = true,
                ForeColor = ThemeColors.Subtext,
                Padding = new Padding(10),
            };

            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 2,
                AutoSize = true,
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            // 三列布局：路径框占满剩余宽度，两个按钮自适应，避免 FlowLayoutPanel 溢出分组右边界
            var rowPanel = new TableLayoutPanel
            {
                ColumnCount = 3,
                AutoSize = true,
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
            };
            rowPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            rowPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            rowPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            _txtTg = new TextBox { Dock = DockStyle.Fill };
            // 占位提示（.NET Framework 无原生 PlaceholderText，用提示标签代替）
            var btnBrowse = new Button { Text = "浏览...", AutoSize = true };
            btnBrowse.Click += (s, e) => BrowseTortoiseGit();
            var btnClear = new Button { Text = "清除", AutoSize = true };
            btnClear.Click += (s, e) => _txtTg.Clear();

            rowPanel.Controls.Add(_txtTg, 0, 0);
            rowPanel.Controls.Add(btnBrowse, 1, 0);
            rowPanel.Controls.Add(btnClear, 2, 0);

            AddRow(grid, 0, "程序路径:", rowPanel);

            var hint = new Label
            {
                Text = "留空则自动查找（Show Log 快捷操作使用）",
                AutoSize = true,
                ForeColor = ThemeColors.Subtext,
                Font = new Font(Font.FontFamily, 8F),
            };
            grid.Controls.Add(hint, 1, 1);

            group.Controls.Add(grid);
            return group;
        }

        /// <summary>扫描目录分组。</summary>
        private Control BuildScanGroup()
        {
            var group = new GroupBox
            {
                Text = "扫描目录",
                Dock = DockStyle.Top,
                AutoSize = true,
                ForeColor = ThemeColors.Subtext,
                Padding = new Padding(10),
            };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 1,
                AutoSize = true,
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var hint = new Label
            {
                Text = "在这些目录下递归搜索 git 仓库（最多 3 层）",
                AutoSize = true,
                ForeColor = ThemeColors.Subtext,
                Font = new Font(Font.FontFamily, 8F),
            };

            _lstScan = new ListBox
            {
                Height = 80,
                Dock = DockStyle.Top,
                HorizontalScrollbar = true,
            };

            var rowPanel = new TableLayoutPanel
            {
                ColumnCount = 3,
                AutoSize = true,
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
            };
            rowPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            rowPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            rowPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _txtScan = new TextBox { Dock = DockStyle.Fill };
            var btnAdd = new Button { Text = "添加", AutoSize = true };
            btnAdd.Click += (s, e) => AddScanPath();
            var btnDel = new Button { Text = "移除", AutoSize = true };
            btnDel.Click += (s, e) => RemoveScanPath();
            rowPanel.Controls.Add(_txtScan, 0, 0);
            rowPanel.Controls.Add(btnAdd, 1, 0);
            rowPanel.Controls.Add(btnDel, 2, 0);

            layout.Controls.Add(hint, 0, 0);
            layout.Controls.Add(_lstScan, 0, 1);
            layout.Controls.Add(rowPanel, 0, 2);

            group.Controls.Add(layout);
            return group;
        }

        /// <summary>确定 / 取消按钮。</summary>
        private Control BuildButtons()
        {
            var panel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Dock = DockStyle.Top,
                WrapContents = false,
            };

            var btnOk = new Button { Text = "确定", DialogResult = DialogResult.None, Width = 80 };
            btnOk.Click += (s, e) => SaveConfig();
            var btnCancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 80 };

            panel.Controls.Add(btnOk);
            panel.Controls.Add(btnCancel);

            AcceptButton = btnOk;
            CancelButton = btnCancel;
            return panel;
        }

        private static void AddRow(TableLayoutPanel grid, int row, string label, Control control)
        {
            var lbl = new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 6, 3, 3),
            };
            grid.Controls.Add(lbl, 0, row);
            grid.Controls.Add(control, 1, row);
        }

        // ═══════════════════════════
        //  加载 / 保存
        // ═══════════════════════════

        private void LoadConfig()
        {
            AppConfig data = AppConfig.Load();
            GlobalSettings glb = data.Global;
            _numInterval.Value = Math.Min(_numInterval.Maximum, Math.Max(_numInterval.Minimum, glb.IntervalSec));
            _numLogCount.Value = Math.Min(_numLogCount.Maximum, Math.Max(_numLogCount.Minimum, glb.LogCount));
            _chkNotify.Checked = glb.ShowNotifications;
            _chkSound.Checked = glb.NotificationSound;
            _chkAutostart.Checked = glb.StartWithWindows;
            _txtTg.Text = glb.TortoiseGitPath ?? "";
            foreach (string p in data.ScanPaths)
                _lstScan.Items.Add(p);
        }

        private void SaveConfig()
        {
            AppConfig data = AppConfig.Load();
            data.Global.IntervalSec = (int)_numInterval.Value;
            data.Global.LogCount = (int)_numLogCount.Value;
            data.Global.ShowNotifications = _chkNotify.Checked;
            data.Global.NotificationSound = _chkSound.Checked;
            data.Global.StartWithWindows = _chkAutostart.Checked;
            data.Global.TortoiseGitPath = _txtTg.Text.Trim();

            data.ScanPaths.Clear();
            foreach (object item in _lstScan.Items)
                data.ScanPaths.Add(item.ToString());

            data.Save();
            Autostart.Set(_chkAutostart.Checked);

            DialogResult = DialogResult.OK;
            Close();
        }

        // ═══════════════════════════
        //  交互
        // ═══════════════════════════

        private void BrowseTortoiseGit()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "选择 TortoiseGitProc.exe",
                Filter = "TortoiseGitProc (TortoiseGitProc.exe)|TortoiseGitProc.exe|所有文件 (*.*)|*.*",
            })
            {
                if (dlg.ShowDialog() == DialogResult.OK)
                    _txtTg.Text = dlg.FileName;
            }
        }

        private void AddScanPath()
        {
            string path = _txtScan.Text.Trim();
            if (path.Length == 0)
                return;
            try
            {
                path = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return;
            }
            if (!_lstScan.Items.Contains(path))
                _lstScan.Items.Add(path);
            _txtScan.Clear();
        }

        private void RemoveScanPath()
        {
            while (_lstScan.SelectedItems.Count > 0)
                _lstScan.Items.Remove(_lstScan.SelectedItems[0]);
        }
    }
}
