// zh-net.cs —— 飞控网口(NET_*)设置调试插件 v1.0
// 放在「玄云ECU」勾选框右侧；支持 ArduCopter / ArduPlane / ArduRover（NET 参数为公共库，机型无关）
// 功能：连接后读取 NET 参数 / 四个网口(类型/协议/端口/对端IP)+全局(本机IP/网关/掩码/DHCP) /
//       修改高亮 / 读取+写入按钮 / 正在通讯网口绿色指示 + 监听窗口（raw socket 需管理员）
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Windows.Forms;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using MissionPlanner;

namespace ZhNet
{
    public class ZhNetPlugin : MissionPlanner.Plugin.Plugin
    {
        public override string Name { get { return "ZhNet"; } }
        public override string Version { get { return "1.1"; } }
        public override string Author { get { return "local"; } }

        // 勾选框 + 面板
        CheckBox chkNet;
        Panel netPanel;
        bool uiAdded = false;
        int ensureRetries = 0;

        // 全局控件
        CheckBox chkEnable, chkDhcp;
        NumericUpDown numNetmask;
        TextBox txtLocalIp, txtGw;
        Label lblMac, lblConn, lblStatus;

        // 网口卡
        readonly List<PortCard> cards = new List<PortCard>();

        // 参数快照（读取时的飞控值）
        Dictionary<string, double> snap = new Dictionary<string, double>();
        bool loading = false;

        // 最近流量记录（IP -> 时间），由监听器更新
        readonly ConcurrentDictionary<string, DateTime> lastTraffic = new ConcurrentDictionary<string, DateTime>();

        class PortCard
        {
            public string Pfx;              // "NET_P1"
            public Label Dot;              // 通讯状态指示
            public ComboBox CmbType, CmbProto;
            public NumericUpDown NumPort;
            public TextBox TxtIp;
            public Button BtnListen;
        }

        public override bool Init() { return true; }

        public override bool Loaded()
        {
            loopratehz = 2;   // 2Hz：注入勾选框 + 通讯状态刷新
            try
            {
                // 诊断标记：能编译加载就会写此文件
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "zh_net_loaded.txt"),
                    "loaded " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            catch { }
            return true;
        }

        public override bool Loop()
        {
            try
            {
                Control mf = Host.MainForm;
                if (mf == null || mf.IsDisposed) return true;
                if (mf.InvokeRequired)
                    mf.BeginInvoke((Action)delegate { EnsureUI(); UpdateActive(); });
                else
                { EnsureUI(); UpdateActive(); }
            }
            catch { }
            return true;
        }

        public override bool Exit() { return true; }

        // ---------- 注入勾选框：优先排在「玄云ECU」右侧 ----------
        void EnsureUI()
        {
            if (uiAdded) return;
            Form mf = Host.MainForm as Form;
            if (mf == null || mf.IsDisposed) return;

            // 等待 ECU 插件的勾选框出现（同批插件，最多约 20 秒）
            CheckBox ecu = FindControl<CheckBox>(mf, "CHK_ecu");
            if (ecu == null && ensureRetries < 40)
            {
                ensureRetries++;
                return;
            }

            CheckBox anchor;
            int offset;                     // 自动平移右侧预留 ECU 的宽度
            if (ecu != null) { anchor = ecu; offset = 8; }
            else
            {
                CheckBox autopan = FindControl<CheckBox>(mf, "CHK_autopan");
                if (autopan == null) autopan = FindCheckByText(mf, "自动平移");
                if (autopan == null) return;
                anchor = autopan; offset = 8 + 86;
            }

            chkNet = new CheckBox();
            chkNet.Text = "飞控网口";
            chkNet.AutoSize = true;
            chkNet.Checked = false;
            chkNet.CheckedChanged += OnNetChecked;
            chkNet.Name = "CHK_net";
            Control parent = anchor.Parent;
            parent.Controls.Add(chkNet);
            chkNet.BringToFront();
            int x = anchor.Right + offset;
            if (x + 90 > parent.ClientSize.Width)
                x = Math.Max(0, parent.ClientSize.Width - 90);
            chkNet.Location = new Point(x, anchor.Top);
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
                if (cb != null && cb.Text != null && cb.Text.Replace(" ", "") == text.Replace(" ", ""))
                    return cb;
                CheckBox r = FindCheckByText(c, text);
                if (r != null) return r;
            }
            return null;
        }

        void OnNetChecked(object sender, EventArgs e)
        {
            if (chkNet.Checked) ShowPanel();
            else if (netPanel != null && !netPanel.IsDisposed) netPanel.Visible = false;
        }

        void ShowPanel()
        {
            if (netPanel != null && !netPanel.IsDisposed)
            {
                netPanel.Visible = true;
                netPanel.BringToFront();
                return;
            }
            netPanel = BuildPanel();
            Form mf = Host.MainForm as Form;
            if (mf != null)
            {
                mf.Controls.Add(netPanel);
                int x = Math.Max(0, mf.ClientSize.Width - netPanel.Width - 20);
                int y = Math.Max(0, mf.ClientSize.Height - netPanel.Height - 20);
                // 若 ECU 面板可见，放在它左侧避免重叠
                Panel ecu = null;
                foreach (Control c in mf.Controls)
                    if (c is Panel && c.Name == "ecuPanel" && c.Visible) { ecu = c as Panel; break; }
                if (ecu != null)
                    x = Math.Max(0, ecu.Left - netPanel.Width - 10);
                netPanel.Location = new Point(x, y);
                netPanel.BringToFront();
            }
            netPanel.Visible = true;
            LoadParams();   // 打开即读取
        }

        // ---------- 面板构建 ----------
        const int PW = 395;   // 与 ECU 面板同宽

        static readonly string[] TYPE_ITEMS = { "0=禁用", "1=UDP客户端", "2=UDP服务器", "3=TCP客户端", "4=TCP服务器" };
        static readonly string[] PROTO_ITEMS = {
            "-1=无", "1=MAVLink1", "2=MAVLink2", "3=Frsky D",
            "4=FrSky SPort", "5=GPS", "7=Alexmos云台串口", "8=云台",
            "9=测距仪", "10=FrSky SPort直通(OpenTX)", "11=激光360", "13=信标",
            "14=Volz舵机输出", "15=SBus舵机输出", "16=电调遥测", "17=Devo遥测",
            "18=光流", "19=Robotis舵机", "20=NMEA输出", "21=风向标",
            "22=SLCAN", "23=遥控输入", "24=EFI Serial", "25=LTM",
            "26=RunCam", "27=HottTelem", "28=脚本", "29=Crossfire图传",
            "30=发电机", "31=绞盘", "32=MSP", "33=大疆FPV",
            "34=空速", "35=ADSB", "36=AHRS", "37=SmartAudio",
            "38=FETtecOneWire", "39=Torqeedo", "40=AIS船舶识别", "41=CoDevESC",
            "42=DisplayPort", "43=MAVLink高延迟链路", "44=IRC Tramp", "45=DDS XRCE",
            "46=IMUDATA", "48=PPP", "49=i-BUS Telemetry", "50=IOMCU",
        };

        Panel BuildPanel()
        {
            Panel p = new Panel();
            p.Name = "netPanel";
            p.BorderStyle = BorderStyle.FixedSingle;
            p.BackColor = Color.White;
            p.MouseDown += OnPanelMouseDown;
            p.MouseMove += OnPanelMouseMove;
            p.MouseUp += OnPanelMouseUp;

            int y = 8;
            Label title = new Label();
            title.Text = "飞控网口设置 v1.1";
            title.Font = new Font("微软雅黑", 10f, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(10, y);
            p.Controls.Add(title);
            y += 24;

            lblConn = new Label();
            lblConn.Text = "● 未连接";
            lblConn.ForeColor = Color.Gray;
            lblConn.Tag = "keep";
            lblConn.AutoSize = true;
            lblConn.Location = new Point(PW - 150, y - 22);
            p.Controls.Add(lblConn);
            y += 4;

            // --- 全局区 ---
            y = AddSectionTitle(p, "网络全局（重启飞控后生效）", y);

            chkEnable = new CheckBox();
            chkEnable.Text = "启用网络";
            chkEnable.AutoSize = true;
            chkEnable.Location = new Point(10, y);
            chkEnable.CheckedChanged += delegate { HighlightGlobal(); };
            p.Controls.Add(chkEnable);

            chkDhcp = new CheckBox();
            chkDhcp.Text = "DHCP";
            chkDhcp.AutoSize = true;
            chkDhcp.Location = new Point(120, y);
            chkDhcp.CheckedChanged += delegate { HighlightGlobal(); };
            p.Controls.Add(chkDhcp);

            Label lMask = new Label();
            lMask.Text = "掩码位数";
            lMask.AutoSize = true;
            lMask.Location = new Point(205, y + 3);
            p.Controls.Add(lMask);

            numNetmask = new NumericUpDown();
            numNetmask.Minimum = 0; numNetmask.Maximum = 32; numNetmask.Width = 40;
            numNetmask.BorderStyle = BorderStyle.FixedSingle;
            numNetmask.Location = new Point(258, y);
            numNetmask.ValueChanged += delegate { HighlightGlobal(); };
            p.Controls.Add(numNetmask);
            y += 26;

            Label lIp = new Label();
            lIp.Text = "本机IP";
            lIp.AutoSize = true;
            lIp.Location = new Point(10, y + 3);
            p.Controls.Add(lIp);
            txtLocalIp = new TextBox();
            txtLocalIp.Width = 110;
            txtLocalIp.BorderStyle = BorderStyle.FixedSingle;
            txtLocalIp.Location = new Point(62, y);
            txtLocalIp.TextChanged += delegate { HighlightGlobal(); };
            p.Controls.Add(txtLocalIp);

            Label lGw = new Label();
            lGw.Text = "网关";
            lGw.AutoSize = true;
            lGw.Location = new Point(195, y + 3);
            p.Controls.Add(lGw);
            txtGw = new TextBox();
            txtGw.Width = 110;
            txtGw.BorderStyle = BorderStyle.FixedSingle;
            txtGw.Location = new Point(232, y);
            txtGw.TextChanged += delegate { HighlightGlobal(); };
            p.Controls.Add(txtGw);
            y += 26;

            lblMac = new Label();
            lblMac.Text = "MAC：—（只读）";
            lblMac.ForeColor = Color.Gray;
            lblMac.Tag = "keep";
            lblMac.AutoSize = true;
            lblMac.Location = new Point(10, y);
            p.Controls.Add(lblMac);
            y += 20;

            // --- 四个网口卡 ---
            for (int i = 1; i <= 4; i++)
            {
                y = AddPortCard(p, "NET_P" + i, "网口" + i, y);
            }

            // --- 底部按钮 ---
            Button btnRead = MakeButton(p, "读取参数", 10, y, 90, 28, delegate { LoadParams(); });
            Button btnWrite = MakeButton(p, "写入修改", 108, y, 90, 28, delegate { WriteParams(); });
            Button btnReconn = MakeButton(p, "重连刷新", 206, y, 90, 28, delegate
            {
                if (Connected()) { LoadParams(); }
                else Status("未连接飞控。");
            });
            y += 32;

            lblStatus = new Label();
            lblStatus.Text = "连接后自动读取；黄色底=已修改未写入";
            lblStatus.ForeColor = Color.FromArgb(96, 96, 96);
            lblStatus.Tag = "keep";
            lblStatus.AutoSize = true;
            lblStatus.Location = new Point(10, y);
            p.Controls.Add(lblStatus);
            y += 18;

            // 标签作为拖动把手
            foreach (Control c in p.Controls)
                if (c is Label)
                {
                    c.MouseDown += OnPanelMouseDown;
                    c.MouseMove += OnPanelMouseMove;
                    c.MouseUp += OnPanelMouseUp;
                }

            p.Size = new Size(PW, y + 8);
            SetDarkText(p);   // 白底面板：统一黑色文字，避免深色主题把文字刷成白色而看不见
            return p;
        }

        // 递归把控件文字改成黑色；Tag="keep" 的控件保留自己的前景色
        static void SetDarkText(Control root)
        {
            foreach (Control c in root.Controls)
            {
                bool keep = "keep".Equals(c.Tag);
                if (!keep &&
                    (c is Label || c is CheckBox || c is ComboBox || c is TextBox ||
                     c is NumericUpDown || c is Button || c is RadioButton || c is RichTextBox))
                    c.ForeColor = Color.Black;
                if (c.HasChildren) SetDarkText(c);
            }
        }

        // 把下拉框放进 1px 灰边框容器，实现常显边框
        static void AddBordered(ComboBox cmb, int x, int yy, Panel parent)
        {
            Panel bp = new Panel();
            bp.Location = new Point(x, yy);
            bp.Size = new Size(cmb.Width + 2, cmb.Height + 2);
            bp.Padding = new Padding(1);
            bp.BackColor = Color.FromArgb(150, 150, 150);
            cmb.Location = new Point(0, 0);
            cmb.Dock = DockStyle.Fill;
            bp.Controls.Add(cmb);
            parent.Controls.Add(bp);
        }

        int AddSectionTitle(Panel p, string text, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = new Font("微软雅黑", 9f, FontStyle.Bold);
            l.ForeColor = Color.FromArgb(0, 90, 160);
            l.Tag = "keep";
            l.AutoSize = true;
            l.Location = new Point(10, y);
            p.Controls.Add(l);
            return y + 20;
        }

        // 网口卡：状态灯+标题+监听按钮 / 类型+协议 / 对端IP+端口
        int AddPortCard(Panel p, string pfx, string name, int y)
        {
            PortCard c = new PortCard();
            c.Pfx = pfx;

            c.Dot = new Label();
            c.Dot.Text = "○ 无流量";
            c.Dot.AutoSize = true;
            c.Dot.ForeColor = Color.Gray;
            c.Dot.Tag = "keep";
            c.Dot.Location = new Point(10, y + 2);
            p.Controls.Add(c.Dot);

            Label h = new Label();
            h.Text = name;
            h.Font = new Font("微软雅黑", 9f, FontStyle.Bold);
            h.AutoSize = true;
            h.Location = new Point(78, y + 2);
            p.Controls.Add(h);

            c.BtnListen = MakeButton(p, "监听", PW - 70, y, 60, 22, delegate { OpenListener(c, name); });

            y += 24;
            Label lt = new Label();
            lt.Text = "类型";
            lt.AutoSize = true;
            lt.Location = new Point(10, y + 3);
            p.Controls.Add(lt);
            c.CmbType = new ComboBox();
            c.CmbType.DropDownStyle = ComboBoxStyle.DropDownList;
            c.CmbType.FlatStyle = FlatStyle.Popup;
            c.CmbType.Items.AddRange(TYPE_ITEMS);
            c.CmbType.Width = 100;
            c.CmbType.DropDownWidth = 150;   // 下拉列表加宽，避免长名称被截断
            AddBordered(c.CmbType, 48, y, p);   // Popup 风格只有悬停才画边框，外包 1px 容器实现常显边框

            Label lp = new Label();
            lp.Text = "协议";
            lp.AutoSize = true;
            lp.Location = new Point(158, y + 3);
            p.Controls.Add(lp);
            c.CmbProto = new ComboBox();
            c.CmbProto.DropDownStyle = ComboBoxStyle.DropDownList;
            c.CmbProto.FlatStyle = FlatStyle.Popup;
            c.CmbProto.Items.AddRange(PROTO_ITEMS);
            c.CmbProto.Width = 120;
            c.CmbProto.DropDownWidth = 240;   // 协议名最长约 22 字符
            AddBordered(c.CmbProto, 192, y, p);
            y += 26;

            Label li = new Label();
            li.Text = "对端IP";
            li.AutoSize = true;
            li.Location = new Point(10, y + 3);
            p.Controls.Add(li);
            c.TxtIp = new TextBox();
            c.TxtIp.Width = 120;
            c.TxtIp.BorderStyle = BorderStyle.FixedSingle;
            c.TxtIp.Location = new Point(58, y);
            p.Controls.Add(c.TxtIp);

            Label lpt = new Label();
            lpt.Text = "端口";
            lpt.AutoSize = true;
            lpt.Location = new Point(195, y + 3);
            p.Controls.Add(lpt);
            c.NumPort = new NumericUpDown();
            c.NumPort.Minimum = 0; c.NumPort.Maximum = 65535; c.NumPort.Width = 55;
            c.NumPort.BorderStyle = BorderStyle.FixedSingle;
            c.NumPort.Location = new Point(233, y);
            p.Controls.Add(c.NumPort);
            y += 28;

            // 修改高亮：控件值与快照不一致时黄底
            c.CmbType.SelectedIndexChanged += delegate { HighlightCard(c); };
            c.CmbProto.SelectedIndexChanged += delegate { HighlightCard(c); };
            c.TxtIp.TextChanged += delegate { HighlightCard(c); };
            c.NumPort.ValueChanged += delegate { HighlightCard(c); };

            cards.Add(c);
            return y;
        }

        Button MakeButton(Panel p, string text, int x, int y, int w, int h, EventHandler onClick)
        {
            Button b = new Button();
            b.Text = text;
            b.Location = new Point(x, y);
            b.Size = new Size(w, h);
            b.Font = new Font("微软雅黑", 9f);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Color.FromArgb(0, 90, 160);
            b.BackColor = Color.White;
            b.ForeColor = Color.Black;
            b.Click += onClick;
            p.Controls.Add(b);
            return b;
        }

        // ---------- 参数读取 / 高亮 / 写入 ----------
        static string[] IpParts(string pfx) { return new string[4] { pfx + "0", pfx + "1", pfx + "2", pfx + "3" }; }

        double GetParam(string name)
        {
            try
            {
                var prms = MainV2.comPort.MAV.param;
                if (prms == null || !prms.ContainsKey(name)) return 0;
                // MAVLinkParam 提供到 float 的转换（与 MP 自身 CurrentState.cs 用法一致）
                return (float)prms[name];
            }
            catch { return 0; }
        }

        bool Connected()
        {
            return MainV2.comPort != null && MainV2.comPort.BaseStream != null &&
                MainV2.comPort.BaseStream.IsOpen;
        }

        void LoadParams()
        {
            if (!Connected())
            {
                Status("未连接飞控，无法读取。");
                return;
            }
            if (MainV2.comPort.MAV.param == null || MainV2.comPort.MAV.param.Count == 0)
            {
                Status("飞控参数尚未下载完成，请稍后再点「读取参数」。");
                return;
            }
            loading = true;
            snap = new Dictionary<string, double>();
            // 全局
            double en = GetParam("NET_ENABLE"), dh = GetParam("NET_DHCP");
            snap["NET_ENABLE"] = en; snap["NET_DHCP"] = dh;
            chkEnable.Checked = en > 0.5; chkDhcp.Checked = dh > 0.5;
            double nm = GetParam("NET_NETMASK"); snap["NET_NETMASK"] = nm;
            numNetmask.Value = (decimal)(nm < 0 ? 0 : (nm > 32 ? 32 : nm));
            txtLocalIp.Text = JoinIp(new string[4] { "NET_IPADDR0", "NET_IPADDR1", "NET_IPADDR2", "NET_IPADDR3" });
            txtGw.Text = JoinIp(new string[4] { "NET_GWADDR0", "NET_GWADDR1", "NET_GWADDR2", "NET_GWADDR3" });
            string mac = "MAC：";
            for (int i = 0; i < 6; i++)
            {
                double mv = GetParam("NET_MACADDR" + i);
                snap["NET_MACADDR" + i] = mv;
                mac += (i > 0 ? ":" : "") + ((int)mv).ToString("X2");
            }
            lblMac.Text = mac + "（只读）";
            // 四网口
            foreach (PortCard c in cards)
            {
                double t = GetParam(c.Pfx + "_TYPE"); snap[c.Pfx + "_TYPE"] = t;
                SelectByCode(c.CmbType, (int)t);
                double pr = GetParam(c.Pfx + "_PROTOCOL"); snap[c.Pfx + "_PROTOCOL"] = pr;
                SelectByCode(c.CmbProto, (int)pr);
                double pt = GetParam(c.Pfx + "_PORT"); snap[c.Pfx + "_PORT"] = pt;
                c.NumPort.Value = (decimal)(pt < 0 ? 0 : (pt > 65535 ? 65535 : pt));
                c.TxtIp.Text = JoinIp(IpParts(c.Pfx + "_IP"));
            }
            loading = false;
            foreach (PortCard c in cards) HighlightCard(c);
            HighlightGlobal();
            Status(string.Format("已读取 {0} 项参数。黄色底=已修改未写入。", snap.Count));
        }

        string JoinIp(string[] names)
        {
            double a0 = 0, a1 = 0, a2 = 0, a3 = 0;
            foreach (string n in names)
            {
                double v = GetParam(n);
                snap[n] = v;
                if (n == names[0]) a0 = v; else if (n == names[1]) a1 = v;
                else if (n == names[2]) a2 = v; else a3 = v;
            }
            return string.Format("{0}.{1}.{2}.{3}", (int)a0, (int)a1, (int)a2, (int)a3);
        }

        static void SelectByCode(ComboBox cmb, int code)
        {
            string want = code + "=";
            for (int i = 0; i < cmb.Items.Count; i++)
            {
                string it = (string)cmb.Items[i];
                if (it.StartsWith(want)) { cmb.SelectedIndex = i; return; }
            }
            // 未收录的编码：51 及以上是厂商自定义协议（玄云等），其余按未知处理
            string add = code >= 51 ? (code + "=自定义（" + code + "）") : (code + "=未知（" + code + "）");
            cmb.Items.Add(add);
            cmb.SelectedIndex = cmb.Items.Count - 1;
        }

        // 高亮：控件值与 snap 比较；不同 → 黄底
        void HighlightCard(PortCard c)
        {
            if (loading) return;
            bool dirty =
                ParseCombo(c.CmbType) != SnapD(c.Pfx + "_TYPE") ||
                ParseCombo(c.CmbProto) != SnapD(c.Pfx + "_PROTOCOL") ||
                (double)c.NumPort.Value != SnapD(c.Pfx + "_PORT") ||
                c.TxtIp.Text != SnapIp(IpParts(c.Pfx + "_IP"));
            Color bg = dirty ? Color.FromArgb(255, 255, 200) : Color.White;
            c.CmbType.BackColor = bg; c.CmbProto.BackColor = bg;
            c.NumPort.BackColor = bg; c.TxtIp.BackColor = bg;
        }

        void HighlightGlobal()
        {
            if (loading) return;
            numNetmask.BackColor = (double)numNetmask.Value != SnapD("NET_NETMASK") ?
                Color.FromArgb(255, 255, 200) : Color.White;
            txtLocalIp.BackColor = txtLocalIp.Text != SnapIp(new string[4] { "NET_IPADDR0", "NET_IPADDR1", "NET_IPADDR2", "NET_IPADDR3" }) ?
                Color.FromArgb(255, 255, 200) : Color.White;
            txtGw.BackColor = txtGw.Text != SnapIp(new string[4] { "NET_GWADDR0", "NET_GWADDR1", "NET_GWADDR2", "NET_GWADDR3" }) ?
                Color.FromArgb(255, 255, 200) : Color.White;
            chkEnable.BackColor = (chkEnable.Checked ? 1.0 : 0.0) != SnapD("NET_ENABLE") ?
                Color.FromArgb(255, 255, 200) : Color.Transparent;
            chkDhcp.BackColor = (chkDhcp.Checked ? 1.0 : 0.0) != SnapD("NET_DHCP") ?
                Color.FromArgb(255, 255, 200) : Color.Transparent;
        }

        double SnapD(string name) { double v; return snap.TryGetValue(name, out v) ? v : 0; }
        string SnapIp(string[] names)
        {
            return string.Format("{0}.{1}.{2}.{3}",
                (int)SnapD(names[0]), (int)SnapD(names[1]), (int)SnapD(names[2]), (int)SnapD(names[3]));
        }

        static double ParseCombo(ComboBox cmb)
        {
            if (cmb.SelectedIndex < 0) return -999;
            string it = (string)cmb.SelectedItem;
            int eq = it.IndexOf('=');
            double v;
            double.TryParse(it.Substring(0, eq), out v);
            return v;
        }

        // 校验 IP 文本 → 4 字节；失败返回 null
        static byte[] ParseIp(string text)
        {
            if (text == null) return null;
            string[] ps = text.Split('.');
            if (ps.Length != 4) return null;
            byte[] r = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                int b;
                if (!int.TryParse(ps[i].Trim(), out b) || b < 0 || b > 255) return null;
                r[i] = (byte)b;
            }
            return r;
        }

        void WriteParams()
        {
            if (!Connected()) { Status("未连接飞控，无法写入。"); return; }
            if (snap.Count == 0) { Status("请先读取参数。"); return; }
            // 收集修改
            List<KeyValuePair<string, double>> changes = new List<KeyValuePair<string, double>>();
            if ((chkEnable.Checked ? 1.0 : 0.0) != SnapD("NET_ENABLE"))
                changes.Add(new KeyValuePair<string, double>("NET_ENABLE", chkEnable.Checked ? 1 : 0));
            if ((chkDhcp.Checked ? 1.0 : 0.0) != SnapD("NET_DHCP"))
                changes.Add(new KeyValuePair<string, double>("NET_DHCP", chkDhcp.Checked ? 1 : 0));
            if ((double)numNetmask.Value != SnapD("NET_NETMASK"))
                changes.Add(new KeyValuePair<string, double>("NET_NETMASK", (double)numNetmask.Value));
            CollectIp(changes, txtLocalIp, new string[4] { "NET_IPADDR0", "NET_IPADDR1", "NET_IPADDR2", "NET_IPADDR3" });
            CollectIp(changes, txtGw, new string[4] { "NET_GWADDR0", "NET_GWADDR1", "NET_GWADDR2", "NET_GWADDR3" });
            foreach (PortCard c in cards)
            {
                if (ParseCombo(c.CmbType) != SnapD(c.Pfx + "_TYPE"))
                    changes.Add(new KeyValuePair<string, double>(c.Pfx + "_TYPE", ParseCombo(c.CmbType)));
                if (ParseCombo(c.CmbProto) != SnapD(c.Pfx + "_PROTOCOL"))
                    changes.Add(new KeyValuePair<string, double>(c.Pfx + "_PROTOCOL", ParseCombo(c.CmbProto)));
                if ((double)c.NumPort.Value != SnapD(c.Pfx + "_PORT"))
                    changes.Add(new KeyValuePair<string, double>(c.Pfx + "_PORT", (double)c.NumPort.Value));
                CollectIp(changes, c.TxtIp, IpParts(c.Pfx + "_IP"));
            }
            if (changes.Count == 0) { Status("没有修改，无需写入。"); return; }

            StringBuilder sb = new StringBuilder();
            sb.Append("即将写入 ").Append(changes.Count).Append(" 项参数：\n");
            int n = 0;
            foreach (KeyValuePair<string, double> kv in changes)
            {
                if (n++ >= 15) { sb.Append("…（其余省略）\n"); break; }
                sb.Append("  ").Append(kv.Key).Append(" = ").Append(kv.Value.ToString("0.###")).Append("\n");
            }
            sb.Append("\nNET 参数需要重启飞控后生效。确认写入吗？");
            DialogResult r = MessageBox.Show(sb.ToString(), "飞控网口 — 写入确认",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (r != DialogResult.Yes) return;

            int okc = 0, failc = 0;
            foreach (KeyValuePair<string, double> kv in changes)
            {
                try
                {
                    // 3 参重载会报 CS0612（已过时警告），不影响编译，保持兼容性
                    if (MainV2.comPort.setParam(kv.Key, kv.Value)) okc++;
                    else failc++;
                }
                catch { failc++; }
            }
            Status(string.Format("写入完成：成功 {0} 项，失败 {1} 项。重启飞控后生效。", okc, failc));
            LoadParams();   // 重新读取回显
        }

        void CollectIp(List<KeyValuePair<string, double>> changes, TextBox txt, string[] names)
        {
            if (txt.Text == SnapIp(names)) return;
            byte[] ip = ParseIp(txt.Text);
            if (ip == null)
            {
                Status("IP 格式错误：" + txt.Text + "（应为 x.x.x.x，每段 0~255）");
                return;
            }
            for (int i = 0; i < 4; i++)
                if (ip[i] != (int)SnapD(names[i]))
                    changes.Add(new KeyValuePair<string, double>(names[i], ip[i]));
        }

        void Status(string s)
        {
            if (lblStatus != null) lblStatus.Text = s;
        }

        // ---------- 通讯状态（绿色指示） ----------
        void UpdateActive()
        {
            if (netPanel == null || netPanel.IsDisposed || !netPanel.Visible) return;
            bool conn = Connected();
            // 已连接且还没读到参数（含"连上时参数表还没下载完"的情况）→ 自动读一次
            if (conn && snap.Count == 0 && cards.Count > 0 &&
                MainV2.comPort.MAV.param != null && MainV2.comPort.MAV.param.Count > 0)
                LoadParams();
            if (lblConn != null)
            {
                lblConn.Text = conn ? "● 已连接" : "● 未连接";
                lblConn.ForeColor = conn ? Color.Green : Color.Gray;
            }
            TcpConnectionInformation[] tcps = null;
            try
            {
                // TCP 类型网口：查本机与对端的 ESTABLISHED 连接（无需管理员）
                if (conn) tcps = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections();
            }
            catch { }
            DateTime now = DateTime.Now;
            foreach (PortCard c in cards)
            {
                if (c.Dot == null) continue;
                int type = (int)SnapD(c.Pfx + "_TYPE");
                if (type == 0 && snap.ContainsKey(c.Pfx + "_TYPE"))
                { c.Dot.Text = "× 禁用"; c.Dot.ForeColor = Color.Gray; continue; }
                string ip = c.TxtIp != null ? c.TxtIp.Text.Trim() : "";
                bool active = false;
                if (tcps != null && (type == 3 || type == 4))
                {
                    foreach (TcpConnectionInformation t in tcps)
                    {
                        string ra = t.RemoteEndPoint.Address.ToString();
                        if (ra == ip) { active = true; break; }
                    }
                }
                // 监听器捕获到最近流量（任意类型）
                if (!active)
                {
                    DateTime last;
                    if (lastTraffic.TryGetValue(c.Pfx, out last) && (now - last).TotalSeconds < 5)
                        active = true;
                }
                c.Dot.Text = active ? "● 通讯中" : "○ 无流量";
                c.Dot.ForeColor = active ? Color.Green : Color.Gray;
            }
        }

        // ---------- 监听窗口（raw socket 需管理员权限） ----------
        void OpenListener(PortCard c, string name)
        {
            string ip = c.TxtIp != null ? c.TxtIp.Text.Trim() : "";
            if (ParseIp(ip) == null)
            {
                MessageBox.Show("对端 IP 无效：" + ip, "飞控网口");
                return;
            }
            ListenForm f = new ListenForm(name, ip, c.Pfx, lastTraffic);
            f.Show(Host.MainForm as Form);
        }

        class ListenForm : Form
        {
            readonly RichTextBox rtb;
            readonly Sniffer sniffer;
            readonly Button btnPause;
            readonly List<string> pending = new List<string>();
            System.Windows.Forms.Timer flushTimer;
            int dropped = 0;
            bool paused = false;
            readonly ConcurrentDictionary<string, DateTime> lastTraffic;
            readonly string trafficKey;

            public ListenForm(string name, string targetIp, string pfx, ConcurrentDictionary<string, DateTime> traffic)
            {
                lastTraffic = traffic;
                trafficKey = pfx;
                Text = "网口监听 — " + name + " (" + targetIp + ")";
                FormBorderStyle = FormBorderStyle.Sizable;
                Size = new Size(760, 480);
                StartPosition = FormStartPosition.CenterParent;
                Font = new Font("微软雅黑", 9f);

                rtb = new RichTextBox();
                rtb.Dock = DockStyle.Fill;
                rtb.ReadOnly = true;
                rtb.BackColor = Color.White;
                rtb.Font = new Font("Consolas", 9f);
                rtb.WordWrap = false;
                Controls.Add(rtb);

                Panel bottom = new Panel();
                bottom.Dock = DockStyle.Bottom;
                bottom.Height = 34;
                Controls.Add(bottom);
                bottom.BringToFront();

                btnPause = new Button();
                btnPause.Text = "暂停";
                btnPause.Left = 10; btnPause.Width = 70;
                btnPause.Click += delegate { paused = !paused; btnPause.Text = paused ? "继续" : "暂停"; };
                bottom.Controls.Add(btnPause);

                Button btnClear = new Button();
                btnClear.Text = "清空";
                btnClear.Left = 88; btnClear.Width = 70;
                btnClear.Click += delegate { rtb.Clear(); };
                bottom.Controls.Add(btnClear);

                Label lblTip = new Label();
                lblTip.Text = "UDP/TCP 端口与 MAVLink 消息名自动解析；需管理员权限";
                lblTip.AutoSize = true;
                lblTip.Left = 180; lblTip.Top = 8;
                lblTip.ForeColor = Color.Gray;
                lblTip.Tag = "keep";
                bottom.Controls.Add(lblTip);
                SetDarkText(this);   // 监听窗口同样统一黑字（白底）

                flushTimer = new System.Windows.Forms.Timer();
                flushTimer.Interval = 200;
                flushTimer.Tick += delegate { FlushPending(); };
                flushTimer.Start();

                sniffer = new Sniffer(targetIp, delegate(string line) { OnLine(line); });
                if (!sniffer.Start())
                {
                    AppendLine("[错误] " + sniffer.Error);
                    AskElevate();
                }
                else
                    AppendLine("[监听已启动] 目标 " + targetIp + "，只显示与该 IP 相关的包。\n");
                FormClosed += delegate { if (flushTimer != null) flushTimer.Stop(); sniffer.Stop(); };
            }

            // 抓包需要管理员权限（Windows 对 raw socket 的硬性限制），提供一键提权重启
            void AskElevate()
            {
                try
                {
                    DialogResult r = MessageBox.Show(this,
                        "抓包监听需要管理员权限——这是 Windows 对原始套接字（raw socket）的硬性限制，" +
                        "任何抓包工具（含 Wireshark）都一样，程序无法自行绕过。\n\n" +
                        "是否以管理员身份重新启动 Mission Planner？\n" +
                        "（当前 MP 会关闭；重新打开后再点“监听”即可抓包，其他功能不受影响）",
                        "飞控网口 — 需要管理员权限",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);
                    if (r == DialogResult.Yes) RestartElevated();
                }
                catch { }
            }

            static void RestartElevated()
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = Application.ExecutablePath;
                    psi.UseShellExecute = true;
                    psi.Verb = "runas";      // 触发 UAC 提权
                    Process.Start(psi);
                    Application.Exit();      // 关闭当前（非管理员）实例
                }
                catch { }                    // 用户取消 UAC 时忽略
            }

            // 监听线程只把行放进缓冲（每秒可能有上百包，逐条 BeginInvoke 会灌满 UI 队列导致卡死/闪退）
            void OnLine(string line)
            {
                try
                {
                    // 记录最近流量时间（用于面板绿色指示；ConcurrentDictionary 保证线程安全）
                    if (lastTraffic != null)
                    {
                        try { lastTraffic[trafficKey] = DateTime.Now; }
                        catch { }
                    }
                    lock (pending)
                    {
                        pending.Add(line);
                        if (pending.Count > 500)
                        {
                            pending.RemoveRange(0, pending.Count - 500);
                            dropped++;
                        }
                    }
                }
                catch { }
            }

            // UI 线程定时批量上屏
            void FlushPending()
            {
                try
                {
                    string[] lines;
                    lock (pending)
                    {
                        if (paused) return;
                        if (pending.Count == 0) return;
                        lines = pending.ToArray();
                        pending.Clear();
                    }
                    StringBuilder sb = new StringBuilder();
                    if (dropped > 0)
                    {
                        sb.Append("…（丢弃 ").Append(dropped).Append(" 条）\n");
                        dropped = 0;
                    }
                    foreach (string l in lines) sb.Append(l).Append("\n");
                    AppendLine(sb.ToString().TrimEnd());
                }
                catch { }
            }

            void AppendLine(string s)
            {
                if (rtb.TextLength > 400000) rtb.Clear();
                rtb.AppendText(s + "\n");
                rtb.SelectionStart = rtb.Text.Length;
                rtb.ScrollToCaret();
            }
        }

        // raw socket 嗅探：过滤与 targetIp 相关的包
        class Sniffer
        {
            readonly string targetIp;
            readonly Action<string> report;
            Socket sock;
            Thread th;
            volatile bool running = false;
            public string Error = "";
            public string TargetIp { get { return targetIp; } }

            public Sniffer(string targetIp, Action<string> report)
            {
                this.targetIp = targetIp;
                this.report = report;
            }

            public bool Start()
            {
                try
                {
                    IPAddress local = PickLocalIp(targetIp);
                    sock = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.IP);
                    sock.Bind(new IPEndPoint(local, 0));
                    // SIO_RCVALL = 0x98000001，超出 int 范围需显式转换（IOControl 形参是 int）
                    sock.IOControl(unchecked((int)0x98000001), BitConverter.GetBytes(1), BitConverter.GetBytes(0));
                    sock.ReceiveTimeout = 500;
                    running = true;
                    th = new Thread(RxLoop);
                    th.IsBackground = true;
                    th.Start();
                    return true;
                }
                catch (Exception ex)
                {
                    Error = "启动原始套接字失败（可能需要以管理员身份运行 Mission Planner）：" + ex.Message;
                    return false;
                }
            }

            static IPAddress PickLocalIp(string targetIp)
            {
                try
                {
                    IPAddress tgt = IPAddress.Parse(targetIp);
                    foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up) continue;
                        foreach (UnicastIPAddressInformation a in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                            byte[] tb = tgt.GetAddressBytes(), lb = a.Address.GetAddressBytes();
                            if (tb[0] == lb[0] && tb[1] == lb[1] && tb[2] == lb[2])
                                return a.Address;
                        }
                    }
                    foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up) continue;
                        foreach (UnicastIPAddressInformation a in ni.GetIPProperties().UnicastAddresses)
                            if (a.Address.AddressFamily == AddressFamily.InterNetwork) return a.Address;
                    }
                }
                catch { }
                return IPAddress.Loopback;
            }

            void RxLoop()
            {
                byte[] buf = new byte[65536];
                while (running)
                {
                    int n = 0;
                    try { n = sock.Receive(buf); }
                    catch
                    {
                        if (!running) break;
                        continue;   // 超时继续
                    }
                    if (n < 20) continue;   // 小于 IP 头
                    try
                    {
                        int proto = buf[9];
                        if (proto != 6 && proto != 17) continue;   // 只显示 TCP/UDP（ICMP 分片、不可达等噪音不显示）
                        int len = (buf[2] << 8) | buf[3];
                        string src = buf[12] + "." + buf[13] + "." + buf[14] + "." + buf[15];
                        string dst = buf[16] + "." + buf[17] + "." + buf[18] + "." + buf[19];
                        bool fromTgt = src == targetIp, toTgt = dst == targetIp;
                        if (!fromTgt && !toTgt) continue;
                        if (len > n || len == 0) len = n;
                        int ihl = (buf[0] & 0x0F) * 4;
                        if (ihl < 20 || ihl >= len) ihl = 20;

                        string pname = proto == 6 ? "TCP" : (proto == 17 ? "UDP" : (proto == 1 ? "ICMP" : "IP" + proto));
                        StringBuilder sb = new StringBuilder();
                        sb.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append("  ");
                        sb.Append(fromTgt ? "飞控→" : "    →");
                        sb.Append("  ").Append(src).Append(" → ").Append(dst);
                        sb.Append("  ").Append(pname).Append(" ").Append(len - ihl).Append("B");

                        if ((proto == 6 || proto == 17) && len >= ihl + 4)
                        {
                            int sp = (buf[ihl] << 8) | buf[ihl + 1];
                            int dp = (buf[ihl + 2] << 8) | buf[ihl + 3];
                            sb.Append("  :").Append(sp).Append("→:").Append(dp);
                        }
                        // MAVLink 帧探测（UDP 载荷第 1 字节 0xFE/0xFD）
                        if (proto == 17 && len > ihl + 8)
                        {
                            byte m0 = buf[ihl + 8];
                            if (m0 == 0xFE && len > ihl + 12)
                                sb.Append("  [MAVLink1 ").Append(MavName(buf[ihl + 11])).Append("]");
                            else if (m0 == 0xFD && len > ihl + 13)
                                sb.Append("  [MAVLink2 ").Append(MavName(buf[ihl + 12])).Append("]");
                        }
                        int off = ihl + ((proto == 17) ? 8 : ((proto == 6) ? 20 : 0));
                        int hexN = Math.Min(16, len - off);
                        if (hexN > 0)
                        {
                            StringBuilder hb = new StringBuilder();
                            for (int i = 0; i < hexN; i++)
                                hb.Append(buf[off + i].ToString("X2")).Append(" ");
                            sb.Append("\n    ").Append(hb.ToString().Trim());
                        }
                        report(sb.ToString());
                    }
                    catch { }
                }
            }

            static string MavName(byte id)
            {
                switch (id)
                {
                    case 0: return "HEARTBEAT";
                    case 1: return "SYS_STATUS";
                    case 2: return "SYSTEM_TIME";
                    case 22: return "PARAM_VALUE";
                    case 23: return "PARAM_SET";
                    case 24: return "GPS_RAW_INT";
                    case 27: return "RAW_IMU";
                    case 29: return "SCALED_PRESSURE";
                    case 30: return "ATTITUDE";
                    case 32: return "LOCAL_POSITION_NED";
                    case 33: return "GLOBAL_POSITION_INT";
                    case 36: return "SERVO_OUTPUT_RAW";
                    case 65: return "RC_CHANNELS";
                    case 74: return "VFR_HUD";
                    case 111: return "TIMESYNC";
                    case 147: return "BATTERY_STATUS";
                    case 241: return "VIBRATION";
                    case 251: return "NAMED_VALUE_FLOAT";
                    case 253: return "STATUSTEXT";
                    default: return "msg" + id;
                }
            }

            public void Stop()
            {
                running = false;
                try { if (sock != null) sock.Close(); }
                catch { }
            }
        }

        // ---------- 面板拖动 ----------
        bool dragging = false;
        Point dragOffset;
        void OnPanelMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                dragging = true;
                dragOffset = e.Location;
                if (sender != netPanel && sender is Control)
                {
                    Control c = (Control)sender;
                    dragOffset = new Point(e.X + c.Left, e.Y + c.Top);
                }
            }
        }
        void OnPanelMouseMove(object sender, MouseEventArgs e)
        {
            if (dragging && netPanel != null)
            {
                Point p = netPanel.PointToScreen(new Point(e.X, e.Y));
                Point np = netPanel.Parent.PointToClient(p);
                netPanel.Location = new Point(np.X - dragOffset.X, np.Y - dragOffset.Y);
            }
        }
        void OnPanelMouseUp(object sender, MouseEventArgs e) { dragging = false; }
    }
}
