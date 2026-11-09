# release — 打包发布分支

**规矩：这个分支只接收合并，不直接改代码。** 任何修复都回到对应 `feature/*` 改完、合到 `main`、验证通过后再合并进来。
这样任何时候 `release` 上的提交都等于"已验收、可出包"的状态。

## 源码位置（对应安装包里的插件）

| 安装包内文件 | 仓库源码 |
|---|---|
| `plugins/zh-brand.cs`、`zh-menu-cn.cs`、`zh-quickview-rf.cs` | `mp-cn/` |
| `plugins/zh-ecu.cs` | `ecu-panel/zh-ecu.cs` |
| 飞控 SD `scripts/swiwin_ecu_bridge.lua` | `ecu-panel/swiwin_ecu_bridge.lua` |
| `plugins/zh-net.cs` | `net-panel/zh-net.cs` |
| `plugins/zh-serial.cs` | `serial-monitor/zh-serial.cs` |
| 语言包 / 中文参数表 | 交接包 `02_中文参数表_pdef`、`03_插件源码`（不入库） |

## 出包流程

```bash
cd C:/Users/245420122/Desktop/涡喷

# 1. 确认 main 已验证（MP 里三个面板都试过）
git switch main && git status            # 必须干净

# 2. 合并到 release
git switch release && git merge main

# 3. 按下面清单核对并出包（见"打包前检查清单"）

# 4. 打发布标签
git tag -a rel-2026.11.08 -m "发布：中文化 v1.1 + ECU v1.2 + 网口 v1.1"
```

## 打包前检查清单

- [ ] `git status` 干净，工作区无未提交改动
- [ ] `zh-diag.cs` **不得进入安装包**（仓库里已被 `.gitignore` 排除；仍要确认 `plugins\` 目录和打包工程里没有它）
- [ ] `test_script.lua`、`verify_lua_v12.py` 等调试脚本不得进包
- [ ] 三个插件的 `Version` 属性与面板标题版本号一致
  - `net-panel/zh-net.cs`：`Version` 返回 `1.1`，标题 `飞控网口设置 v1.1`
  - `ecu-panel/zh-ecu.cs`：`Version` / 面板标题 `v1.2`
  - `serial-monitor/zh-serial.cs`：`Version` 返回 `1.4`，窗口标题 `遥测通信监听 v1.4`
  - `mp-cn/` 三件套：`v1.1`
- [ ] 源码改动已从 `feature/*` 同步到 `main`（用 `git checkout feature/xxx -- 子目录/` 核对无差异）
- [ ] 中文化语言包 / 中文参数表从交接包 `02_中文参数表_pdef`、`03_插件源码` 取最新
- [ ] 打包工程用交接包 `04_打包工程`，不手工拼装
- [ ] 安装包在干净环境装一次：MP 启动无报错、三个勾选框都在、面板能读出参数
- [ ] `C:/ProgramData/Mission Planner/zh_plugin_errors.txt` 无新增报错（有则说明有插件编译失败）

## 版本号规则

| 位置 | 规则 |
|---|---|
| 插件 `Version` 属性 | `1.1` / `1.2` 这类，改动即升 |
| 面板标题 | 与 `Version` 完全一致（`飞控网口设置 v1.1`） |
| 发布标签 | `rel-YYYY.MM.DD`，一天内多次发布加后缀 `rel-2026.11.08b` |
| 模块里程碑标签 | `mpcn-v1.x`、`ecu-v1.x`、`net-v1.x`（打在对应 `feature` 分支上） |
| 安装包文件名 | 沿用交接包的命名，内部版本号与标签对应 |

## 交付物清单（一个完整包应包含）

```
MissionPlanner（安装目录）
└── plugins/
    ├── zh-brand.cs            中文化·品牌        ← mp-cn/zh-brand.cs
    ├── zh-menu-cn.cs          中文化·菜单        ← mp-cn/zh-menu-cn.cs
    ├── zh-quickview-rf.cs     中文化·快捷视图    ← mp-cn/zh-quickview-rf.cs
    ├── zh-ecu.cs              玄云 ECU 控件     ← ecu-panel/zh-ecu.cs
    ├── zh-net.cs              飞控网口调试控件   ← net-panel/zh-net.cs
    └── zh-serial.cs           遥测通信监听       ← serial-monitor/zh-serial.cs
    语言包 zh-Hans / zh-Hant / zh-cn / zh-tw
    中文参数表 *.apm.pdef.xml
```

外加：ECU 方案用到的 `swiwin_ecu_bridge.lua`（刷进飞控）、部署与使用说明文档。

## 发布记录

| 标签 | 日期 | 产物 | 大小 / SHA256 前 16 位 | 内容 |
|---|---|---|---|---|
| `rel-2026.11.09` | 2026-11-09 | `MissionPlanner_中文版_1.3.9679.3015_加固版5.msi` | 216,142,152 B / `e1aa1fe0317e1b5c` | 中文化 v1.1 + ECU v1.2 + 网口 v1.1 + 遥测监听 **v1.4**（参数区配色跟随 MP 主题） |
| `rel-2026.11.08` | 2026-11-08 | `MissionPlanner_中文版_1.3.9679.3015_加固版4.msi` | — | 中文化 v1.1 + ECU v1.2 + 网口 v1.1 + 遥测监听 v1.3（参数区固定白底） |
| `rel-2026.11.01` | 2026-11-01 | 首个加固版 | — | 中文化 v1.1 + ECU v1.2 + 网口 v1.1 |

出包环境固定参数（加固版5 起）：

- `ProductVersion="1.3.9680"` + `<MajorUpgrade AllowSameVersionUpgrades="yes">` —— 保证能直接覆盖旧版安装，不用先卸载
- 包内 6 个插件与仓库源码 **MD5 逐字节一致**（用 `dark.exe -x` 反编译核对）
- `MissionPlanner.exe.config` 里 9 个自动更新键全部清空（防止装完被拉回英文原版）

## 回滚

```bash
git switch release
git reset --hard rel-2026.11.01     # 回到上一个发布点（谨慎，会丢 release 上未发布的合并）
```

线上包回滚建议直接重发上一个标签对应的 MSI，不要在用户机器上手改插件文件。
