# MP 中文化 · feature/mp-cn

本分支只放「Mission Planner 中文化」模块，源码目录：`mp-cn/`。

- 职责、验收点、坑位：见 [`FEATURE.md`](FEATURE.md)
- 完整集成内容（三个模块一起）在 `main` 分支；打包在 `release` 分支

## 在本分支开发

```bash
git switch feature/mp-cn
# 改 mp-cn/*.cs
git commit -am "mpcn: 补全 xxx 词条"
```

## 合并回集成版

```bash
git switch main
git checkout feature/mp-cn -- mp-cn/
git commit -m "mpcn: 同步中文化改动"
```

因为模块独占子目录，这条命令只会覆盖 `mp-cn/`，不会动到 `ecu-panel/`、`net-panel/`。

## 部署验证

把 `mp-cn/*.cs` 复制到 `C:\Program Files (x86)\Mission Planner\plugins\`，重启 MP。
