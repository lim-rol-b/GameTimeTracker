namespace GameActivityTracker.Core.Statistics;
public static class TrackingYears
{
    public static int[] Available(int firstYear,int currentYear,IEnumerable<int> recordedYears)
    {
        var start=Math.Min(Math.Clamp(firstYear,1,9999),currentYear);
        foreach(var year in recordedYears) if(year>=1 && year<=currentYear) start=Math.Min(start,year);
        return Enumerable.Range(start,currentYear-start+1).Reverse().ToArray();
    }
}
