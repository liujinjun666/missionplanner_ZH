# 涡喷项目 · Mission Planner 二次开发

面向玄云 SW400Pro 涡喷动力 + ZeroOneX6 飞控的 Mission Planner 定制，包含三个独立开发项。
三项功能最终打包进同一个 MP 安装包（MSI），但**分分支维护**。

仓库采用**按模块分子目录**的结构：每个模块独占一个子目录，因此 `feature/*` 分支可以只放本模块子目录，
既能在分支上单独阅读/编译，也能安全地 `checkout 分支 -- 子目录` 合并回 `main`，不会误覆盖别人的文件。

## 分支模型

| 分支 | 开发项 | 独占子目录 | 当前版本 |
|---|---|---|---|
| `main` | 集成基线（三项合并，可直接部署本机验证） | 全部 | — |
| `release` | **打包专用**：只接收合并、不直接改代码，出包与打版本标签都在这里 | 全部（打包时剔除 `zh-diag.cs`） | 按发布记录 |
| `feature/mp-cn` | **MP 中文化**：界面汉化、品牌替换、中文参数表、菜单汉化、快捷视图汉化 | `mp-cn/` | v1.1 |
| `feature/ecu-panel` | **玄云 ECU 控件**：飞行数据页 ECU 面板（转速/排气温度/推力/油量等遥测 + 双向控制） | `ecu-panel/` | v1.2 |
| `feature/net-panel` | **飞控网口调试控件**：NET_P1~P4 四个网口读写、高亮、通讯指示、抓包监听 | `net-panel/` | v1.1 |
| `feature/serial-monitor` | **遥测通信监听**：监听 MP 当前连接的收发报文、字段/十六进制详情、消息类型单选+多选、STATUSTEXT 打印着色、过滤/保存/导出、发送页（22 条 MAV_CMD，每条命令自带专属参数控件 + 写参数） | `serial-monitor/` | v1.1 |

工作方式：

- 单独改某一项 → 切到对应 `feature/*` 分支改，改完 `git commit`，再合并回 `main` 做集成验证；
- 打包 → `main` 验证通过后合并到 `release`，在 `release` 上出包并打发布标签（流程见 `RELEASE.md`）；
- 每个分支根目录有 `FEATURE.md`，写明职责、涉及文件、验收点，避免改串模块。

**分支内容互相独立**：三个 `feature/*` 分支只有本模块的子目录（打开分支只看到该模块源码与说明），
不是"各复制一份完整集成版"。从分支同步到 `main` 用子目录路径即可，天然不冲突：

```bash
git switch main
git checkout feature/net-panel -- net-panel/
git commit -m "net: 同步网口控件改动"
```

完整集成版（全部目录）只在 `main`，打包在 `release`。

### 提交信息规范

统一 `<模块>: <做了什么>`，模块前缀固定四个：

| 前缀 | 适用 |
|---|---|
| `mpcn:` | MP 中文化（汉化词条、品牌、参数表）→ `mp-cn/` |
| `ecu:` | 玄云 ECU 控件 → `ecu-panel/` |
| `net:` | 飞控网口调试控件 → `net-panel/` |
| `serial:` | 遥测通信监听 → `serial-monitor/` |
| `docs:` / `chore:` | 文档、脚本（`docs/`、`packages/`）、仓库维护 |

示例：

```
net: 监听输出改为 200ms 批量上屏，修复长时间开启闪退
ecu: 面板宽度 415→395，遥测列重排
mpcn: 补全 Config/Tuning 菜单 37 条词条
```

提交模板已配置（`git config commit.template .gitmessage`），`git commit` 时会自动带出提示。

## 目录说明

```
mp-cn/                        【feature/mp-cn】MP 中文化
  zh-brand.cs                 品牌/关于页替换
  zh-menu-cn.cs               主菜单汉化
  zh-quickview-rf.cs          快捷视图汉化

ecu-panel/                    【feature/ecu-panel】玄云 ECU 控件
  zh-ecu.cs                   面板插件（v1.2，宽 395px）
  swiwin_ecu_bridge.lua       ECU 通讯桥接脚本（跑在飞控上）
  LHY_motor.lua               发动机控制脚本
  玄云ECU双向控制方案_部署与使用说明.md

net-panel/                    【feature/net-panel】飞控网口调试控件
  zh-net.cs                   NET_P1~P4 读写/高亮/通讯指示/抓包（v1.1，47,987 字节）
  一键部署zh-net插件.bat        本机部署（拷贝+校验+可选自动重启 MP）

serial-monitor/               【feature/serial-monitor】遥测通信监听
  zh-serial.cs                只监听 MP 连接：报文/发送/打印三页（v1.1）
  一键部署zh-serial插件.bat     本机部署（拷贝+校验+可选自动重启 MP）
  README.md                   使用说明与坑位

docs/                         文档与参考资料
  *.html                      调试指导手册、发动机说明书、参数详解
  *.pdf                       官方技术说明书 / 尺寸图 / ZK 通讯协议
  原参数.param                 飞控原始参数备份

packages/                     发布物归档
  ECU双向控制方案/             最新 ECU 方案安装包（v1.2）；旧版本与 zip/rar 不推送

zh-diag.cs                    临时诊断插件（导出插件编译错误；仅本地调试用，不推送）
test_script.lua               调试脚本（不推送）
verify_lua_v12.py             调试脚本（不推送）
图片/                         产品实拍图 —— 仅本地保留，不推送（隐私）
官网文件/                     厂商工具（ZK 软件、驱动、升级工具）—— 仅本地保留，不推送
```

## 部署（本机验证）

MP 只从安装目录的 `plugins` 子目录加载 `.cs` 插件：

```
C:\Program Files (x86)\Mission Planner\plugins\
```

- `net-panel/zh-net.cs`：双击运行 `net-panel/一键部署zh-net插件.bat`（右键以管理员身份运行），
  脚本会拷贝、打印实际字节数并询问是否自动重启 MP。
- `ecu-panel/zh-ecu.cs`、`mp-cn/*.cs`、`serial-monitor/zh-serial.cs`：同样复制到上述目录后重启 MP。

验证入口：MP「飞行数据」页工具栏 →「玄云ECU」「飞控网口」「遥测通信监听」三个勾选框。

注意：`.cs` 插件由 MP 启动时用 Roslyn 现场编译，**编译失败不会报错，只会静默不加载**。
排查时看 `zh-diag.cs` 导出的 `C:\ProgramData\Mission Planner\zh_plugin_errors.txt`（含 Roslyn 报错原文）。

## 已知限制

- 网口「监听」用 raw socket 抓包，**Windows 强制要求管理员权限**，无法绕过；无权限时面板会弹窗并提供「以管理员身份重启 MP」。建议给 MP 快捷方式勾选「以管理员身份运行」。
- 所有 `NET_*` 参数均为 RebootRequired，写入后需重启飞控才生效。
- 固件中协议值 51~54 为厂商自定义协议，界面显示为「自定义（51）」等。

## 相关外部资产

- 中文化交接包（834MB，不入库）：`C:\Users\245420122\Doubao\chats\2026-09-08\new-chat\MP中文化_交接包_1.3.9679.3015\`
  - `00_HANDOFF交接说明_必读.md`、`01_最终安装包`、`02_中文参数表_pdef`、`03_插件源码`、`04_打包工程`

## 版本记录

| 日期 | 项目 | 版本 | 说明 |
|---|---|---|---|
| 2026-11-05 | ECU 控件 | v1.2 | 面板收窄至 395px，遥测列重排 |
| 2026-11-08 | 网口调试控件 | v1.1 | 四网口读写/高亮/通讯指示/监听抓包；修文字底色、框线、监听卡死与闪退 |
| 2026-11-08 | 遥测通信监听 | v1.1 | 发送页改版：22 条命令每条自带专属参数控件（下拉/带单位数值/勾选），不再手填 P1~P7；新增模式、任务、ROI、校准等命令；修 CS1503 |
| 2026-11-08 | 遥测通信监听 | v1.0 | 只监听 MP 连接（收/发双向）、字段与 hex 详情、STATUSTEXT 打印着色、消息类型单选+多选、过滤/保存/导出 |
| 2026-11-08 | 仓库 | — | 目录结构改为按模块分子目录（`mp-cn/` `ecu-panel/` `net-panel/`） |

## 仓库内容边界（重要）

本仓库**只推送最新成果**，以下内容仅保留在本地、不入库（见 `.gitignore`）：

| 不推送的内容 | 原因 |
|---|---|
| `zh-diag.cs` | 临时诊断插件，导出 MP 插件编译错误用 |
| `test_script.lua`、`verify_lua_v12.py` | 调试/校验脚本 |
| `packages/ECU双向控制方案/…_v1.1/`、`*_v1.1.rar` | 旧版本安装包（只保留最新 v1.2） |
| `packages/**/*.zip`、`*.rar` | 发布归档压缩包（源文件已在 `packages/…_v1.2/`） |
| `官网文件/` | 厂商第三方工具（ZK 软件、驱动、升级工具），非本项目成果 |
| `图片/` | 产品实拍图，涉及隐私，仅本地保留 |
