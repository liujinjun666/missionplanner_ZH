# 玄云 ECU 控件 · feature/ecu-panel

本分支只放「玄云 ECU 控件」模块，源码目录：`ecu-panel/`。

- 职责、验收点、坑位：见 [`FEATURE.md`](FEATURE.md)
- 完整集成内容（三个模块一起）在 `main` 分支；打包在 `release` 分支

## 在本分支开发

```bash
git switch feature/ecu-panel
# 改 ecu-panel/zh-ecu.cs、ecu-panel/*.lua
git commit -am "ecu: 增加 xxx 遥测"
```

## 合并回集成版

```bash
git switch main
git checkout feature/ecu-panel -- ecu-panel/
git commit -m "ecu: 同步 ECU 控件改动"
```

因为模块独占子目录，这条命令只会覆盖 `ecu-panel/`，不会动到 `mp-cn/`、`net-panel/`。

## 部署验证

1. `ecu-panel/zh-ecu.cs` → `C:\Program Files (x86)\Mission Planner\plugins\`
2. `ecu-panel/swiwin_ecu_bridge.lua` → 飞控 SD 卡 `scripts/`（`SCR_ENABLE=1`）
3. 重启 MP，「飞行数据」页勾选「玄云ECU」
