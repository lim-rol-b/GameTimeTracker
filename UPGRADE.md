# 升级与保留数据

本版沿用之前版本的数据目录和 SQLite 表结构，无需重新导入游戏或手动迁移历史记录：

```
%LOCALAPPDATA%\GameActivityTracker\activity.db
```

1. 在旧版的系统托盘菜单中选择 **Exit / 退出**，不要只关闭主窗口。
2. 本版采用 `GameActivityTracker.exe` + `runtime` 文件夹。建议完整解压到新目录；原地升级时，先退出旧版，完整替换 exe 和 runtime 文件夹，避免混用不同版本依赖。不要删除用户数据目录。
3. 使用同一个 Windows 用户启动 `GameActivityTracker.exe`，自动读取原有游戏、识别规则、会话、活动区间和设置。

数据库位于用户数据目录，不在发布包中，替换程序不会覆盖数据库。本版不清空数据、不修改数据库文件名，不需要重新导入 SteamLibrary。

如果更换 Windows 用户或电脑，先退出记录器，再将整个 `%LOCALAPPDATA%\GameActivityTracker` 文件夹复制到新用户的同名位置。迁移游戏安装路径后，可在 Games 页面修改原有识别规则。

如果启用了登录启动，建议保留原程序目录；移动程序后请重新保存一次启动设置，使注册项指向新位置。

## 后续版本兼容约定

继续使用上述固定目录和 `activity.db` 文件名。数据库结构当前为版本 1；未来有结构变化时必须执行保留已有记录的版本化迁移，不得通过删除／重建数据库实现升级。游戏及设置的现有字段需向后兼容。

## 旧版解压缓存

本版不会生成单文件运行库解压缓存。旧版留下的 `%TEMP%\.net\GameActivityTracker` 不会自动删除；退出所有旧版程序后可手动删除该缓存目录。不要删除 `%LOCALAPPDATA%\GameActivityTracker`，那里存放历史记录。

## v1.0.2 轻量版要求

轻量版需要 Windows 10/11 x64，并安装 .NET 8 Desktop Runtime（Windows x64）。官方下载：https://dotnet.microsoft.com/download/dotnet/8.0 ，选择“.NET Desktop Runtime → Windows → x64”。普通 .NET Runtime、ASP.NET Core Runtime 和 x86 运行库不能替代。免安装版不需要额外安装运行库。

完整解压后保留 exe 旁边的 runtime 文件夹；不要只复制 exe。两种版本使用同一个用户数据目录，切换版本无需迁移记录。
