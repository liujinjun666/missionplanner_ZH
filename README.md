# 遥测通信监听 · feature/serial-monitor

本分支只放「遥测通信监听」模块，源码目录：`serial-monitor/`。

- 职责、验收点、坑位：见 [`FEATURE.md`](FEATURE.md)（**改代码前必读"坑与约定"**）
- 完整集成内容（全部模块）在 `main` 分支；打包在 `release` 分支

## 在本分支开发

```bash
git switch feature/serial-monitor
# 改 serial-monitor/zh-serial.cs
git commit -am "serial: 修复 xxx"
```

## 合并回集成版

```bash
git switch main
git checkout feature/serial-monitor -- serial-monitor/
git commit -m "serial: 同步遥测监听改动"
```

因为模块独占子目录，这条命令只会覆盖 `serial-monitor/`，不会动到 `mp-cn/`、`ecu-panel/`、`net-panel/`。

## 部署验证

双击 `serial-monitor/一键部署zh-serial插件.bat`（右键以管理员身份运行），脚本会拷贝到
`C:\Program Files (x86)\Mission Planner\plugins\`、打印实际字节数并询问是否自动重启 MP。

装完在 MP「飞行数据」页工具栏勾选「遥测通信监听」（排在「飞控网口」右侧）。

> 改了 `.cs` 就一定要重新拷贝 —— MP 只在启动时编译 `plugins\` 下的文件，热替换不生效。

## 快速上手

1. **看 MP 与飞控的对话**：MP 连上飞控 → 勾「遥测通信监听」→ 报文页自动滚动，收/发都在。
2. **只看某一类报文**：「消息类型」下拉选一类；要同时看几类就点「多选…」勾上。
3. **看飞控打印的告警/日志**：切到「打印」页，或勾「只看文本/打印」。
4. **在窗口里发命令**：发送页选命令（如 `511 设置消息频率`）→ 下方参数区会换成这条命令的专属控件
   （下拉/带单位的数字框/勾选）→ 点「发送命令」；发完切回报文页，能看到自己发的 `COMMAND_LONG` 和飞控回的 `COMMAND_ACK`。
   列表里没有的命令选「自定义命令…」，手填 ID 与参数。
5. **导出**：「保存日志」出 TSV（Excel 可开）；「导出原始帧」出 .bin（可再用 MAVLink 工具离线分析）。
