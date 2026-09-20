游戏时长记录器 v1.0.2 — 轻量版使用要求

系统：Windows 10 / 11，x64。
必须预先安装 Microsoft .NET 8 Desktop Runtime（Windows x64）。
官方下载：https://dotnet.microsoft.com/download/dotnet/8.0
在下载页选择“.NET Desktop Runtime → Windows → x64”安装程序。
注意：普通 .NET Runtime、ASP.NET Core Runtime 或 x86 版本不能替代。
如果启动时提示需要安装 .NET，请检查是否安装了桌面运行库和正确架构。

完整解压 ZIP，运行 GameActivityTracker.exe，保留同目录 runtime 文件夹。
不要只复制 exe，也不要与旧版运行库混用。
不希望额外安装运行库，请下载不带 lite 的免安装版。

轻量版和免安装版的 UI、功能与数据目录相同。
数据：%LOCALAPPDATA%\GameActivityTracker\activity.db
升级前从托盘完全退出旧程序；更换程序文件不会删除此目录的历史记录。

轻量版体积不包含另外安装的系统 .NET 桌面运行库。
用户反馈优化后后台内存约 96 MB，实际占用随设备、游戏和历史数据变化。
