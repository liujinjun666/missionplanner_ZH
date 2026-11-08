// zh-serial.cs —— 遥测通信监听 v1.0
// 监听 MP 与飞控之间的 MAVLink 收发（双向），并提供：
//   · 报文列表（时间/方向/消息/ID/系统组件/序号/长度/摘要）+ 字段反射与十六进制详情
//   · 消息类型过滤（单选 / 多选）、方向过滤、关键字过滤、只看文本
//   · 打印页：STATUSTEXT 按级别着色
//   · 发送页：MAV_CMD 命令（可改 sysid/compid/7 个参数）、参数写入
//   · 暂停/清空/保存日志/导出原始帧
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using MissionPlanner;

namespace ZhSerial
{
    // ───────────────────────── 一行记录 ─────────────────────────
    public class RowInfo
    {
        public DateTime Time;
        public bool Sent;          // true=发送
        public uint MsgId;
        public string Name = "";
        public int SysId;
        public int CompId;
        public int Seq;
        public int Len;            // 帧长（含头与 CRC）
        public int Payload;
        public string Content = "";
        public byte[] Raw;
        public object Data;        // MAVLink 解析后的结构体
    }

    // 下拉项：消息类型
    public class TypeItem
    {
        public uint Id;
        public string Name = "";
        public TypeItem(uint id, string name) { Id = id; Name = name; }
        public override string ToString() { return Name; }
    }

    public class ZhSerialPlugin : MissionPlanner.Plugin.Plugin
    {
        public override string Name { get { return "ZhSerial"; } }
        public override string Version { get { return "1.0"; } }
        public override string Author { get { return "local"; } }

        const int MaxRows = 5000;         // 列表保留行数
        const int MaxPending = 3000;      // 未上屏缓冲上限
        const int MaxTextChars = 400000;  // 打印页保留字符

        // ── 注入到飞行数据页 ──
        CheckBox chkEntry;
        bool uiAdded;
        int retries;

        // ── 窗口与控件 ──
        Form fm;
        TabControl tabs;
        DataGridView grid;
        TextBox txtDetail;
        RichTextBox txtPrint;
        SplitContainer split;
        TextBox txtFilter;
        ComboBox cmbMsgType, cmbDir;
        CheckBox chkTextOnly, chkAutoScroll, chkTopMost;
        Button btnPause, btnClear, btnSave, btnRaw, btnPickType;
        Label lblSrc, lblState;
        ToolStripStatusLabel lblCnt, lblRate;

        // 发送页
        NumericUpDown numSys, numComp, numCmd;
        NumericUpDown[] numP = new NumericUpDown[7];
        ComboBox cmbCmd;
        Label lblCmdHint, lblSent;
        TextBox txtParamName;
        NumericUpDown numParamValue;

        // ── 数据缓冲 ──
        readonly List<RowInfo> pending = new List<RowInfo>();
        System.Windows.Forms.Timer uiTimer;
        volatile bool paused;
        long cRx, cTx, cBytes, cDrop;
        long lastBytes;

        // 过滤条件快照（收包线程只读）
        volatile bool fltTextOnly;
        volatile int fltDir;
        volatile string fltText = "";
        HashSet<uint> selTypes;              // null = 全部；整体替换，不原地改

        // 已见过的消息类型（供类型下拉使用）
        readonly object typeLock = new object();
        readonly HashSet<uint> typeSeen = new HashSet<uint>();
        readonly List<TypeItem> typePending = new List<TypeItem>();

        // ── MP 事件挂钩 ──
        MissionPlanner.MAVLinkInterface attached;

        // ═══════════════════════ 插件入口 ═══════════════════════

        public override bool Init() { return true; }

        public override bool Loaded()
        {
            loopratehz = 1;
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "zh_serial_loaded.txt"),
                    "loaded " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            catch { }
            return true;
        }

        public override bool Loop()
        {
            try
            {
                Form mf = Host.MainForm as Form;
                if (mf == null || mf.IsDisposed) return true;
                if (mf.InvokeRequired)
                    mf.BeginInvoke((Action)delegate { EnsureUI(); AttachMp(); UpdateSrc(); });
                else
                { EnsureUI(); AttachMp(); UpdateSrc(); }
            }
            catch { }
            return true;
        }

        public override bool Exit()
        {
            try { DetachMp(); } catch { }
            try { if (uiTimer != null) uiTimer.Stop(); } catch { }
            try { if (fm != null && !fm.IsDisposed) fm.Dispose(); } catch { }
            return true;
        }

        // ═══════════════════════ 勾选框注入 ═══════════════════════

        void EnsureUI()
        {
            if (uiAdded) return;
            Form mf = Host.MainForm as Form;
            if (mf == null || mf.IsDisposed) return;
            retries++;

            // 位置约定：排在「飞控网口」勾选框右侧。
            // zh-net 是靠「玄云ECU」定位的，如果本插件先跑完就会占它的位置，所以要等 CHK_net 出现。
            CheckBox anchor = FindControl<CheckBox>(mf, "CHK_net");
            if (anchor == null && retries < 8) return;          // 最多等 8 秒（loopratehz = 1）

            if (anchor == null) anchor = FindControl<CheckBox>(mf, "CHK_ecu");
            if (anchor == null)
            {
                anchor = FindCheckByText(mf, "飞控网口");
                if (anchor == null) anchor = FindCheckByText(mf, "玄云ECU");
                if (anchor == null) anchor = FindCheckByText(mf, "自动平移");
            }
            if (anchor == null)
            {
                if (retries < 40) return;
                try
                {
                    ToolStripMenuItem mi = new ToolStripMenuItem("遥测通信监听");
                    mi.Click += delegate { ToggleForm(); };
                    Host.FDMenuMap.Items.Add(mi);
                }
                catch { }
                uiAdded = true;
                return;
            }

            chkEntry = new CheckBox();
            chkEntry.Text = "遥测通信监听";
            chkEntry.AutoSize = true;
            chkEntry.Name = "CHK_serial";
            chkEntry.Checked = false;
            chkEntry.CheckedChanged += OnEntryChecked;

            Control parent = anchor.Parent;
            parent.Controls.Add(chkEntry);
            chkEntry.BringToFront();
            int x = anchor.Right + 8;
            if (x + 104 > parent.ClientSize.Width)
                x = Math.Max(0, parent.ClientSize.Width - 104);
            chkEntry.Location = new Point(x, anchor.Top);
            uiAdded = true;
        }

        static T FindControl<T>(Control root, string name) where T : Control
        {
            if (root == null) return null;
            if (root.Name == name && root is T) return (T)root;
            foreach (Control c in root.Controls)
            {
                T r = FindControl<T>(c, name);
                if (r != null) return r;
            }
            return null;
        }

        static CheckBox FindCheckByText(Control root, string text)
        {
            if (root == null) return null;
            foreach (Control c in root.Controls)
            {
                CheckBox cb = c as CheckBox;
                if (cb != null && cb.Text != null &&
                    cb.Text.Replace(" ", "") == text.Replace(" ", ""))
                    return cb;
                CheckBox r = FindCheckByText(c, text);
                if (r != null) return r;
            }
            return null;
        }

        void OnEntryChecked(object sender, EventArgs e)
        {
            try
            {
                if (chkEntry.Checked) { EnsureForm(); fm.Show(); fm.BringToFront(); }
                else if (fm != null && !fm.IsDisposed) fm.Hide();
            }
            catch { }
        }

        void ToggleForm()
        {
            if (fm == null || fm.IsDisposed) EnsureForm();
            if (fm.Visible) fm.Hide(); else { fm.Show(); fm.BringToFront(); }
            try { if (chkEntry != null) chkEntry.Checked = fm.Visible; } catch { }
        }

        void FmClosing(object s, FormClosingEventArgs e)
        {
            if (e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            fm.Hide();
            try { if (chkEntry != null) chkEntry.Checked = false; } catch { }
        }

        // ═══════════════════════ 界面构建 ═══════════════════════

        void EnsureForm()
        {
            if (fm != null && !fm.IsDisposed) return;

            fm = new Form();
            fm.Text = "遥测通信监听 v" + Version;
            fm.Size = new Size(1020, 700);
            fm.MinimumSize = new Size(880, 540);
            fm.StartPosition = FormStartPosition.CenterScreen;
            fm.ShowIcon = false;
            fm.ShowInTaskbar = true;
            fm.FormClosing += FmClosing;
            fm.Shown += delegate
            {
                try { split.SplitterDistance = 400; } catch { }
            };

            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 78;
            fm.Controls.Add(top);

            // ── 第一行：源 + 操作按钮 ──
            lblSrc = new Label();
            lblSrc.Text = "源：MP 未连接";
            lblSrc.AutoSize = false;
            lblSrc.TextAlign = ContentAlignment.MiddleLeft;
            lblSrc.Location = new Point(10, 8);
            lblSrc.Size = new Size(430, 22);
            lblSrc.ForeColor = Color.Black;
            top.Controls.Add(lblSrc);

            btnPause = new Button();
            btnPause.Text = "暂停";
            btnPause.Location = new Point(548, 6);
            btnPause.Size = new Size(70, 26);
            btnPause.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnPause.Click += delegate
            {
                paused = !paused;
                btnPause.Text = paused ? "继续" : "暂停";
                SetState(paused ? "已暂停（仍在统计）" : "监听中");
            };
            top.Controls.Add(btnPause);

            btnClear = new Button();
            btnClear.Text = "清空";
            btnClear.Location = new Point(624, 6);
            btnClear.Size = new Size(70, 26);
            btnClear.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnClear.Click += delegate { ClearAll(); };
            top.Controls.Add(btnClear);

            btnSave = new Button();
            btnSave.Text = "保存日志";
            btnSave.Location = new Point(700, 6);
            btnSave.Size = new Size(88, 26);
            btnSave.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnSave.Click += delegate { SaveLog(); };
            top.Controls.Add(btnSave);

            btnRaw = new Button();
            btnRaw.Text = "导出原始帧";
            btnRaw.Location = new Point(794, 6);
            btnRaw.Size = new Size(100, 26);
            btnRaw.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnRaw.Click += delegate { ExportRaw(); };
            top.Controls.Add(btnRaw);

            chkTopMost = new CheckBox();
            chkTopMost.Text = "置顶";
            chkTopMost.AutoSize = true;
            chkTopMost.Location = new Point(904, 11);
            chkTopMost.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            chkTopMost.CheckedChanged += delegate { try { fm.TopMost = chkTopMost.Checked; } catch { } };
            top.Controls.Add(chkTopMost);

            // ── 第二行：过滤 ──
            Label lf = new Label();
            lf.Text = "关键字：";
            lf.AutoSize = true;
            lf.Location = new Point(10, 49);
            lf.ForeColor = Color.Black;
            top.Controls.Add(lf);

            txtFilter = new TextBox();
            txtFilter.Location = new Point(64, 46);
            txtFilter.Width = 180;
            txtFilter.BorderStyle = BorderStyle.FixedSingle;
            top.Controls.Add(txtFilter);

            Label lt = new Label();
            lt.Text = "消息类型：";
            lt.AutoSize = true;
            lt.Location = new Point(254, 49);
            lt.ForeColor = Color.Black;
            top.Controls.Add(lt);

            cmbMsgType = new ComboBox();
            cmbMsgType.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbMsgType.Location = new Point(324, 46);
            cmbMsgType.Width = 168;
            cmbMsgType.Items.Add("全部消息类型");
            cmbMsgType.SelectedIndex = 0;
            cmbMsgType.SelectedIndexChanged += delegate { OnTypeChanged(); };
            top.Controls.Add(cmbMsgType);

            btnPickType = new Button();
            btnPickType.Text = "多选…";
            btnPickType.Location = new Point(498, 45);
            btnPickType.Size = new Size(64, 24);
            btnPickType.Click += delegate { PickTypes(); };
            top.Controls.Add(btnPickType);

            cmbDir = new ComboBox();
            cmbDir.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbDir.Location = new Point(570, 46);
            cmbDir.Width = 86;
            cmbDir.Items.AddRange(new object[] { "全部方向", "仅接收", "仅发送" });
            cmbDir.SelectedIndex = 0;
            top.Controls.Add(cmbDir);

            chkTextOnly = new CheckBox();
            chkTextOnly.Text = "只看文本/打印";
            chkTextOnly.AutoSize = true;
            chkTextOnly.Location = new Point(666, 48);
            top.Controls.Add(chkTextOnly);

            chkAutoScroll = new CheckBox();
            chkAutoScroll.Text = "自动滚动";
            chkAutoScroll.AutoSize = true;
            chkAutoScroll.Checked = true;
            chkAutoScroll.Location = new Point(778, 48);
            chkAutoScroll.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            top.Controls.Add(chkAutoScroll);

            lblState = new Label();
            lblState.Text = "就绪";
            lblState.AutoSize = true;
            lblState.Location = new Point(868, 49);
            lblState.ForeColor = Color.FromArgb(0, 110, 0);
            lblState.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            top.Controls.Add(lblState);

            // ── 页签 ──
            tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            fm.Controls.Add(tabs);
            fm.Controls.SetChildIndex(tabs, 0);   // Fill 排最前，先分给 Top/Bottom 再填满

            // ── 报文页 ──
            TabPage p1 = new TabPage("报文");
            p1.BackColor = Color.White;
            tabs.TabPages.Add(p1);

            split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Horizontal;
            p1.Controls.Add(split);

            grid = new DataGridView();
            grid.Dock = DockStyle.Fill;
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.MultiSelect = true;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.BackgroundColor = Color.White;
            grid.GridColor = Color.FromArgb(224, 224, 224);
            grid.BorderStyle = BorderStyle.None;
            grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei", 9F);
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(245, 245, 245);
            grid.EnableHeadersVisualStyles = false;
            grid.DefaultCellStyle.Font = new Font("Consolas", 9F);
            grid.DefaultCellStyle.BackColor = Color.White;
            grid.DefaultCellStyle.ForeColor = Color.Black;
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(51, 153, 255);
            grid.DefaultCellStyle.SelectionForeColor = Color.White;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            grid.SelectionChanged += delegate { ShowDetail(); };
            AddCol("时间", 92);
            AddCol("方向", 50);
            AddCol("消息", 176);
            AddCol("ID", 56);
            AddCol("系统/组件", 78);
            AddCol("序号", 48);
            AddCol("长度", 48);
            GridFill(AddCol("内容", 360));
            split.Panel1.Controls.Add(grid);

            ContextMenuStrip cms = new ContextMenuStrip();
            cms.Items.Add("复制选中行", null, delegate { CopyRows(true); });
            cms.Items.Add("复制全部", null, delegate { CopyRows(false); });
            cms.Items.Add(new ToolStripSeparator());
            cms.Items.Add("导出选中原始帧…", null, delegate { ExportSelectedRaw(); });
            grid.ContextMenuStrip = cms;

            Panel dp = new Panel();
            dp.Dock = DockStyle.Fill;
            dp.BackColor = Color.White;
            split.Panel2.Controls.Add(dp);

            Label ldt = new Label();
            ldt.Text = "详情（选中报文行后显示字段与十六进制）";
            ldt.Dock = DockStyle.Top;
            ldt.Height = 20;
            ldt.ForeColor = Color.Black;
            ldt.BackColor = Color.FromArgb(245, 245, 245);
            dp.Controls.Add(ldt);

            txtDetail = new TextBox();
            txtDetail.Multiline = true;
            txtDetail.ReadOnly = true;
            txtDetail.ScrollBars = ScrollBars.Both;
            txtDetail.WordWrap = false;
            txtDetail.Dock = DockStyle.Fill;
            txtDetail.Font = new Font("Consolas", 9F);
            txtDetail.BackColor = Color.White;
            txtDetail.ForeColor = Color.Black;
            txtDetail.BorderStyle = BorderStyle.None;
            dp.Controls.Add(txtDetail);
            txtDetail.BringToFront();

            // ── 发送页 ──
            TabPage pSend = new TabPage("发送");
            pSend.BackColor = Color.White;
            tabs.TabPages.Add(pSend);
            BuildSendPage(pSend);

            // ── 打印页 ──
            TabPage p2 = new TabPage("打印");
            p2.BackColor = Color.White;
            tabs.TabPages.Add(p2);

            txtPrint = new RichTextBox();
            txtPrint.Dock = DockStyle.Fill;
            txtPrint.ReadOnly = true;
            txtPrint.WordWrap = false;
            txtPrint.BackColor = Color.White;
            txtPrint.ForeColor = Color.Black;
            txtPrint.Font = new Font("Consolas", 9F);
            txtPrint.BorderStyle = BorderStyle.None;
            p2.Controls.Add(txtPrint);

            StatusStrip ss = new StatusStrip();
            lblCnt = new ToolStripStatusLabel("收 0 / 发 0");
            lblRate = new ToolStripStatusLabel("0 B/s");
            lblRate.BorderSides = ToolStripStatusLabelBorderSides.Left;
            ss.Items.Add(lblCnt);
            ss.Items.Add(lblRate);
            fm.Controls.Add(ss);

            uiTimer = new System.Windows.Forms.Timer();
            uiTimer.Interval = 200;
            uiTimer.Tick += delegate { Flush(); };
            uiTimer.Start();

            PrintSys("就绪。监听 MP 当前连接的全部收发报文；「发送」页可直接下发 MAV_CMD 命令与写参数。");
        }

        DataGridViewTextBoxColumn AddCol(string text, int width)
        {
            DataGridViewTextBoxColumn c = new DataGridViewTextBoxColumn();
            c.HeaderText = text;
            c.Width = width;
            c.SortMode = DataGridViewColumnSortMode.NotSortable;
            c.Resizable = DataGridViewTriState.True;
            grid.Columns.Add(c);
            return c;
        }

        static void GridFill(DataGridViewColumn c)
        {
            c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            c.MinimumWidth = 200;
        }

        // ═══════════════════════ 发送页 ═══════════════════════

        // 常用命令：ID + 显示名 + 参数提示
        static readonly int[] CMD_IDS = new int[]
        { 400, 22, 21, 20, 176, 178, 183, 179, 300, 511, 512, 246, 241, 16 };
        static readonly string[] CMD_LABELS = new string[]
        {
            "400 · 解锁 / 上锁",
            "22  · 起飞 TAKEOFF",
            "21  · 降落 LAND",
            "20  · 返航 RTL",
            "176 · 设置模式 DO_SET_MODE",
            "178 · 修改速度 CHANGE_SPEED",
            "183 · 设置舵机 DO_SET_SERVO",
            "179 · 设置 Home",
            "300 · 开始任务 MISSION_START",
            "511 · 设置消息频率 SET_MESSAGE_INTERVAL",
            "512 · 请求消息 REQUEST_MESSAGE",
            "246 · 重启飞控 REBOOT",
            "241 · 预检校准 CALIBRATION",
            "16  · 航点 NAV_WAYPOINT"
        };
        static readonly string[] CMD_HINTS = new string[]
        {
            "P1 = 1 解锁 / 0 上锁；P2 = 21196 可强制（危险，飞控可能拒绝）",
            "P7 = 起飞高度（米）；多旋翼需先解锁",
            "P1 = 0 降落到当前位置",
            "返航到 Home 点",
            "P1 = 模式号（Copter：0Stab 1Acro 2AltHold 3Auto 4Guided 5Loiter 6RTL 7Circle 9Land 16PosHold 17Brake）；P2 = 1",
            "P1 = 0 空速 / 1 地速；P2 = 速度 m/s",
            "P1 = 舵机输出号；P2 = PWM（1000~2000）",
            "P1 = 0 表示当前位置设为 Home",
            "开始执行已上传的任务",
            "P1 = 消息ID；P2 = 间隔(微秒)，-1 恢复默认 —— 用它提高/降低某类遥测频率",
            "P1 = 消息ID，请求飞控立刻发一次",
            "P1 = 1 重启飞控",
            "P1 = 1 陀螺 / P2 = 1 磁罗盘 / P3 = 1 加速度计（谨慎，飞控会要求静止）",
            "航点导航，一般由任务自动下发"
        };

        void BuildSendPage(TabPage page)
        {
            GroupBox g1 = new GroupBox();
            g1.Text = "发送 MAV_CMD 命令（COMMAND_LONG）";
            g1.Location = new Point(10, 8);
            g1.Size = new Size(680, 182);
            g1.ForeColor = Color.Black;
            page.Controls.Add(g1);

            Label l1 = new Label();
            l1.Text = "目标 Sysid";
            l1.AutoSize = true;
            l1.Location = new Point(12, 26);
            l1.ForeColor = Color.Black;
            g1.Controls.Add(l1);

            numSys = new NumericUpDown();
            numSys.Minimum = 0;
            numSys.Maximum = 255;
            numSys.Value = 1;
            numSys.Location = new Point(86, 24);
            numSys.Width = 54;
            g1.Controls.Add(numSys);

            Label lc = new Label();
            lc.Text = "Compid";
            lc.AutoSize = true;
            lc.Location = new Point(150, 26);
            lc.ForeColor = Color.Black;
            g1.Controls.Add(lc);

            numComp = new NumericUpDown();
            numComp.Minimum = 0;
            numComp.Maximum = 255;
            numComp.Value = 1;
            numComp.Location = new Point(200, 24);
            numComp.Width = 54;
            g1.Controls.Add(numComp);

            Label l2 = new Label();
            l2.Text = "命令";
            l2.AutoSize = true;
            l2.Location = new Point(266, 26);
            l2.ForeColor = Color.Black;
            g1.Controls.Add(l2);

            cmbCmd = new ComboBox();
            cmbCmd.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbCmd.Location = new Point(300, 24);
            cmbCmd.Width = 200;
            cmbCmd.Items.AddRange(CMD_LABELS);
            cmbCmd.SelectedIndex = 0;
            cmbCmd.SelectedIndexChanged += delegate { SyncCmdFromCombo(); };
            g1.Controls.Add(cmbCmd);

            Label l3 = new Label();
            l3.Text = "ID";
            l3.AutoSize = true;
            l3.Location = new Point(510, 26);
            l3.ForeColor = Color.Black;
            g1.Controls.Add(l3);

            numCmd = new NumericUpDown();
            numCmd.Minimum = 0;
            numCmd.Maximum = 65535;
            numCmd.Value = 400;
            numCmd.Location = new Point(534, 24);
            numCmd.Width = 74;
            numCmd.ValueChanged += delegate { SyncHint(); };
            g1.Controls.Add(numCmd);

            for (int i = 0; i < 7; i++)
            {
                Label lp = new Label();
                lp.Text = "P" + (i + 1);
                lp.AutoSize = true;
                lp.Location = new Point(12 + (i % 4) * 164, 62 + (i / 4) * 34);
                lp.ForeColor = Color.Black;
                g1.Controls.Add(lp);

                NumericUpDown n = new NumericUpDown();
                n.Minimum = -1000000;
                n.Maximum = 1000000;
                n.DecimalPlaces = 3;
                n.Increment = 1;
                n.Location = new Point(38 + (i % 4) * 164, 60 + (i / 4) * 34);
                n.Width = 104;
                g1.Controls.Add(n);
                numP[i] = n;
            }

            lblCmdHint = new Label();
            lblCmdHint.AutoSize = false;
            lblCmdHint.Location = new Point(12, 128);
            lblCmdHint.Size = new Size(600, 18);
            lblCmdHint.ForeColor = Color.FromArgb(90, 90, 90);
            lblCmdHint.Text = CMD_HINTS[0];
            g1.Controls.Add(lblCmdHint);

            Button btnSend = new Button();
            btnSend.Text = "发送命令";
            btnSend.Location = new Point(12, 148);
            btnSend.Size = new Size(96, 26);
            btnSend.Click += delegate { SendCommand(); };
            g1.Controls.Add(btnSend);

            Label l4 = new Label();
            l4.Text = "（发送后可在「报文」页看到发出的包和飞控回的 COMMAND_ACK）";
            l4.AutoSize = true;
            l4.Location = new Point(116, 154);
            l4.ForeColor = Color.Gray;
            g1.Controls.Add(l4);

            GroupBox g2 = new GroupBox();
            g2.Text = "写入参数（PARAM_SET）";
            g2.Location = new Point(10, 198);
            g2.Size = new Size(680, 74);
            g2.ForeColor = Color.Black;
            page.Controls.Add(g2);

            Label lp1 = new Label();
            lp1.Text = "参数名";
            lp1.AutoSize = true;
            lp1.Location = new Point(12, 30);
            lp1.ForeColor = Color.Black;
            g2.Controls.Add(lp1);

            txtParamName = new TextBox();
            txtParamName.Location = new Point(64, 28);
            txtParamName.Width = 190;
            txtParamName.BorderStyle = BorderStyle.FixedSingle;
            txtParamName.CharacterCasing = CharacterCasing.Upper;
            g2.Controls.Add(txtParamName);

            Label lp2 = new Label();
            lp2.Text = "值";
            lp2.AutoSize = true;
            lp2.Location = new Point(266, 30);
            lp2.ForeColor = Color.Black;
            g2.Controls.Add(lp2);

            numParamValue = new NumericUpDown();
            numParamValue.Minimum = -1000000;
            numParamValue.Maximum = 1000000;
            numParamValue.DecimalPlaces = 4;
            numParamValue.Location = new Point(288, 28);
            numParamValue.Width = 110;
            g2.Controls.Add(numParamValue);

            Button btnWrite = new Button();
            btnWrite.Text = "写入";
            btnWrite.Location = new Point(410, 26);
            btnWrite.Size = new Size(70, 26);
            btnWrite.Click += delegate { WriteParam(); };
            g2.Controls.Add(btnWrite);

            Label lp3 = new Label();
            lp3.Text = "例：FLTMODE1=0 或 SERVO1_FUNCTION=70（写前请确认参数名）";
            lp3.AutoSize = true;
            lp3.Location = new Point(492, 30);
            lp3.ForeColor = Color.Gray;
            g2.Controls.Add(lp3);

            lblSent = new Label();
            lblSent.AutoSize = false;
            lblSent.Location = new Point(10, 282);
            lblSent.Size = new Size(680, 40);
            lblSent.ForeColor = Color.FromArgb(0, 110, 0);
            lblSent.Text = "尚未发送。";
            page.Controls.Add(lblSent);
        }

        void SyncCmdFromCombo()
        {
            int idx = cmbCmd.SelectedIndex;
            if (idx < 0 || idx >= CMD_IDS.Length) return;
            try { numCmd.Value = CMD_IDS[idx]; } catch { }
            SyncHint();
        }

        void SyncHint()
        {
            if (lblCmdHint == null || lblCmdHint.IsDisposed) return;
            int id = (int)numCmd.Value;
            string hint = "自定义命令 ID：" + id + "（参数含义请查 MAV_CMD 表）";
            for (int i = 0; i < CMD_IDS.Length; i++)
                if (CMD_IDS[i] == id) { hint = CMD_HINTS[i]; break; }
            lblCmdHint.Text = hint;
        }

        bool EnsureConnected()
        {
            try
            {
                if (MainV2.comPort != null && MainV2.comPort.BaseStream != null && MainV2.comPort.BaseStream.IsOpen)
                    return true;
            }
            catch { }
            MessageBox.Show(fm, "MP 还没有连接飞控。\n请先在 MP 里连接，再发送指令。",
                "未连接", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        void SendCommand()
        {
            if (!EnsureConnected()) return;
            byte sysid = (byte)numSys.Value;   // MP 1.3.83 的重载是 (byte sysid, byte compid, ...)
            byte compid = (byte)numComp.Value;
            int cmdId = (int)numCmd.Value;
            float[] p = new float[7];
            for (int i = 0; i < 7; i++) p[i] = (float)numP[i].Value;

            string arg = "";
            for (int i = 0; i < 7; i++) arg += (i > 0 ? " " : "") + p[i].ToString("0.###");
            PrintSys("→ 发送 COMMAND_LONG cmd=" + cmdId + "  " + arg);
            lblSent.ForeColor = Color.Gray;
            lblSent.Text = "正在发送 cmd=" + cmdId + " …";

            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = false;
                string err = "";
                try
                {
                    ok = MainV2.comPort.doCommand(sysid, compid, (MAVLink.MAV_CMD)cmdId,
                        p[0], p[1], p[2], p[3], p[4], p[5], p[6], false);
                }
                catch (Exception ex) { err = ex.Message; }

                string msg = ok
                    ? "已发送 cmd=" + cmdId + "（" + DateTime.Now.ToString("HH:mm:ss") + "）"
                    : "发送失败 cmd=" + cmdId + (err.Length > 0 ? "：" + err : "");
                bool okk = ok;
                UiPost(delegate
                {
                    lblSent.ForeColor = okk ? Color.FromArgb(0, 110, 0) : Color.FromArgb(190, 30, 30);
                    lblSent.Text = msg;
                    PrintSys(msg);
                });
            });
        }

        void WriteParam()
        {
            if (!EnsureConnected()) return;
            string name = txtParamName.Text == null ? "" : txtParamName.Text.Trim();
            if (name.Length == 0)
            {
                MessageBox.Show(fm, "请先填参数名（例如 FLTMODE1）", "参数名",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            byte sysid = (byte)numSys.Value;
            byte compid = (byte)numComp.Value;
            double val = (double)numParamValue.Value;

            PrintSys("→ 写入参数 " + name + " = " + val);
            lblSent.ForeColor = Color.Gray;
            lblSent.Text = "正在写入 " + name + " …";

            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = false;
                string err = "";
                try { ok = MainV2.comPort.setParam(sysid, compid, name, val, false); }
                catch (Exception ex) { err = ex.Message; }

                string msg = ok
                    ? "参数已写入 " + name + " = " + val
                    : "参数写入失败 " + name + (err.Length > 0 ? "：" + err : "");
                bool okk = ok;
                UiPost(delegate
                {
                    lblSent.ForeColor = okk ? Color.FromArgb(0, 110, 0) : Color.FromArgb(190, 30, 30);
                    lblSent.Text = msg;
                    PrintSys(msg);
                });
            });
        }

        void UiPost(Action a)
        {
            try
            {
                if (fm != null && !fm.IsDisposed)
                    fm.BeginInvoke(a);
            }
            catch { }
        }

        // ═══════════════════════ 消息类型过滤 ═══════════════════════

        void OnTypeChanged()
        {
            int idx = cmbMsgType.SelectedIndex;
            if (idx <= 0)
            {
                selTypes = null;
                SetState("消息类型：全部");
                return;
            }
            TypeItem it = cmbMsgType.Items[idx] as TypeItem;
            if (it == null) { selTypes = null; return; }
            HashSet<uint> s = new HashSet<uint>();
            s.Add(it.Id);
            selTypes = s;
            SetState("消息类型：" + it.Name);
        }

        void PickTypes()
        {
            List<TypeItem> all = new List<TypeItem>();
            if (cmbMsgType != null)
            {
                for (int i = 0; i < cmbMsgType.Items.Count; i++)
                {
                    TypeItem ti = cmbMsgType.Items[i] as TypeItem;
                    if (ti != null) all.Add(ti);
                }
            }
            if (all.Count == 0)
            {
                MessageBox.Show(fm, "还没有收到任何报文，等一下再来选。", "消息类型",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            all.Sort(delegate (TypeItem a, TypeItem b) { return string.Compare(a.Name, b.Name); });

            Form f = new Form();
            f.Text = "选择要显示的消息类型（可多选）";
            f.Size = new Size(360, 520);
            f.MinimumSize = new Size(300, 360);
            f.StartPosition = FormStartPosition.CenterParent;
            f.MinimizeBox = false;
            f.MaximizeBox = false;
            f.ShowIcon = false;

            Panel pnlBottom = new Panel();
            pnlBottom.Dock = DockStyle.Bottom;
            pnlBottom.Height = 42;
            f.Controls.Add(pnlBottom);

            Button bAll = new Button();
            bAll.Text = "全选";
            bAll.Location = new Point(8, 8);
            bAll.Size = new Size(64, 26);
            pnlBottom.Controls.Add(bAll);

            Button bNone = new Button();
            bNone.Text = "全不选";
            bNone.Location = new Point(78, 8);
            bNone.Size = new Size(72, 26);
            pnlBottom.Controls.Add(bNone);

            Button bOk = new Button();
            bOk.Text = "确定";
            bOk.Location = new Point(190, 8);
            bOk.Size = new Size(70, 26);
            bOk.DialogResult = DialogResult.OK;
            pnlBottom.Controls.Add(bOk);

            Button bCancel = new Button();
            bCancel.Text = "取消";
            bCancel.Location = new Point(266, 8);
            bCancel.Size = new Size(70, 26);
            bCancel.DialogResult = DialogResult.Cancel;
            pnlBottom.Controls.Add(bCancel);

            CheckedListBox clb = new CheckedListBox();
            clb.Dock = DockStyle.Fill;
            clb.CheckOnClick = true;
            clb.Font = new Font("Consolas", 9F);
            for (int i = 0; i < all.Count; i++)
            {
                bool sel = selTypes != null && selTypes.Contains(all[i].Id);
                clb.Items.Add(all[i], sel);
            }
            f.Controls.Add(clb);
            clb.BringToFront();

            bAll.Click += delegate
            {
                for (int i = 0; i < clb.Items.Count; i++) clb.SetItemChecked(i, true);
            };
            bNone.Click += delegate
            {
                for (int i = 0; i < clb.Items.Count; i++) clb.SetItemChecked(i, false);
            };

            f.AcceptButton = bOk;
            f.CancelButton = bCancel;

            if (f.ShowDialog(fm) != DialogResult.OK) return;

            HashSet<uint> picked = new HashSet<uint>();
            for (int i = 0; i < clb.Items.Count; i++)
            {
                if (!clb.GetItemChecked(i)) continue;
                TypeItem ti = clb.Items[i] as TypeItem;
                if (ti != null) picked.Add(ti.Id);
            }
            if (picked.Count == 0)
            {
                selTypes = null;
                try { cmbMsgType.SelectedIndex = 0; } catch { }
                SetState("消息类型：全部");
                return;
            }
            selTypes = picked;
            SetState("消息类型：已选 " + picked.Count + " 种");
        }

        // ═══════════════════════ MP 连接透听 ═══════════════════════

        void AttachMp()
        {
            try
            {
                if (MainV2.comPort == null) return;
                if (ReferenceEquals(MainV2.comPort, attached)) return;
                DetachMp();
                attached = MainV2.comPort;
                attached.OnPacketReceived += OnRecv;
                attached.OnPacketSent += OnSent;
            }
            catch { }
        }

        void DetachMp()
        {
            if (attached == null) return;
            try { attached.OnPacketReceived -= OnRecv; } catch { }
            try { attached.OnPacketSent -= OnSent; } catch { }
            attached = null;
        }

        void OnRecv(object sender, MAVLink.MAVLinkMessage m) { Ingest(m, false); }
        void OnSent(object sender, MAVLink.MAVLinkMessage m) { Ingest(m, true); }

        void Ingest(MAVLink.MAVLinkMessage m, bool sent)
        {
            try
            {
                RowInfo r = new RowInfo();
                r.Time = DateTime.Now;
                r.Sent = sent;
                r.MsgId = (uint)m.msgid;
                r.Name = MsgName(r.MsgId);
                r.SysId = m.sysid;
                r.CompId = m.compid;
                r.Seq = m.seq;
                r.Raw = m.buffer;
                r.Data = m.data;
                r.Len = r.Raw != null ? r.Raw.Length : 0;
                r.Payload = PayloadLen(r.Raw);
                r.Content = BuildContent(r);

                NoteType(r.MsgId, r.Name);

                if (r.Len > 0) Interlocked.Add(ref cBytes, r.Len);
                if (r.Sent) Interlocked.Increment(ref cTx); else Interlocked.Increment(ref cRx);

                if (paused) return;
                HashSet<uint> types = selTypes;
                if (types != null && !types.Contains(r.MsgId)) return;
                lock (pending)
                {
                    if (pending.Count >= MaxPending) { Interlocked.Increment(ref cDrop); return; }
                    pending.Add(r);
                }
            }
            catch { }
        }

        void NoteType(uint id, string name)
        {
            lock (typeLock)
            {
                if (!typeSeen.Add(id)) return;
                typePending.Add(new TypeItem(id, name));
            }
        }

        static int PayloadLen(byte[] b)
        {
            if (b == null || b.Length < 2) return 0;
            return b[1];
        }

        static string MsgName(uint id)
        {
            Dictionary<uint, string> map = NameMap();
            string n;
            if (map.TryGetValue(id, out n) && !string.IsNullOrEmpty(n)) return n;
            return "ID_" + id.ToString();
        }

        static Dictionary<uint, string> nameMap;

        static Dictionary<uint, string> NameMap()
        {
            if (nameMap != null) return nameMap;
            Dictionary<uint, string> d = new Dictionary<uint, string>();
            try
            {
                foreach (object v in Enum.GetValues(typeof(MAVLink.MAVLINK_MSG_ID)))
                {
                    try
                    {
                        uint k = Convert.ToUInt32(v);
                        if (!d.ContainsKey(k)) d[k] = v.ToString();
                    }
                    catch { }
                }
            }
            catch { }
            nameMap = d;
            return d;
        }

        // ═══════════════════════ 内容摘要 ═══════════════════════

        static readonly Dictionary<Type, FieldInfo[]> fieldCache = new Dictionary<Type, FieldInfo[]>();

        static FieldInfo[] GetFields(Type t)
        {
            lock (fieldCache)
            {
                FieldInfo[] f;
                if (fieldCache.TryGetValue(t, out f)) return f;
                try { f = t.GetFields(BindingFlags.Public | BindingFlags.Instance); }
                catch { f = new FieldInfo[0]; }
                fieldCache[t] = f;
                return f;
            }
        }

        static object SafeGet(FieldInfo f, object o)
        {
            try { return f.GetValue(o); } catch { return null; }
        }

        static string BuildContent(RowInfo r)
        {
            object d = r.Data;
            if (d == null) return HexShort(r.Raw, 24);

            FieldInfo[] fs = GetFields(d.GetType());
            object sev = null;
            string text = null;
            for (int i = 0; i < fs.Length; i++)
            {
                string fn = fs[i].Name;
                if (fn == "severity") sev = SafeGet(fs[i], d);
                else if (fn == "text") text = SafeGet(fs[i], d) as string;
            }
            if (text != null && text.Trim().Length > 0)
            {
                string s = text.Trim();
                int si = ToIntVal(sev, -1);
                if (si >= 0) return "[" + SevName(si) + "] " + s;
                return s;
            }

            StringBuilder sb = new StringBuilder();
            int used = 0;
            for (int i = 0; i < fs.Length && used < 5; i++)
            {
                try
                {
                    object v = SafeGet(fs[i], d);
                    if (v is Array && !(v is byte[])) continue;
                    if (sb.Length > 0) sb.Append("  ");
                    sb.Append(fs[i].Name).Append('=').Append(FmtVal(v));
                    used++;
                }
                catch { }
            }
            string outp = sb.ToString();
            if (outp.Length > 300) outp = outp.Substring(0, 300) + "…";
            return outp;
        }

        static int ToIntVal(object v, int dflt)
        {
            if (v == null) return dflt;
            try { return Convert.ToInt32(v); } catch { return dflt; }
        }

        static string SevName(int s)
        {
            switch (s)
            {
                case 0: return "紧急";
                case 1: return "警报";
                case 2: return "严重";
                case 3: return "错误";
                case 4: return "警告";
                case 5: return "注意";
                case 6: return "信息";
                case 7: return "调试";
                default: return "级别" + s;
            }
        }

        static Color SevColor(int s)
        {
            if (s <= 3) return Color.FromArgb(190, 30, 30);
            if (s == 4) return Color.FromArgb(200, 110, 0);
            if (s == 5) return Color.FromArgb(0, 90, 190);
            if (s == 7) return Color.Gray;
            return Color.Black;
        }

        static string FmtVal(object v)
        {
            if (v == null) return "null";
            string s = v as string;
            if (s != null) return s;
            if (v is float) return ((float)v).ToString("0.###");
            if (v is double) return ((double)v).ToString("0.######");
            if (v is bool) return ((bool)v) ? "1" : "0";
            byte[] b = v as byte[];
            if (b != null)
            {
                int printable = 0;
                for (int i = 0; i < b.Length; i++)
                    if (b[i] >= 32 && b[i] < 127) printable++;
                if (b.Length > 0 && printable * 2 >= b.Length)
                {
                    string t = Encoding.ASCII.GetString(b).TrimEnd('\0').Trim();
                    if (t.Length > 0 && t.Length <= 64) return t;
                }
                return HexShort(b, 8);
            }
            Array a = v as Array;
            if (a != null) return "[" + a.Length + "]";
            return v.ToString();
        }

        static string HexShort(byte[] b, int max)
        {
            if (b == null || b.Length == 0) return "";
            StringBuilder sb = new StringBuilder();
            int n = Math.Min(max, b.Length);
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(b[i].ToString("X2"));
            }
            if (b.Length > n) sb.Append(" …");
            return sb.ToString();
        }

        // ═══════════════════════ 过滤 / 上屏 ═══════════════════════

        bool Pass(RowInfo r)
        {
            if (fltTextOnly && r.Name != "STATUSTEXT") return false;
            if (fltDir == 1 && r.Sent) return false;
            if (fltDir == 2 && !r.Sent) return false;
            string f = fltText;
            if (f != null && f.Length > 0)
            {
                string[] parts = f.Split(new char[] { ',', ';', '|', ' ' });
                bool any = false;
                for (int i = 0; i < parts.Length; i++)
                {
                    string p = parts[i].Trim();
                    if (p.Length == 0) continue;
                    if (r.Name != null && r.Name.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) { any = true; break; }
                    if (r.Content != null && r.Content.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0) { any = true; break; }
                }
                if (!any) return false;
            }
            return true;
        }

        void Flush()
        {
            if (fm == null || fm.IsDisposed) return;

            // 控件只能在 UI 线程读，映射成快照给收包线程用
            try
            {
                fltTextOnly = chkTextOnly != null && chkTextOnly.Checked;
                fltDir = cmbDir == null ? 0 : cmbDir.SelectedIndex;
                string t = txtFilter == null ? "" : txtFilter.Text;
                fltText = t == null ? "" : t.Trim();
            }
            catch { }

            // 新出现的消息类型补进下拉
            List<TypeItem> add = null;
            lock (typeLock)
            {
                if (typePending.Count > 0)
                {
                    add = new List<TypeItem>(typePending);
                    typePending.Clear();
                }
            }
            if (add != null && cmbMsgType != null)
            {
                for (int i = 0; i < add.Count; i++) cmbMsgType.Items.Add(add[i]);
            }

            List<RowInfo> batch = null;
            lock (pending)
            {
                if (pending.Count > 0)
                {
                    batch = new List<RowInfo>(pending);
                    pending.Clear();
                }
            }
            if (batch != null)
            {
                List<RowInfo> shown = new List<RowInfo>();
                for (int i = 0; i < batch.Count; i++)
                    if (Pass(batch[i])) shown.Add(batch[i]);

                if (shown.Count > 0 && grid != null && !grid.IsDisposed)
                {
                    grid.SuspendLayout();
                    try
                    {
                        for (int i = 0; i < shown.Count; i++) AddRow(shown[i]);
                        int over = grid.RowCount - MaxRows;
                        for (int i = 0; i < over; i++) grid.Rows.RemoveAt(0);
                    }
                    catch { }
                    grid.ResumeLayout();
                    if (chkAutoScroll != null && chkAutoScroll.Checked && grid.RowCount > 0)
                    {
                        try { grid.FirstDisplayedScrollingRowIndex = grid.RowCount - 1; } catch { }
                    }
                }
                if (shown.Count > 0) PrintRows(shown);
            }
            UpdateStats();
        }

        void AddRow(RowInfo r)
        {
            int idx = grid.Rows.Add();
            DataGridViewRow row = grid.Rows[idx];
            row.Cells[0].Value = r.Time.ToString("HH:mm:ss.fff");
            row.Cells[1].Value = r.Sent ? "发" : "收";
            row.Cells[2].Value = r.Name;
            row.Cells[3].Value = r.MsgId.ToString();
            row.Cells[4].Value = r.SysId + " / " + r.CompId;
            row.Cells[5].Value = r.Seq.ToString();
            row.Cells[6].Value = r.Len.ToString();
            row.Cells[7].Value = r.Content;
            row.Tag = r;
            if (r.Sent) row.DefaultCellStyle.BackColor = Color.FromArgb(238, 245, 255);
            else if (r.Name == "STATUSTEXT")
            {
                int sev = 6;
                object d = r.Data;
                if (d != null)
                {
                    FieldInfo[] fs = GetFields(d.GetType());
                    for (int i = 0; i < fs.Length; i++)
                        if (fs[i].Name == "severity") sev = ToIntVal(SafeGet(fs[i], d), 6);
                }
                row.DefaultCellStyle.ForeColor = SevColor(sev);
            }
        }

        void UpdateStats()
        {
            try
            {
                long now = Interlocked.Read(ref cBytes);
                long delta = now - lastBytes;
                lastBytes = now;
                if (lblRate != null) lblRate.Text = FmtRate(delta * 5);
                if (lblCnt != null)
                    lblCnt.Text = "收 " + Interlocked.Read(ref cRx) + " / 发 " + Interlocked.Read(ref cTx)
                        + " / 丢弃 " + Interlocked.Read(ref cDrop);
            }
            catch { }
        }

        static string FmtRate(long bytesPerSec)
        {
            if (bytesPerSec < 1024) return bytesPerSec + " B/s";
            if (bytesPerSec < 1024 * 1024) return (bytesPerSec / 1024.0).ToString("0.0") + " KB/s";
            return (bytesPerSec / 1048576.0).ToString("0.00") + " MB/s";
        }

        void SetState(string s)
        {
            try { if (lblState != null && !lblState.IsDisposed) lblState.Text = s; } catch { }
        }

        void ClearAll()
        {
            lock (pending) pending.Clear();
            try { if (grid != null) grid.Rows.Clear(); } catch { }
            try { if (txtPrint != null) txtPrint.Clear(); } catch { }
            try { if (txtDetail != null) txtDetail.Text = ""; } catch { }
            Interlocked.Exchange(ref cRx, 0);
            Interlocked.Exchange(ref cTx, 0);
            Interlocked.Exchange(ref cBytes, 0);
            Interlocked.Exchange(ref cDrop, 0);
            lastBytes = 0;
            UpdateStats();
        }

        // ═══════════════════════ 详情 ═══════════════════════

        void ShowDetail()
        {
            try
            {
                if (txtDetail == null || txtDetail.IsDisposed) return;
                if (grid == null || grid.SelectedRows.Count == 0) { txtDetail.Text = ""; return; }
                RowInfo r = grid.SelectedRows[0].Tag as RowInfo;
                if (r == null) { txtDetail.Text = ""; return; }

                StringBuilder sb = new StringBuilder();
                sb.Append("时间      : ").Append(r.Time.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("\r\n");
                sb.Append("方向      : ").Append(r.Sent ? "发送" : "接收").Append("\r\n");
                sb.Append("消息      : ").Append(r.Name).Append("  (ID ").Append(r.MsgId).Append(")\r\n");
                sb.Append("系统/组件 : ").Append(r.SysId).Append(" / ").Append(r.CompId).Append("\r\n");
                sb.Append("序号      : ").Append(r.Seq).Append("    帧长: ").Append(r.Len).Append(" B");
                if (r.Payload > 0) sb.Append("    载荷: ").Append(r.Payload).Append(" B");
                sb.Append("\r\n");

                object d = r.Data;
                if (d != null)
                {
                    sb.Append("\r\n── 字段 ──\r\n");
                    FieldInfo[] fs = GetFields(d.GetType());
                    for (int i = 0; i < fs.Length; i++)
                    {
                        object v = SafeGet(fs[i], d);
                        sb.Append(fs[i].Name).Append(" = ").Append(FmtVal(v)).Append("\r\n");
                    }
                }

                sb.Append("\r\n── 原始字节 ──\r\n");
                sb.Append(HexDump(r.Raw));
                txtDetail.Text = sb.ToString();
                txtDetail.SelectionStart = 0;
                txtDetail.ScrollToCaret();
            }
            catch { }
        }

        static string HexDump(byte[] b)
        {
            if (b == null || b.Length == 0) return "(无)\r\n";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < b.Length; i += 16)
            {
                sb.Append(i.ToString("X4")).Append("  ");
                int n = Math.Min(16, b.Length - i);
                for (int j = 0; j < 16; j++)
                {
                    if (j < n) sb.Append(b[i + j].ToString("X2")).Append(' ');
                    else sb.Append("   ");
                }
                sb.Append(" |");
                for (int j = 0; j < n; j++)
                {
                    byte c = b[i + j];
                    sb.Append((c >= 32 && c < 127) ? (char)c : '.');
                }
                sb.Append("|\r\n");
            }
            return sb.ToString();
        }

        // ═══════════════════════ 打印页 ═══════════════════════

        void PrintSys(string s)
        {
            AppendPrint("· " + s, Color.Gray);
        }

        void AppendPrint(string line, Color c)
        {
            if (txtPrint == null || txtPrint.IsDisposed) return;
            try
            {
                if (txtPrint.TextLength > MaxTextChars) txtPrint.Clear();
                txtPrint.SelectionStart = txtPrint.TextLength;
                txtPrint.SelectionLength = 0;
                txtPrint.SelectionColor = c;
                txtPrint.AppendText(line + "\n");
                txtPrint.SelectionStart = txtPrint.TextLength;
                txtPrint.ScrollToCaret();
            }
            catch { }
        }

        void PrintRows(List<RowInfo> rows)
        {
            if (txtPrint == null || txtPrint.IsDisposed) return;
            if (rows == null || rows.Count == 0) return;
            txtPrint.SuspendLayout();
            for (int i = 0; i < rows.Count; i++)
            {
                RowInfo r = rows[i];
                if (r.Name != "STATUSTEXT") continue;
                int sev = 6;
                object d = r.Data;
                if (d != null)
                {
                    FieldInfo[] fs = GetFields(d.GetType());
                    for (int j = 0; j < fs.Length; j++)
                        if (fs[j].Name == "severity") sev = ToIntVal(SafeGet(fs[j], d), 6);
                }
                AppendPrint(r.Time.ToString("HH:mm:ss.fff") + "  " + r.Content, SevColor(sev));
            }
            txtPrint.ResumeLayout();
        }

        // ═══════════════════════ 保存 / 导出 ═══════════════════════

        string FormatRow(RowInfo r)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(r.Time.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append('\t');
            sb.Append(r.Sent ? "发" : "收").Append('\t');
            sb.Append(r.Name).Append('\t');
            sb.Append(r.MsgId.ToString()).Append('\t');
            sb.Append(r.SysId.ToString()).Append('\t');
            sb.Append(r.CompId.ToString()).Append('\t');
            sb.Append(r.Seq.ToString()).Append('\t');
            sb.Append(r.Len.ToString()).Append('\t');
            sb.Append(r.Content);
            if (r.Raw != null) sb.Append('\t').Append(HexShort(r.Raw, 64));
            return sb.ToString();
        }

        void SaveLog()
        {
            if (grid == null || grid.RowCount == 0) { SetState("没有可保存的记录"); return; }
            SaveFileDialog sfd = new SaveFileDialog();
            sfd.Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
            sfd.FileName = "telemetry-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
            if (sfd.ShowDialog() != DialogResult.OK) return;
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("# 遥测通信监听 v").Append(Version)
                  .Append("  导出时间 ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("\r\n");
                sb.Append("# 时间\t方向\t消息\tID\tsysid\tcompid\tseq\t帧长\t内容\t原始字节\r\n");
                for (int i = 0; i < grid.RowCount; i++)
                {
                    RowInfo r = grid.Rows[i].Tag as RowInfo;
                    if (r != null) sb.Append(FormatRow(r)).Append("\r\n");
                }
                File.WriteAllText(sfd.FileName, sb.ToString(), new UTF8Encoding(true));
                SetState("已保存 " + grid.RowCount + " 行：" + sfd.FileName);
                PrintSys("保存日志：" + sfd.FileName);
            }
            catch (Exception ex) { SetState("保存失败：" + ex.Message); }
        }

        void ExportRaw()
        {
            if (grid == null || grid.RowCount == 0) { SetState("没有可导出的记录"); return; }
            SaveFileDialog sfd = new SaveFileDialog();
            sfd.Filter = "原始帧 (*.bin)|*.bin|所有文件 (*.*)|*.*";
            sfd.FileName = "telemetry-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bin";
            if (sfd.ShowDialog() != DialogResult.OK) return;
            try
            {
                using (FileStream fs = new FileStream(sfd.FileName, FileMode.Create, FileAccess.Write))
                {
                    for (int i = 0; i < grid.RowCount; i++)
                    {
                        RowInfo r = grid.Rows[i].Tag as RowInfo;
                        if (r != null && r.Raw != null) fs.Write(r.Raw, 0, r.Raw.Length);
                    }
                }
                SetState("已导出原始帧：" + sfd.FileName);
                PrintSys("导出原始帧：" + sfd.FileName);
            }
            catch (Exception ex) { SetState("导出失败：" + ex.Message); }
        }

        void ExportSelectedRaw()
        {
            if (grid == null || grid.SelectedRows.Count == 0) return;
            SaveFileDialog sfd = new SaveFileDialog();
            sfd.Filter = "原始帧 (*.bin)|*.bin|所有文件 (*.*)|*.*";
            sfd.FileName = "telemetry-sel-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bin";
            if (sfd.ShowDialog() != DialogResult.OK) return;
            try
            {
                using (FileStream fs = new FileStream(sfd.FileName, FileMode.Create, FileAccess.Write))
                {
                    for (int i = 0; i < grid.SelectedRows.Count; i++)
                    {
                        RowInfo r = grid.SelectedRows[i].Tag as RowInfo;
                        if (r != null && r.Raw != null) fs.Write(r.Raw, 0, r.Raw.Length);
                    }
                }
                SetState("已导出选中帧：" + sfd.FileName);
            }
            catch (Exception ex) { SetState("导出失败：" + ex.Message); }
        }

        void CopyRows(bool selectedOnly)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                if (selectedOnly)
                {
                    for (int i = 0; i < grid.SelectedRows.Count; i++)
                    {
                        RowInfo r = grid.SelectedRows[i].Tag as RowInfo;
                        if (r != null) sb.Append(FormatRow(r)).Append("\r\n");
                    }
                }
                else
                {
                    for (int i = 0; i < grid.RowCount; i++)
                    {
                        RowInfo r = grid.Rows[i].Tag as RowInfo;
                        if (r != null) sb.Append(FormatRow(r)).Append("\r\n");
                    }
                }
                if (sb.Length > 0) Clipboard.SetText(sb.ToString());
                SetState("已复制到剪贴板");
            }
            catch (Exception ex) { SetState("复制失败：" + ex.Message); }
        }

        // ═══════════════════════ 小工具 ═══════════════════════

        void UpdateSrc()
        {
            if (lblSrc == null || lblSrc.IsDisposed) return;
            string t = "源：MP 未连接";
            try
            {
                if (MainV2.comPort != null && MainV2.comPort.BaseStream != null && MainV2.comPort.BaseStream.IsOpen)
                    t = "源：MP 当前连接  " + MainV2.comPort.BaseStream.PortName
                        + " @ " + MainV2.comPort.BaseStream.BaudRate + "（自动跟随）";
            }
            catch { }
            lblSrc.Text = t;
        }
    }
}
