
local port = serial:find_serial(0)
if not port then
    gcs:send_text(0, "No Scripting Serial Port")
    return
end
gcs:send_text(6, "Lua running: sending Motor")


port:begin(9600)
port:set_flow_control(0)

local MAV_SEVERITY = {EMERGENCY=0, ALERT=1, CRITICAL=2, ERROR=3, WARNING=4, NOTICE=5, INFO=6, DEBUG=7}
-- 全局变量存储解析结果
-- 全局变量存储解析结果
local engine_data = {
    rpm = 0,
    temperature = 0,
    flow_rate=0,
    flow_total=0,
    power_voltage =0
}

local rpm = 0
local temperature = 0
local flow_rate = 0
local flow_total = 0
local power_voltage=0

-- 接收缓冲区
local rx_buffer = {}
local packet_started = false



function crc8_non_table_simple(msg, crc_len, seed)
    local crc = seed
    
    for i = 1, crc_len do
        local byte_val = msg:byte(i)
        local k = byte_val ~ crc  -- 使用Lua的异或操作符
        
        crc = 0
        
        -- 手动检查每一位
        if (k & 1) ~= 0 then         -- 第0位 (0x01)
            crc = crc ~ 0x5e
        end
        if (k & 2) ~= 0 then         -- 第1位 (0x02)
            crc = crc ~ 0xbc
        end
        if (k & 4) ~= 0 then         -- 第2位 (0x04)
            crc = crc ~ 0x61
        end
        if (k & 8) ~= 0 then         -- 第3位 (0x08)
            crc = crc ~ 0xc2
        end
        if (k & 16) ~= 0 then        -- 第4位 (0x10)
            crc = crc ~ 0x9d
        end
        if (k & 32) ~= 0 then        -- 第5位 (0x20)
            crc = crc ~ 0x23
        end
        if (k & 64) ~= 0 then        -- 第6位 (0x40)
            crc = crc ~ 0x46
        end
        if (k & 128) ~= 0 then       -- 第7位 (0x80)
            crc = crc ~ 0x8c
        end
    end
    
    return crc
end

-- 位操作辅助函数
local function bit_band(a, b)
    return a & b
end

local function bit_bor(a, b)
    return a | b
end

local function bit_rshift(a, n)
    return a >> n
end

local function bit_lshift(a, n)
    return a << n
end


function parse_ecu_packet(data)
    if not data or #data < 7 then
        return nil, "Packet too short or nil data"
    end

--    local debug_str = "Data bytes: "
--    for i = 1, math.min(#data, 7) do
--        debug_str = debug_str .. string.format("0x%02X ", data:byte(i))
--    end
    --gcs:send_text(MAV_SEVERITY.DEBUG, debug_str)

    local head = data:byte(1)

    if head == 0xF1 then
        local crc_received = data:byte(7)
        local crc_calculated = crc8_non_table_simple(data, 6, 0)
    
        if crc_received ~= crc_calculated then
            return nil, "CRC mismatch: " .. string.format("0x%02X vs 0x%02X", crc_received, crc_calculated)
        end

        -- 解析转速 RPM
        local byte1 = data:byte(2)
        local byte2 = data:byte(3)
        rpm = (byte2 << 8 | byte1) * 10

        -- 解析温度
        local byte4 = data:byte(5)
        local byte5 = data:byte(6)
        local temp_low = (byte4 >> 2) & 0x07
        temperature = (temp_low << 8 | byte5) - 50

    elseif head == 0xF2 then
        -- 解析转速 RPM
        local byte1 = data:byte(2)
        local byte2 = data:byte(3)
        rpm = (byte2 << 8 | byte1) * 10
        --解析动力电压：动力电压 版本<=3 0.1V, 版本>=4  0.2V
        local byte4 = data:byte(5)
        power_voltage = byte4 * 0.2


    elseif head == 0xF3 then
        local byte1 = data:byte(2)
        local byte2 = data:byte(3)
        rpm = (byte2 << 8 | byte1) * 10

    elseif head == 0xF4 then
        local byte1 = data:byte(2)
        local byte2 = data:byte(3)
        rpm = (byte2 << 8 | byte1) * 10

    elseif head == 0xF5 then
        local byte1 = data:byte(2)
        local byte2 = data:byte(3)
        rpm = (byte2 << 8 | byte1) * 10

    elseif head == 0xF6 then
        local byte1 = data:byte(2)
        local byte2 = data:byte(3)
        rpm = (byte2 << 8 | byte1) * 10

    elseif head == 0xF7 then
        local byte1 = data:byte(2)
        local byte2 = data:byte(3)
        rpm = (byte2 << 8 | byte1) * 10
        --燃油速率
        local byte3 = data:byte(4)
        local byte4 = data:byte(5)
        local byte5 = data:byte(6)

        local flow_rate_low = byte4& 0x03
        --flow rate :单位：0.01L/min
        flow_rate = (flow_rate_low << 8 | byte3)
        --flow total:单位：0.01L
        flow_total = (byte2 << 6 |  byte4>>2)


    elseif head == 0xF8 then
        local byte1 = data:byte(2)
        local byte2 = data:byte(3)
        rpm = (byte2 << 8 | byte1) * 10
        
    elseif head == 0xF9 then
        local byte1 = data:byte(2)
        local byte2 = data:byte(3)
        rpm = (byte2 << 8 | byte1) * 10

    elseif head == 0xFA then  
        local byte1 = data:byte(2)
        local byte2 = data:byte(3)
        rpm = (byte2 << 8 | byte1) * 10

    else
        return nil, "Invalid packet header: " .. string.format("0x%02X", head)
    end  -- 这里添加了缺失的 end

    -- gcs:send_named_float('ECU_RMP', rpm)
    -- gcs:send_named_float('ECU_TEMP', temperature)

    return {
        rpm = rpm,
        temperature = temperature,
        flow_rate=flow_rate,
        flow_total=flow_total,
        power_voltage = power_voltage
    }
end


-- 处理接收到的ECU数据包
function handle_ECU_packet(data)

    if not data then
        gcs:send_text(MAV_SEVERITY.WARNING, "Received nil packet data")
        return false
    end

    local result, err = parse_ecu_packet(data)
    if result then 
        -- 更新全局数据
        engine_data.rpm = result.rpm
        engine_data.temperature = result.temperature
        engine_data.flow_rate = result.flow_rate
        engine_data.flow_total = result.flow_total
        engine_data.power_voltage = result.power_voltage
       
       
        
        return true
    else
        gcs:send_text(MAV_SEVERITY.WARNING, "ECU packet error: " .. err)
        return false
    end
end



function process_byte(byte)

    -- gcs:send_text(MAV_SEVERITY.INFO, string.format("read byte: 0x%02X", byte))
    
    if byte == 0xF1 or  byte == 0xF2  or  byte == 0xF3 or  byte == 0xF4 or  byte == 0xF5 or  byte == 0xF6 or  byte == 0xF7 or  byte == 0xF8 or  byte == 0xF9 or  byte == 0xFA then
        -- 检测到包头，开始新数据包
        rx_buffer = {byte}
        packet_started = true
    elseif packet_started then
        table.insert(rx_buffer, byte)
        
        -- 检查是否收到完整数据包（7字节）
        if #rx_buffer == 7 then
            local packet_data = string.char(table.unpack(rx_buffer))
            handle_ECU_packet(packet_data)
            packet_started = false
            rx_buffer = {}
        end
    end
end



-- 请求计时器，控制发送频率
local last_request_time = 0
local request_interval = 100 -- 100ms = 10次/秒
local request_counter = 0
local last_second_time = millis()


-- 发送请求数据的函数
function send_data_request()
    --CMD ID 0 :主机不控制ECU，但是ECU会返回发动机状态，用于保持连接
    local request_data = string.char(0xFF, 0x00, 0x00, 0x00)
    
    -- 调试：打印发送的字节
    local debug_str = "Sent: "
    for i = 1, #request_data do
        port:write(request_data:byte(i))
        debug_str = debug_str .. string.format("0x%02X ", request_data:byte(i))
    end
    -- gcs:send_text(MAV_SEVERITY.DEBUG, debug_str)
    
end


-- 高度（m）
local heights = {0, 1000, 2000, 3000, 4000, 5000, 6000, 7000, 8000, 9000, 10000}

-- 转速（RPM）
local rpms = {38000, 40600, 45000, 51600, 56500, 61500, 66000, 70000, 73500, 76900, 79500, 82400, 85000, 88000, 89000, 91000, 92500, 94000, 95500, 96600, 98000}

-- 油耗表（高度 x 转速），单位 g/min（取 0Ma 列）
local fuel_table = {
    {174,189,237,277,309,340,376,413,441,483,515,559,588,633,662,703,737,772,800,831,865},  -- 0m
    {153,189,209,244,273,300,332,364,389,426,454,493,519,558,584,620,650,681,706,733,763},  -- 1000m
    {135,166,184,215,240,264,292,320,342,375,399,433,456,491,513,545,571,599,620,644,671},  -- 2000m
    {118,145,161,188,210,231,255,281,300,328,350,380,399,430,450,478,501,524,543,565,588},  -- 3000m
    {103,127,141,164,183,202,223,245,262,287,306,332,349,376,393,417,438,458,475,493,514},  -- 4000m
    {90,111,123,144,160,176,195,214,229,250,267,290,305,328,343,364,382,400,415,431,449},   -- 5000m
    {79,97,107,125,139,153,170,186,199,218,232,252,265,286,299,317,333,349,361,375,391},    -- 6000m
    {68,84,93,109,121,133,148,162,173,189,202,219,231,248,260,276,289,303,314,326,340},     -- 7000m
    {59,73,81,94,105,116,128,140,150,164,175,190,200,215,225,239,250,262,272,282,294},      -- 8000m
    {51,63,69,81,91,100,110,121,129,142,151,164,172,186,194,206,216,226,234,244,255},       -- 9000m
    {44,54,60,70,78,86,95,104,112,122,130,141,149,160,167,178,186,195,202,210,221},         -- 10000m
}
-- ==========================================
-- 双线性插值辅助函数
-- ==========================================
local function find_interval(list, value)
    for i = 1, #list - 1 do
        if value <= list[i + 1] then
            return i
        end
    end
    return #list - 1
end

local function bilinear_interpolation(x, y, x_list, y_list, table)
    if y <=20000 then
        return 0
    end

    local i = find_interval(x_list, x)
    local j = find_interval(y_list, y)

    local x1, x2 = x_list[i], x_list[i + 1]
    local y1, y2 = y_list[j], y_list[j + 1]

    local Q11 = table[i][j]
    local Q12 = table[i][j + 1]
    local Q21 = table[i + 1][j]
    local Q22 = table[i + 1][j + 1]

    if x >= x_list[#x_list] then
        i = #x_list - 1
        x1, x2 = x_list[i], x_list[i + 1]
        Q11, Q21 = table[i][j], table[i + 1][j]
        Q12, Q22 = table[i][j + 1], table[i + 1][j + 1]
    end
    if y >= y_list[#y_list] then
        j = #y_list - 1
        y1, y2 = y_list[j], y_list[j + 1]
        Q11, Q12 = table[i][j], table[i][j + 1]
        Q21, Q22 = table[i + 1][j], table[i + 1][j + 1]
    end

    local denom = (x2 - x1) * (y2 - y1)
    local fxy = (Q11 * (x2 - x) * (y2 - y)
              +  Q21 * (x - x1) * (y2 - y)
              +  Q12 * (x2 - x) * (y - y1)
              +  Q22 * (x - x1) * (y - y1)) / denom
    return fxy
end
local total_fuel = 0.0  -- 总油耗
local time_fuel = 0.0  -- 瞬时油耗

function update_fuel(dt,rpm_acture)
    -- 飞控接口
    --local loc = gps:get_location(0)   -- 0 表示 GPS1
    local pos = ahrs:get_position()
    local altitude = 0 --高度
    if pos ~= nil then
        pos:relative_alt(false)
        pos:terrain_alt(false)
        pos:origin_alt(false)
        
        altitude = pos:alt()   --cm
        altitude = altitude/100.0 --m  默认就是 WGS84 椭球高度 
    else
        altitude = 0
    end


    --baro:altitude() or 0
    local rpm = rpm_acture  -- ⚠️ 这里先用变量占位，你后续替换成飞控rpm接口

    -- 当前油耗 g/min
    local fuel_rate = bilinear_interpolation(altitude, rpm, heights, rpms, fuel_table)
    fuel_rate = fuel_rate/1000.0 --将 g/min 转为 kg/min
    --记录瞬时油耗
    time_fuel = fuel_rate
    -- dt 秒 → 分钟
    total_fuel = total_fuel + fuel_rate * (dt / 60.0)

    -- 打印
    --gcs:send_text(0, string.format("Alt=%.0fm, RPM=%.0f, Rate=%.3f kg/min, Total=%.3f kg",
    --    altitude, rpm, fuel_rate, total_fuel))

    return total_fuel
end



local function update()

    local current_time = millis()
     -- 每秒重置计数器并显示发送频率
     if current_time - last_second_time >= 1000 then
        -- gcs:send_text(MAV_SEVERITY.INFO, string.format("Send frequency: %d times/sec", request_counter))
        request_counter = 0
        last_second_time = current_time
    end

    if current_time - last_request_time >= request_interval then
         send_data_request()
        last_request_time = current_time
    end
    

        --gcs:send_text(6, "update")
    -- 高效的串口数据处理
    local available = port:available()
    if available and  available>0 then 
        --gcs:send_text(6, "port:available")
        -- 限制单次处理的字节数量，防止卡顿
        local available_num = tonumber(tostring(available)) or 0
        local bytes_to_process = math.min(available_num , 150)
        -- gcs:send_text(MAV_SEVERITY.INFO, string.format("bytes_to_process=%d", bytes_to_process))
        
        --local debug_str1 = "receive: "
        for i = 1, bytes_to_process do
            local byte = port:read()
            --debug_str1 = debug_str1 .. string.format("0x%02X ", byte)
            --gcs:send_text(6, debug_str1)
            if byte then
                process_byte(byte)
            end
        end
        
    --     -- 如果缓冲区积累过多数据，清空缓冲区防止溢出
    --     if port:available() > 100 then
    --         gcs:send_text(MAV_SEVERITY.WARNING, "ECU packet many: ")
    --         while port:available() > 0 do
    --             port:read()
    --         end
    --         -- 重置数据包状态
    --         rx_buffer = {}
    --         packet_started = false
    --     end
     end

    -- 计算油耗：
    update_fuel(1,rpm)
    gcs:send_named_float('ecu_ft',total_fuel) -- fuel total
    gcs:send_named_float('ecu_fr',time_fuel) -- fuel 瞬时

    -- 发送遥测数据
    gcs:send_named_float('ecu_rpm', rpm)
    gcs:send_named_float('ecu_temp', temperature)
    --gcs:send_named_float('ecu_fr', flow_rate) --Flow Rate
    --gcs:send_named_float('ecu_ft', flow_total) --Flow Total 
    gcs:send_named_float('ecu_power', power_voltage) -- Power Voltage
    ---rpm,温度，油量，2 动力电电压，

    --   gcs:send_text(MAV_SEVERITY.INFO, string.format("temp=%d, rpm=%d", temperature, rpm))
      
    --   logger.write('EFSP', 'RPM,TEMP', 'ff', rpm, temperature)
  
    --   local buffer = {0xF1,0x00,0x00,0x00,0x00,0x4C,0xA0}
    --   local packet_data = string.char(table.unpack(buffer))
    --   handle_ECU_packet(packet_data)

    return update, 1000
 end
 gcs:send_text(MAV_SEVERITY.INFO, "scripts load!!!!!!!!!!!!!")

 return update()

 