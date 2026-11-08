-- ============================================================================
-- swiwin_ecu_bridge.lua
-- 玄云 / SWIWIN 涡喷 ECU（ZK Protocol V1.2）双向转发脚本
-- 适配所有支持 ArduPilot Lua 脚本的飞控（Plane 4.5+，含 X6 Pro 等）
--
-- 上行（ECU -> 地面站）：
--   解析 ECU 串口发来的 7 字节状态帧（ID1~ID9），
--   以 NAMED_VALUE_FLOAT 推送遥测 + 写入飞控日志。
--
-- 下行（地面站 -> ECU）：
--   监听地面站下发的 MAV_CMD_USER_1 自定义命令，
--   组 4 字节 ZK 控制帧（0xFF | CmdID | Param | CRC8）写入 ECU 串口。
--   完整控制 + 参数读改（启停/油门/测试模式/转速/点火电压/加速曲线/大气压）。
--
-- 通信参数（依据玄云《ZK-Protocol V1.2》）：
--   9600bps · 8 数据位 · 无校验 · 停止位 1（收不到帧改 2）
--   状态帧 7 字节：[0]=0xF0|ID  [1..5]=数据  [6]=CRC8(字节0..5, 种子0x00)
--   控制帧 4 字节：[0]=0xFF      [1]=CmdID|SW [2]=参数   [3]=CRC8(字节1..2, 种子0x00)
--   CRC8 算法与种子已用官方文档全部示例帧验证（状态帧+控制帧）。
--
-- 飞控参数设置：
--   SERIALx_PROTOCOL = 28  (Scripting，x 为接 ECU 的串口号)
--   SERIALx_BAUD     = 9600
--   SCR_ENABLE       = 1
--   LOG_DISARMED     = 1   （建议，地面联调也记录）
--
-- 安全说明：下行控制仅在解锁参数（Cmd ID3）后对 ID4/ID5 生效；
--   油门/启停等控制命令需地面站明确下发才执行，脚本不会自动发控制帧。
--
-- v1.2 变更（相对 v1.1）：
--   1) 修正 ID8 解析：BYTE4[3:0]|BYTE5 为启动时间(0.1s)，原误标为启动次数；
--      新增 ESR(ECU请求大气压) 与 SCL(转速闭环) 标志提取；
--   2) 补发缺失遥测：SWST(控制状态)/IDLERPM(怠速)/MAXPMP(油泵最大电压)/
--      STARTIME(启动时间)/ESR/SCL/PROTO(协议版本)；STARTUPS 字段移除。
-- ============================================================================

local PARAM_PREFIX = "ECU_"

-- ---- 用户可调配置 ----------------------------------------------------------
local STOP_BITS   = 1     -- 1=默认；连续乱码改 2 再试
local SEND_HZ_DIV = 5     -- 遥测推送降频分母（脚本100ms一轮，5轮≈0.5s）
-- ---------------------------------------------------------------------------

local port = serial:find_serial(0)
if not port then
   gcs:send_text(4, "ECU-Bridge: no scripting serial (SERIALx_PROTOCOL=28?)")
   local function noop() return noop, 1000 end
   return noop, 1000
end
port:begin(9600)
if STOP_BITS == 2 then port:set_stop_bits(2) else port:set_stop_bits(1) end
port:set_flow_control(0)

-- ---- MAVLink 下行接收 ------------------------------------------------------
-- 优先使用官方 MAVLink/mavlink_msgs.lua 模块（需放 SD 卡 APM/scripts/MAVLink/ 下）；
-- 找不到时用内置手动解码器（自包含，只解 COMMAND_LONG），保证任何固件都能跑。
local COMMAND_LONG_ID = 76
local mavlink_msgs = nil
do
   local ok, mod = pcall(require, "MAVLink/mavlink_msgs")
   if ok and mod and mod.get_msgid then
      mavlink_msgs = mod
      COMMAND_LONG_ID = mod.get_msgid("COMMAND_LONG")
      gcs:send_text(6, "ECU-Bridge: using MAVLink/mavlink_msgs module")
   else
      gcs:send_text(6, "ECU-Bridge: module not found, using builtin decoder")
   end
end
local msg_map = { [COMMAND_LONG_ID] = "COMMAND_LONG" }
mavlink:init(10, 1)
mavlink:register_rx_msgid(COMMAND_LONG_ID)

-- 内置 COMMAND_LONG 手动解码器（依据 MAVLink 1/2 帧格式）
-- 返回 {msgid, sysid, compid, command, param1..param7} 或 nil
local function decode_command_long(msg)
   local ok = pcall(string.unpack, "<H", msg, 1)
   if not ok then return nil end
   local magic = string.unpack("<B", msg, 3)
   if magic ~= 0xFE and magic ~= 0xFD then return nil end
   local sysid, compid, msgid, payload_off
   if magic == 0xFE or #msg < 298 then
      -- MAVLink1 结构 或 旧固件 291 字节布局：seq(1) sysid(1) compid(1) msgid(3)
      local _seq
      _seq, sysid, compid = string.unpack("<BBB", msg, 7)
      msgid = string.unpack("<I3", msg, 10)
      payload_off = 13
   else
      -- MAVLink2.1 32位sysid 布局：seq(1) sysid(4) compid(1) msgid(3)
      local _seq
      _seq = string.unpack("<B", msg, 7)
      sysid = string.unpack("<I4", msg, 8)
      compid = string.unpack("<B", msg, 12)
      msgid = string.unpack("<I3", msg, 13)
      payload_off = 14
   end
   if msgid ~= COMMAND_LONG_ID then return nil end
   -- COMMAND_LONG 载荷：param1..7(7×float) + command(uint16) + target_system + target_component + confirmation
   local p = { msgid = msgid, sysid = sysid, compid = compid }
   for i = 1, 7 do
      p["param" .. i] = string.unpack("<f", msg, payload_off + (i - 1) * 4)
   end
   p.command = string.unpack("<H", msg, payload_off + 28)
   return p
end

-- 统一解码入口
local function decode_msg(msg)
   if mavlink_msgs then
      return mavlink_msgs.decode(msg, msg_map)
   end
   return decode_command_long(msg)
end

-- 自定义命令号（与 MP 控件约定一致）
-- 31000 = MAV_CMD_WAYPOINT_USER_1（ArduPilot Lua 示例亦用此值；MP 侧 MAV_CMD.WAYPOINT_USER_1）
local MAV_CMD_ECU = 31000   -- 玄云 ECU 控制命令
mavlink:block_command(MAV_CMD_ECU)

-- ---- bit 兼容层 -------------------------------------------------------------
-- 部分厂商固件（如零一定制版）裁掉了 Lua 的 bit 库。
-- 这里用纯 Lua 实现兜底：不用原生位运算符（避免 Lua 5.1/5.2 语法错误），
-- 任何 Lua 版本都能跑。数据量小（每秒几百次运算），性能无压力。
if not bit then
   local function band(a, b)
      local r, v = 0, 1
      while a > 0 and b > 0 do
         if a % 2 == 1 and b % 2 == 1 then r = r + v end
         a = math.floor(a / 2); b = math.floor(b / 2); v = v * 2
      end
      return r
   end
   local function bor(a, b)
      local r, v = 0, 1
      while a > 0 or b > 0 do
         if a % 2 == 1 or b % 2 == 1 then r = r + v end
         a = math.floor(a / 2); b = math.floor(b / 2); v = v * 2
      end
      return r
   end
   local function bxor(a, b)
      local r, v = 0, 1
      while a > 0 or b > 0 do
         if (a % 2) ~= (b % 2) then r = r + v end
         a = math.floor(a / 2); b = math.floor(b / 2); v = v * 2
      end
      return r
   end
   bit = {
      band = band, bor = bor, bxor = bxor,
      lshift = function(a, n) return a * (2 ^ n) end,
      rshift = function(a, n) return math.floor(a / (2 ^ n)) end,
   }
   gcs:send_text(6, "ECU-Bridge: bit lib missing, pure-lua shim active")
end

-- ---- CRC8（ZK 官方算法，种子 0x00，已用官方示例帧验证） --------------------
local CRC8_TABLE = {
   0x00,0x5e,0xbc,0xe2,0x61,0x3f,0xdd,0x83,0xc2,0x9c,0x7e,0x20,0xa3,0xfd,0x1f,0x41,
   0x9d,0xc3,0x21,0x7f,0xfc,0xa2,0x40,0x1e,0x5f,0x01,0xe3,0xbd,0x3e,0x60,0x82,0xdc,
   0x23,0x7d,0x9f,0xc1,0x42,0x1c,0xfe,0xa0,0xe1,0xbf,0x5d,0x03,0x80,0xde,0x3c,0x62,
   0xbe,0xe0,0x02,0x5c,0xdf,0x81,0x63,0x3d,0x7c,0x22,0xc0,0x9e,0x1d,0x43,0xa1,0xff,
   0x46,0x18,0xfa,0xa4,0x27,0x79,0x9b,0xc5,0x84,0xda,0x38,0x66,0xe5,0xbb,0x59,0x07,
   0xdb,0x85,0x67,0x39,0xba,0xe4,0x06,0x58,0x19,0x47,0xa5,0xfb,0x78,0x26,0xc4,0x9a,
   0x65,0x3b,0xd9,0x87,0x04,0x5a,0xb8,0xe6,0xa7,0xf9,0x1b,0x45,0xc6,0x98,0x7a,0x24,
   0xf8,0xa6,0x44,0x1a,0x99,0xc7,0x25,0x7b,0x3a,0x64,0x86,0xd8,0x5b,0x05,0xe7,0xb9,
   0x8c,0xd2,0x30,0x6e,0xed,0xb3,0x51,0x0f,0x4e,0x10,0xf2,0xac,0x2f,0x71,0x93,0xcd,
   0x11,0x4f,0xad,0xf3,0x70,0x2e,0xcc,0x92,0xd3,0x8d,0x6f,0x31,0xb2,0xec,0x0e,0x50,
   0xaf,0xf1,0x13,0x4d,0xce,0x90,0x72,0x2c,0x6d,0x33,0xd1,0x8f,0x0c,0x52,0xb0,0xee,
   0x32,0x6c,0x8e,0xd0,0x53,0x0d,0xef,0xb1,0xf0,0xae,0x4c,0x12,0x91,0xcf,0x2d,0x73,
   0xca,0x94,0x76,0x28,0xab,0xf5,0x17,0x49,0x08,0x56,0xb4,0xea,0x69,0x37,0xd5,0x8b,
   0x57,0x09,0xeb,0xb5,0x36,0x68,0x8a,0xd4,0x95,0xcb,0x29,0x77,0xf4,0xaa,0x48,0x16,
   0xe9,0xb7,0x55,0x0b,0x88,0xd6,0x34,0x6a,0x2b,0x75,0x97,0xc9,0x4a,0x14,0xf6,0xa8,
   0x74,0x2a,0xc8,0x96,0x15,0x4b,0xa9,0xf7,0xb6,0xe8,0x0a,0x54,0xd7,0x89,0x6b,0x35,
}
local function crc8(msg, len)
   local crc = 0
   for i = 1, len do
      crc = CRC8_TABLE[bit.bxor(crc, msg[i]) + 1]
   end
   return crc
end

-- ---- 引擎状态码 / 错误码 名称 ------------------------------------------------
local STATE_NAMES = {
   [0]="停止",[1]="待机/自动冷却",[2]="点火(需最低油门)",[3]="点火",
   [4]="预热",[5]="油路渐充",[6]="运行(学习模式·最大油门)",
   [7]="运行(学习模式·最小油门)",[8]="运行(学习怠速)",[9]="运行(最低油门)",
   [10]="运行(警告:油泵达限制)",[11]="运行",[12]="冷却",[13]="重启",
   [14]="测试火头",[15]="测试主阀",[16]="测试点火阀",[17]="测试油泵",
   [18]="测试启动电机",[19]="排油"
}
local ERR_NAMES = {
   [0]="",[1]="超时",[2]="电压低",[3]="火头故障",[4]="油泵故障",
   [5]="启动电机故障",[6]="转速低",[7]="转速不稳定",[8]="排温高",
   [9]="排温低",[10]="排温传感器故障",[11]="点火阀故障",[12]="主阀故障",
   [13]="失控信号丢失",[14]="启动电机控制器温度高",[15]="油泵控制器温度高",
   [16]="离合器故障",[17]="电流过载",[18]="发动机离线"
}

-- ---- 运行状态变量 ----------------------------------------------------------
local proto_ver = 4
local volt_div  = 5
local frame = {}
local nstate, nerr = -1, -1
local last_data_ms = 0
local started = false
local timeout_reported = false
local crc_fail = 0
local loop_count = 0
local v = {}

-- ---- 油耗估算（双线性插值，合并自 LHY_motor.lua） ---------------------------
-- 依据高度(m) × 转速(rpm) 查油耗表（g/min），估算瞬时油耗与累计油耗。
-- 注意：这是"经验表估算"，精度低于 ECU 实际回传的燃油流量(FLOW)/累计油耗(FUEL)。
-- 当 ECU 回传了 FLOW/FUEL 时应优先用回传值，本估算仅作兜底/交叉验证。
local est_heights = {0, 1000, 2000, 3000, 4000, 5000, 6000, 7000, 8000, 9000, 10000}
local est_rpms = {38000, 40600, 45000, 51600, 56500, 61500, 66000, 70000, 73500, 76900, 79500, 82400, 85000, 88000, 89000, 91000, 92500, 94000, 95500, 96600, 98000}
local est_fuel_table = {
   {174,189,237,277,309,340,376,413,441,483,515,559,588,633,662,703,737,772,800,831,865},
   {153,189,209,244,273,300,332,364,389,426,454,493,519,558,584,620,650,681,706,733,763},
   {135,166,184,215,240,264,292,320,342,375,399,433,456,491,513,545,571,599,620,644,671},
   {118,145,161,188,210,231,255,281,300,328,350,380,399,430,450,478,501,524,543,565,588},
   {103,127,141,164,183,202,223,245,262,287,306,332,349,376,393,417,438,458,475,493,514},
   {90,111,123,144,160,176,195,214,229,250,267,290,305,328,343,364,382,400,415,431,449},
   {79,97,107,125,139,153,170,186,199,218,232,252,265,286,299,317,333,349,361,375,391},
   {68,84,93,109,121,133,148,162,173,189,202,219,231,248,260,276,289,303,314,326,340},
   {59,73,81,94,105,116,128,140,150,164,175,190,200,215,225,239,250,262,272,282,294},
   {51,63,69,81,91,100,110,121,129,142,151,164,172,186,194,206,216,226,234,244,255},
   {44,54,60,70,78,86,95,104,112,122,130,141,149,160,167,178,186,195,202,210,221},
}
local est_total_fuel = 0.0   -- 估算累计油耗 kg
local est_rate = 0.0         -- 估算瞬时油耗 kg/min
local est_last_ms = 0

local function est_find_interval(list, value)
   for i = 1, #list - 1 do
      if value <= list[i + 1] then return i end
   end
   return #list - 1
end

local function est_bilinear(x, y)
   if y <= 20000 then return 0 end   -- 转速过低视为不耗油
   local i = est_find_interval(est_heights, x)
   local j = est_find_interval(est_rpms, y)
   local x1, x2 = est_heights[i], est_heights[i + 1]
   local y1, y2 = est_rpms[j], est_rpms[j + 1]
   local t = est_fuel_table
   local Q11, Q12 = t[i][j], t[i][j + 1]
   local Q21, Q22 = t[i + 1][j], t[i + 1][j + 1]
   local denom = (x2 - x1) * (y2 - y1)
   if denom == 0 then return Q11 end
   return (Q11 * (x2 - x) * (y2 - y)
        +  Q21 * (x - x1) * (y2 - y)
        +  Q12 * (x2 - x) * (y - y1)
        +  Q22 * (x - x1) * (y - y1)) / denom
end

-- 更新估算油耗。rpm 为当前转速，dt 为距上次调用的秒数。
local function update_fuel_estimate(rpm)
   local alt = 0
   if ahrs then
      local ok, pos = pcall(function() return ahrs:get_position() end)
      if ok and pos then
         local ok2 = pcall(function() return pos:alt() end)
         if ok2 then
            alt = (pos:alt() or 0) / 100.0   -- cm -> m
         end
      end
   end
   local now = millis()
   local dt = 0.1    -- 按100ms一轮估算
   if est_last_ms > 0 then
      dt = (now - est_last_ms) / 1000.0
      if dt <= 0 or dt > 10 then dt = 0.05 end
   end
   est_last_ms = now

   local g_per_min = est_bilinear(alt, rpm or 0)
   est_rate = g_per_min / 1000.0   -- g/min -> kg/min
   est_total_fuel = est_total_fuel + est_rate * (dt / 60.0)
   return est_total_fuel, est_rate
end

local function sendf(name, val) gcs:send_named_float(PARAM_PREFIX .. name, val) end

-- ---- 帧解析（状态帧，7字节） ------------------------------------------------
local function parse_frame(f)
   local id = f[1] - 0xF0
   local B1, B2, B3, B4, B5 = f[2], f[3], f[4], f[5], f[6]
   local rpm = (B2 * 256 + B1) * 10

   if id == 1 then
      v.rpm   = rpm
      v.state = bit.band(B3, 0x1F)
      v.err   = bit.bor(bit.lshift(bit.band(B4, 0x03), 3), bit.rshift(B3, 5))
      v.egt   = bit.band(bit.rshift(B4, 2), 0x07) * 256 + B5 - 50
      v.swst  = bit.band(bit.rshift(B4, 5), 0x03)
   elseif id == 2 then
      v.rpm = rpm; v.vrx = B3 / volt_div; v.vbat = B4 / volt_div; v.pump = B5 / volt_div
   elseif id == 3 then
      v.rpm = rpm; v.thr = B3; v.pres = (B5 * 256 + B4) / 50
   elseif id == 4 then
      v.rpm = rpm
      v.curr   = bit.bor(bit.lshift(bit.band(B4, 0x01), 8), B3) / 10
      v.thrust = bit.bor(bit.lshift(bit.rshift(B4, 1), 8), B5) / 10
   elseif id == 5 then
      v.rpm = rpm; v.ignpump = B3 * 2 / 100; v.acc = B4; v.dec = B5
   elseif id == 6 then
      v.rpm = rpm; v.maxrpm = B3 * 1000; v.maxpmp = B4 / volt_div
      local newver = bit.rshift(B5, 2)
      if newver > 0 and newver ~= proto_ver then
         proto_ver = newver
         volt_div = (proto_ver >= 4) and 5 or 10
         gcs:send_text(6, string.format("ECU: protocol v%d, srate=%s", proto_ver,
            ({"20Hz","50Hz","100Hz"})[bit.band(B5, 0x03) + 1] or "?"))
      end
   elseif id == 7 then
      v.rpm = rpm
      v.flow = bit.bor(bit.lshift(bit.band(B4, 0x03), 8), B3) / 100
      v.fuel = (B5 * 64 + bit.rshift(B4, 2)) / 10
   elseif id == 8 then
      -- ID8: 怠速转速 | ESR(请求大气压) | SCL(转速闭环) | 启动时间(0.1s)
      v.rpm = rpm; v.idlerpm = B3 * 1000
      v.startime = bit.bor(bit.lshift(bit.band(B4, 0x0F), 8), B5) / 10   -- 0.1s -> s
      v.esr = bit.band(bit.rshift(B4, 5), 0x01)
      v.scl = bit.band(bit.rshift(B4, 4), 0x01)
   elseif id == 9 then
      v.rpm = rpm; v.ecutmp = B3 - 50
   end
   last_data_ms = millis()
end

-- ---- 下行控制：组 ZK 控制帧并写入 ECU --------------------------------------
-- 发送 4 字节控制帧 [0xFF, b1, b2, crc8(b1,b2)]
local function send_frame(b1, b2)
   local msg = { 0xFF, bit.band(b1, 0xFF), bit.band(b2, 0xFF) }
   local crc = crc8({ msg[2], msg[3] }, 2)
   port:write(msg[1])
   port:write(msg[2])
   port:write(msg[3])
   port:write(crc)
end

-- 处理 MAV_CMD_USER_1：param1=CmdID(含SW位), param2=参数值
--   约定：param1 = (CmdID << 4) | (SW << 1)   —— 见 MP 控件说明
--   简单约定：param1 = CmdID (0..8)，param2 = 参数（SW/油门/转速等由调用方编码）
local function handle_ecu_cmd(cmd_id, arg1, arg2)
   if cmd_id == 1 then
      -- 引擎状态控制：arg1=SW(0..3)，arg2=油门(0..1000)
      local sw = bit.band(arg1 or 0, 0x03)
      local thr = bit.band(arg2 or 0, 0x3FF)
      -- byte1 = 0x1S (S=SW高3位: bit4..6)，byte2 = 油门低8位
      -- ZK ID1: byte1 bit[6:4]=SW, 低4位=001? 官方实例 SW=1油门100 => 0xFF 0x14 0x64
      --   0x14 = 0001 0100 => 高4位0x1=CmdID1，SW在byte1的bit[6:4]=001? 实际0x14 bit4=1
      --   对照官方：CmdID1 SW1 => byte1=0x14；SW3 => 0x1D
      --   即 byte1 = 0x10 | (SW<<2)。SW=1=>0x14, SW=3=>0x1C? 但官方SW3=0x1D
      --   修正：byte1 = 0x10 | (SW<<2)  SW1=>0x14 ✓, SW3=>0x1C≠0x1D
      --   官方 SW3 实例 byte1=0x1D = 0x10 | (3<<2)=0x1C +1？ 见下精确编码
      --   实测官方：SW=1,thr=100 => 0x14 0x64；SW=3,thr=500 => 0x1D 0xF4
      --   0x1D = 0001 1101：bit0=1, bit2=1, bit3=1, bit4=1
      --   拆解：CmdID1 占 bit7..4=0001；SW 占 bit3..2；油门高2位占 bit1..0
      --   SW=3 => bit3..2=11 => 0x0C；thr=500=0x1F4 高2位=01 => bit1..0=01
      --   0x10 | 0x0C | 0x01 = 0x1D ✓✓
      --   SW=1 => bit3..2=01=0x04；thr=100=0x64 高2位=00 => 0x10|0x04|0=0x14 ✓✓
      local sw_bits = bit.lshift(bit.band(sw, 0x03), 2)
      local thr_hi  = bit.band(bit.rshift(thr, 8), 0x03)
      local b1 = bit.bor(0x10, bit.bor(sw_bits, thr_hi))
      local b2 = bit.band(thr, 0xFF)
      send_frame(b1, b2)
      gcs:send_text(6, string.format("ECU: 状态控制 SW=%d 油门=%d", sw, thr))
      return true
   elseif cmd_id == 2 then
      -- 测试/维护命令：arg1 = 参数(1..13)
      local p = bit.band(arg1 or 0, 0x1F)
      send_frame(0x20, p)
      gcs:send_text(6, string.format("ECU: 维护命令 %d", p))
      return true
   elseif cmd_id == 3 then
      -- 解锁参数设置
      send_frame(0x30, 0)
      gcs:send_text(6, "ECU: 解锁参数设置")
      return true
   elseif cmd_id == 4 then
      -- 设置点火油泵电压 (0.02V单位，0~5.1V => 0~255)
      local p = bit.band(arg1 or 0, 0xFF)
      send_frame(0x40, p)
      gcs:send_text(6, string.format("ECU: 设置点火电压 %.2fV", p * 0.02))
      return true
   elseif cmd_id == 5 then
      -- 设置加速曲线 (10~70)
      local p = bit.band(arg1 or 0, 0xFF)
      send_frame(0x50, p)
      gcs:send_text(6, string.format("ECU: 设置加速曲线 %d", p))
      return true
   elseif cmd_id == 6 then
      -- SW(bit1-0) + RPM_X(bit4-2)，均在 byte2；byte1=CmdID6
      -- 参数：arg1=RPM_X(0~6)，arg2=SW(0~3)
      local rpmx = bit.band(arg1 or 0, 0x07)
      local sw   = bit.band(arg2 or 0, 0x03)
      local b2 = bit.bor(bit.lshift(rpmx, 2), sw)
      send_frame(0x60, b2)
      gcs:send_text(6, string.format("ECU: 转速模式 RPM_X=%d SW=%d", rpmx, sw))
      return true
   elseif cmd_id == 7 then
      -- 设置转速 (0~4095)
      local rpm = bit.band(arg1 or 0, 0x0FFF)
      local b1 = bit.bor(0x70, bit.rshift(rpm, 8))
      local b2 = bit.band(rpm, 0xFF)
      send_frame(b1, b2)
      gcs:send_text(6, string.format("ECU: 设置转速 %d", rpm))
      return true
   elseif cmd_id == 8 then
      -- 大气压 hPa (0~1024)
      local p = bit.band(arg1 or 0, 0x03FF)
      local b1 = bit.bor(0x80, bit.rshift(p, 8))
      local b2 = bit.band(p, 0xFF)
      send_frame(b1, b2)
      gcs:send_text(6, string.format("ECU: 大气压 %d hPa", p))
      return true
   end
   return false
end

-- 处理收到的 COMMAND_LONG
local function handle_command_long(cmd)
   if cmd.command == MAV_CMD_ECU then
      local ok = handle_ecu_cmd(cmd.param1, cmd.param2, cmd.param3)
      if ok then
         return 0  -- MAV_RESULT_ACCEPTED
      end
      return 4  -- MAV_RESULT_FAILED
   end
   return nil
end

-- ---- 主循环 ----------------------------------------------------------------
function update()
   -- 1) 下行：处理地面站命令
   local msg, chan = mavlink:receive_chan()
   if msg ~= nil then
      local parsed = decode_msg(msg)
      if parsed ~= nil and parsed.msgid == COMMAND_LONG_ID then
         local result = handle_command_long(parsed)
         if result ~= nil and mavlink_msgs then
            -- 只有用官方模块时才回 COMMAND_ACK（内置解码器不编码ACK，靠文字提示反馈）
            local ack = {}
            ack.command = parsed.command
            ack.result = result
            ack.progress = 0
            ack.result_param2 = 0
            ack.target_system = 0
            ack.target_component = parsed.compid
            local msgid, payload = mavlink_msgs.encode("COMMAND_ACK", ack)
            mavlink:send_chan(chan, msgid, payload, parsed.sysid)
         end
      end
   end

   -- 2) 上行：收字节 + 组状态帧
   while port:available() > 0 do
      local b = port:read()
      if b < 0 then break end
      if #frame == 0 then
         if b >= 0xF1 and b <= 0xF9 then frame = { b } end
      else
         frame[#frame + 1] = b
         if #frame == 7 then
            if crc8(frame, 6) == frame[7] then
               parse_frame(frame)
            else
               crc_fail = crc_fail + 1
            end
            frame = {}
         end
      end
   end

   -- 3) 首帧提示
   if not started and last_data_ms > 0 then
      started = true
      gcs:send_text(6, string.format("ECU-Bridge: telemetry OK, proto v%d", proto_ver))
   end

   -- 4) 断流报警
   local now = millis()
   if started and not timeout_reported and (now - last_data_ms) > 2000 then
      timeout_reported = true
      gcs:send_text(4, "ECU-Bridge: telemetry TIMEOUT (>2s)")
   elseif timeout_reported and (now - last_data_ms) <= 2000 then
      timeout_reported = false
      gcs:send_text(6, "ECU-Bridge: telemetry recovered")
   end

   -- 5) 状态/错误变化提示
   if v.state and v.state ~= nstate then
      nstate = v.state
      gcs:send_text(6, string.format("ECU: [%d] %s RPM=%d EGT=%d",
         nstate, STATE_NAMES[nstate] or "?", v.rpm or 0, v.egt or 0))
   end
   if v.err and v.err ~= nerr then
      if v.err == 0 then
         if nerr and nerr > 0 then gcs:send_text(6, "ECU: 错误已清除") end
      else
         gcs:send_text(4, string.format("ECU: 错误#%d %s", v.err, ERR_NAMES[v.err] or "未知"))
      end
      nerr = v.err
   end

   -- 6) 降频推送遥测
   loop_count = loop_count + 1
   if loop_count >= SEND_HZ_DIV then
      loop_count = 0
      -- 油耗估算（双线性插值，兜底用）
      update_fuel_estimate(v.rpm or 0)
      sendf("RPM",   v.rpm or 0)
      sendf("EGT",   v.egt or 0)
      sendf("STATE", v.state or 0)
      sendf("ERR",   v.err or 0)
      sendf("PUMP",  v.pump or 0)
      sendf("VBAT",  v.vbat or 0)
      sendf("VRX",   v.vrx or 0)
      sendf("THR",   v.thr or 0)
      sendf("FUEL",  v.fuel or 0)
      sendf("FLOW",  v.flow or 0)
      sendf("PRES",  v.pres or 0)
      sendf("ECUT",  v.ecutmp or 0)
      sendf("IGNP",  v.ignpump or 0)
      sendf("THRUST",v.thrust or 0)
      sendf("CURR",  v.curr or 0)
      sendf("ACC",   v.acc or 0)
      sendf("DEC",   v.dec or 0)
      sendf("MAXRPM",v.maxrpm or 0)
      sendf("SWST",  v.swst or 0)
      sendf("IDLERPM", v.idlerpm or 0)
      sendf("MAXPMP",  v.maxpmp or 0)
      sendf("STARTIME", v.startime or 0)
      sendf("ESR",   v.esr or 0)
      sendf("SCL",   v.scl or 0)
      sendf("PROTO", proto_ver)
      sendf("ESTFUEL", est_total_fuel)   -- 估算累计油耗 kg
      sendf("ESTRATE", est_rate)         -- 估算瞬时油耗 kg/min
      logger:write("ECU",
         "RPM,EGT,STAT,ERR,PUMP,VBAT,THR,FUEL,FLOW",
         "fffffffff",
         v.rpm or 0, v.egt or 0, v.state or 0, v.err or 0,
         v.pump or 0, v.vbat or 0, v.thr or 0, v.fuel or 0, v.flow or 0)
   end

   return update, 100
end

gcs:send_text(6, "ECU-Bridge: swiwin_ecu_bridge loaded (bidirectional)")
return update, 100
