using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GameActivityTracker.Core.Statistics;

namespace GameActivityTracker.Windows.UI;
public sealed record HeatmapDay(DateOnly Date,bool InYear,Brush Color,string Tooltip)
{
    public double Opacity=>InYear?1:0;
    public string Label=>Tooltip;
}
public sealed record HeatmapMonth(string Name, double Offset);
public sealed class HeatmapViewModel : ObservableObject
{
    public const double CellPitch = 21;
    public ObservableCollection<HeatmapDay> Days { get; }=[];
    public ObservableCollection<HeatmapMonth> Months { get; }=[];
    public double CalendarWidth => Days.Count / 7 * CellPitch;
    public ICommand SelectDay { get; }
    public HeatmapViewModel(Action<DateOnly> selected) => SelectDay=new RelayCommand(o=>{if(o is HeatmapDay {InYear:true} day) selected(day.Date);});
    public void Update(int year,IReadOnlyDictionary<DateOnly,DailyStatistics> daily,IReadOnlyList<Game> games)
    {
        var first=new DateOnly(year,1,1); var last=new DateOnly(year,12,31);
        var start=first.AddDays(-((int)first.DayOfWeek+6)%7);
        var finish=last.AddDays(6-((int)last.DayOfWeek+6)%7);
        var updated=new List<HeatmapDay>();
        for(var day=start;day<=finish;day=day.AddDays(1))
        {
            daily.TryGetValue(day,out var stats);
            var details=stats is null ? "暂无记录" : string.Join("\n",stats.GameRunningSeconds.OrderByDescending(g=>g.Value).Select(g=>$"{games.FirstOrDefault(x=>x.Id==g.Key)?.Name ?? "Unknown"} · Active {DurationFormat.Short(stats.GameActiveSeconds.GetValueOrDefault(g.Key))}"));
            var tooltip=$"{day:yyyy-MM-dd}\nActive {DurationFormat.Short(stats?.ActiveSeconds??0)} · Running {DurationFormat.Short(stats?.RunningSeconds??0)}\n\n{details}";
            updated.Add(new(day,day.Year==year,(Brush)Application.Current.FindResource($"Heat{StatisticsService.HeatLevel(stats?.ActiveSeconds??0)}"),tooltip));
        }
        // Keep the calendar extent and scroll position stable during periodic dashboard refreshes.
        if(Days.Count==updated.Count && Days.Count>0 && Days[0].Date==start)
        {
            for(var i=0;i<updated.Count;i++) if(Days[i]!=updated[i]) Days[i]=updated[i];
        }
        else
        {
            Days.Clear();foreach(var day in updated) Days.Add(day);
            Months.Clear();
            for(var month=1;month<=12;month++)
            {
                var date=new DateOnly(year,month,1);
                Months.Add(new(date.ToString("MMM",CultureInfo.InvariantCulture),(date.DayNumber-start.DayNumber)/7*CellPitch+2.5));
            }
            Changed(nameof(CalendarWidth));
        }
    }
}
