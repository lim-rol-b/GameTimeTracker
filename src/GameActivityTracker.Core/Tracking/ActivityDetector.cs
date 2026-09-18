namespace GameActivityTracker.Core.Tracking;

public sealed record ActivityObservation(bool? IsForeground, DateTimeOffset? LastInput);
public interface IForegroundWindowDetector { int? GetForegroundProcessId(); }
public interface IKeyboardMouseActivityProvider { DateTimeOffset? GetLastInput(DateTimeOffset now); }
public interface IControllerActivityProvider { DateTimeOffset? Poll(DateTimeOffset now, double deadZone); void Reset(); }
public sealed class ActivityDetector
{
    public ActivityState Detect(DateTimeOffset now, ActivityObservation input, TimeSpan threshold)
    {
        if (input.IsForeground is null) return ActivityState.UNKNOWN;
        if (input.IsForeground == false) return ActivityState.BACKGROUND;
        if (input.LastInput is null || input.LastInput > now) return ActivityState.UNKNOWN;
        return now - input.LastInput.Value < threshold ? ActivityState.ACTIVE : ActivityState.IDLE;
    }
}
public readonly record struct ControllerSample(ushort Buttons, short LX, short LY, short RX, short RY, byte LT, byte RT);
public static class ControllerFilter
{
    public static ControllerSample Normalize(ControllerSample s, double deadZone) => new(s.Buttons,
        Axis(s.LX, deadZone), Axis(s.LY, deadZone), Axis(s.RX, deadZone), Axis(s.RY, deadZone),
        s.LT < 30 ? (byte)0 : s.LT, s.RT < 30 ? (byte)0 : s.RT);
    private static short Axis(short value, double deadZone) => Math.Abs((int)value) / 32768.0 < deadZone ? (short)0 : value;
    public static bool HasMeaningfulChange(ControllerSample previous, ControllerSample current, double deadZone) =>
        Normalize(previous, deadZone) != Normalize(current, deadZone);
}
