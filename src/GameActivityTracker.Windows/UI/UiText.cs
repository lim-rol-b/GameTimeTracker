namespace GameActivityTracker.Windows.UI;

// Translate persisted identifiers only for display; keep stored values compatible.
public static class UiText
{
    public static string State(ActivityState state) => state switch
    {
        ActivityState.ACTIVE => "活跃",
        ActivityState.IDLE => "空闲",
        ActivityState.BACKGROUND => "后台",
        _ => "未知"
    };

    public static string Source(string source) => source switch
    {
        "Observed" => "本地观测",
        "Observed; no pre-detection activity inferred" => "本地观测（不推算检测前的活动）",
        _ => "其他来源"
    };

    public static string EndReason(string? reason) => reason switch
    {
        null => "记录中",
        "ProcessExited" => "游戏进程退出",
        "GameDeleted" => "游戏已删除",
        "ClockMovedBackwards" => "系统时间回拨",
        "TrackerExit" => "记录器退出",
        "RecoveredAtCheckpoint" => "从最近保存点恢复",
        "SystemSuspend" => "系统休眠",
        "WindowsSessionEnding" => "系统会话结束",
        _ => "其他原因"
    };
}
