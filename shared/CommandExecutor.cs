using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
internal static class CommandExecutor
{
    public static void Handle(Stream stream,CancellationToken cancel=default(CancellationToken))
    {
        HandleRequest(stream,RemsupProtocol.ReadLine(stream,100000),cancel);
    }
    public static void HandleRequest(Stream stream,string header,CancellationToken cancel=default(CancellationToken))
    {
        var p=header.Split(new[]
        {
            ' '
        }
        ,5);
        int timeout,max;
        if(p.Length!=5||p[0]!="REMSUP1"||p[1]!="EXEC"||!int.TryParse(p[2],out timeout)||timeout<1||timeout>300||!int.TryParse(p[3],out max)||max<1024||max>1048576)
        {
            RemsupProtocol.WriteLine(stream,"REMSUP1 ERROR invalid-request");
            return;
        }
        string command;
        try
        {
            command=Encoding.UTF8.GetString(Convert.FromBase64String(p[4]));
        }
        catch(FormatException)
        {
            RemsupProtocol.WriteLine(stream,"REMSUP1 ERROR invalid-command");
            return;
        }
        var r=Execute(command,timeout,max,cancel);
        RemsupProtocol.WriteLine(stream,"REMSUP1 RESULT "+r.ExitCode+" "+(r.Truncated?"1":"0")+" "+r.DurationMs+" "+RemsupProtocol.Encode(r.Stdout)+" "+RemsupProtocol.Encode(r.Stderr));
    }
    public static CommandResult Execute(string command,int timeout,int max,CancellationToken cancel=default(CancellationToken))
    {
        var watch=Stopwatch.StartNew();
        var r=new CommandResult();
        var sync=new object();
        int bytes=0;
        var output=new StringBuilder();
        var errors=new StringBuilder();
        cancel.ThrowIfCancellationRequested();
        using(var owned=new OwnedCommandProcess(command))
        {
            var process=owned.Process;
            Action<StreamReader,StringBuilder> drain=(reader,target)=>
            {
                try
                {
                    int ch;
                    while((ch=reader.Read())!=-1)
                    {
                        lock(sync)
                        {
                            string s=((char)ch).ToString();
                            int n=Encoding.UTF8.GetByteCount(s);
                            if(bytes+n<=max)
                            {
                                target.Append(s);
                                bytes+=n;
                            }
                            else r.Truncated=true;
                        }
                    }
                }
                catch(IOException)
                {
                }
                catch(ObjectDisposedException)
                {
                }
            }
            ;
            var a=new Thread(()=>drain(owned.StandardOutput,output))
            {
                IsBackground=true
            }
            ;
            var b=new Thread(()=>drain(owned.StandardError,errors))
            {
                IsBackground=true
            }
            ;
            a.Start();
            b.Start();
            while(!process.WaitForExit(100)&&watch.ElapsedMilliseconds<timeout*1000&&!cancel.IsCancellationRequested)
            {
            }
            if(!process.HasExited)
            {
                r.ExitCode=124;
            }
            else r.ExitCode=owned.ExitCode;
            // Also stop descendants left behind by a shell that already exited.
            owned.StopTree();
            if(!a.Join(5000)||!b.Join(5000))throw new IOException("Command output streams did not close.");
            lock(sync)
            {
                r.Stdout=output.ToString();
                r.Stderr=errors.ToString();
            }
        }
        r.DurationMs=(int)watch.ElapsedMilliseconds;
        return r;
    }
}
