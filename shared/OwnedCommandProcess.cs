using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

// Start suspended so no command or descendant can run before job ownership is set.
// Closing the private job terminates its whole process tree, including on UI exit.
internal sealed class OwnedCommandProcess : IDisposable
{
    public Process Process { get; private set; }
    public StreamReader StandardOutput { get; private set; }
    public StreamReader StandardError { get; private set; }
    private IntPtr job;
    private IntPtr processHandle;
    public int ExitCode { get { uint code;if(!GetExitCodeProcess(processHandle,out code))throw new Win32Exception();return unchecked((int)code); } }

    public OwnedCommandProcess(string command)
    {
        IntPtr outputRead=IntPtr.Zero, outputWrite=IntPtr.Zero, errorRead=IntPtr.Zero, errorWrite=IntPtr.Zero, input=IntPtr.Zero;
        var info=new ProcessInformation();
        try
        {
            job=CreateJobObject(IntPtr.Zero,null);
            if(job==IntPtr.Zero)throw new Win32Exception();
            var limits=new ExtendedLimits();
            limits.Basic.LimitFlags=0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if(!SetInformationJobObject(job,9,ref limits,(uint)Marshal.SizeOf(typeof(ExtendedLimits))))throw new Win32Exception();
            var security=new SecurityAttributes { Length=Marshal.SizeOf(typeof(SecurityAttributes)), InheritHandle=true };
            if(!CreatePipe(out outputRead,out outputWrite,ref security,0)||!CreatePipe(out errorRead,out errorWrite,ref security,0))throw new Win32Exception();
            if(!SetHandleInformation(outputRead,1,0)||!SetHandleInformation(errorRead,1,0))throw new Win32Exception();
            input=CreateFile("NUL",0x80000000,3,ref security,3,0,IntPtr.Zero);
            if(input==new IntPtr(-1))throw new Win32Exception();
            var startup=new StartupInfo { Size=Marshal.SizeOf(typeof(StartupInfo)),Flags=0x100,Input=input,Output=outputWrite,Error=errorWrite };
            string executable=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"cmd.exe");
            var line=new StringBuilder("\""+executable+"\" /d /c "+command);
            if(!CreateProcess(executable,line,IntPtr.Zero,IntPtr.Zero,true,0x08000004,IntPtr.Zero,null,ref startup,out info))throw new Win32Exception();
            if(!AssignProcessToJobObject(job,info.Process))throw new Win32Exception();
            Process=Process.GetProcessById((int)info.ProcessId);
            var encoding=Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            StandardOutput=new StreamReader(new FileStream(new SafeFileHandle(outputRead,true),FileAccess.Read),encoding);outputRead=IntPtr.Zero;
            StandardError=new StreamReader(new FileStream(new SafeFileHandle(errorRead,true),FileAccess.Read),encoding);errorRead=IntPtr.Zero;
            if(ResumeThread(info.Thread)==uint.MaxValue)throw new Win32Exception();
            processHandle=info.Process;info.Process=IntPtr.Zero;
        }
        catch
        {
            if(info.Process!=IntPtr.Zero)TerminateProcess(info.Process,124);
            Dispose();
            throw;
        }
        finally
        {
            Close(ref outputRead);Close(ref outputWrite);Close(ref errorRead);Close(ref errorWrite);
            if(input!=new IntPtr(-1))Close(ref input);
            Close(ref info.Thread);Close(ref info.Process);
        }
    }
    public void StopTree()
    {
        if(job!=IntPtr.Zero&&!TerminateJobObject(job,124))throw new Win32Exception();
        if(Process!=null&&!Process.WaitForExit(5000))throw new IOException("The owned command process did not stop.");
        var wait=Stopwatch.StartNew();
        while(job!=IntPtr.Zero)
        {
            var accounting=new Accounting();
            if(!QueryInformationJobObject(job,1,ref accounting,(uint)Marshal.SizeOf(typeof(Accounting)),IntPtr.Zero))throw new Win32Exception();
            if(accounting.ActiveProcesses==0)break;
            if(wait.ElapsedMilliseconds>5000)throw new IOException("The owned command descendants did not stop.");
            System.Threading.Thread.Sleep(20);
        }
    }
    public void Dispose()
    {
        Close(ref job);
        if(Process!=null){Process.WaitForExit(5000);Process.Dispose();Process=null;}
        Close(ref processHandle);
        if(StandardOutput!=null){StandardOutput.Dispose();StandardOutput=null;}
        if(StandardError!=null){StandardError.Dispose();StandardError=null;}
    }
    static void Close(ref IntPtr handle){if(handle!=IntPtr.Zero){CloseHandle(handle);handle=IntPtr.Zero;}}
    [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct StartupInfo { public int Size;public string Reserved,Desktop,Title;public uint X,Y,XSize,YSize,XCount,YCount,Fill,Flags;public short Show,ReservedSize;public IntPtr ReservedBytes,Input,Output,Error; }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInformation { public IntPtr Process,Thread;public uint ProcessId,ThreadId; }
    [StructLayout(LayoutKind.Sequential)] struct BasicLimits { public long ProcessTime,JobTime;public uint LimitFlags;public UIntPtr MinWorkingSet,MaxWorkingSet;public uint ActiveProcesses;public UIntPtr Affinity;public uint Priority,Scheduling; }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong ReadOps,WriteOps,OtherOps,ReadBytes,WriteBytes,OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] struct ExtendedLimits { public BasicLimits Basic;public IoCounters Io;public UIntPtr ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory; }
    [StructLayout(LayoutKind.Sequential)] struct Accounting { public long UserTime,KernelTime,PeriodUserTime,PeriodKernelTime;public uint PageFaults,TotalProcesses,ActiveProcesses,TerminatedProcesses; }
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool QueryInformationJobObject(IntPtr job,int kind,ref Accounting accounting,uint size,IntPtr length);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateJobObject(IntPtr attributes,string name);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetInformationJobObject(IntPtr job,int kind,ref ExtendedLimits limits,uint size);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool TerminateJobObject(IntPtr job,uint exitCode);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool CreatePipe(out IntPtr read,out IntPtr write,ref SecurityAttributes attributes,uint size);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetHandleInformation(IntPtr handle,uint mask,uint flags);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateFile(string name,uint access,uint sharing,ref SecurityAttributes attributes,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateProcess(string application,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,bool inherit,uint flags,IntPtr environment,string directory,ref StartupInfo startup,out ProcessInformation info);
    [DllImport("kernel32.dll",SetLastError=true)] static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool TerminateProcess(IntPtr process,uint exitCode);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetExitCodeProcess(IntPtr process,out uint exitCode);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
