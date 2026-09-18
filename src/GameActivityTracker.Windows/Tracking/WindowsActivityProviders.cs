using System.Runtime.InteropServices;
using GameActivityTracker.Core.Tracking;

namespace GameActivityTracker.Windows.Tracking;

public sealed class ForegroundWindowDetector : IForegroundWindowDetector
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    public int? GetForegroundProcessId()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return null;
        return GetWindowThreadProcessId(window, out var pid) == 0 ? null : (int)pid;
    }
}
public sealed class KeyboardMouseActivityProvider : IKeyboardMouseActivityProvider
{
    [StructLayout(LayoutKind.Sequential)] private struct LastInputInfo { public uint Size; public uint Time; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
    public DateTimeOffset? GetLastInput(DateTimeOffset now)
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return null;
        var milliseconds = unchecked((uint)Environment.TickCount64 - info.Time);
        return now.AddMilliseconds(-milliseconds);
    }
}
public sealed class ControllerActivityProvider : IControllerActivityProvider
{
    [StructLayout(LayoutKind.Sequential)] private struct Gamepad
    {
        public ushort Buttons; public byte LT; public byte RT; public short LX; public short LY; public short RX; public short RY;
        public readonly ControllerSample Sample => new(Buttons,LX,LY,RX,RY,LT,RT);
    }
    [StructLayout(LayoutKind.Sequential)] private struct State { public uint Packet; public Gamepad Gamepad; }
    [DllImport("xinput1_4.dll", EntryPoint="XInputGetState")] private static extern uint GetState(uint index, out State state);
    private readonly ControllerSample?[] _previous = new ControllerSample?[4];
    private DateTimeOffset? _lastInput;
    private bool _unavailable;
    public DateTimeOffset? Poll(DateTimeOffset now, double deadZone)
    {
        if (_unavailable) return null;
        try
        {
            for (uint i=0;i<4;i++)
            {
                if (GetState(i,out var state) != 0) { _previous[i] = null; continue; }
                var sample = state.Gamepad.Sample;
                // Connecting a controller alone is not activity. Held steady controls do not refresh the clock.
                if (_previous[i] is {} old && ControllerFilter.HasMeaningfulChange(old,sample,deadZone)) _lastInput=now;
                _previous[i]=sample;
            }
        }
        catch (DllNotFoundException) { _unavailable=true; }
        catch (EntryPointNotFoundException) { _unavailable=true; }
        return _lastInput;
    }
    public void Reset() { Array.Clear(_previous); _lastInput=null; }
}
