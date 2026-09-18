namespace GameActivityTracker.Windows.Services;

public sealed class FileTrackerLog(string directory) : ITrackerLog
{
    private readonly object _gate = new();
    public void Write(string message, Exception? error = null)
    {
        lock(_gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path=Path.Combine(directory,$"tracker-{DateTime.Now:yyyy-MM-dd}.log");
                // Avoid unbounded disk growth in a long-running background app.
                if (new FileInfo(path) is { Exists:true, Length:>5_000_000 }) return;
                File.AppendAllText(path,$"[{DateTimeOffset.Now:O}] {message}{(error is null ? "" : " | "+error)}{Environment.NewLine}");
                foreach(var old in Directory.EnumerateFiles(directory,"tracker-*.log").Where(f=>File.GetLastWriteTimeUtc(f)<DateTime.UtcNow.AddDays(-30))) File.Delete(old);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
