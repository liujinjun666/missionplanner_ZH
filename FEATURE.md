# feature/serial-monitor — 遥测通信监听

> 本分支**只含本模块**：源码在 `serial-monitor/`，根目录只有本说明与 `README.md`。

## 职责
把「MP 和飞控到底在说什么」变成可见的：实时列出 MP 当前连接上的全部 MAVLink 收发报文与打印文本，
并能在同一个窗口里发命令、写参数。用于排障（连不上、参数读不出、ECU 不通、模式切不动、飞控启动日志）。

## 涉及文件
| 文件 | 作用 |
|---|---|
| `serial-monitor/zh-serial.cs` | MP 插件（v1.0）；飞行数据页「遥测通信监听」勾选框 + 独立窗口 |
| `serial-monitor/一键部署zh-serial插件.bat` | 本机部署（拷贝 + 校验字节数 + 可选自动重启 MP） |
| `serial-monitor/README.md` | 功能说明与使用方式 |
| `zh-diag.cs` | 临时诊断插件，导出插件编译错误；**不入库、打包前移除** |

## 数据源：只有 MP 当前连接

挂 `MainV2.comPort.OnPacketReceived` / `OnPacketSent` 两个事件，双向都能看。
零配置、不影响 MP 正常工作、能看到 MP **发送**了什么（请求、命令、参数写入）。

**为什么不再做「自己开串口」和「SERIALn 协议归属」**（两次都实现过，已删除）：
1. 飞控 UART 是板内总线，不是电脑 COM 口。数据只有三条路到 PC：
   ① 固件封装成 MAVLink 上报（MP 已全收，直接读即可）；
   ② 自己开 COM 口抓字节 —— 串口独占，MP 连着时抢不过来，要先断开 MP，「接管」代价大于收益；
   ③ `SERIAL_PASS1/PASS2` 透传 —— 要改参数，`PASSTIMO` 到时间才自动恢复，否则重启飞控，风险高。
2. 协议为「未配置」或「自定义」的口，固件压根不上报，任何软件都看不到原始字节 —— 这是链路决定的，不是工具缺陷。

代价：看不到 MP 解析失败的物理层垃圾字节（错帧、乱码、波特率不对的噪声）。

## 界面：三个页签

| 页签 | 内容 |
|---|---|
| **报文** | 双向报文列表 + 字段反射详情 + 十六进制 dump |
| **发送** | `MAV_CMD` 命令（COMMAND_LONG）、写参数（PARAM_SET） |
| **打印** | `STATUSTEXT` 文本流，按严重级别着色 |

## 功能清单
0. 入口：飞行数据页工具栏「遥测通信监听」勾选框，**固定排在「飞控网口」右侧**
   （先等 `CHK_net` 出现再放，避免和 zh-net 抢位置重叠；`CHK_net` 找不到才退化到「玄云ECU」右侧，最后兜底加进「飞行数据」菜单）
1. 报文列表：时间 / 方向 / 消息名 / ID / sysid:compid / seq / 帧长 / 内容摘要
2. 选中一行 → 下方显示全部字段（结构体反射展开）与十六进制 dump（含 ASCII 列）
3. 打印页：`STATUSTEXT` 按严重级别着色（紧急/严重/错误红、警告橙、注意蓝）
4. 过滤：关键字（逗号分隔，匹配消息名或内容）、**消息类型单选**、**消息类型多选**（`CheckedListBox`，含全选/全不选）、方向（全部/仅接收/仅发送）、只看文本/打印
5. 暂停 / 清空 / 保存日志（TSV，可导入 Excel）/ 导出原始帧（.bin，可喂给其它 MAVLink 工具）
6. 消息类型下拉**动态补项**：只有真收到过才出现；刚打开只有「全部消息类型」是正常的
7. 状态栏：收/发/丢弃计数 + 实时速率（按**字节**算）；左侧显示数据源状态
8. 右键菜单：复制选中行、复制全部、导出选中原始帧；窗口可置顶
9. **发送页 · 命令**：目标 sysid/compid 可改（默认 1/1），14 条预设 + 任意命令号，P1~P7 可填，选中预设时显示参数提示
10. **发送页 · 参数**：参数名 + 值 → `PARAM_SET`

### 预设命令（`CMD_IDS` / `CMD_LABELS` / `CMD_HINTS`）
400 解锁上锁、22 起飞、21 降落、20 返航、176 设置模式、178 改速度、183 设舵机、179 设 Home、
300 开始任务、511 设消息频率、512 请求消息、246 重启飞控、241 预检校准、16 航点。

`511 SET_MESSAGE_INTERVAL` 是排障利器：P1 = 消息ID，P2 = 间隔微秒（-1 恢复默认），
可以临时把某类遥测刷到 10Hz 观察，或者反过来把刷屏的消息压下去。

## 坑与约定（改这个文件前必读）
- **编译失败静默不加载**：MP 对成功/失败打同样 INFO 日志，错误只在 `PluginLoader.ErrorInfo`。
  排查用 `zh-diag.cs` → `C:/ProgramData/Mission Planner/zh_plugin_errors.txt`。
- **收包线程不能碰控件**：过滤条件在 UI 线程拍快照（`fltTextOnly` / `fltDir` / `fltText` 均 `volatile`），
  收包线程只读快照；直接读 `CheckBox.Checked` 跨线程会炸。
- **`selTypes` 只整体替换、不原地增删**：收包线程持引用读 `Contains`，原地改 `HashSet` 会并发崩。
- **UI 不能被灌爆**：报文入 `pending` 缓冲（上限 3000），200ms 定时批量上屏，超限计入「丢弃」。
- **列表上限 5000 行**，超出从头部删，避免长时间运行内存膨胀。
- **打印页上限 40 万字符**，超出整体清空。
- **主题前景色**：MP 深色主题会把新建控件文字刷白，面板/表格/标签显式设 `ForeColor = Black`。
- **Dock 顺序**：`TabControl(Fill)` 必须 `SetChildIndex(tabs, 0)`，否则被 Top 过滤面板压住。
- **事件挂钩防重**：`attached` 字段记录当前挂的是哪个 `MAVLinkInterface`，换连接对象要重挂；
  `DetachMp()` 在窗口关闭和 `Exit()` 时都要调，否则 MP 退出时可能空引用。
- **发送走后台线程**：`doCommand` / `setParam` 会阻塞等串口，放 UI 线程会卡界面；回写控件统一走 `UiPost()`。
- **`requireack: false`**：不等 ACK 直接返回，ACK 自己到「报文」页看 `COMMAND_ACK`，避免界面假死。
- 本模块**不需要管理员权限**（和网口 raw socket 抓包不同）。

## 开放的数据来源（源码核实，别靠记忆改）
- `MainV2.comPort.OnPacketReceived` / `OnPacketSent`：`EventHandler<MAVLinkMessage>`，签名 `(object, MAVLink.MAVLinkMessage m)`；`m` 提供 `msgid / sysid / compid / seq / buffer / data`
- `MainV2.comPort.doCommand(uint sysid, byte compid, MAV_CMD id, float p1..p7, bool requireack = true, Action uicallback = null)`
- `MainV2.comPort.setParam(uint sysid, byte compid, string name, double value, bool force = false)`

## 当前版本
v1.0（2026-11-08）：只监听 MP 连接；报文/发送/打印三页；消息类型单选 + 多选；14 条预设命令 + 任意命令号；参数写入。

## 验收点
- MP 连接飞控后勾「遥测通信监听」，报文页出现 `HEARTBEAT` / `SYS_STATUS` / `ATTITUDE`，方向「收」「发」都有
- 「消息类型」下拉随运行逐渐出现更多类型；「多选…」勾两项后列表只剩这两类
- 发送页选 `512 请求消息`、P1=0 发送 → 报文页出现 `COMMAND_LONG` 与 `COMMAND_ACK`
- 打印页出现 PreArm / 模式切换文本，颜色随级别变化
- 连续跑 10 分钟不卡、不闪退、内存稳定

## 待办
- 试用反馈；如需要，可加「按 sysid/compid 分栏」、「CRC 校验开关」、「报文回放」
