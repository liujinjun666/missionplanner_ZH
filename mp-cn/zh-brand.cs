using System;
using System.Collections.Generic;
using System.Windows.Forms;
using MissionPlanner;

namespace ZhBrand
{
    // 品牌/定制（不改任何功能逻辑）：
    // 1) 闪屏背景 splashbg.png 由官方机制加载，这里仅隐藏其上残留的原生署名/版本小标签；
    // 2) 主窗口标题持续追加“云成科技 MP中文调参版”（MP 连接/刷新会重写标题，故幂等补回）；
    // 3) 屏蔽在线更新：隐藏“帮助”页的 检查更新/检查测试版升级 两个按钮（官方商店版同款做法），
    //    配合已清空的更新地址，做到界面无入口、后台无地址。
    public class BrandPlugin : MissionPlanner.Plugin.Plugin
    {
        const string SUF = " - 云成科技 MP中文调参版";
        static readonly string[] HideUpdate = { "BUT_updatecheck", "BUT_betaupdate" };
        bool _splashDone, _updateHidden;
        int _tick;

        public override string Name { get { return "ZhBrand"; } }
        public override string Version { get { return "1.1"; } }
        public override string Author { get { return "local"; } }

        public override bool Init() { return true; }

        public override bool Loaded()
        {
            loopratehz = 4;
            _splashDone = false; _updateHidden = false; _tick = 0;
            return true;
        }

        void FixSplash()
        {
            try
            {
                Form sp = MissionPlanner.Program.Splash;
                if (sp == null || sp.IsDisposed) { _splashDone = true; return; }
                foreach (Control c in sp.Controls)
                {
                    if (c != null && (c.Name == "label1" || c.Name == "TXT_version"))
                        c.Visible = false;
                }
                if (sp.Text != null && !sp.Text.Contains("MP中文调参版"))
                    sp.Text = "云成科技 MP中文调参版 - " + sp.Text;
                _splashDone = true;
            }
            catch { }
        }

        static void FixTitle(Control mf)
        {
            try
            {
                if (mf == null || mf.IsDisposed) return;
                string t = mf.Text ?? "";
                string b = t.EndsWith(SUF) ? t.Substring(0, t.Length - SUF.Length) : t;
                string want = b + SUF;
                if (t != want) mf.Text = want;
            }
            catch { }
        }

        // 递归遍历控件树隐藏更新按钮（帮助页为懒加载，未打开前找不到属正常）
        static void HideUpdateButtons(Control root, ref bool settled)
        {
            int found = 0;
            var st = new Stack<Control>();
            st.Push(root);
            while (st.Count > 0)
            {
                Control c = st.Pop();
                if (Array.IndexOf(HideUpdate, c.Name) >= 0)
                {
                    if (c.Visible) c.Visible = false;
                    found++;
                }
                foreach (Control ch in c.Controls) st.Push(ch);
            }
            settled = found >= HideUpdate.Length;
        }

        public override bool Loop()
        {
            try
            {
                Control mf = Host.MainForm;
                Action job = delegate
                {
                    if (!_splashDone) FixSplash();
                    FixTitle(mf);
                    _tick++;
                    // 未隐藏成功前每周期找（帮助页一打开即隐藏）；成功后每5秒复查一次应对重建
                    if (mf != null && !mf.IsDisposed && (!_updateHidden || _tick % 20 == 0))
                        HideUpdateButtons(mf, ref _updateHidden);
                };
                if (mf != null && !mf.IsDisposed && mf.InvokeRequired)
                    mf.BeginInvoke(job);
                else
                    job();
            }
            catch { }
            return true;
        }

        public override bool Exit() { return true; }
    }
}
