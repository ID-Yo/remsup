using System;
using System.Security.Principal;
using System.Threading;
internal sealed class ApplicationInstance : IDisposable
{
    readonly Mutex mutex;bool owned;EventWaitHandle activation;RegisteredWaitHandle activationWait;Action activate;
    internal string Name{get;private set;}
    internal bool IsOwner{get{return owned;}}
    internal static string DefaultName{get{return @"Local\Remsup.Support."+WindowsIdentity.GetCurrent().User.Value;}}
    public ApplicationInstance():this(DefaultName){}
    internal ApplicationInstance(string mutexName,bool activateExisting=false)
    {
        Name=mutexName;mutex=new Mutex(false,mutexName);
        if(activateExisting)activation=new EventWaitHandle(false,EventResetMode.AutoReset,mutexName+".Show");
        bool acquired=false;
        try
        {
            acquired=mutex.WaitOne(0);
        }
        catch(AbandonedMutexException)
        {
            acquired=true;
        }
        owned=acquired;
        if(!acquired)
        {
            if(activateExisting){activation.Set();return;}
            mutex.Dispose();
            throw new InvalidOperationException("Remsup is already open for this Windows user. Use the existing window or close it from its tray menu first.");
        }
    }
    internal void OnActivate(Action show){activate=show;if(!owned||activation==null)return;activationWait=ThreadPool.RegisterWaitForSingleObject(activation,(_,__)=>show(),null,Timeout.Infinite,false);}
    internal void ReleaseForUpdate(){if(owned){if(activationWait!=null){activationWait.Unregister(null);activationWait=null;}mutex.ReleaseMutex();owned=false;}}
    internal void ReacquireAfterUpdate(){try{owned=mutex.WaitOne(0);}catch(AbandonedMutexException){owned=true;}if(!owned)throw new InvalidOperationException("Update rollback could not restore the single-instance lock.");if(activate!=null)OnActivate(activate);}
    public void Dispose()
    {
        if(owned){mutex.ReleaseMutex();owned=false;}
        if(activationWait!=null)activationWait.Unregister(null);if(activation!=null)activation.Dispose();mutex.Dispose();
    }
}
