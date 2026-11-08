using System;
using System.Windows.Forms;
using MissionPlanner;
using MissionPlanner.Controls;

namespace ZhQuickViewRf
{
    // 快速栏(QuickView)测距仪中文化：RangeFinder1 (cm) -> 测距仪1 (m)，数值厘米->米
    // 不改数据源(CurrentState.rangefinderN 仍为 cm)，仅在 WinForms 绑定显示层换算，功能逻辑零改动
    public class ZhQuickViewRfPlugin : MissionPlanner.Plugin.Plugin
    {
        public override string Name { get { return "ZhQuickViewRf"; } }
        public override string Version { get { return "1.0"; } }
        public override string Author { get { return "local"; } }

        public override bool Init() { return true; }

        public override bool Loaded()
        {
            loopratehz = 2; // 2Hz 扫描，翻译+绑定换算幂等
            return true;
        }

        void TranslateQuickView()
        {
            Control mf = Host.MainForm;
            if (mf == null || mf.IsDisposed) return;

            foreach (QuickView qv in FindQuickViews(mf))
            {
                try
                {
                    // 1) 描述翻译：RangeFinder1 (cm) -> 测距仪1 (m)
                    string d = qv.desc;
                    if (!string.IsNullOrEmpty(d) && d.StartsWith("RangeFinder", StringComparison.OrdinalIgnoreCase))
                    {
                        string rest = d.Substring("RangeFinder".Length); // 可能 "1 (cm)" / "(cm)"
                        string idx = "";
                        int p = 0;
                        while (p < rest.Length && char.IsDigit(rest[p])) { idx += rest[p]; p++; }
                        qv.desc = "测距仪" + idx + " (m)";
                    }

                    // 2) number 绑定加厘米->米换算
                    //    只对测距仪字段(rangefinder1~10)挂载，其他快速栏项绝不能动
                    foreach (Binding b in qv.DataBindings)
                    {
                        if (b.PropertyName != "number")
                            continue;
                        if (!IsRangefinderField(b))
                            continue;
                        if (HasFormat(b))
                            continue;
                        b.Format += new ConvertEventHandler(OnFormatCmToM);
                        Processed.Add(b);
                    }
                }
                catch { }
            }
        }

        static readonly System.Collections.Generic.HashSet<Binding> Processed = new System.Collections.Generic.HashSet<Binding>();

        static bool HasFormat(Binding b)
        {
            return Processed.Contains(b);
        }

        static bool IsRangefinderField(Binding b)
        {
            string field = b.BindingMemberInfo.BindingField;
            return !string.IsNullOrEmpty(field) &&
                   field.StartsWith("rangefinder", StringComparison.OrdinalIgnoreCase);
        }

        static void OnFormatCmToM(object sender, ConvertEventArgs e)
        {
            // 双保险：再次确认是测距仪字段才换算
            Binding b = sender as Binding;
            if (b == null || !IsRangefinderField(b)) return;
            if (e.Value == null || e.Value is bool) return;
            try
            {
                double v = Convert.ToDouble(e.Value);
                e.Value = v / 100.0; // cm -> m
            }
            catch { }
        }

        static System.Collections.Generic.IEnumerable<QuickView> FindQuickViews(Control c)
        {
            if (c == null) yield break;
            foreach (Control ch in c.Controls)
            {
                if (ch is QuickView)
                    yield return (QuickView)ch;
                foreach (QuickView sub in FindQuickViews(ch))
                    yield return sub;
            }
        }

        public override bool Loop()
        {
            try
            {
                Control mf = Host.MainForm;
                if (mf == null || mf.IsDisposed) return true;
                if (mf.InvokeRequired)
                    mf.BeginInvoke((Action)TranslateQuickView);
                else
                    TranslateQuickView();
            }
            catch { }
            return true;
        }

        public override bool Exit() { return true; }
    }
}
