using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace GameActivityTracker.Windows.UI;
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name=null) => PropertyChanged?.Invoke(this,new(name));
    protected bool Set<T>(ref T field,T value,[CallerMemberName] string? name=null)
    { if(EqualityComparer<T>.Default.Equals(field,value)) return false; field=value; Changed(name); return true; }
}
public sealed class RelayCommand(Action<object?> execute,Func<object?,bool>? canExecute=null) : ICommand
{
    public bool CanExecute(object? parameter)=>canExecute?.Invoke(parameter)??true;
    public void Execute(object? parameter)=>execute(parameter);
    public event EventHandler? CanExecuteChanged { add=>CommandManager.RequerySuggested+=value; remove=>CommandManager.RequerySuggested-=value; }
}
public static class DurationFormat
{
    public static string Short(double seconds) => seconds<60 ? $"{Math.Floor(seconds):0}秒" : seconds<3600 ? $"{seconds/60:0}分钟" : $"{(int)(seconds/3600)}小时{(int)(seconds%3600/60):00}分钟";
    public static string Clock(double seconds) => $"{(int)(seconds/3600):00}:{(int)(seconds%3600/60):00}:{(int)(seconds%60):00}";
}
