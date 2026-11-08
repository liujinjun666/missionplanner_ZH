using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MissionPlanner.Utilities;
using MissionPlanner.Controls;
using System.IO;
using System.Windows.Forms;
using System.Diagnostics;
using MissionPlanner;
using System.Drawing;
using GMap.NET.WindowsForms;
using MissionPlanner.GCSViews;
using MissionPlanner.Maps;
using MissionPlanner.Comms;
using System.Net.Sockets;
using System.Net;
using System.Threading.Tasks;
using System.Threading;

namespace ZhMenuCn
{
    // 飞行数据/飞行计划 地图右键菜单中文化（运行时按英文原文精确替换，幂等，不改任何功能逻辑）
    public class ZhMenuPlugin : MissionPlanner.Plugin.Plugin
    {
        public override string Name { get { return "ZhMenuCn"; } }
        public override string Version { get { return "1.1"; } }
        public override string Author { get { return "local"; } }

        static readonly Dictionary<string, string> Map = new Dictionary<string, string>
        {
            // ===== 飞行数据页地图右键（FlightData / FDMenuMap）=====
            {"Fly To Coords", "飞行到坐标"},
            {"Add Poi", "添加兴趣点"},
            {"Point Camera Coords", "在坐标处放置相机"},
            {"TakeOff", "起飞"},
            {"Camera Overlap", "相机重叠"},
            {"Jump To Tag", "跳转到标记"},
            {"Gimbal Video", "云台视频"},
            {"Mini", "小窗"},
            {"Pop Out", "弹出窗口"},
            {"Full Sized", "全尺寸"},
            {"Customize", "自定义"},
            {"Delete", "删除"},
            {"Ground Color", "背景颜色"},
            {"GStreamer Stop", "停止 GStreamer"},
            {"HereLink Video", "HereLink 视频"},
            {"Load File", "加载文件"},
            {"Save File", "保存文件"},
            {"MultiLine", "多行"},
            {"Coords", "坐标"},
            {"Record Hud to AVI", "录制 HUD 为 AVI"},
            {"Russian Hud", "俄式 HUD"},
            {"Set Aspect Ratio", "设置宽高比"},
            {"Battery Cell Voltage", "电池电芯电压"},
            {"Set EKF Origin Here", "在此设置 EKF 原点"},
            {"Set GStreamer Source", "设置 GStreamer 源"},
            {"Set MJPEG source", "设置 MJPEG 源"},
            {"Set View Count", "设置视图数量"},
            {"Show icons", "显示图标"},
            {"Start Camera", "启动相机"},
            {"Stop Record", "停止录制"},
            {"Swap With Map", "与地图交换"},
            {"Undock", "取消停靠"},
            {"User Items", "自定义项"},
            {"Video", "视频"},

            // ===== 飞行计划页地图右键（FlightPlanner / FPMenuMap）=====
            {"Draw a Polygon", "绘制多边形"},
            {"Area", "区域面积"},
            {"Auto WP", "自动航点"},
            {"Clear Mission", "清除任务"},
            {"Clear Polygon", "清除多边形"},
            {"Clear Rally Points", "清除集结点"},
            {"Clear", "清除"},
            {"From Current Waypoints", "来自当前航点"},
            {"Create Circle Survey", "创建圆形测绘"},
            {"Create Spline Circle", "创建样条圆"},
            {"Create Wp Circle", "创建航点圆"},
            {"At Current Position", "在当前位置"},
            {"Delete WP", "删除航点"},
            {"Elevation Graph", "高程图"},
            {"Enter UTM Coord", "输入 UTM 坐标"},
            {"Fence Exclusion", "围栏排除区"},
            {"Fence Inclusion", "围栏包含区"},
            {"File Load/Save", "文件 加载/保存"},
            {"From SHP", "来自 SHP"},
            {"GDAL Opacity", "GDAL 不透明度"},
            {"Download", "下载"},
            {"Geo-Fence", "地理围栏"},
            {"Upload", "上传"},
            {"Insert Spline WP", "插入样条航点"},
            {"Insert Wp", "插入航点"},
            {"Start", "开始"},
            {"Jump", "跳转"},
            {"WP #", "航点编号"},
            {"KML Overlay", "KML 叠加层"},
            {"Land", "降落"},
            {"Load and Append", "加载并追加"},
            {"Load from File", "从文件加载"},
            {"Load KML File", "加载 KML 文件"},
            {"Load Polygon", "加载多边形"},
            {"Load SHP File", "加载 SHP 文件"},
            {"Load WP File", "加载航点文件"},
            {"Circles", "圆圈"},
            {"Forever", "永久"},
            {"Time", "定时"},
            {"Loiter", "悬停"},
            {"Map Tool", "地图工具"},
            {"Modify Alt", "修改高度"},
            {"Offset Polygon", "偏移多边形"},
            {"Add", "添加"},
            {"Edit", "编辑"},
            {"POI", "兴趣点"},
            {"Polygon", "多边形"},
            {"Prefetch", "预取"},
            {"Prefetch WP Path", "预取航点路径"},
            {"Rally Points", "集结点"},
            {"Reverse WPs", "反转航点顺序"},
            {"Rotate Map", "旋转地图"},
            {"RTL", "返航"},
            {"Save Polygon", "保存多边形"},
            {"Save to File", "保存到文件"},
            {"Save WP File", "保存航点文件"},
            {"Set Rally Point", "设置集结点"},
            {"Set Return Location", "设置返航点"},
            {"Survey (Grid)", "测绘(网格)"},
            {"Switch Docking", "切换停靠"},
            {"Takeoff", "起飞"},
            {"test", "测试"},
            {"Text", "文本"},
            {"Tracker Home", "跟踪器家位置"},
            {"Zoom to Home", "缩放到家"},
            {"Zoom to Mission", "缩放到任务"},
            {"Zoom To", "缩放到"},
            {"Zoom to Vehicle", "缩放到飞行器"},

            // ===== 源码/编译插件动态菜单项 =====
            {"Altitude Angel Settings", "Altitude Angel 设置"},
            {"CAN Log Extract", "CAN 日志提取"},
            {"tracemp", "轨迹追踪"},
            {"Share to API", "共享到 API"},
            {"Forward between links", "链路间转发"},
            {"Setup other positions", "设置其他位置"},
            {"Change icon Description", "更改图标描述"},
            {"Anonymize Bin Log...", "匿名化 Bin 日志..."},
            {"Mass", "批量控制"},
            {"Arm", "解锁"},
            {"Arm(Force)", "解锁(强制)"},
            {"GUIDED", "引导(GUIDED)"},
            {"AUTO", "自动(AUTO)"},
            {"TAKEOFF", "起飞(TAKEOFF)"},
            {"TakeOff (2m)", "起飞(2米)"},
            {"Fly To", "飞行到"},
            {"LED", "LED 灯"},
            {"Red", "红色"},
            {"Green", "绿色"},
            {"Blue", "蓝色"},
            {"White", "白色"},
            {"Black", "熄灭"},
            {"Rainbow", "彩虹"},
            {"Stats", "统计"},
            {"Dowding Point At", "Dowding 指向"},
            {"Draw Fence Dist", "绘制围栏距离"}
        };

        // 轻量调度：翻译到位后自动休眠，仅周期性轻量复查，稳态几乎不占资源
        int _tick;
        bool _settled;

        public override bool Init() { return true; }

        public override bool Loaded()
        {
            loopratehz = 1; // 每秒1次；菜单全部翻译完后会自动降到约每5秒1次空检查
            _tick = 0;
            _settled = false;
            return true;
        }

        // 返回本次是否真的改动了文本
        static bool TranslateItems(ToolStripItemCollection items)
        {
            if (items == null) return false;
            bool changed = false;
            foreach (ToolStripItem it in items)
            {
                if (it == null) continue;
                try
                {
                    string t = it.Text;
                    string zh;
                    if (!string.IsNullOrEmpty(t) && Map.TryGetValue(t, out zh) && t != zh)
                    {
                        it.Text = zh;
                        changed = true;
                    }
                    var dd = it as ToolStripMenuItem;
                    if (dd != null && TranslateItems(dd.DropDownItems)) changed = true;
                }
                catch { }
            }
            return changed;
        }

        bool DoTranslate()
        {
            bool changed = false;
            try { if (Host.FDMenuMap != null && TranslateItems(Host.FDMenuMap.Items)) changed = true; } catch { }
            try { if (Host.FPMenuMap != null && TranslateItems(Host.FPMenuMap.Items)) changed = true; } catch { }
            return changed;
        }

        public override bool Loop()
        {
            try
            {
                _tick++;
                // 已稳定后，每 5 个周期才轻量复查一次（应对晚加载/切页后新出现的菜单项）
                if (_settled && (_tick % 5 != 0)) return true;

                Control mf = Host.MainForm;
                if (mf == null || mf.IsDisposed) return true;

                if (mf.InvokeRequired)
                {
                    mf.BeginInvoke((Action)delegate
                    {
                        bool c = DoTranslate();
                        _settled = !c;
                    });
                }
                else
                {
                    _settled = !DoTranslate();
                }
            }
            catch { }
            return true;
        }

        public override bool Exit() { return true; }
    }
}
