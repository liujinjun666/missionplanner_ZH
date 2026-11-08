// zh-serial.cs —— 飞控串口监听工具 v1.0
// 两种源：① MP 当前连接（挂在 MAVLink 收发事件上，零配置）② 飞控串口（自动定位飞控 COM 口，可接管 MP 占用的串口）
// 内容：报文列表（时间/方向/消息/ID/系统组件/序号/长度/摘要）+ 选中行字段与十六进制
//       打印页（STATUSTEXT 告警文本 + 串口 ASCII 调试行）+ 过滤/暂停/保存/导出原始帧
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
        public bool IsAscii;       // 串口 ASCII 调试行（非 MAVLink）
        public uint MsgId;
        public string Name = "";
        public int SysId;
        public int CompId;
        public int Seq;
        public int Len;            // 帧长（含头与 CRC）
        public int Payload;
        public string Content = "";
        public byte[] Raw;
        public object Data;        // MAVLink 解析后的结构体（飞控串口模式为 null）
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
        RadioButton rbMp, rbPort;
        ComboBox cmbPort, cmbBaud, cmbDir;
        Button btnOpen, btnRefresh, btnScan, btnPause, btnClear, btnSave, btnRaw;
        TextBox txtFilter;
        CheckBox chkTextOnly, chkAutoScroll, chkTopMost;
        Label lblSrc, lblState;
        ToolStripStatusLabel lblCnt, lblRate;

        // ── 数据缓冲 ──
        readonly List<RowInfo> pending = new List<RowInfo>();
        System.Windows.Forms.Timer uiTimer;
        volatile bool paused;

        // 过滤条件缓存：控件只能在 UI 线程读，收包线程用这几个快照
        volatile bool fltTextOnly;
        volatile int fltDir;
        volatile string fltText = "";
        long cRx, cTx, cAscii, cErr, cBytes, cDrop;
        long lastBytes;

        // ── MP 事件挂钩 ──
        MissionPlanner.MAVLinkInterface attached;

        // ── 飞控串口 ──
        System.IO.Ports.SerialPort sp;
        Thread th;
        volatile bool running;
        readonly List<byte> rbuf = new List<byte>();
        bool closing;

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
            closing = true;
            try { DetachMp(); } catch { }
            try { ClosePort(); } catch { }
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
            // 但 zh-net 自己也是靠「玄云ECU」定位的，如果本插件先跑完全，就会占到它的位置，
            // 等 zh-net 注入时又按 ecu.Right+8 算，两个框会叠在一起 —— 所以先等「飞控网口」出现。
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
                // 兜底：加到「飞行数据」页菜单里
                try
                {
                    ToolStripMenuItem mi = new ToolStripMenuItem("串口监听");
                    mi.Click += delegate { ToggleForm(); };
                    Host.FDMenuMap.Items.Add(mi);
                }
                catch { }
                uiAdded = true;
                return;
            }

            chkEntry = new CheckBox();
            chkEntry.Text = "串口监听";
            chkEntry.AutoSize = true;
            chkEntry.Name = "CHK_serial";
            chkEntry.Checked = false;
            chkEntry.CheckedChanged += OnEntryChecked;

            Control parent = anchor.Parent;
            parent.Controls.Add(chkEntry);
            chkEntry.BringToFront();
            int x = anchor.Right + 8;
            if (x + 96 > parent.ClientSize.Width)
                x = Math.Max(0, parent.ClientSize.Width - 96);
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
            if (closing) return;
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                fm.Hide();
                try { if (chkEntry != null) chkEntry.Checked = false; } catch { }
            }
        }

        // ═══════════════════════ 界面构建 ═══════════════════════

        void EnsureForm()
        {
            if (fm != null && !fm.IsDisposed) return;

            fm = new Form();
            fm.Text = "飞控串口监听 v" + Version;
            fm.Size = new Size(1000, 680);
            fm.MinimumSize = new Size(840, 500);
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
            top.Height = 102;
            fm.Controls.Add(top);

            // ── 第一行：数据源 ──
            rbMp = new RadioButton();
            rbMp.Text = "MP 当前连接";
            rbMp.AutoSize = true;
            rbMp.Checked = true;
            rbMp.Location = new Point(10, 12);
            rbMp.CheckedChanged += ModeChanged;
            top.Controls.Add(rbMp);

            rbPort = new RadioButton();
            rbPort.Text = "飞控串口";
            rbPort.AutoSize = true;
            rbPort.Location = new Point(150, 12);
            rbPort.CheckedChanged += ModeChanged;
            top.Controls.Add(rbPort);

            cmbPort = new ComboBox();
            cmbPort.DropDownStyle = ComboBoxStyle.DropDownList;   // 只从识别结果里选，避免手打出错
            cmbPort.Location = new Point(242, 10);
            cmbPort.Width = 96;
            top.Controls.Add(cmbPort);

            cmbBaud = new ComboBox();
            cmbBaud.DropDownStyle = ComboBoxStyle.DropDown;
            cmbBaud.Location = new Point(346, 10);
            cmbBaud.Width = 96;
            cmbBaud.Items.AddRange(new object[] { "9600", "19200", "38400", "57600", "115200",
                "230400", "460800", "500000", "921600", "1000000", "1500000", "2000000", "3000000" });
            cmbBaud.Text = "57600";
            top.Controls.Add(cmbBaud);

            btnOpen = new Button();
            btnOpen.Text = "打开";
            btnOpen.Location = new Point(450, 9);
            btnOpen.Size = new Size(70, 25);
            btnOpen.Click += delegate { if (sp == null) OpenPort(); else ClosePort(); };
            top.Controls.Add(btnOpen);

            btnRefresh = new Button();
            btnRefresh.Text = "刷新";
            btnRefresh.Location = new Point(526, 9);
            btnRefresh.Size = new Size(60, 25);
            btnRefresh.Click += delegate { RefreshPorts(); };
            top.Controls.Add(btnRefresh);

            btnScan = new Button();
            btnScan.Text = "自动扫描波特率";
            btnScan.Location = new Point(592, 9);
            btnScan.Size = new Size(116, 25);
            btnScan.Click += delegate { ScanBaud(); };
            top.Controls.Add(btnScan);

            lblSrc = new Label();
            lblSrc.Text = "未连接";
            lblSrc.AutoSize = false;
            lblSrc.TextAlign = ContentAlignment.MiddleLeft;
            lblSrc.Location = new Point(720, 12);
            lblSrc.Size = new Size(260, 20);
            lblSrc.ForeColor = Color.Black;
            lblSrc.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            top.Controls.Add(lblSrc);

            // ── 第二行：过滤 ──
            Label lf = new Label();
            lf.Text = "过滤：";
            lf.AutoSize = true;
            lf.Location = new Point(10, 47);
            lf.ForeColor = Color.Black;
            top.Controls.Add(lf);

            txtFilter = new TextBox();
            txtFilter.Location = new Point(56, 44);
            txtFilter.Width = 300;
            txtFilter.BorderStyle = BorderStyle.FixedSingle;
            txtFilter.Text = "";
            top.Controls.Add(txtFilter);

            Label lh = new Label();
            lh.Text = "多个关键字用逗号分隔（匹配消息名或内容）";
            lh.AutoSize = true;
            lh.Location = new Point(362, 47);
            lh.ForeColor = Color.Gray;
            top.Controls.Add(lh);

            cmbDir = new ComboBox();
            cmbDir.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbDir.Location = new Point(640, 44);
            cmbDir.Width = 86;
            cmbDir.Items.AddRange(new object[] { "全部方向", "仅接收", "仅发送" });
            cmbDir.SelectedIndex = 0;
            top.Controls.Add(cmbDir);

            chkTextOnly = new CheckBox();
            chkTextOnly.Text = "只看文本/打印";
            chkTextOnly.AutoSize = true;
            chkTextOnly.Location = new Point(736, 46);
            top.Controls.Add(chkTextOnly);

            // ── 第三行：操作 ──
            btnPause = new Button();
            btnPause.Text = "暂停";
            btnPause.Location = new Point(10, 72);
            btnPause.Size = new Size(70, 25);
            btnPause.Click += delegate
            {
                paused = !paused;
                btnPause.Text = paused ? "继续" : "暂停";
                SetState(paused ? "已暂停（仍在统计）" : "监听中");
            };
            top.Controls.Add(btnPause);

            btnClear = new Button();
            btnClear.Text = "清空";
            btnClear.Location = new Point(86, 72);
            btnClear.Size = new Size(70, 25);
            btnClear.Click += delegate { ClearAll(); };
            top.Controls.Add(btnClear);

            btnSave = new Button();
            btnSave.Text = "保存日志";
            btnSave.Location = new Point(162, 72);
            btnSave.Size = new Size(86, 25);
            btnSave.Click += delegate { SaveLog(); };
            top.Controls.Add(btnSave);

            btnRaw = new Button();
            btnRaw.Text = "导出原始帧";
            btnRaw.Location = new Point(254, 72);
            btnRaw.Size = new Size(100, 25);
            btnRaw.Click += delegate { ExportRaw(); };
            top.Controls.Add(btnRaw);

            chkAutoScroll = new CheckBox();
            chkAutoScroll.Text = "自动滚动";
            chkAutoScroll.AutoSize = true;
            chkAutoScroll.Checked = true;
            chkAutoScroll.Location = new Point(368, 76);
            top.Controls.Add(chkAutoScroll);

            chkTopMost = new CheckBox();
            chkTopMost.Text = "窗口置顶";
            chkTopMost.AutoSize = true;
            chkTopMost.Location = new Point(462, 76);
            chkTopMost.CheckedChanged += delegate { try { fm.TopMost = chkTopMost.Checked; } catch { } };
            top.Controls.Add(chkTopMost);

            lblState = new Label();
            lblState.Text = "就绪";
            lblState.AutoSize = true;
            lblState.Location = new Point(556, 78);
            lblState.ForeColor = Color.FromArgb(0, 110, 0);
            top.Controls.Add(lblState);

            // ── 主体 ──
            tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            fm.Controls.Add(tabs);
            fm.Controls.SetChildIndex(tabs, 0);   // Fill 必须排在最前，先分给 Top/Bottom 再填满

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

            RefreshPorts();
            SetModeEnabled(false);
            PrintSys("就绪。「MP 当前连接」直接看 MP 与飞控的收发；「飞控串口」自动定位飞控所在串口，可抓原始字节与打印。");
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

        void SetModeEnabled(bool portMode)
        {
            cmbPort.Enabled = portMode;
            cmbBaud.Enabled = portMode;
            btnOpen.Enabled = portMode;
            btnRefresh.Enabled = portMode;
            btnScan.Enabled = portMode;
        }

        // ═══════════════════════ 模式切换 ═══════════════════════

        void ModeChanged(object sender, EventArgs e)
        {
            if (rbMp == null || rbPort == null) return;
            bool portMode = rbPort.Checked;
            SetModeEnabled(portMode);
            if (portMode)
            {
                DetachMp();
                SetState("飞控串口模式：已自动定位飞控串口，确认波特率后点「打开」");
                PrintSys("切换到飞控串口模式（自动定位 MP 对应的串口）");
            }
            else
            {
                ClosePort();
                AttachMp();
                SetState("MP 连接模式");
                PrintSys("切换到 MP 连接模式");
            }
        }

        // ═══════════════════════ MP 连接透听 ═══════════════════════

        void AttachMp()
        {
            if (rbPort != null && rbPort.Checked) return;
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
                Count(r);
                Enqueue(r);
            }
            catch { }
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
            if (d == null)
            {
                if (r.IsAscii) return r.Content;
                return HexShort(r.Raw, 24);
            }
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
                int si = SysIdToInt(sev, -1);
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

        static int SysIdToInt(object v, int dflt)
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

        // ═══════════════════════ 飞控串口（自动识别） ═══════════════════════

        // 下拉项：COM3   ★ 飞控（MP 正在使用）
        class PortItem
        {
            public string Name = "";
            public string Note = "";
            public override string ToString()
            {
                return Note.Length == 0 ? Name : Name + "   " + Note;
            }
        }

        string SelectedPortName()
        {
            if (cmbPort == null) return "";
            PortItem it = cmbPort.SelectedItem as PortItem;
            if (it != null) return it.Name;
            return cmbPort.Text == null ? "" : cmbPort.Text.Trim();
        }

        // MP 当前正打开着的串口
        static string LiveMpPort()
        {
            try
            {
                if (MainV2.comPort != null && MainV2.comPort.BaseStream != null && MainV2.comPort.BaseStream.IsOpen)
                    return MainV2.comPort.BaseStream.PortName;
            }
            catch { }
            return "";
        }

        // MP 配置里记着的串口（上次连接用的）
        static string CfgMpPort()
        {
            try
            {
                string s = MissionPlanner.Utilities.Settings.Instance["comport"];
                if (s == null) return "";
                return s.Trim();
            }
            catch { }
            return "";
        }

        // 从注册表 SERIALCOMM 判断哪些是 USB 转串口（飞控/数传基本都是 USB 虚拟串口）
        static HashSet<string> UsbSerialPorts()
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (Microsoft.Win32.RegistryKey k =
                    Microsoft.Win32.Registry.LocalMachine.OpenSubKey("HARDWARE\\DEVICEMAP\\SERIALCOMM"))
                {
                    if (k != null)
                    {
                        string[] names = k.GetValueNames();
                        for (int i = 0; i < names.Length; i++)
                        {
                            string dev = names[i] == null ? "" : names[i].ToUpperInvariant();
                            if (dev.IndexOf("USBSER") < 0 && dev.IndexOf("VCP") < 0 && dev.IndexOf("USB") < 0) continue;
                            string port = Convert.ToString(k.GetValue(names[i]));
                            if (!string.IsNullOrEmpty(port)) set.Add(port.Trim());
                        }
                    }
                }
            }
            catch { }
            return set;
        }

        static int PortNumber(string name)
        {
            if (name == null) return 0;
            StringBuilder d = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
                if (name[i] >= '0' && name[i] <= '9') d.Append(name[i]);
            return ToInt(d.ToString(), 0);
        }

        // 列出串口：飞控相关排前面并打星，其余按串口号排
        void RefreshPorts()
        {
            if (cmbPort == null) return;
            string keep = SelectedPortName();
            string[] names = new string[0];
            try { names = System.IO.Ports.SerialPort.GetPortNames(); }
            catch (Exception ex) { SetState("枚举串口失败：" + ex.Message); return; }

            string live = LiveMpPort();
            string cfg = CfgMpPort();
            HashSet<string> usb = UsbSerialPorts();

            List<PortItem> fc = new List<PortItem>();
            List<PortItem> usbList = new List<PortItem>();
            List<PortItem> other = new List<PortItem>();

            for (int i = 0; i < names.Length; i++)
            {
                PortItem it = new PortItem();
                it.Name = names[i];
                if (sp != null && string.Compare(names[i], sp.PortName, true) == 0)
                {
                    it.Note = "● 正在监听";
                    fc.Add(it);
                }
                else if (live.Length > 0 && string.Compare(names[i], live, true) == 0)
                {
                    it.Note = "★ 飞控（MP 正在使用）";
                    fc.Add(it);
                }
                else if (cfg.Length > 0 && string.Compare(names[i], cfg, true) == 0)
                {
                    it.Note = "★ 飞控（MP 上次连接）";
                    fc.Add(it);
                }
                else if (usb.Contains(names[i]))
                {
                    it.Note = "USB 串口";
                    usbList.Add(it);
                }
                else other.Add(it);
            }

            SortByPortNo(fc);
            SortByPortNo(usbList);
            SortByPortNo(other);

            cmbPort.BeginUpdate();
            cmbPort.Items.Clear();
            for (int i = 0; i < fc.Count; i++) cmbPort.Items.Add(fc[i]);
            for (int i = 0; i < usbList.Count; i++) cmbPort.Items.Add(usbList[i]);
            for (int i = 0; i < other.Count; i++) cmbPort.Items.Add(other[i]);
            cmbPort.EndUpdate();

            // 默认选中：上次选的 → MP 正在用的 → MP 配置的 → 第一个
            string want = keep;
            if (want.Length == 0) want = live;
            if (want.Length == 0) want = cfg;
            if (want.Length == 0 && cmbPort.Items.Count > 0) want = ((PortItem)cmbPort.Items[0]).Name;
            SelectPort(want);
        }

        static void SortByPortNo(List<PortItem> list)
        {
            for (int i = 1; i < list.Count; i++)
            {
                PortItem cur = list[i];
                int j = i - 1;
                while (j >= 0 && PortNumber(list[j].Name) > PortNumber(cur.Name))
                {
                    list[j + 1] = list[j];
                    j--;
                }
                list[j + 1] = cur;
            }
        }

        void SelectPort(string name)
        {
            if (cmbPort == null || name == null) return;
            for (int i = 0; i < cmbPort.Items.Count; i++)
            {
                PortItem it = cmbPort.Items[i] as PortItem;
                if (it != null && string.Compare(it.Name, name, true) == 0)
                {
                    cmbPort.SelectedIndex = i;
                    return;
                }
            }
            if (cmbPort.Items.Count > 0) cmbPort.SelectedIndex = 0;
        }

        void OpenPort()
        {
            if (sp != null) return;
            string name = SelectedPortName();
            if (name.Length == 0) { SetState("请先选择飞控对应的串口"); return; }
            int baud = ToInt(cmbBaud.Text, 57600);
            if (!TryOpen(name, baud))
            {
                // 被 MP 占着：问一次要不要接管
                if (!IsPortBusy) return;
                string live = LiveMpPort();
                string tip = "串口 " + name + " 正被 Mission Planner 占用。\r\n\r\n"
                    + "是否断开 MP 与飞控的连接，把该串口交给监听工具？\r\n"
                    + "（之后在 MP 里点「连接」可恢复）";
                if (live.Length > 0 && string.Compare(live, name, true) != 0)
                    tip = "串口 " + name + " 正被其它程序占用。\r\n\r\n请先关闭占用它的程序后重试。";
                if (live.Length > 0 && string.Compare(live, name, true) == 0)
                {
                    if (MessageBox.Show(fm, tip, "串口被占用", MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning) != DialogResult.Yes)
                    {
                        SetState("已取消：串口 " + name + " 仍被 MP 占用");
                        return;
                    }
                    try { DetachMp(); } catch { }
                    try { MainV2.comPort.Close(); } catch { }
                    Thread.Sleep(400);
                    if (!TryOpen(name, baud))
                    {
                        SetState("接管失败：" + LastOpenError);
                        PrintSys("接管 " + name + " 失败：" + LastOpenError);
                        return;
                    }
                }
                else
                {
                    MessageBox.Show(fm, tip, "串口被占用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            lock (rbuf) rbuf.Clear();
            running = true;
            th = new Thread(ReadLoop);
            th.IsBackground = true;
            th.Name = "zh-serial-read";
            th.Start();
            try { btnOpen.Text = "关闭"; } catch { }
            SetState("已打开 " + name + " @ " + baud);
            PrintSys("打开串口 " + name + " @ " + baud);
            if (rbPort != null) RefreshPorts();
        }

        bool IsPortBusy;
        string LastOpenError = "";

        bool TryOpen(string name, int baud)
        {
            IsPortBusy = false;
            LastOpenError = "";
            try
            {
                System.IO.Ports.SerialPort p = new System.IO.Ports.SerialPort();
                p.PortName = name;
                p.BaudRate = baud;
                p.Parity = System.IO.Ports.Parity.None;
                p.DataBits = 8;
                p.StopBits = System.IO.Ports.StopBits.One;
                p.ReadTimeout = 500;
                p.WriteTimeout = 500;
                try { p.ReadBufferSize = 65536; } catch { }
                p.Open();
                sp = p;
                return true;
            }
            catch (Exception ex)
            {
                sp = null;
                LastOpenError = ex.Message;
                // 拒绝访问 / 已被占用
                string m = ex.Message == null ? "" : ex.Message.ToUpperInvariant();
                if (ex is UnauthorizedAccessException || m.IndexOf("ACCESS") >= 0 || m.IndexOf("拒绝") >= 0
                    || m.IndexOf("BUSY") >= 0 || m.IndexOf("占用") >= 0)
                    IsPortBusy = true;
                SetState("打开 " + name + " 失败：" + ex.Message);
                PrintSys("打开 " + name + " 失败：" + ex.Message);
                return false;
            }
        }

        void ClosePort()
        {
            running = false;
            System.IO.Ports.SerialPort p = sp;
            sp = null;
            try { if (p != null && p.IsOpen) p.Close(); } catch { }
            try { if (p != null) p.Dispose(); } catch { }
            try { btnOpen.Text = "打开"; } catch { }
            lock (rbuf) rbuf.Clear();
            if (p != null)
            {
                SetState("串口已关闭");
                PrintSys("关闭串口");
            }
        }

        void ReadLoop()
        {
            byte[] tmp = new byte[8192];
            while (running)
            {
                System.IO.Ports.SerialPort p = sp;
                if (p == null) break;
                try
                {
                    int avail = p.BytesToRead;
                    if (avail <= 0) { Thread.Sleep(20); continue; }
                    int want = avail;
                    if (want > tmp.Length) want = tmp.Length;
                    int got = p.Read(tmp, 0, want);
                    if (got > 0)
                    {
                        lock (rbuf)
                        {
                            for (int i = 0; i < got; i++) rbuf.Add(tmp[i]);
                            if (rbuf.Count > 262144) rbuf.RemoveRange(0, rbuf.Count - 262144);
                        }
                        Drain();
                    }
                }
                catch (TimeoutException) { }
                catch (Exception ex)
                {
                    if (!running) break;      // 主动关闭导致的中断，不报错
                    running = false;
                    string msg = ex.Message;
                    try
                    {
                        if (fm != null && !fm.IsDisposed)
                            fm.BeginInvoke((Action)delegate
                            {
                                SetState("串口读取中断：" + msg);
                                PrintSys("串口读取中断：" + msg);
                                ClosePort();
                            });
                    }
                    catch { }
                    break;
                }
            }
        }

        // 把缓冲区里的字节拆成：MAVLink v1/v2 帧 + ASCII 文本行
        void Drain()
        {
            List<RowInfo> rows = new List<RowInfo>();
            lock (rbuf)
            {
                int i = 0;
                int count = rbuf.Count;
                while (i < count)
                {
                    byte b = rbuf[i];
                    if (b == 0xFE)
                    {
                        if (count - i < 2) break;
                        int len = rbuf[i + 1];
                        int total = 6 + len + 2;
                        if (count - i < total) break;
                        rows.Add(MakeFrameRow(i, total, false));
                        i += total;
                        continue;
                    }
                    if (b == 0xFD)
                    {
                        if (count - i < 10) break;
                        int len = rbuf[i + 1];
                        int incompat = rbuf[i + 2];
                        int total = 10 + len + 2 + (((incompat & 0x01) != 0) ? 13 : 0);
                        if (count - i < total) break;
                        rows.Add(MakeFrameRow(i, total, true));
                        i += total;
                        continue;
                    }
                    int j = i + 1;
                    while (j < count && rbuf[j] != 0xFE && rbuf[j] != 0xFD && (j - i) < 512) j++;
                    string txt = AsciiOf(rbuf, i, j - i);
                    if (txt != null && txt.Length > 0)
                    {
                        RowInfo r = new RowInfo();
                        r.Time = DateTime.Now;
                        r.IsAscii = true;
                        r.Name = "TEXT";
                        r.MsgId = 0xFFFFFFFF;
                        r.Content = txt;
                        byte[] raw = new byte[j - i];
                        for (int k = 0; k < raw.Length; k++) raw[k] = rbuf[i + k];
                        r.Raw = raw;
                        r.Len = raw.Length;
                        rows.Add(r);
                    }
                    i = j;
                }
                if (i > 0) rbuf.RemoveRange(0, i);
            }
            for (int k = 0; k < rows.Count; k++)
            {
                Count(rows[k]);
                Enqueue(rows[k]);
            }
        }

        static string AsciiOf(List<byte> buf, int start, int len)
        {
            if (len <= 0) return null;
            StringBuilder sb = new StringBuilder();
            int printable = 0;
            for (int i = 0; i < len && sb.Length < 512; i++)
            {
                byte c = buf[start + i];
                if (c == 13) continue;
                if (c == 10) { sb.Append(' '); continue; }
                if (c >= 32 && c < 127) { sb.Append((char)c); printable++; }
                else if (c == 9) sb.Append(' ');
                else { sb.Append('·'); }
            }
            if (printable < 2) return null;
            string s = sb.ToString().Trim();
            if (s.Length < 2) return null;
            return s;
        }

        RowInfo MakeFrameRow(int off, int total, bool v2)
        {
            RowInfo r = new RowInfo();
            r.Time = DateTime.Now;
            r.Len = total;
            if (v2)
            {
                r.Seq = rbuf[off + 4];
                r.SysId = rbuf[off + 5];
                r.CompId = rbuf[off + 6];
                r.MsgId = (uint)(rbuf[off + 7] | (rbuf[off + 8] << 8) | (rbuf[off + 9] << 16));
                r.Payload = rbuf[off + 1];
            }
            else
            {
                r.Seq = rbuf[off + 2];
                r.SysId = rbuf[off + 3];
                r.CompId = rbuf[off + 4];
                r.MsgId = rbuf[off + 5];
                r.Payload = rbuf[off + 1];
            }
            byte[] raw = new byte[total];
            for (int i = 0; i < total; i++) raw[i] = rbuf[off + i];
            r.Raw = raw;
            r.Name = MsgName(r.MsgId);
            r.Content = "payload " + r.Payload + "B  " + HexShort(raw, 16);
            return r;
        }

        // 自动扫描波特率：逐个尝试，收到合法 MAVLink 帧即认为命中
        void ScanBaud()
        {
            if (sp != null) { SetState("请先关闭当前串口再扫描"); return; }
            string name = SelectedPortName();
            if (name.Length == 0) { SetState("请先选择飞控对应的串口"); return; }
            int[] cand = new int[] { 115200, 57600, 921600, 460800, 230400, 38400, 19200, 9600, 1500000, 2000000 };
            SetState("正在扫描波特率…");
            StringBuilder cl = new StringBuilder();
            for (int i = 0; i < cand.Length; i++)
            {
                if (i > 0) cl.Append(',');
                cl.Append(cand[i]);
            }
            PrintSys("开始扫描 " + name + " 的波特率：" + cl.ToString());
            Thread t = new Thread(delegate ()
            {
                for (int i = 0; i < cand.Length; i++)
                {
                    int baud = cand[i];
                    if (closing) return;
                    try
                    {
                        if (fm != null && !fm.IsDisposed)
                            fm.BeginInvoke((Action)delegate { SetState("扫描中… " + baud); });
                    }
                    catch { }
                    bool hit = ProbeBaud(name, baud);
                    if (hit)
                    {
                        try
                        {
                            if (fm != null && !fm.IsDisposed)
                                fm.BeginInvoke((Action)delegate
                                {
                                    cmbBaud.Text = baud.ToString();
                                    SetState("命中波特率 " + baud + "，已打开");
                                    PrintSys("命中波特率 " + baud);
                                    OpenPort();
                                });
                        }
                        catch { }
                        return;
                    }
                }
                try
                {
                    if (fm != null && !fm.IsDisposed)
                        fm.BeginInvoke((Action)delegate
                        {
                            SetState("未扫描到 MAVLink 数据（线序/端口是否正确？）");
                            PrintSys("扫描结束：未发现 MAVLink 帧");
                        });
                }
                catch { }
            });
            t.IsBackground = true;
            t.Name = "zh-serial-scan";
            t.Start();
        }

        bool ProbeBaud(string name, int baud)
        {
            System.IO.Ports.SerialPort p = null;
            try
            {
                p = new System.IO.Ports.SerialPort(name, baud, System.IO.Ports.Parity.None, 8, System.IO.Ports.StopBits.One);
                p.ReadTimeout = 300;
                p.Open();
                DateTime end = DateTime.Now.AddMilliseconds(1400);
                byte[] tmp = new byte[4096];
                List<byte> acc = new List<byte>();
                while (DateTime.Now < end && acc.Count < 8192)
                {
                    try
                    {
                        int n = p.BytesToRead;
                        if (n <= 0) { Thread.Sleep(20); continue; }
                        if (n > tmp.Length) n = tmp.Length;
                        int got = p.Read(tmp, 0, n);
                        for (int i = 0; i < got; i++) acc.Add(tmp[i]);
                    }
                    catch (TimeoutException) { }
                }
                // 找合法帧头 + 合理长度
                for (int i = 0; i + 2 < acc.Count; i++)
                {
                    if (acc[i] == 0xFD && acc[i + 2] <= 4) return true;
                    if (acc[i] == 0xFE && acc[i + 1] > 0 && acc[i + 1] < 255) return true;
                }
                return false;
            }
            catch { return false; }
            finally
            {
                try { if (p != null && p.IsOpen) p.Close(); } catch { }
                try { if (p != null) p.Dispose(); } catch { }
            }
        }

        // ═══════════════════════ 统计 / 入队 / 上屏 ═══════════════════════

        void Count(RowInfo r)
        {
            if (r.Len > 0) Interlocked.Add(ref cBytes, r.Len);
            if (r.Sent) Interlocked.Increment(ref cTx); else Interlocked.Increment(ref cRx);
            if (r.IsAscii) Interlocked.Increment(ref cAscii);
            if (r.Raw == null) Interlocked.Increment(ref cErr);
        }

        void Enqueue(RowInfo r)
        {
            if (paused) return;
            if (!Pass(r)) return;
            lock (pending)
            {
                if (pending.Count >= MaxPending) { Interlocked.Increment(ref cDrop); return; }
                pending.Add(r);
            }
        }

        bool Pass(RowInfo r)
        {
            if (fltTextOnly)
            {
                if (!r.IsAscii && r.Name != "STATUSTEXT" && r.Name != "TEXT") return false;
            }
            if (fltDir == 1 && r.Sent) return false;
            if (fltDir == 2 && !r.Sent) return false;
            string f = fltText;
            if (f != null && f.Length > 0)
            {
                string[] parts = f.Split(new char[] { ',', ';', '|' });
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

            // 把过滤条件从控件搬到快照（收包线程只读快照）
            try
            {
                fltTextOnly = chkTextOnly != null && chkTextOnly.Checked;
                fltDir = cmbDir == null ? 0 : cmbDir.SelectedIndex;
                string t = txtFilter == null ? "" : txtFilter.Text;
                fltText = t == null ? "" : t.Trim();
            }
            catch { }

            List<RowInfo> batch = null;
            lock (pending)
            {
                if (pending.Count > 0)
                {
                    batch = new List<RowInfo>(pending);
                    pending.Clear();
                }
            }
            if (batch != null && grid != null && !grid.IsDisposed)
            {
                grid.SuspendLayout();
                try
                {
                    for (int i = 0; i < batch.Count; i++) AddRow(batch[i]);
                    int over = grid.RowCount - MaxRows;
                    for (int i = 0; i < over; i++) grid.Rows.RemoveAt(0);
                }
                catch { }
                grid.ResumeLayout();
                if (chkAutoScroll != null && chkAutoScroll.Checked && grid.RowCount > 0)
                {
                    try { grid.FirstDisplayedScrollingRowIndex = grid.RowCount - 1; } catch { }
                }
                PrintRows(batch);
            }
            UpdateStats();
        }

        void AddRow(RowInfo r)
        {
            int idx = grid.Rows.Add();
            DataGridViewRow row = grid.Rows[idx];
            row.Cells[0].Value = r.Time.ToString("HH:mm:ss.fff");
            row.Cells[1].Value = r.IsAscii ? "文本" : (r.Sent ? "发" : "收");
            row.Cells[2].Value = r.Name;
            row.Cells[3].Value = r.IsAscii ? "" : r.MsgId.ToString();
            row.Cells[4].Value = r.IsAscii ? "" : (r.SysId + " / " + r.CompId);
            row.Cells[5].Value = r.IsAscii ? "" : r.Seq.ToString();
            row.Cells[6].Value = r.Len.ToString();
            row.Cells[7].Value = r.Content;
            row.Tag = r;
            if (r.Sent) row.DefaultCellStyle.BackColor = Color.FromArgb(238, 245, 255);
            if (r.IsAscii) row.DefaultCellStyle.ForeColor = Color.FromArgb(0, 110, 90);
            else if (r.Name == "STATUSTEXT")
            {
                int sev = 6;
                object d = r.Data;
                if (d != null)
                {
                    FieldInfo[] fs = GetFields(d.GetType());
                    for (int i = 0; i < fs.Length; i++)
                        if (fs[i].Name == "severity") sev = SysIdToInt(SafeGet(fs[i], d), 6);
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
                string s = "收 " + Interlocked.Read(ref cRx) + " / 发 " + Interlocked.Read(ref cTx)
                    + " / 文本 " + Interlocked.Read(ref cAscii)
                    + " / 丢弃 " + Interlocked.Read(ref cDrop);
                if (lblCnt != null) lblCnt.Text = s;
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
            Interlocked.Exchange(ref cAscii, 0);
            Interlocked.Exchange(ref cErr, 0);
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
                sb.Append("方向      : ").Append(r.IsAscii ? "串口文本" : (r.Sent ? "发送" : "接收")).Append("\r\n");
                sb.Append("消息      : ").Append(r.Name).Append("  (ID ").Append(r.MsgId == 0xFFFFFFFF ? "-" : r.MsgId.ToString()).Append(")\r\n");
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
                else if (r.IsAscii)
                {
                    sb.Append("\r\n文本内容：\r\n").Append(r.Content).Append("\r\n");
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
                if (r.IsAscii) AppendPrint(r.Time.ToString("HH:mm:ss.fff") + "  " + r.Content, Color.FromArgb(0, 110, 90));
                else if (r.Name == "STATUSTEXT")
                {
                    int sev = 6;
                    object d = r.Data;
                    if (d != null)
                    {
                        FieldInfo[] fs = GetFields(d.GetType());
                        for (int j = 0; j < fs.Length; j++)
                            if (fs[j].Name == "severity") sev = SysIdToInt(SafeGet(fs[j], d), 6);
                    }
                    AppendPrint(r.Time.ToString("HH:mm:ss.fff") + "  " + r.Content, SevColor(sev));
                }
            }
            txtPrint.ResumeLayout();
        }

        // ═══════════════════════ 保存 / 导出 ═══════════════════════

        string FormatRow(RowInfo r)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(r.Time.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append('\t');
            sb.Append(r.IsAscii ? "文本" : (r.Sent ? "发" : "收")).Append('\t');
            sb.Append(r.Name).Append('\t');
            sb.Append(r.IsAscii ? "" : r.MsgId.ToString()).Append('\t');
            sb.Append(r.IsAscii ? "" : r.SysId.ToString()).Append('\t');
            sb.Append(r.IsAscii ? "" : r.CompId.ToString()).Append('\t');
            sb.Append(r.IsAscii ? "" : r.Seq.ToString()).Append('\t');
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
            sfd.FileName = "serial-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
            if (sfd.ShowDialog() != DialogResult.OK) return;
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("# 飞控串口监听 v").Append(Version)
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
            sfd.FileName = "serial-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bin";
            if (sfd.ShowDialog() != DialogResult.OK) return;
            try
            {
                using (FileStream fs = new FileStream(sfd.FileName, FileMode.Create, FileAccess.Write))
                {
                    for (int i = 0; i < grid.RowCount; i++)
                    {
                        RowInfo r = grid.Rows[i].Tag as RowInfo;
                        if (r != null && r.Raw != null && !r.IsAscii) fs.Write(r.Raw, 0, r.Raw.Length);
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
            sfd.FileName = "serial-sel-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bin";
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

        static int ToInt(string s, int dflt)
        {
            if (s == null) return dflt;
            int v;
            if (int.TryParse(s.Trim(), out v)) return v;
            return dflt;
        }

        void UpdateSrc()
        {
            if (lblSrc == null || lblSrc.IsDisposed) return;
            if (rbPort != null && rbPort.Checked)
            {
                if (sp != null) lblSrc.Text = "飞控串口：" + sp.PortName + " @ " + sp.BaudRate;
                else lblSrc.Text = "飞控串口：未打开";
                return;
            }
            string t = "MP：未连接";
            try
            {
                if (MainV2.comPort != null && MainV2.comPort.BaseStream != null && MainV2.comPort.BaseStream.IsOpen)
                    t = "MP：" + MainV2.comPort.BaseStream.PortName + " @ " + MainV2.comPort.BaseStream.BaudRate;
            }
            catch { }
            lblSrc.Text = t;
        }
    }
}
