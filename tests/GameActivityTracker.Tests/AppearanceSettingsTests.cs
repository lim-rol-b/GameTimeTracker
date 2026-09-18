using System.Text.Json;
using GameActivityTracker.Core;
using GameActivityTracker.Core.Statistics;
using GameActivityTracker.Data;
using Xunit;
namespace GameActivityTracker.Tests;
public sealed class AppearanceSettingsTests
{
    [Fact] public void YearListStartsNowAndGrowsWithoutFutureYears()
    {
        Assert.Equal(new[]{2026},TrackingYears.Available(2026,2026,[]));
        Assert.Equal(new[]{2028,2027,2026},TrackingYears.Available(2026,2028,[]));
    }
    [Fact] public void ExistingHistoryRemainsAccessibleAndClockRollbackIsSafe()
    {
        Assert.Equal(new[]{2026,2025,2024},TrackingYears.Available(2026,2026,[2024,2030]));
        Assert.Equal(new[]{2025},TrackingYears.Available(2026,2025,[]));
    }
    [Fact] public void OldSettingsDefaultToSystemAndCurrentYear()
    {
        var settings=JsonSerializer.Deserialize<TrackerSettings>("{\"IdleThresholdSeconds\":90}")!;
        Assert.Equal("System",settings.ThemeMode);Assert.Equal(DateTime.Now.Year,settings.FirstTrackingYear);
        Assert.Equal(90,settings.IdleThresholdSeconds);settings.Validate();
    }
    [Fact] public void ThemeAndFirstYearSurviveDatabaseReopen()
    {
        var dir=Path.Combine(Path.GetTempPath(),"theme-tests-"+Guid.NewGuid());
        try
        {
            var path=Path.Combine(dir,"activity.db");var db=new TrackerDatabase(path);
            db.SaveSettings(new(){ThemeMode="Dark",FirstTrackingYear=2026,IdleThresholdSeconds=90});
            var reopened=new TrackerDatabase(path).GetSettings();
            Assert.Equal("Dark",reopened.ThemeMode);Assert.Equal(2026,reopened.FirstTrackingYear);Assert.Equal(90,reopened.IdleThresholdSeconds);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(dir))Directory.Delete(dir,true); }
    }
}
