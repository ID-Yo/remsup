using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
internal sealed class SupportHost : IDisposable
{
    readonly Func<IPAddress,int,string> lanPolicy;
    readonly Func<System.Security.Cryptography.X509Certificates.X509Certificate2> lanIdentity;
    internal SupportHost(Func<IPAddress,int,string> policy=null,Func<System.Security.Cryptography.X509Certificates.X509Certificate2> identity=null){lanPolicy=policy??LanAccessPolicy.UnavailableReason;lanIdentity=identity??(()=>LanTls.Identity);}
    const int MaxTrackedSockets=5;
    readonly object sync=new object();
    readonly object lifecycle=new object();
    readonly SessionGate commandGate=new SessionGate();
    readonly SessionGate screenGate=new SessionGate();
    TcpClient activeSocket;
    TcpClient activeScreenSocket;
    CancellationTokenSource cancellation;
    CancellationTokenSource relayCancellation;
    CancellationTokenSource lanCancellation;
    TcpListener listener;
    System.Threading.Timer lanPolicyWatch;
    readonly List<TcpClient> sockets=new List<TcpClient>();
    readonly List<TcpClient> relaySockets=new List<TcpClient>();
    readonly List<TcpClient> lanSockets=new List<TcpClient>();
    int generation,relayEpoch;
    bool lanReady;string lanWarning="";
    bool relaySuspended,relayCommandReady,relayScreenReady;
    string configuredRelay,configuredId,configuredPassword,configuredPin;
    IPAddress configuredLanAddress;int configuredLanPort;bool configuredFixtureRelay;
    SupportState relayState=SupportState.Stopped;
    bool active,relaySession;
    SupportSnapshot snapshot=new SupportSnapshot(0,SupportState.Stopped,false,false,"Stopped");
    public event Action<SupportSnapshot> Changed;
    internal event Action RelayRegistrationsReady;
    public Action<Stream,CancellationToken> ScreenHandler;
    public SupportSnapshot Snapshot
    {
        get
        {
            lock(sync)return snapshot;
        }
    }
    bool settingsReserved;
    internal bool TryReserveSettings(){lock(sync){if(settingsReserved||commandGate.IsBusy||active)return false;settingsReserved=true;return true;}}
    internal void ReleaseSettingsReservation(){lock(sync)settingsReserved=false;}
    internal bool TryStopForSettings(){lock(lifecycle)lock(sync){if(commandGate.IsBusy||active)return false;StopCore();return true;}}
    internal bool OperationsIdle {get{return !commandGate.IsBusy&&!screenGate.IsBusy;}}
    internal bool RelayRegistrationReady
    {
        get { lock(sync)return !relaySuspended&&relayCommandReady&&(ScreenHandler==null||relayScreenReady); }
    }
    void Publish(int gen,SupportState state,string reason)
    {
        SupportSnapshot value;
        Action<SupportSnapshot> callback;
        lock(sync)
        {
            if(gen!=generation)return;
            relayState=state;
            snapshot=new SupportSnapshot(gen,active?SupportState.SessionActive:state,lanReady,relaySession,reason,state==SupportState.RelayWaiting||(active&&relaySession),lanWarning);
            value=snapshot;
            callback=Changed;
        }
        if(callback!=null)callback(value);
    }
    public static bool PrivateAddress(IPAddress ip)
    {
        if(ip==null)return false;
        var b=ip.GetAddressBytes();
        return b.Length==4&&(b[0]==10||(b[0]==172&&b[1]>=16&&b[1]<=31)||(b[0]==192&&b[1]==168));
    }
    public static bool LocalPrivateAddress(IPAddress ip)
    {
        if(!PrivateAddress(ip))return false;
        return LocalIPv4Addresses().Contains(ip.ToString());
    }
    public static List<string> LocalIPv4Addresses()
    {
        var addresses=new List<string>();
        foreach(var nic in NetworkInterface.GetAllNetworkInterfaces())
            if(nic.OperationalStatus==OperationalStatus.Up)
                foreach(var item in nic.GetIPProperties().UnicastAddresses)
                    if(PrivateAddress(item.Address))
                    {var value=item.Address.ToString();if(!addresses.Contains(value))addresses.Add(value);}
        return addresses;
    }
    public void Start(string relay,string id,string password,IPAddress lanAddress,int lanPort,string pin=null)
    {
        lock(lifecycle) StartCore(relay,id,password,lanAddress,lanPort,pin,false);
    }
    internal void StartFixture(string relay,string id,string password,IPAddress lanAddress=null,int lanPort=0)
    {lock(lifecycle)StartCore(relay,id,password,lanAddress,lanPort,null,true);}
    void StartCore(string relay,string id,string password,IPAddress lanAddress,int lanPort,string pin,bool fixtureRelay)
    {
        string host=null;
        int port=0;
        string resolvedPin=null;
        if(!string.IsNullOrWhiteSpace(relay))
        {
            RemsupProtocol.ParseEndpoint(relay,out host,out port);
            if(fixtureRelay)RelayTls.ValidateLoopbackFixture(host);
            else resolvedPin=RelayTls.PinFor(host,port,pin,false);
        }
        RemsupProtocol.ValidateCredentials(id,password);
        Stop();
        if(commandGate.IsBusy||screenGate.IsBusy)throw new InvalidOperationException("The previous session is stopping. Wait a moment before starting support again.");
        int gen,epoch;
        CancellationToken token;
        lock(sync)
        {
            gen=++generation;
            cancellation=new CancellationTokenSource();
            token=cancellation.Token;
            relayCancellation=CancellationTokenSource.CreateLinkedTokenSource(token);
            epoch=++relayEpoch;
            configuredRelay=relay;configuredId=id;configuredPassword=password;configuredPin=resolvedPin;configuredFixtureRelay=fixtureRelay;
            configuredLanAddress=lanAddress;configuredLanPort=lanPort;
            relaySuspended=false;relayCommandReady=relayScreenReady=false;
        }
        if(lanAddress!=null){
            RefreshLan();
            lanPolicyWatch=new System.Threading.Timer(_=>{if(!Monitor.TryEnter(lifecycle))return;try{RefreshLan();}finally{Monitor.Exit(lifecycle);}},null,10000,10000);
        }
        if(string.IsNullOrWhiteSpace(relay))
        {
            Publish(gen,SupportState.Failed,"Relay is not configured.");
            return;
        }
        Publish(gen,SupportState.Connecting,"Connecting to relay");
        CancellationToken relayToken;
        lock(sync)relayToken=relayCancellation.Token;
        new Thread(()=>RelayLoop(host,port,id,password,gen,epoch,relayToken,false,resolvedPin,fixtureRelay))
        {
            IsBackground=true
        }
        .Start();
        if(ScreenHandler!=null)
        {
            new Thread(()=>RelayLoop(host,port,id,password,gen,epoch,relayToken,true,resolvedPin,fixtureRelay))
            {
                IsBackground=true
            }
            .Start();
        }
    }
    void Track(TcpClient c,int gen,bool lan=false,bool relay=false)
    {
        lock(sync)
        {
            if(gen!=generation||(lan&&!lanReady)||(relay&&relaySuspended))
            {
                c.Close();
                throw new OperationCanceledException();
            }
            if(sockets.Count>=MaxTrackedSockets)
            {
                c.Close();
                throw new IOException("Support connection capacity is full.");
            }
            sockets.Add(c);
            if(lan)lanSockets.Add(c);
            if(relay)relaySockets.Add(c);
        }
    }
    void Untrack(TcpClient c)
    {
        lock(sync){sockets.Remove(c);lanSockets.Remove(c);relaySockets.Remove(c);}
    }
    internal void RefreshLan()
    {
        lock(lifecycle)
        {
            IPAddress address;int port,gen;string id,password;CancellationToken token;TcpListener current;
            lock(sync){if(cancellation==null||cancellation.IsCancellationRequested||relaySuspended||configuredLanAddress==null)return;address=configuredLanAddress;port=configuredLanPort;gen=generation;id=configuredId;password=configuredPassword;token=cancellation.Token;current=lanReady?listener:null;}
            try{
                string unavailable=lanPolicy(address,port);
                if(unavailable.Length>0){lock(sync)lanWarning=unavailable;if(current!=null)DisableLan(gen,current);else Publish(gen,relayState,"LAN unavailable.");return;}
                if(current!=null)return;
                var checkedIdentity=lanIdentity();var lan=new TcpListener(address,port);CancellationToken lanToken;
                try{lan.Start(8);lock(sync){if(gen!=generation||token.IsCancellationRequested||relaySuspended)throw new OperationCanceledException();listener=lan;lanReady=true;lanWarning="";if(lanCancellation!=null)lanCancellation.Dispose();lanCancellation=CancellationTokenSource.CreateLinkedTokenSource(token);lanToken=lanCancellation.Token;}}
                catch{lan.Stop();throw;}
                new Thread(()=>LanLoop(lan,id,password,gen,lanToken)){IsBackground=true}.Start();
                Publish(gen,relayState,"LAN listener ready.");
            }catch(Exception failure){
                lock(sync)lanWarning=failure is System.Security.Cryptography.CryptographicException?"LAN identity is unavailable. Restore this Windows user's protected identity; it was not regenerated.":failure is SocketException?"LAN listener could not start. Check whether the address/port is already in use.":failure.Message;
                if(current!=null)DisableLan(gen,current);else Publish(gen,relayState,"LAN unavailable.");
            }
        }
    }
    void DisableLan(int gen,TcpListener lan)
    {
        lock(sync)
        {
            if(gen!=generation||!lanReady)return;
            lanReady=false;
            lan.Stop();
            if(lanCancellation!=null)lanCancellation.Cancel();
            foreach(var c in lanSockets)c.Close();
        }
        Publish(gen,relayState,"LAN disabled: interface, Private profile or firewall rule changed. The listener will resume after the policy is corrected.");
    }
    void RelayLoop(string host,int port,string id,string password,int gen,int epoch,CancellationToken token,bool screen,string pin,bool fixtureRelay)
    {
        while(!token.IsCancellationRequested)
        {
            using(var c=new TcpClient())
            {
                var socket=c.Client;
                try
                {
                    Track(c,gen,false,true);
                    if(!screen)Publish(gen,SupportState.Connecting,"Connecting to relay");
                    RemsupProtocol.Connect(c,host,port,token);
                    RemsupProtocol.KeepAlive(c);
                    c.ReceiveTimeout=10000;
                    c.SendTimeout=10000;
                    Stream stream=fixtureRelay?(Stream)c.GetStream():RelayTls.OpenStream(c,host,pin,token);
                    RemsupProtocol.WriteLine(stream,"AGENT "+RemsupProtocol.NormalizeId(id)+" "+password+" "+RemsupProtocol.GenerateKey()+(screen?" SCREEN":""));
                    if(RemsupProtocol.ReadLine(stream,512)!="WAIT")throw new IOException("Relay registration refused.");
                    MarkRelayRegistered(gen,epoch,screen,fixtureRelay);
                    c.ReceiveTimeout=0;
                    if(!screen&&!RelayRegistrationReady)Publish(gen,SupportState.Connecting,"Command relay registered; waiting for screen channel registration");
                    if(RemsupProtocol.ReadLine(stream,512)!="OK")throw new IOException("Relay pairing failed.");
                    c.ReceiveTimeout=10000;
                    if(screen)Serve(c,gen,true,token,true,false,stream);
                    else
                    {
                        using(var watch=new System.Threading.Timer(_=>
                        {
                            try
                            {
                                if(socket.Poll(0,SelectMode.SelectRead)&&socket.Available==0)
                                {
                                    bool owns;lock(sync)
                                    {
                                        owns=gen==generation&&activeSocket==c;if(owns)relaySession=false;
                                    }
                                    if(owns)Publish(gen,SupportState.Reconnecting,"Relay lost; command may still be running");c.Close();
                                }
                            }
                            catch(ObjectDisposedException){}
                            catch(SocketException){}
                        },null,1000,1000)) Serve(c,gen,true,token,false,false,stream);
                    }
                }
                catch(Exception)
                {
                    if(!screen&&!token.IsCancellationRequested)Publish(gen,SupportState.Reconnecting,"Relay disconnected or unavailable; retrying.");
                }
                finally
                {
                    Untrack(c);
                    MarkRelayUnregistered(gen,epoch,screen);
                }
            }
            if(!screen&&!token.IsCancellationRequested)
            {
                Publish(gen,SupportState.Reconnecting,"Session ended; reconnecting.");
                try{token.WaitHandle.WaitOne(1000);}catch(ObjectDisposedException){break;}
            }
            else if(screen&&!token.IsCancellationRequested){try{token.WaitHandle.WaitOne(1000);}catch(ObjectDisposedException){break;}}
        }
    }
    static string BusyResponse(){return "ERR busy";}
    void MarkRelayRegistered(int gen,int epoch,bool screen,bool fixtureRelay)
    {
        Action ready=null;bool becameReady=false;
        lock(sync)
        {
            if(gen!=generation||epoch!=relayEpoch||relaySuspended)return;
            bool wasReady=relayCommandReady&&(ScreenHandler==null||relayScreenReady);
            if(screen)relayScreenReady=true;else relayCommandReady=true;
            if(!wasReady&&relayCommandReady&&(ScreenHandler==null||relayScreenReady)){becameReady=true;ready=RelayRegistrationsReady;}
        }
        if(becameReady)Publish(gen,SupportState.RelayWaiting,fixtureRelay?"Command and screen channels are registered over the isolated loopback fixture":"Command and screen channels are registered over pinned TLS");
        // Readiness observers are advisory; their failure must never tear down registered channels.
        if(ready!=null)foreach(Action observer in ready.GetInvocationList())try{observer();}catch(Exception){}
    }
    void MarkRelayUnregistered(int gen,int epoch,bool screen)
    {
        bool wasReady=false;
        lock(sync)
        {
            if(gen!=generation||epoch!=relayEpoch)return;
            wasReady=relayCommandReady&&(ScreenHandler==null||relayScreenReady);
            if(screen)relayScreenReady=false;else relayCommandReady=false;
        }
        if(wasReady)Publish(gen,SupportState.Reconnecting,"A relay channel disconnected; reconnecting.");
    }
    internal Func<string> DiagnosticsHandler;
    void Serve(TcpClient c,int gen,bool relay,CancellationToken token,bool screen,bool reserved=false,Stream transport=null)
    {
        var gate=screen?screenGate:commandGate;
        bool entered=reserved;
        if(!reserved)
        {
            lock(sync)
            {
                if(!settingsReserved&&gen==generation&&(!relay||!relaySuspended)&&(!token.IsCancellationRequested)&&gate.TryEnter())entered=true;
            }
            if(!entered)
            {
                try{RemsupProtocol.WriteLine(transport??c.GetStream(),relay&&RelaySuspended?"ERR unavailable":BusyResponse());}catch{}
                return;
            }
        }
        try
        {
            lock(sync)
            {
                if(gen!=generation||(!relay&&!lanReady)||relaySuspended||token.IsCancellationRequested)return;
                if(!screen)
                {
                    active=true;
                    relaySession=relay;
                    activeSocket=c;
                }
                else if(!relay)activeScreenSocket=c;
            }
            var stream=transport??c.GetStream();string header=RemsupProtocol.ReadLine(stream,100000);
            lock(sync)if(gen!=generation||(!relay&&!lanReady)||relaySuspended||token.IsCancellationRequested)return;
            if(screen)
            {
                var handler=ScreenHandler;
                if(header!="REMSUPSCREEN1")RemsupProtocol.WriteLine(stream,"REMSUPSCREEN1 ERROR wrong-channel");
                else if(handler==null)RemsupProtocol.WriteLine(stream,"REMSUPSCREEN1 ERROR screen-disabled");
                else handler(stream,token);
            }
            else if(header=="REMSUPDIAG1")
            {
                var handler=DiagnosticsHandler;
                if(handler==null)RemsupProtocol.WriteLine(stream,"REMSUPDIAG1 ERROR unavailable");
                else {
                    try {string value=handler();if(value==null||System.Text.Encoding.UTF8.GetByteCount(value)>16384)throw new IOException();RemsupProtocol.WriteLine(stream,"REMSUPDIAG1 "+RemsupProtocol.Encode(value));}
                    catch {RemsupProtocol.WriteLine(stream,"REMSUPDIAG1 ERROR unavailable");}
                }
            }
            else if(header!=null&&header.StartsWith("REMSUP1 EXEC ",StringComparison.Ordinal))
            {
                Publish(gen,relayState,relay?"Active relay session":"Active LAN session");
                CommandExecutor.HandleRequest(stream,header,token);
            }
            else RemsupProtocol.WriteLine(stream,"REMSUP1 ERROR wrong-channel");
        }
        finally
        {
            lock(sync)
            {
                if(!screen&&gen==generation&&activeSocket==c)
                {
                    active=false;
                    relaySession=false;
                    activeSocket=null;
                }
                if(screen&&gen==generation&&activeScreenSocket==c)activeScreenSocket=null;
            }
            gate.Exit();
            if(!screen)Publish(gen,relay?SupportState.Reconnecting:relayState,"Session ended");
        }
    }
    bool RelaySuspended { get { lock(sync)return relaySuspended; } }
    void LanLoop(TcpListener lan,string id,string password,int gen,CancellationToken token)
    {
        var failures=new Dictionary<string,DateTime>();
        var attempts=new Dictionary<string,int>();
        while(!token.IsCancellationRequested)
        {
            TcpClient c=null;
            Stream transport=null;
            try
            {
                c=lan.AcceptTcpClient();
                Track(c,gen,true);
                c.ReceiveTimeout=10000;
                c.SendTimeout=10000;
                if(!LanAccessPolicy.SameSubnet(((IPEndPoint)lan.LocalEndpoint).Address,((IPEndPoint)c.Client.RemoteEndPoint).Address))
                {
                    c.Close();
                    continue;
                }
                string remote=((IPEndPoint)c.Client.RemoteEndPoint).Address.ToString();
                DateTime until;
                // Single bounded handshake at a time; capped table and cooldown avoid unlimited resources.
                if(failures.TryGetValue(remote,out until)&&until>DateTime.UtcNow)
                {
                    c.Close();
                    continue;
                }
                var stream=transport=LanTls.Accept(c,lanIdentity(),token);
                var p=RemsupProtocol.ReadLine(stream,512).Split(new[]{' '},StringSplitOptions.RemoveEmptyEntries);
                bool screen=p.Length==5&&p[4]=="SCREEN";
                bool command=p.Length==4;
                if((!command&&!screen)||p[0]!="OPERATOR"||p[1]!=RemsupProtocol.NormalizeId(id)||p[2]!=password||!RemsupProtocol.ValidKey(p[3]))
                {
                    int count;
                    attempts.TryGetValue(remote,out count);
                    if(attempts.Count>=128)attempts.Clear();
                    attempts[remote]=count+1;
                    if(count+1>=3)
                    {
                        if(failures.Count>=128)failures.Clear();
                        failures[remote]=DateTime.UtcNow.AddMinutes(1);
                        attempts.Remove(remote);
                    }
                    RemsupProtocol.WriteLine(stream,"ERR no-agent");
                    continue;
                }
                if(screen&&ScreenHandler==null)
                {
                    RemsupProtocol.WriteLine(stream,"ERR no-agent");
                    continue;
                }
                var gate=screen?screenGate:commandGate;
                bool entered=false;
                lock(sync)
                {
                    if(!settingsReserved&&gen==generation&&lanReady&&!relaySuspended&&!token.IsCancellationRequested)entered=gate.TryEnter();
                }
                if(!entered)
                {
                    RemsupProtocol.WriteLine(stream,RelaySuspended?"ERR unavailable":BusyResponse());
                    continue;
                }
                try
                {
                    RemsupProtocol.WriteLine(stream,"OK");
                }
                catch
                {
                    gate.Exit();
                    throw;
                }
                var accepted=c;
                var acceptedTransport=transport;
                transport=null;
                c=null;
                new Thread(()=>
                {
                    try
                    {
                        Serve(accepted,gen,false,token,screen,true,acceptedTransport);
                    }
                    catch
                    {
                    }
                    finally
                    {
                        try{acceptedTransport.Dispose();}finally{accepted.Close();Untrack(accepted);}
                    }
                }
                )
                {
                    IsBackground=true
                }
                .Start();
            }
            catch
            {
                if(token.IsCancellationRequested||!lanReady)break;
            }
            finally
            {
                try{if(transport!=null)transport.Dispose();}
                finally{if(c!=null)
                {
                    c.Close();
                    Untrack(c);
                }}
            }
        }
    }
    public void Stop()
    {
        lock(lifecycle) StopCore();
    }
    internal bool TrySuspendRelayForUpdate()
    {
        return TrySuspendRelayForUpdate(null);
    }
    internal bool TrySuspendRelayForUpdate(Func<bool> screenIsPendingOrGranted)
    {
        lock(lifecycle)
        lock(sync)
        {
            if(relaySuspended)return true;
            if(commandGate.IsBusy||screenGate.IsBusy||active||activeScreenSocket!=null||screenIsPendingOrGranted!=null&&screenIsPendingOrGranted())return false;
            relaySuspended=true;
            relayEpoch++;
            relayCommandReady=relayScreenReady=false;
            if(relayCancellation!=null){relayCancellation.Cancel();relayCancellation.Dispose();relayCancellation=null;}
            foreach(var client in relaySockets)try{client.Close();}catch{}
            relaySockets.Clear();
            if(lanPolicyWatch!=null){lanPolicyWatch.Dispose();lanPolicyWatch=null;}
            if(lanCancellation!=null){lanCancellation.Cancel();lanCancellation.Dispose();lanCancellation=null;}
            if(listener!=null){listener.Stop();listener=null;}
            foreach(var client in lanSockets)try{client.Close();}catch{}
            lanSockets.Clear();sockets.Clear();lanReady=false;
            relayState=SupportState.Stopped;snapshot=new SupportSnapshot(generation,SupportState.Stopped,false,false,"Relay and LAN listeners paused for update.");
            var callback=Changed;var value=snapshot;if(callback!=null)ThreadPool.QueueUserWorkItem(_=>callback(value));
            return true;
        }
    }
    internal void ResumeRelayAfterUpdate()
    {
        lock(lifecycle)
        {
            string relay,id,password,pin;IPAddress address;int lanPort;bool fixtureRelay;
            lock(sync)
            {
                if(!relaySuspended)return;
                if(commandGate.IsBusy||screenGate.IsBusy)throw new InvalidOperationException("A remote session is still active; relay resume is deferred.");
                relay=configuredRelay;id=configuredId;password=configuredPassword;pin=configuredPin;address=configuredLanAddress;lanPort=configuredLanPort;fixtureRelay=configuredFixtureRelay;
            }
            StartCore(relay,id,password,address,lanPort,pin,fixtureRelay);
        }
    }
    void StopCore()
    {
        Action<SupportSnapshot> callback;
        SupportSnapshot value;
        lock(sync)
        {
            generation++;
            lanWarning="";
            if(lanPolicyWatch!=null)
            {
                lanPolicyWatch.Dispose();
                lanPolicyWatch=null;
            }
            if(cancellation!=null)
            {
                cancellation.Cancel();
                cancellation=null;
            }
            if(relayCancellation!=null){relayCancellation.Cancel();relayCancellation.Dispose();relayCancellation=null;}
            if(lanCancellation!=null)
            {
                lanCancellation.Cancel();
                lanCancellation.Dispose();
                lanCancellation=null;
            }
            if(listener!=null)
            {
                listener.Stop();
                listener=null;
            }
            foreach(var c in sockets)c.Close();
            sockets.Clear();
            relaySockets.Clear();
            lanSockets.Clear();
            lanReady=false;
            active=false;
            activeSocket=null;
            activeScreenSocket=null;
            relaySession=false;
            relaySuspended=false;relayCommandReady=relayScreenReady=false;
            relayState=SupportState.Stopped;
            snapshot=new SupportSnapshot(generation,SupportState.Stopped,false,false,"Stopped");
            value=snapshot;
            callback=Changed;
        }
        if(callback!=null)callback(value);
    }
    public void Dispose()
    {
        Stop();
    }
}
