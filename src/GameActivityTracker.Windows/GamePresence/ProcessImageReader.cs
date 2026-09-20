using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
namespace GameActivityTracker.Windows.GamePresence;
internal static class ProcessImageReader
{
    [DllImport("kernel32.dll",SetLastError=true)]
    private static extern SafeProcessHandle OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process,uint flags,StringBuilder path,ref int size);
    [DllImport("kernel32.dll",SetLastError=true)]
    private static extern bool GetProcessTimes(SafeProcessHandle process,out long created,out long exited,out long kernel,out long user);
    public static (string? Path,DateTimeOffset? Started) Read(int pid)
    {
        using var handle=OpenProcess(0x1000,false,pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if(handle.IsInvalid) return (null,null);
        var size=512;var path=new StringBuilder(size);
        string? image=null;
        if(QueryFullProcessImageName(handle,0,path,ref size)) image=path.ToString();
        else if(Marshal.GetLastWin32Error()==122) // ERROR_INSUFFICIENT_BUFFER: preserve long-path support.
        {
            size=32768;path.EnsureCapacity(size);
            if(QueryFullProcessImageName(handle,0,path,ref size)) image=path.ToString();
        }
        DateTimeOffset? started=GetProcessTimes(handle,out var created,out _,out _,out _)?new DateTimeOffset(DateTime.FromFileTimeUtc(created)):null;
        return (image,started);
    }
}
