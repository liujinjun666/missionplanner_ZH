# feature/net-panel — 飞控网口调试控件

> 本分支**只含本模块**：源码在 `net-panel/`，根目录只有本说明与 `README.md`。

## 职责
MP「飞行数据」页注入「飞控网口」勾选框（位于「玄云ECU」右侧）：读写 ArduPilot `NET_*` 参数，
显示 P1~P4 四个网口配置，改动高亮，通讯状态指示，并提供抓包监听。

## 涉及文件
| 文件 | 作用 |
|---|---|
| `net-panel/zh-net.cs` | MP 插件（47,987 字节，v1.1） |
| `net-panel/一键部署zh-net插件.bat` | 本机部署（拷贝+校验字节数+可选自动重启 MP） |
| `zh-diag.cs` | 临时诊断插件，导出插件编译错误；**不入库、打包前移除** |

## 参数依据
`NET_*` 共 55 个（ArduCopter / ArduPlane 的 pdef 完全一致，公共库 AP_Networking；Rover 4.8.0 pdef 尚未收录）。
读参数的正确写法：`MainV2.comPort.MAV.param.ContainsKey(name)` + `(float)param[name]`
（`MAVLinkParamList`，不是 Hashtable，也没有 `MAVLink.param` 类型）。

## 功能清单
1. 连接飞控后自动读取 NET 参数（参数表下载完成后自动读一次，也可手点「读取参数」）
2. 四个网口：类型 / 协议 / 端口 / 对端 IP；全局：启用、DHCP、掩码位数、本机 IP、网关、MAC（只读）
3. 改动未写入 → 控件黄底高亮；写入前弹改动清单，写后提示需重启飞控
4. 底部：读取参数 / 写入修改 / 重连刷新
5. 通讯中网口绿色「● 通讯中」（TCP 查 ESTABLISHED；UDP 依赖监听器捕获）
6. 监听窗口：时间戳、方向、源/目的 IP、端口、字节数、MAVLink 消息名、前 16 字节 hex；暂停/清空

## 坑与约定（改这个文件前必读）
- **编译失败静默不加载**：MP 对编译成功/失败打同样 INFO 日志，错误只在 `PluginLoader.ErrorInfo`。
  排查用 `zh-diag.cs` → `C:/ProgramData/Mission Planner/zh_plugin_errors.txt`。
- **主题前景色**：MP 深色主题会把控件文字刷白，白底面板上会隐形。新加控件必须过 `SetDarkText()`，
  需要语义色的设 `Tag = "keep"`。
- **下拉框边框**：`FlatStyle.Popup/Flat` 只在悬停画边框，统一用 `AddBordered()` 套 1px 边容器；
  TextBox/NumericUpDown 用 `BorderStyle.FixedSingle`。
- **抓包必须管理员**（Windows raw socket 限制）：启动失败弹窗 + 一键 `runas` 重启 MP。
- **UI 线程不能被灌爆**：监听输出走 200ms 批量上屏（缓冲上限 500 条），禁止逐包 `BeginInvoke`。
- **跨线程共享状态**：`lastTraffic` 必须是 `ConcurrentDictionary`，键用网口卡前缀（`NET_P1`）。
- 51 及以上协议值是厂商自定义，显示「自定义（51）」。

## 当前版本
v1.1（2026-11-08）：四网口读写/高亮/通讯指示/监听抓包；修复文字底色、常显边框、监听卡死与闪退。

## 待办
- 试用观察；打包前确认 `zh-diag.cs` 未进入安装包
