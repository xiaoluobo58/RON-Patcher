# RON Patcher v0.2.4

Ready or Not 的 WinUI 3 / Fluent UI 本地模式管理器。

支持：

- 局域网补丁与 Steam 正版切换
- 首次设置备份原版游戏文件并计算哈希
- ZIP、RAR、7Z 补丁包
- 扫描、修复、回滚和 ColdClientLoader 启动
- x64 单文件便携发布
- 从 GitHub Release 检查更新

## 构建

需要 Windows 10 2004（19041）或更高版本、.NET SDK 9、Windows SDK 10.0.26100 和 Windows App SDK NuGet 包。

```powershell
dotnet restore .\RonPatcher.sln
dotnet publish .\src\RonPatcher\RonPatcher.csproj -c Release -r win-x64 --self-contained true
```

## 使用

1. 选择未修改的正版 Ready or Not 游戏目录。
2. 选择 ZIP、RAR 或 7Z 补丁包。
3. 扫描哈希并按需切换模式。

原版备份和操作状态保存在游戏目录的 `.ron-patcher` 文件夹中。

本项目不包含游戏文件或第三方联机补丁。请自行提供并确认补丁来源。
