using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GameActivityTracker.Windows.GamePresence;

internal static class ProcessCatalog
{
    internal readonly record struct Entry(int Id,string Name);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size,Usage,ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId,Threads,ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string Executable;
    }
    [DllImport("kernel32.dll",SetLastError=true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags,uint processId);
    [DllImport("kernel32.dll",EntryPoint="Process32FirstW",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeFileHandle snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll",EntryPoint="Process32NextW",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeFileHandle snapshot,ref ProcessEntry entry);

    public static IReadOnlyList<Entry> Candidates(Func<string,bool> matchesName)
    {
        using var snapshot=CreateToolhelp32Snapshot(2,0); // TH32CS_SNAPPROCESS
        if(snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var entry=new ProcessEntry { Size=(uint)Marshal.SizeOf<ProcessEntry>(),Executable="" };
        var result=new List<Entry>();
        var available=Process32First(snapshot,ref entry);
        while(available)
        {
            var name=Path.GetFileNameWithoutExtension(entry.Executable);
            if(entry.ProcessId!=0 && matchesName(name)) result.Add(new((int)entry.ProcessId,name));
            available=Process32Next(snapshot,ref entry);
        }
        var error=Marshal.GetLastWin32Error();
        // Never treat a failed enumeration as "all games exited".
        if(error!=18) throw new Win32Exception(error); // ERROR_NO_MORE_FILES
        return result;
    }
}
