# 飞控网口调试控件 · feature/net-panel

本分支只放「飞控网口调试控件」模块，源码目录：`net-panel/`。

- 职责、验收点、坑位：见 [`FEATURE.md`](FEATURE.md)（**改代码前必读"坑与约定"**）
- 完整集成内容（三个模块一起）在 `main` 分支；打包在 `release` 分支

## 在本分支开发

```bash
git switch feature/net-panel
# 改 net-panel/zh-net.cs
git commit -am "net: 修复 xxx"
```

## 合并回集成版

```bash
git switch main
git checkout feature/net-panel -- net-panel/
git commit -m "net: 同步网口控件改动"
```

因为模块独占子目录，这条命令只会覆盖 `net-panel/`，不会动到 `mp-cn/`、`ecu-panel/`。

## 部署验证

双击 `net-panel/一键部署zh-net插件.bat`（右键以管理员身份运行），脚本会拷贝到
`C:\Program Files (x86)\Mission Planner\plugins\`、打印实际字节数并询问是否自动重启 MP。

装完在 MP「飞行数据」页勾选「飞控网口」。监听抓包需要 MP 以管理员身份运行。
