# 玄云涡喷 ECU 双向控制方案 — 部署与使用说明

> 技术路线：**Lua 双向转发**（不编译固件，适配所有支持 ArduPilot Lua 脚本的飞控）
> 配套文件：
> - 飞控侧：`swiwin_ecu_bridge.lua`
> - 地面站侧：`zh-ecu.cs`（Mission Planner 插件）
> 适用：X6 Pro（Plane 4.5+）及一切支持 Lua 脚本的 ArduPilot 固定翼/多旋翼板子。

---

## 一、总体架构

```
ECU(V4)  ──232转TTL──>  飞控串口(Serialx)  ──Lua脚本──>  MAVLink NAMED_VALUE_FLOAT ──>  MP(插件显示)
                         ▲                                      ▲
                         └──────── MAV_CMD.WAYPOINT_USER_1 ─────┘  (MP 插件下发控制)
```

- **上行遥测**：Lua 脚本解析 ECU 的 7 字节状态帧（ZK 协议 V1.2），以 `NAMED_VALUE_FLOAT`
  （`ECU_RPM/EGT/STATE/ERR/PUMP/VBAT/THR/FUEL/FLOW/...`）推给地面站。
- **下行控制**：MP 插件发 `MAV_CMD.WAYPOINT_USER_1`（31000），Lua 脚本收到后组 4 字节
  ZK 控制帧（`0xFF | CmdID | Param | CRC8`）写入 ECU 串口。

---

## 二、飞控侧部署

### 1. 飞控参数（Mission Planner 全部参数表）

| 参数 | 值 | 说明 |
|---|---|---|
| `SERIALx_PROTOCOL` | **28** (Scripting) | x 为接 ECU 的串口号（X6 Pro 常用 SERIAL2/TELEM2） |
| `SERIALx_BAUD` | **9600** | 与 ECU 默认波特率一致（收不到帧改核对 8N1/8N2） |
| `SCR_ENABLE` | **1** | 启用 Lua 脚本 |
| `SCR_VM_I_COUNT` | **200000** | **Lua 虚拟机每轮指令预算（关键！）**。零一定制固件默认值偏低，会导致脚本报 `exceeded maximum execution time` 被停。若仍偶发超时，可提到 500000 |
| `LOG_DISARMED` | **1** | 建议，地面联调也记录日志 |

### 2. 部署脚本

脚本文件：`swiwin_ecu_bridge.lua`（**完整 23,588 字节**，**不要截断**）。

放到飞控 SD 卡脚本目录（对应 MAVFTP 路径 `/APM/scripts/`），两种方式：

- **SD 卡直拷**：把文件放进 SD 卡的 `APM/scripts/`（模拟器是 `<SD>/scripts/`）。
- **MAVFTP 上传**（MP → 配置/调试 → 全部参数表上方，或 pymavlink）：
  上传到 `/APM/scripts/swiwin_ecu_bridge.lua`，**上传后核对字节数完整**（此前踩过 113 字节截断坑）。

重启飞控，脚本加载成功会在 MP「消息」栏显示：
`ECU-Bridge: swiwin_ecu_bridge loaded (bidirectional)`，
随后收到第一帧显示 `ECU-Bridge: telemetry OK, proto v4`。

---

## 三、地面站侧部署（MP 插件）

1. 把 `zh-ecu.cs` 放进 Mission Planner 程序目录的 `plugins\` 子目录
   （默认 `C:\Program Files (x86)\Mission Planner\plugins\`）。
2. **彻底关闭并重启 MP**（插件只在启动时编译加载，不热加载）。
3. 进入「飞行数据」页，地图下方工具栏「自动平移」**左侧**会出现勾选框 **「玄云ECU」**。
4. 勾选后弹出完整调试面板（**右下角**，可拖动，不遮挡 HUD/功能区）。

> 说明：插件为 C# 5 语法、免编译运行时加载（Roslyn 后台编译），与已有 zh-brand / zh-quickview-rf
> 插件同一机制。需在 Windows 已装 MP 的机器上由用户重启 MP 生效。

---

## 四、面板功能

### 遥测显示（自动刷新，约 2Hz）
转速、排温、发动机状态（中文）、错误码（中文）、油泵电压、电源电压、油门、累计油耗、
燃油流量、ECU 温度、**估算累计油耗（kg）/ 估算瞬时油耗（g/min，双线性插值查表）**。
连接状态右上角「● 已连接/未连接」。

### 发动机控制
- **停车 / 待机 / 运行**：发 `ID1` 控制帧（SW=1/2/3）。
- **交还遥控**：发 `ID1 SW=0` 控制帧，让 ECU 停止响应串口油门、改由遥控器 PWM 控制
  （带二次确认弹窗，交还后油门滑杆自动归零）。
- **油门滑杆**（0~100%）：拖动实时下发（`ID1 SW=3 + 油门 0~1000`）。
  ⚠️ **油门实时下发仅在运行态有效，且会覆盖遥控器 PWM——测试时务必有人守在油门物理开关旁。**
  需要交还遥控时点「交还遥控」按钮即可一键退回 PWM 控制。

### 测试 / 维护（发 `ID2`）
排油、测试火头、测试主阀、测试点火阀、测试油泵、测试启动电机、长开油泵、关闭油泵、重置油耗。

### 参数设置
- **解锁参数设置**（`ID3`，前置：`ID4/ID5` 仅解锁后生效）。
- 点火油泵电压（0~5.1V，`ID4`）、加速曲线（10~70，`ID5`）、目标转速（0~4095，`ID7`）、
  大气压（hPa，`ID8`）。

---

## 五、命令号与协议约定（两侧一致，勿改）

- 命令号：`MAV_CMD.WAYPOINT_USER_1` = **31000**（Lua 脚本 `MAV_CMD_ECU` 同值）。
- 参数映射：`param1 = CmdID(0~8)`，`param2 = arg1`，`param3 = arg2`。

| CmdID | 用途 | arg1 | arg2 |
|---|---|---|---|
| 0 | 空循环保持连接 | — | — |
| 1 | 引擎状态 + 油门 | SW(0~3) | 油门(0~1000) |
| 2 | 测试/维护 | 参数(1~13) | — |
| 3 | 解锁参数 | — | — |
| 4 | 点火油泵电压(0.02V/字) | 原始值 | — |
| 5 | 加速曲线(10~70) | 值 | — |
| 6 | SW + RPM_X(0~6) | RPM_X | SW |
| 7 | 目标转速(0~4095) | 值 | — |
| 8 | 大气压(hPa) | 值 | — |

### CRC8（ZK 官方算法，种子 0x00，已用官方全部示例帧验证）
- 状态帧：`CRC8(byte0..5)`，7 字节帧 `0xF0|ID + 5数据 + CRC`。
- 控制帧：`CRC8(byte1..2)`，4 字节帧 `0xFF + CmdID|参数 + 参数 + CRC`。
- 官方实例均已匹配：
  - 状态 `F1 00 00 00 00 4C A0`（停止，排温 26℃）
  - 状态 `F6 00 00 A0 00 10 AB`（最高转速 160000，协议 v4，20Hz）
  - 控制 `FF 14 64 D3`（SW=1 油门 100）
  - 控制 `FF 1D F4 70`（SW=3 油门 500）

---

## 六、安全注意（重要）

1. **下行控制会覆盖遥控器 PWM**（ZK 协议：SW 非零时 PWM 无效）。地面测试时：
   - 油门滑杆、停车/待机/运行按钮务必谨慎操作；
   - 测试模式（排油/阀/油泵/启动电机）需确认发动机处于安全状态；
   - 有人在物理开关/灭火器旁值守。
2. **参数读写需先「解锁参数设置」**，`ID4/ID5` 才生效。
3. 油门实时下发未做失控保护——若用于实际飞行，建议只在**地面试车台**使用控制功能，
   飞行中仅用遥测显示（把油门滑杆留在 0，或临时注释掉 Scroll 里的 `SendEcuCmd`）。
4. 波特率/停止位不匹配会连续 CRC 失败（脚本有 `crc_fail` 计数与 2 秒断流报警提示）。

---

## 七、验证情况

- **Lua 脚本**：真实 Lua 运行时（lupa）加载执行，CRC8 表 256 项与官方文档完全一致，
  状态帧解析、控制帧编码（对照官方 4 个实例）全部断言通过。已内置纯 Lua bit 兼容层
  （零一定制固件无 `bit` 库）与自包含 COMMAND_LONG 解码器（无 `mavlink_msgs` 模块也可用）。
- **MP 插件**：所有 API 经 .NET 反射逐项核实（`MainV2.comPort.doCommand`、`OnPacketReceived`、
  `MAVLink.MAVLinkMessage.ToStructure`、`mavlink_named_value_float_t`、`MAV_CMD.WAYPOINT_USER_1=31000`）。
- **真机验证**：通讯已打通（遥测正常）。曾踩过的坑：①脚本被截断；②`bit` 库缺失；③`mavlink_msgs`
  模块缺失；④Lua VM 指令预算不足报 `exceeded maximum execution time`（解法见 `SCR_VM_I_COUNT`）。
  以上均已修复。
