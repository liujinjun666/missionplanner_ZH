# 涡喷项目 · Mission Planner 二次开发

面向玄云 SW400Pro 涡喷动力 + ZeroOneX6 飞控的 Mission Planner 定制，包含三个独立开发项。
三项功能最终打包进同一个 MP 安装包（MSI），但**分分支维护**。

## 分支模型

| 分支 | 开发项 | 涉及文件 | 当前版本 |
|---|---|---|---|
| `main` | 集成基线（三项合并，可直接部署本机验证） | 全部 | — |
| `release` | **打包专用**：只接收合并、不直接改代码，出包与打版本标签都在这里 | 全部（打包时剔除 `zh-diag.cs`） | 按发布记录 |
| `feature/mp-cn` | **MP 中文化**：界面汉化、品牌替换、中文参数表、菜单汉化、快捷视图汉化 | `zh-brand.cs`、`zh-menu-cn.cs`、`zh-quickview-rf.cs`、语言包/参数表（见交接包） | v1.1 |
| `feature/ecu-panel` | **玄云 ECU 控件**：飞行数据页 ECU 面板（转速/排气温度/推力/油量等遥测 + 双向控制） | `zh-ecu.cs`、`swiwin_ecu_bridge.lua`、`玄云ECU双向控制方案_部署与使用说明.md` | v1.2 |
| `feature/net-panel` | **飞控网口调试控件**：NET_P1~P4 四个网口读写、高亮、通讯指示、抓包监听 | `zh-net.cs`、`一键部署zh-net插件.bat` | v1.1 |

工作方式：

- 单独改某一项 → 切到对应 `feature/*` 分支改，改完 `git commit`，再合并回 `main` 做集成验证；
- 打包 → `main` 验证通过后合并到 `release`，在 `release` 上出包并打发布标签（流程见 `RELEASE.md`）；
- 每个分支根目录有 `FEATURE.md`，写明职责、涉及文件、验收点，避免改串模块。

**分支内容互相独立**：三个 `feature/*` 分支只放本模块的文件（打开分支只能看到该模块的源码与说明），
不是"各复制一份完整集成版"。各自的 `README.md` 里写了同步命令，例如：

```bash
git switch main
git checkout feature/net-panel -- zh-net.cs 一键部署zh-net插件.bat
git commit -m "net: 同步网口控件改动"
```

完整集成版（全部文件）只在 `main`，打包在 `release`。

### 提交信息规范

统一 `<模块>: <做了什么>`，模块前缀固定四个：

| 前缀 | 适用 |
|---|---|
| `mpcn:` | MP 中文化（汉化词条、品牌、参数表） |
| `ecu:` | 玄云 ECU 控件（`zh-ecu.cs`、`swiwin_ecu_bridge.lua`） |
| `net:` | 飞控网口调试控件（`zh-net.cs`） |
| `docs:` / `chore:` | 文档、脚本、仓库维护 |

示例：

```
net: 监听输出改为 200ms 批量上屏，修复长时间开启闪退
ecu: 面板宽度 415→395，遥测列重排
mpcn: 补全 Config/Tuning 菜单 37 条词条
```

提交模板已配置（`git config commit.template .gitmessage`），`git commit` 时会自动带出提示。

## 目录说明

```
zh-brand.cs                  品牌/关于页替换（中文化）
zh-menu-cn.cs                主菜单汉化
zh-quickview-rf.cs           快捷视图汉化
zh-ecu.cs                    玄云 ECU 控件（v1.2）
zh-net.cs                    飞控网口调试控件（v1.1，47,987 字节）
zh-diag.cs                   临时诊断插件（导出插件编译错误；仅本地调试用，不推送）
swiwin_ecu_bridge.lua        ECU 通讯桥接脚本（Lua，跑在飞控上）
一键部署zh-net插件.bat        本机部署 zh-net（拷贝+校验+可选自动重启 MP）
玄云ECU双向控制方案_部署与使用说明.md
原参数.param                  飞控原始参数备份
*手册/说明书 .html/.pdf       产品与调试文档
图片/                        产品实拍图 —— 仅本地保留，不推送（隐私）
官网文件/                     厂商工具（ZK 软件、驱动、升级工具）—— 仅本地保留，不推送
安装包/                       最新 ECU 方案安装包（v1.2）；旧版本与 zip/rar 归档不推送
```

## 部署（本机验证）

MP 只从安装目录的 `plugins` 子目录加载 `.cs` 插件：

```
C:\Program Files (x86)\Mission Planner\plugins\
```

- `zh-net.cs`：双击运行 `一键部署zh-net插件.bat`（右键以管理员身份运行），会打印实际字节数并询问是否自动重启 MP。
- `zh-ecu.cs` / 中文化插件：同样复制到上述目录后重启 MP。

验证入口：MP「飞行数据」页工具栏 →「玄云ECU」「飞控网口」两个勾选框。

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

## 仓库内容边界（重要）

本仓库**只推送最新成果**，以下内容仅保留在本地、不入库（见 `.gitignore`）：

| 不推送的内容 | 原因 |
|---|---|
| `zh-diag.cs` | 临时诊断插件，导出 MP 插件编译错误用 |
| `test_script.lua`、`verify_lua_v12.py` | 调试/校验脚本 |
| `安装包/…_v1.1/`、`*_v1.1.rar` | 旧版本安装包（只保留最新 v1.2） |
| `安装包/*.zip`、`*.rar` | 发布归档压缩包（源文件已在 `安装包/…_v1.2/`） |
| `官网文件/` | 厂商第三方工具（ZK 软件、驱动、升级工具），非本项目成果 |
| `图片/` | 产品实拍图，涉及隐私，仅本地保留 |
