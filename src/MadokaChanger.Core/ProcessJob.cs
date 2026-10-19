using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MadokaChanger.Core;

// Kernel-enforced memory bounds for untrusted native package decoding, and child cleanup on app exit.
internal sealed class ProcessJob : IDisposable
{
    private IntPtr handle;
    private ProcessJob(IntPtr h) {handle=h;}
    public static ProcessJob? Attach(Process p,long memoryLimit)
    {
        if(!OperatingSystem.IsWindows()) return null;
        var h=CreateJobObject(IntPtr.Zero,null); if(h==IntPtr.Zero) throw new Win32Exception();
        var job=new ProcessJob(h);
        var info=new ExtendedLimit { Basic=new BasicLimit { Flags=0x2000u | (memoryLimit>0?0x100u:0) },ProcessMemoryLimit=(UIntPtr)(ulong)memoryLimit };
        var size=Marshal.SizeOf<ExtendedLimit>(); var ptr=Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info,ptr,false);
            if(!SetInformationJobObject(h,9,ptr,(uint)size) || !AssignProcessToJobObject(h,p.Handle)) throw new Win32Exception();
            return job;
        }
        catch { job.Dispose(); try {p.Kill(true);} catch(InvalidOperationException) { } throw; }
        finally { Marshal.FreeHGlobal(ptr); }
    }
    public void Dispose() { var h=Interlocked.Exchange(ref handle,IntPtr.Zero); if(h!=IntPtr.Zero) CloseHandle(h); }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimit { public long PerProcessTime,PerJobTime; public uint Flags; public UIntPtr MinimumWorkingSet,MaximumWorkingSet; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass,SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimit { public BasicLimit Basic; public IoCounters Io; public UIntPtr ProcessMemoryLimit,JobMemoryLimit,PeakProcessMemory,PeakJobMemory; }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr CreateJobObject(IntPtr attributes,string? name);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(IntPtr job,int type,IntPtr info,uint length);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
    [DllImport("kernel32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
