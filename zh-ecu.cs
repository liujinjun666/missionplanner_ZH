using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Windows.Forms;
using MissionPlanner;
using MissionPlanner.Controls;

namespace ZhEcu
{
    // =========================================================================
    // 玄云 / SWIWIN 涡喷 ECU（ZK Protocol V1.2）调试与控制控件  v1.2
    //
    // 与飞控侧 Lua 脚本 swiwin_ecu_bridge.lua 配套使用：
    //   - 上行遥测：Lua 脚本解析 ECU 状态帧，以 NAMED_VALUE_FLOAT 推送 ECU_* 字段，
    //     本插件订阅 comPort.OnPacketReceived 解析并显示。
    //   - 下行控制：本插件通过 comPort.doCommand(MAV_CMD.WAYPOINT_USER_1, ...)
    //     下发控制命令，Lua 脚本收到后组 ZK 控制帧写入 ECU 串口。
    //
    // 命令号约定（两侧一致）：
    //   MAV_CMD.WAYPOINT_USER_1 = 31000
    //   param1 = CmdID(0..8)，param2 = arg1，param3 = arg2（见各按钮）
    //
    // v1.2 变更（相对 v1.1）：
    //   1) 新增 转速/排温 双曲线实时示波图（60 秒窗口，自动量程）；
    //   2) 遥测显示 10 项 → 22 项全覆盖（接收机电压/推力/电流/点火泵电压/控制状态/
    //      怠速转速/最高转速/启动时间/协议版本/闭环状态/估算油耗等）；
    //   3) 参数回显：点火泵电压、加/减速曲线当前值从 ECU 遥测实时回读显示；
    //   4) 新增「转速闭环模式」设置行（Cmd ID6：RPM_X + SW，转速控制模式入口，
    //      RPM_X 非 0 时 ID1 的 SW 失效，目标转速 = ID7 值 × RPM_X × 10）；
    //   5) 故障联动：错误码非 0 时错误行变红、面板标题显示「发动机故障」；
    //      ECU 请求大气压（ESR=1）时在气压行提示。
    // =========================================================================
    public class ZhEcuPlugin : MissionPlanner.Plugin.Plugin
    {
        const uint MSG_NAMED_VALUE_FLOAT = 251;

        public override string Name { get { return "ZhEcu"; } }
        public override string Version { get { return "1.2"; } }
        public override string Author { get { return "local"; } }

        // 勾选框 + 面板
        CheckBox chkEcu;
        Panel ecuPanel;
        bool panelAdded = false;

        // 遥测标签（两列 11 行）
        Label lblRpm, lblEgt, lblState, lblErr, lblSwst, lblThr;
        Label lblVbat, lblVrx, lblPump, lblIgnp, lblEcuT, lblCurr;
        Label lblThrust, lblPres, lblFlow, lblFuel, lblMaxrpm, lblIdlerpm;
        Label lblStartime, lblProto, lblEstfuel, lblEstrate;
        Label lblConn;
        Label title;

        // 控制控件
        Button btnStop, btnStandby, btnRun;
        Button btnHandover;   // 交还遥控（SW=0，退回 PWM 控制）
        Button btnDrain, btnGlow, btnMainValve, btnIgnValve, btnFuelPump, btnStarter;
        Button btnUnlock, btnClosePump, btnOpenPump, btnResetFuel;
        TrackBar tbThrottle;
        Label lblThrottle;
        NumericUpDown numIgnVolt, numAccCurve, numRpm, numAirPress;
        NumericUpDown numRpmx, numRpmxSw;

        // 曲线图
        EcuChart chart;

        // 参数回显提示标签（动态更新）
        Label hintIgn, hintAcc, hintRpmMode, hintPres, hintRpm;

        // 命名浮点缓存（名字 -> 值）
        readonly Dictionary<string, float> named = new Dictionary<string, float>();

        public override bool Init() { return true; }

        public override bool Loaded()
        {
            loopratehz = 4; // 4Hz：注入勾选框 + 刷新遥测 + 曲线采样（60s 窗口）
            try
            {
                MainV2.comPort.OnPacketReceived += OnPacket;
            }
            catch { }
            // 诊断标记：能编译加载就会写此文件
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "zh_ecu_loaded.txt"),
                    "loaded " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            catch { }
            return true;
        }

        // ---------- MAVLink 包接收：解析 NAMED_VALUE_FLOAT ----------
        void OnPacket(object sender, MAVLink.MAVLinkMessage msg)
        {
            try
            {
                if (msg == null) return;
                if (msg.msgid == MSG_NAMED_VALUE_FLOAT)
                {
                    var nv = msg.ToStructure<MAVLink.mavlink_named_value_float_t>();
                    string nm = DecodeName(nv.name);
                    if (nm.StartsWith("ECU_", StringComparison.OrdinalIgnoreCase))
                    {
                        lock (named)
                        {
                            named[nm.ToUpper()] = nv.value;
                        }
                    }
                }
            }
            catch { }
        }

        static string DecodeName(byte[] name)
        {
            if (name == null) return "";
            var sb = new StringBuilder();
            foreach (byte b in name)
            {
                if (b == 0) break;
                sb.Append((char)b);
            }
            return sb.ToString();
        }

        float GetNamed(string key)
        {
            lock (named)
            {
                float v;
                if (named.TryGetValue(key, out v)) return v;
            }
            return 0f;
        }

        // ---------- 发送控制命令 ----------
        bool SendEcuCmd(int cmdId, float arg1, float arg2)
        {
            try
            {
                var cp = MainV2.comPort;
                if (cp == null || !cp.BaseStream.IsOpen)
                {
                    MessageBox.Show("未连接飞控，无法发送控制命令。", "玄云 ECU",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                // MAV_CMD.WAYPOINT_USER_1 = 31000；param1=CmdID, param2=arg1, param3=arg2
                cp.doCommand(MAVLink.MAV_CMD.WAYPOINT_USER_1,
                    cmdId, arg1, arg2, 0, 0, 0, 0, false);
                return true;
            }
            catch (Exception ex)
            {
                try { MessageBox.Show("发送失败：" + ex.Message, "玄云 ECU",
                    MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
                return false;
            }
        }

        // ---------- 交还遥控：发 SW=0 退回 PWM 控制（带二次确认） ----------
        void OnHandover(object sender, EventArgs e)
        {
            var r = MessageBox.Show(
                "即将把发动机控制权交还遥控器（发 SW=0 控制帧）。\r\n" +
                "ECU 将停止响应串口下发的油门，改由遥控器 PWM 控制。\r\n\r\n" +
                "确认交还吗？",
                "玄云 ECU — 交还遥控",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (r == DialogResult.Yes)
            {
                // ID1 + SW=0 + 油门0 → 交还 PWM 控制
                SendEcuCmd(1, 0, 0);
                if (tbThrottle != null) tbThrottle.Value = 0;
                if (lblThrottle != null) lblThrottle.Text = "0%";
            }
        }

        // ---------- 注入勾选框 + 面板 ----------
        void EnsureUI()
        {
            if (panelAdded) return;  // 防止重复添加

            Control mf = Host.MainForm;
            if (mf == null || mf.IsDisposed)
            {
                Diag("MainForm null/disposed");
                return;
            }

            // 策略1：按名字找 CHK_autopan（自动平移）
            CheckBox autopan = FindControl<CheckBox>(mf, "CHK_autopan");

            // 策略2：名字找不到就按 Text 匹配"自动平移"
            if (autopan == null)
                autopan = FindCheckByText(mf, "自动平移");

            if (autopan == null)
            {
                Diag("autopan not found, dumping checkboxes");
                DumpCheckBoxes(mf);
                return;
            }

            Control parent = autopan.Parent;
            if (parent == null)
            {
                Diag("autopan.Parent null");
                DumpCheckBoxes(mf);
                return;
            }

            // 创建勾选框，放在"自动平移"右侧
            chkEcu = new CheckBox();
            chkEcu.Text = "玄云ECU";
            chkEcu.AutoSize = true;
            chkEcu.Checked = false;
            chkEcu.CheckedChanged += OnEcuChecked;
            chkEcu.Name = "CHK_ecu";

            parent.Controls.Add(chkEcu);
            chkEcu.BringToFront();

            int w = chkEcu.Width > 0 ? chkEcu.Width : 70;
            int x = autopan.Right + 8;
            if (x + w > parent.ClientSize.Width)   // 右侧空间不足则贴右边缘，防画出界
                x = Math.Max(0, parent.ClientSize.Width - w - 2);
            int y = autopan.Top;
            chkEcu.Location = new Point(x, y);

            panelAdded = true;
            Diag("UI added OK at x=" + x);
        }

        // 诊断日志（只写一次，避免刷屏）
        bool diagWritten = false;
        void Diag(string s)
        {
            if (diagWritten) return;
            diagWritten = true;
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "zh_ecu_loaded.txt"),
                    "\r\n" + DateTime.Now.ToString("HH:mm:ss") + " " + s);
            }
            catch { }
        }

        // 按 Text 找 CheckBox（支持子串匹配）
        static CheckBox FindCheckByText(Control root, string text)
        {
            if (root == null) return null;
            foreach (Control c in root.Controls)
            {
                CheckBox cb = c as CheckBox;
                if (cb != null && cb.Text != null && cb.Text.Contains(text))
                    return cb;
                CheckBox r = FindCheckByText(c, text);
                if (r != null) return r;
            }
            return null;
        }

        // 诊断：把所有 CheckBox 的名字和文本写入日志
        static void DumpCheckBoxes(Control root)
        {
            try
            {
                var sb = new StringBuilder();
                DumpCheckBoxesRec(root, sb, 0);
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "zh_ecu_loaded.txt"),
                    "\r\n=== CheckBox dump " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===\r\n" + sb.ToString());
            }
            catch { }
        }

        static void DumpCheckBoxesRec(Control c, StringBuilder sb, int depth)
        {
            if (c == null) return;
            CheckBox cb = c as CheckBox;
            if (cb != null)
            {
                sb.Append(new string(' ', depth * 2));
                sb.Append("CheckBox name=" + c.Name + " text=" + cb.Text + "\r\n");
            }
            foreach (Control ch in c.Controls)
                DumpCheckBoxesRec(ch, sb, depth + 1);
        }

        void OnEcuChecked(object sender, EventArgs e)
        {
            if (chkEcu.Checked)
                ShowPanel();
            else
                HidePanel();
        }

        void ShowPanel()
        {
            if (ecuPanel != null && !ecuPanel.IsDisposed) { ecuPanel.Visible = true; return; }
            ecuPanel = BuildPanel();
            // 放到主窗体右下角，避开左侧快速栏与上方功能区
            Form mf = Host.MainForm as Form;
            if (mf != null)
            {
                mf.Controls.Add(ecuPanel);
                int x = Math.Max(0, mf.ClientSize.Width - ecuPanel.Width - 20);
                int y = Math.Max(0, mf.ClientSize.Height - ecuPanel.Height - 20);
                ecuPanel.Location = new Point(x, y);
                ecuPanel.BringToFront();
            }
            ecuPanel.Visible = true;
        }

        void HidePanel()
        {
            if (ecuPanel != null && !ecuPanel.IsDisposed) ecuPanel.Visible = false;
        }

        // 拖动支持
        bool dragging = false;
        Point dragOffset;

        void OnPanelMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                dragging = true;
                dragOffset = e.Location;
                if (sender != ecuPanel && sender is Control)
                {
                    Control c = (Control)sender;
                    dragOffset = new Point(e.X + c.Left, e.Y + c.Top);
                }
            }
        }

        void OnPanelMouseMove(object sender, MouseEventArgs e)
        {
            if (dragging && ecuPanel != null)
            {
                Point p = ecuPanel.PointToScreen(new Point(e.X, e.Y));
                Point np = ecuPanel.Parent.PointToClient(p);
                ecuPanel.Location = new Point(np.X - dragOffset.X, np.Y - dragOffset.Y);
            }
        }

        void OnPanelMouseUp(object sender, MouseEventArgs e)
        {
            dragging = false;
        }

        // ---------- 构建完整面板 ----------
        const int PW = 395;   // 面板宽度（用户要求继续收窄：415→395）

        Panel BuildPanel()
        {
            Panel p = new Panel();
            p.Name = "ecuPanel";
            p.BorderStyle = BorderStyle.FixedSingle;
            p.BackColor = Color.White;   // 固定白色底，防主题改色导致白字白底
            p.MouseDown += OnPanelMouseDown;
            p.MouseMove += OnPanelMouseMove;
            p.MouseUp += OnPanelMouseUp;

            int y = 8;
            // 标题
            title = new Label();
            title.Text = "玄云涡喷 ECU 调试面板 v1.2.2";
            title.Font = new Font("微软雅黑", 10f, FontStyle.Bold);
            title.ForeColor = Color.Black;
            title.AutoSize = true;
            title.Location = new Point(10, y);
            p.Controls.Add(title);
            y += 24;

            lblConn = new Label();
            lblConn.Text = "● 未连接";
            lblConn.ForeColor = Color.Gray;
            lblConn.AutoSize = true;
            lblConn.Location = new Point(PW - 150, y - 22);
            p.Controls.Add(lblConn);
            y += 4;

            // --- 曲线图已按用户要求取消（占高过多、遮挡上方连接控件）---
            // 保留遥测区首行的转速/排温数值显示

            // --- 遥测区（两列 11 行）---
            y = AddSectionTitle(p, "遥测数据", y);
            y = TelRow(p, y, "转速 RPM", out lblRpm, "排温 ℃", out lblEgt);
            y = TelRow(p, y, "状态", out lblState, "错误码", out lblErr);
            y = TelRow(p, y, "控制状态", out lblSwst, "油门 %", out lblThr);
            y = TelRow(p, y, "电源电压 V", out lblVbat, "接收机电压 V", out lblVrx);
            y = TelRow(p, y, "油泵电压 V", out lblPump, "点火泵电压 V", out lblIgnp);
            y = TelRow(p, y, "ECU温度 ℃", out lblEcuT, "电流 A", out lblCurr);
            y = TelRow(p, y, "推力 kg", out lblThrust, "大气压 hPa", out lblPres);
            y = TelRow(p, y, "燃油流量 L/min", out lblFlow, "累计油耗 L", out lblFuel);
            y = TelRow(p, y, "最高转速 RPM", out lblMaxrpm, "怠速转速 RPM", out lblIdlerpm);
            y = TelRow(p, y, "启动时间 s", out lblStartime, "协议/闭环", out lblProto);
            y = TelRow(p, y, "估算油耗 kg", out lblEstfuel, "估算流量 g/min", out lblEstrate);
            y += 4;

            // --- 发动机状态控制区 ---
            y = AddSectionTitle(p, "发动机控制", y);

            btnStop = MakeButton(p, "停车", 10, y, 110, 28, delegate { SendEcuCmd(1, 1, 0); });
            btnStandby = MakeButton(p, "待机", 137, y, 110, 28, delegate { SendEcuCmd(1, 2, 0); });
            btnRun = MakeButton(p, "运行", 264, y, 110, 28, delegate { SendEcuCmd(1, 3, 0); });
            y += 32;

            // 交还遥控：一整行醒目按钮，退回遥控器 PWM 控制
            btnHandover = MakeButton(p, "交还遥控（退回 PWM 控制）", 10, y, PW - 20, 28, OnHandover);
            btnHandover.BackColor = Color.FromArgb(255, 240, 200);
            btnHandover.FlatAppearance.BorderColor = Color.FromArgb(200, 120, 0);
            y += 32;

            // 油门滑杆
            Label lblThrCtl = new Label();
            lblThrCtl.Text = "油门 (0~100%)";
            lblThrCtl.AutoSize = true;
            lblThrCtl.Location = new Point(10, y);
            p.Controls.Add(lblThrCtl);

            lblThrottle = new Label();
            lblThrottle.Text = "0%";
            lblThrottle.AutoSize = true;
            lblThrottle.Location = new Point(PW - 50, y);
            p.Controls.Add(lblThrottle);
            y += 16;

            tbThrottle = new TrackBar();
            tbThrottle.Minimum = 0;
            tbThrottle.Maximum = 1000;
            tbThrottle.Value = 0;
            tbThrottle.Location = new Point(8, y);
            tbThrottle.Size = new Size(PW - 24, 40);
            tbThrottle.Scroll += delegate
            {
                lblThrottle.Text = (tbThrottle.Value / 10.0).ToString("0.0") + "%";
                // 实时下发油门（SW=3 运行态 + 油门）
                SendEcuCmd(1, 3, tbThrottle.Value);
            };
            p.Controls.Add(tbThrottle);
            y += 44;
            y += 2;

            // --- 测试/维护区 ---
            y = AddSectionTitle(p, "测试 / 维护", y);

            btnDrain = MakeButton(p, "排油", 10, y, 110, 28, delegate { SendEcuCmd(2, 1, 0); });
            btnGlow = MakeButton(p, "测试火头", 137, y, 110, 28, delegate { SendEcuCmd(2, 2, 0); });
            btnMainValve = MakeButton(p, "测试主阀", 264, y, 110, 28, delegate { SendEcuCmd(2, 3, 0); });
            y += 32;

            btnIgnValve = MakeButton(p, "测试点火阀", 10, y, 110, 28, delegate { SendEcuCmd(2, 4, 0); });
            btnFuelPump = MakeButton(p, "测试油泵", 137, y, 110, 28, delegate { SendEcuCmd(2, 5, 0); });
            btnStarter = MakeButton(p, "测试启动电机", 264, y, 110, 28, delegate { SendEcuCmd(2, 6, 0); });
            y += 32;

            btnOpenPump = MakeButton(p, "长开油泵", 10, y, 110, 28, delegate { SendEcuCmd(2, 12, 0); });
            btnClosePump = MakeButton(p, "关闭油泵", 137, y, 110, 28, delegate { SendEcuCmd(2, 13, 0); });
            btnResetFuel = MakeButton(p, "重置油耗", 264, y, 110, 28, delegate { SendEcuCmd(2, 10, 0); });
            y += 32;
            y += 2;

            // --- 参数读写区 ---
            y = AddSectionTitle(p, "参数设置（需先解锁）", y);

            Label lblUnlockHint = new Label();
            lblUnlockHint.Text = "修改 ECU 参数前须先点「解锁参数设置」，否则 ID4/ID5 不生效。";
            lblUnlockHint.ForeColor = Color.Gray;
            lblUnlockHint.AutoSize = true;
            lblUnlockHint.Location = new Point(12, y);
            p.Controls.Add(lblUnlockHint);
            y += 16;

            btnUnlock = MakeButton(p, "解锁参数设置", 10, y, 145, 28, delegate { SendEcuCmd(3, 0, 0); });
            y += 32;
            y += 2;

            // 点火油泵电压（回显当前值）
            y = AddParamRow(p, "点火油泵电压", 10, y,
                delegate
                {
                    numIgnVolt = new NumericUpDown();
                    numIgnVolt.Minimum = 0;
                    numIgnVolt.Maximum = 255;
                    numIgnVolt.DecimalPlaces = 2;
                    numIgnVolt.Increment = 0.02m;
                    numIgnVolt.Value = 0;
                    numIgnVolt.Width = 60;
                    return numIgnVolt;
                },
                delegate
                {
                    float raw = (float)(numIgnVolt.Value / 0.02m);
                    SendEcuCmd(4, raw, 0);
                },
                out hintIgn);

            // 加速曲线（回显当前加/减速值）
            y = AddParamRow(p, "加速曲线", 10, y,
                delegate
                {
                    numAccCurve = new NumericUpDown();
                    numAccCurve.Minimum = 10;
                    numAccCurve.Maximum = 70;
                    numAccCurve.Value = 30;
                    numAccCurve.Width = 60;
                    return numAccCurve;
                },
                delegate
                {
                    SendEcuCmd(5, (float)numAccCurve.Value, 0);
                },
                out hintAcc);

            // 转速闭环模式（Cmd ID6：RPM_X + SW）
            y = AddRpmModeRow(p, y);

            // 目标转速
            y = AddParamRow(p, "目标转速", 10, y,
                delegate
                {
                    numRpm = new NumericUpDown();
                    numRpm.Minimum = 0;
                    numRpm.Maximum = 4095;
                    numRpm.Value = 0;
                    numRpm.Width = 60;
                    return numRpm;
                },
                delegate
                {
                    SendEcuCmd(7, (float)numRpm.Value, 0);
                },
                out hintRpm);

            // 大气压
            y = AddParamRow(p, "大气压", 10, y,
                delegate
                {
                    numAirPress = new NumericUpDown();
                    numAirPress.Minimum = 0;
                    numAirPress.Maximum = 1024;
                    numAirPress.Value = 1013;
                    numAirPress.Width = 60;
                    return numAirPress;
                },
                delegate
                {
                    SendEcuCmd(8, (float)numAirPress.Value, 0);
                },
                out hintPres);

            // 统一防主题染色：数值控件强制黑字白底；标签作为拖动把手
            foreach (Control c in p.Controls)
            {
                if (c is NumericUpDown || c is TrackBar)
                {
                    c.BackColor = Color.White;
                    c.ForeColor = Color.Black;
                }
                else if (c is Label)
                {
                    c.MouseDown += OnPanelMouseDown;
                    c.MouseMove += OnPanelMouseMove;
                    c.MouseUp += OnPanelMouseUp;
                }
            }

            p.Size = new Size(PW, y + 12);
            return p;
        }

        int AddSectionTitle(Panel p, string text, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = new Font("微软雅黑", 9f, FontStyle.Bold);
            l.ForeColor = Color.FromArgb(0, 90, 160);
            l.AutoSize = true;
            l.Location = new Point(10, y);
            p.Controls.Add(l);
            return y + 22;
        }

        // 遥测双列行：每行两项（名称+值 ×2），返回新的 y
        int TelRow(Panel p, int y, string n1, out Label v1, string n2, out Label v2)
        {
            Label a = new Label();
            a.Text = n1; a.AutoSize = true; a.ForeColor = Color.FromArgb(96, 96, 96);
            a.Location = new Point(12, y);
            p.Controls.Add(a);

            Label av = new Label();
            av.Text = "—"; av.AutoSize = true; av.ForeColor = Color.Black;
            av.Font = new Font("微软雅黑", 9f, FontStyle.Bold);
            av.Location = new Point(108, y);
            p.Controls.Add(av);
            v1 = av;

            Label b = new Label();
            b.Text = n2; b.AutoSize = true; b.ForeColor = Color.FromArgb(96, 96, 96);
            b.Location = new Point(184, y);
            p.Controls.Add(b);

            Label bv = new Label();
            bv.Text = "—"; bv.AutoSize = true; bv.ForeColor = Color.Black;
            bv.Font = new Font("微软雅黑", 9f, FontStyle.Bold);
            bv.Location = new Point(282, y);
            p.Controls.Add(bv);
            v2 = bv;

            return y + 20;   // 行距 20px：9pt 加粗值高约 18px，留 2px 间隙不重叠
        }

        // 参数行：标签 + 数值框 + 设置按钮 + 灰色说明（回显当前值），返回新的 y
        int AddParamRow(Panel p, string label, int x, int y,
                        Func<NumericUpDown> makeNum, EventHandler onSet,
                        out Label hintOut)
        {
            Label lbl = new Label();
            lbl.Text = label;
            lbl.Font = new Font("微软雅黑", 9f, FontStyle.Bold);
            lbl.ForeColor = Color.Black;
            lbl.AutoSize = true;
            lbl.Location = new Point(12, y);
            p.Controls.Add(lbl);

            NumericUpDown num = makeNum();
            num.Location = new Point(160, y - 2);
            p.Controls.Add(num);

            MakeButton(p, "设置", 228, y - 2, 52, 24, onSet);

            y += 25;   // 参数行控件高约 22px；留足间隙，防止说明文字与下一行控件重叠
            Label hintLabel = new Label();
            hintLabel.Text = "";
            hintLabel.ForeColor = Color.Gray;
            hintLabel.AutoSize = true;
            hintLabel.Location = new Point(12, y);
            p.Controls.Add(hintLabel);
            hintOut = hintLabel;
            return y + 18;
        }

        // 转速闭环模式行：RPM_X(0~6) + SW(0~3) → Cmd ID6
        int AddRpmModeRow(Panel p, int y)
        {
            Label lbl = new Label();
            lbl.Text = "转速闭环模式";
            lbl.Font = new Font("微软雅黑", 9f, FontStyle.Bold);
            lbl.ForeColor = Color.Black;
            lbl.AutoSize = true;
            lbl.Location = new Point(12, y);
            p.Controls.Add(lbl);

            numRpmx = new NumericUpDown();
            numRpmx.Minimum = 0; numRpmx.Maximum = 6; numRpmx.Value = 0; numRpmx.Width = 44;
            numRpmx.Location = new Point(160, y - 2);
            p.Controls.Add(numRpmx);

            Label l2 = new Label();
            l2.Text = "SW"; l2.AutoSize = true; l2.ForeColor = Color.FromArgb(96, 96, 96);
            l2.Location = new Point(212, y);
            p.Controls.Add(l2);

            numRpmxSw = new NumericUpDown();
            numRpmxSw.Minimum = 0; numRpmxSw.Maximum = 3; numRpmxSw.Value = 0; numRpmxSw.Width = 40;
            numRpmxSw.Location = new Point(240, y - 2);
            p.Controls.Add(numRpmxSw);

            MakeButton(p, "设置", 292, y - 2, 52, 24, delegate
            {
                SendEcuCmd(6, (float)numRpmx.Value, (float)numRpmxSw.Value);
            });

            y += 25;   // 同 AddParamRow：防说明文字与下一行控件重叠
            hintRpmMode = new Label();
            hintRpmMode.Text = "";
            hintRpmMode.ForeColor = Color.Gray;
            hintRpmMode.AutoSize = true;
            hintRpmMode.Location = new Point(12, y);
            p.Controls.Add(hintRpmMode);
            return y + 18;
        }

        Button MakeButton(Panel p, string text, int x, int y, int w, int h, EventHandler onClick)
        {
            Button b = new Button();
            b.Text = text;
            b.Location = new Point(x, y);
            b.Size = new Size(w, h);
            b.Font = new Font("微软雅黑", 9f);
            // 显式指定样式和颜色：MP 主题系统会给新控件重新着色（曾导致白底白字看不见）
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Color.FromArgb(0, 90, 160);
            b.BackColor = Color.White;
            b.ForeColor = Color.Black;
            b.Click += onClick;
            p.Controls.Add(b);
            return b;
        }

        // ---------- 循环：刷新遥测显示 + 连接状态 + 曲线采样 ----------
        void RefreshTelemetry()
        {
            if (ecuPanel == null || ecuPanel.IsDisposed || !ecuPanel.Visible) return;
            try
            {
                bool connected = MainV2.comPort != null && MainV2.comPort.BaseStream.IsOpen;
                if (lblConn != null)
                {
                    lblConn.Text = connected ? "● 已连接" : "● 未连接";
                    lblConn.ForeColor = connected ? Color.Green : Color.Gray;
                }

                float rpm = GetNamed("ECU_RPM");
                float egt = GetNamed("ECU_EGT");
                int state = (int)GetNamed("ECU_STATE");
                int err = (int)GetNamed("ECU_ERR");

                lblRpm.Text = rpm.ToString("0");
                lblEgt.Text = egt.ToString("0");
                lblState.Text = "[" + state + "] " + StateShort(state);
                lblErr.Text = ErrName(err);
                lblErr.ForeColor = err == 0 ? Color.Black : Color.Red;
                lblSwst.Text = SwstName((int)GetNamed("ECU_SWST"));
                lblThr.Text = GetNamed("ECU_THR").ToString("0");
                lblVbat.Text = GetNamed("ECU_VBAT").ToString("0.0");
                lblVrx.Text = GetNamed("ECU_VRX").ToString("0.0");
                lblPump.Text = GetNamed("ECU_PUMP").ToString("0.0");
                lblIgnp.Text = GetNamed("ECU_IGNP").ToString("0.00");
                lblEcuT.Text = GetNamed("ECU_ECUT").ToString("0");
                lblCurr.Text = GetNamed("ECU_CURR").ToString("0.0");
                lblThrust.Text = GetNamed("ECU_THRUST").ToString("0.0");
                lblPres.Text = GetNamed("ECU_PRES").ToString("0");
                lblFlow.Text = GetNamed("ECU_FLOW").ToString("0.00");
                lblFuel.Text = GetNamed("ECU_FUEL").ToString("0.0");
                lblMaxrpm.Text = GetNamed("ECU_MAXRPM").ToString("0");
                lblIdlerpm.Text = GetNamed("ECU_IDLERPM").ToString("0");
                lblStartime.Text = GetNamed("ECU_STARTIME").ToString("0.0");
                float proto = GetNamed("ECU_PROTO");
                lblProto.Text = (proto > 0 ? "v" + proto.ToString("0") : "—") +
                    " " + (GetNamed("ECU_SCL") > 0.5f ? "闭环" : "开环");
                lblEstfuel.Text = GetNamed("ECU_ESTFUEL").ToString("0.00");
                lblEstrate.Text = (GetNamed("ECU_ESTRATE") * 1000f).ToString("0");

                // 故障联动：错误非 0 时标题告警
                if (title != null)
                {
                    if (err != 0)
                    {
                        title.Text = "⚠ 玄云涡喷 ECU — 发动机故障";
                        title.ForeColor = Color.Red;
                    }
                    else
                    {
                        title.Text = "玄云涡喷 ECU 调试面板 v1.2.2";
                        title.ForeColor = Color.Black;
                    }
                }

                // 参数回显（来自状态帧 ID5/ID6/ID8 的实时值）
                if (hintIgn != null)
                    hintIgn.Text = "0.02V/字，范围 0~5.1V   当前回读: " +
                        GetNamed("ECU_IGNP").ToString("0.00") + " V";
                if (hintAcc != null)
                    hintAcc.Text = "范围 10~70，越大加速越快   当前回读: 加速 " +
                        GetNamed("ECU_ACC").ToString("0") + " / 减速 " + GetNamed("ECU_DEC").ToString("0");
                if (hintRpmMode != null)
                {
                    bool esr = GetNamed("ECU_ESR") > 0.5f;
                    hintRpmMode.Text = "RPM_X(0~6)×10×ID7转速；非0时ID1的SW失效" + (esr ? "  ← ECU请求下发大气压" : "");
                    hintRpmMode.ForeColor = esr ? Color.Red : Color.Gray;
                }
                if (hintRpm != null)
                    hintRpm.Text = "原始值 0~4095，实际转速 = 值 × RPM_X × 10";
                if (hintPres != null)
                {
                    bool esr = GetNamed("ECU_ESR") > 0.5f;
                    hintPres.Text = "范围 0~1024 hPa" + (esr ? "  ← ECU 请求下发大气压，请点「设置」" : "");
                    hintPres.ForeColor = esr ? Color.Red : Color.Gray;
                }

                // 曲线采样
                if (chart != null) chart.Add(rpm, egt);
            }
            catch { }
        }

        static string StateShort(int s)
        {
            string[] names = {
                "停止","待机/自冷","点火","点火","预热","油路渐充","学·最大油门","学·最小油门",
                "学·怠速","最低油门","运行·泵限","运行","冷却","重启","试火头","试主阀",
                "试点火阀","试油泵","试启电机","排油"
            };
            if (s >= 0 && s < names.Length) return names[s];
            return "未知";
        }

        static string SwstName(int swst)
        {
            switch (swst)
            {
                case 0: return "停止";
                case 1: return "待机";
                case 2: return "启动/运行";
                default: return "[" + swst + "]";
            }
        }

        static string ErrName(int e)
        {
            if (e == 0) return "无";
            string[] names = {
                "","超时","电压低","火头故障","油泵故障","启动电机故障","转速低",
                "转速不稳定","排温高","排温低","排温传感器故障","点火阀故障","主阀故障",
                "失控信号丢失","启动电机控制器温度高","油泵控制器温度高","离合器故障",
                "电流过载","发动机离线"
            };
            if (e >= 1 && e < names.Length) return "[" + e + "] " + names[e];
            return "[" + e + "] 未知";
        }

        public override bool Loop()
        {
            try
            {
                Control mf = Host.MainForm;
                if (mf == null || mf.IsDisposed) return true;
                if (mf.InvokeRequired)
                    mf.BeginInvoke((Action)delegate { EnsureUI(); RefreshTelemetry(); });
                else
                { EnsureUI(); RefreshTelemetry(); }
            }
            catch { }
            return true;
        }

        public override bool Exit()
        {
            try { MainV2.comPort.OnPacketReceived -= OnPacket; } catch { }
            return true;
        }

        // ---------- 工具：递归查找控件 ----------
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
    }

    // ============ 转速/排温 双曲线实时图（GDI 自绘，60 秒窗口） ============
    internal class EcuChart : Panel
    {
        const int CAP = 240;          // 4Hz × 60s
        readonly float[] rbuf = new float[CAP];
        readonly float[] ebuf = new float[CAP];
        int count;
        float lastRpm, lastEgt;

        public EcuChart()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
        }

        public void Add(float rpm, float egt)
        {
            if (count < CAP)
            {
                rbuf[count] = rpm; ebuf[count] = egt; count++;
            }
            else
            {
                Array.Copy(rbuf, 1, rbuf, 0, CAP - 1);
                Array.Copy(ebuf, 1, ebuf, 0, CAP - 1);
                rbuf[CAP - 1] = rpm; ebuf[CAP - 1] = egt;
            }
            lastRpm = rpm; lastEgt = egt;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            try
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
            }
            catch { }

            int w = Width, h = Height;
            int top = 20, bottom = 6, left = 6, right = 6;
            int ph = h - top - bottom;
            int pw = w - left - right;
            if (ph < 10 || pw < 10) return;

            // 网格
            using (Pen grid = new Pen(Color.FromArgb(225, 225, 225)))
            {
                for (int i = 0; i <= 4; i++)
                {
                    int gy = top + ph * i / 4;
                    g.DrawLine(grid, left, gy, left + pw, gy);
                }
                for (int i = 0; i <= 6; i++)
                {
                    int gx = left + pw * i / 6;
                    g.DrawLine(grid, gx, top, gx, top + ph);
                }
            }

            if (count < 2)
            {
                using (Brush b = new SolidBrush(Color.FromArgb(160, 160, 160)))
                    g.DrawString("等待遥测数据…", new Font("微软雅黑", 8.5f), b, left + 6, top + 4);
                return;
            }

            // 自动量程
            float maxR = 0, maxE = 0;
            for (int i = 0; i < count; i++)
            {
                if (rbuf[i] > maxR) maxR = rbuf[i];
                if (ebuf[i] > maxE) maxE = ebuf[i];
            }
            float scaleR = Math.Max(40000f, ((float)Math.Ceiling(maxR * 1.1 / 10000)) * 10000);
            float scaleE = Math.Max(400f, ((float)Math.Ceiling(maxE * 1.1 / 100)) * 100);

            // 画曲线
            DrawSeries(g, rbuf, count, scaleR, left, top, pw, ph, Color.FromArgb(0, 90, 160));
            DrawSeries(g, ebuf, count, scaleE, left, top, pw, ph, Color.FromArgb(210, 80, 0));

            // 图例 + 当前值
            using (Font f = new Font("微软雅黑", 8.5f))
            {
                g.DrawString("转速 " + lastRpm.ToString("0") + "  (满量程 " + scaleR.ToString("0") + ")",
                    f, new SolidBrush(Color.FromArgb(0, 90, 160)), left + 2, 3);
                g.DrawString("排温 " + lastEgt.ToString("0") + "℃  (满量程 " + scaleE.ToString("0") + ")",
                    f, new SolidBrush(Color.FromArgb(210, 80, 0)), left + 200, 3);
                using (Brush b = new SolidBrush(Color.FromArgb(160, 160, 160)))
                {
                    g.DrawString("60 秒", f, b, left + 2, h - 18);   // 左下角，避开右边缘 DPI 缩放裁剪
                }
            }
        }

        static void DrawSeries(Graphics g, float[] buf, int n, float scale,
            int left, int top, int pw, int ph, Color color)
        {
            PointF[] pts = new PointF[n];
            float step = (float)pw / (CAP - 1);
            float x0 = left + pw - step * (CAP - 1); // 从右往左推进，右侧为最新
            for (int i = 0; i < n; i++)
            {
                float v = buf[i];
                if (v < 0) v = 0;
                if (v > scale) v = scale;
                pts[i] = new PointF(x0 + step * (CAP - n + i), top + ph - v / scale * ph);
            }
            using (Pen pen = new Pen(color, 1.6f))
                g.DrawLines(pen, pts);
        }
    }
}
