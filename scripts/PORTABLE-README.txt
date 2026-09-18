Game Activity Tracker — Windows 10/11 x64

1. Extract the ENTIRE ZIP into a stable folder. Keep runtime/ beside the EXE.
   Dependencies load directly from runtime/; no runtime extraction cache is created.
   Supporting documents are in runtime/docs/.
2. Run GameActivityTracker.exe. No .NET runtime installation required.
3. Open the top-right settings gear > 游戏管理, click '+ 添加游戏' next to '导入 SteamLibrary…', choose the actual game's EXE, name it, and save.
   Or use settings gear > 游戏管理 > 导入 SteamLibrary… to scan default and registered Steam libraries,
   select games and their EXEs, and import them in a batch.
4. Start the game. Foreground + input within 60 seconds counts as Active.
5. Closing the window hides it to the system tray by default.
6. To fully exit, right-click the tray icon and select Exit / 退出.

Data: %LOCALAPPDATA%\GameActivityTracker\activity.db
Logs: %LOCALAPPDATA%\GameActivityTracker\logs
Back up the entire data folder after exiting the application.

The app is local-only. No account, network service, or API key is needed.
XInput controllers are supported. Configure multiple processes in Games.

This package was cross-compiled on macOS. Native Windows startup and
real game/controller behavior still require Windows acceptance testing.
Source and detailed documentation are provided alongside this package.

UPDATE: Exit the old app from the tray before replacing program files.
The existing %LOCALAPPDATA%\GameActivityTracker data folder is reused.
Games, rules, sessions and settings remain available under the same Windows user.
See UPGRADE.md for details.

Appearance: settings gear > 外观 offers Light, Dark and Follow system. Changes save immediately.
Year selection is a scrollable list beside the annual heatmap. New years are added automatically.

HEATMAP: Larger day squares support hover zoom, horizontal dragging,
an auto-hiding horizontal scrollbar, and Shift + mouse wheel.
Hover a day to see its date above the square. Click a date for 24 horizontal
hour squares (16px, 5px gaps, 2px corners); hover an hour for records below the row.
Existing activity data is reused without migration.

SCROLLBARS: Hidden when idle; scrolling reveals a thin gray indicator.
Hover or drag the thumb to expand/darken it and show the track.
Indicators fade out 1.2 seconds after activity stops.

OVERVIEW UPDATE: The page scrollbar is at the far right.
Recent sessions were removed from Overview; history remains in detail views.
Over the heatmap, the ordinary wheel scrolls the page vertically;
Shift + wheel scrolls the calendar horizontally.

PROCESS DETECTION: Related real EXEs within the game install directory are discovered
in the background. Launchers and common helper tools are excluded from automatic rules.
For Minecraft/PCL, edit the game and use 选择 Minecraft 目录… to select .minecraft
(all versions) or one isolated version directory. Java entry point and gameDir must match.
