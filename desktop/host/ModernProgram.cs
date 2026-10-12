using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
internal static class ModernProgram {
 internal static ApplicationInstance CurrentInstance;
 [STAThread] static void Main(string[] args){
  try{
   RuntimeSetup.Initialize();
   if(Array.IndexOf(args,"--uninstall")>=0){if(UninstallManager.RequestExisting())return;var uninstallApp=new Application();var choice=new NativeDialogs(null).Uninstall();if(choice!=null)UninstallManager.Schedule(new UserInstallation(),choice.Value);return;}

   if(Array.IndexOf(args,"--licenses")>=0){var licenseApp=new Application();BundledNotices.Show(null,false);return;}
   if(args.Length==1&&(args[0]=="--lan-cleanup"||args[0]=="--lan-remove-all")){if(!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))throw new InvalidOperationException("LAN cleanup needs visible administrator authorization.");if(args[0]=="--lan-cleanup")LanAccessPolicy.RemoveObsoleteRules();else LanAccessPolicy.RemoveRule(null,0);return;}
   if(args.Length==3&&(args[0]=="--lan-prepare"||args[0]=="--lan-remove")){if(!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))throw new InvalidOperationException("LAN helper needs visible administrator authorization.");var ip=System.Net.IPAddress.Parse(args[1]);int port=int.Parse(args[2]);if(args[0]=="--lan-prepare")LanAccessPolicy.AddRule(ip,port);else LanAccessPolicy.RemoveRule(ip,port);return;}
   var options=new HostOptions(args);if(options.UpdatePipe!=null){using var timeout=new System.Threading.CancellationTokenSource(10000);options.UpdateSecret=UpdateBootstrap.ReceiveAsync(options.UpdatePipe,timeout.Token).GetAwaiter().GetResult();}using var instance=options.Fixture?null:new ApplicationInstance(ApplicationInstance.DefaultName,true);if(instance!=null&&!instance.IsOwner)return;if(options.UpdatePipe==null&&!options.Fixture)UpdateManager.CleanupPreviousImages(Environment.ProcessPath);CurrentInstance=instance;HostUpdateHandoff.Instance=instance;using var cleanupCancellation=new System.Threading.CancellationTokenSource();if(!options.Fixture){UpdateManager.CleanupAgedBackup(Environment.ProcessPath);_=UpdateManager.RetryCommittedCleanupAsync(Environment.ProcessPath,cleanupCancellation.Token);}using var uninstallRequest=options.Fixture?null:new System.Threading.EventWaitHandle(false,System.Threading.EventResetMode.AutoReset,UninstallManager.EventName);var app=new Application();instance?.OnActivate(()=>app.Dispatcher.BeginInvoke(new Action(()=>{if(app.MainWindow is ModernWindow window)window.Restore();})));System.Threading.RegisteredWaitHandle uninstallWait=null;if(uninstallRequest!=null)uninstallWait=System.Threading.ThreadPool.RegisterWaitForSingleObject(uninstallRequest,(_,timedOut)=>app.Dispatcher.BeginInvoke(new Action(()=>{if(app.MainWindow is ModernWindow window)window.RequestUninstall();})),null,System.Threading.Timeout.Infinite,false);app.Exit+=(_,_)=>uninstallWait?.Unregister(null);app.Exit+=(_,_)=>cleanupCancellation.Cancel();app.ShutdownMode=ShutdownMode.OnMainWindowClose;app.SessionEnding+=(_,e)=>{if(app.MainWindow is ModernWindow window)window.RequestExit();if(app.MainWindow is ModernWindow activeWindow&&!activeWindow.IsExiting)e.Cancel=true;};app.Run(new ModernWindow(options));
  }catch(Exception ex){Environment.ExitCode=1;MessageBox.Show(ex.Message,"Remsup startup",MessageBoxButton.OK,MessageBoxImage.Warning);}
 }
}
internal sealed class ModernWindow:Window {
 EmbeddedUi assets;readonly HostOptions options;readonly WebView2 view=new WebView2();readonly HostController host;readonly BridgeGate gate=new BridgeGate();readonly System.Threading.CancellationTokenSource reviewCancellation=new System.Threading.CancellationTokenSource();NativeTray tray;readonly Stopwatch clock=Stopwatch.StartNew();bool closing,exitRequested,fatal,navigated,ready,dispatching;int generation;
 internal ModernWindow(HostOptions opts){
  options=opts;Title=opts.Client?"Remsup Support":"Remsup";Width=opts.Client?520:900;Height=opts.Client?620:600;MinWidth=Math.Min(opts.Client?420:640,SystemParameters.WorkArea.Width);MinHeight=Math.Min(opts.Client?400:420,SystemParameters.WorkArea.Height);WindowStartupLocation=WindowStartupLocation.CenterScreen;
  if(opts.Fixture&&opts.Scenario.Contains("compact")){Width=opts.Client?420:640;Height=opts.Client?400:420;}
  Width=Math.Min(Width,SystemParameters.WorkArea.Width);Height=Math.Min(Height,SystemParameters.WorkArea.Height);
  using(var stream=System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("Remsup.App.ico")){var icon=System.Windows.Media.Imaging.BitmapFrame.Create(stream,System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);icon.Freeze();Icon=icon;}
  host=new HostController(options,new NativeDialogs(this,()=>host!=null&&host.Dark));host.Changed+=()=>Dispatcher.BeginInvoke(new Action(Publish));host.NoticesRequested+=()=>BundledNotices.Show(this,host.Dark);host.CloseRequested+=()=>Dispatcher.BeginInvoke(new Action(RequestExit));host.MinimizeRequested+=()=>WindowState=WindowState.Minimized;
  SourceInitialized+=(_,__)=>{tray=new NativeTray(this,Restore,()=>{Restore();view.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new{type="navigate",view="settings"}));},RequestExit);tray.Show();};
  StateChanged+=(_,__)=>{if(WindowState==WindowState.Minimized){if(tray!=null&&tray.Show())Hide();else{Restore();MessageBox.Show(this,"Windows could not show the Remsup tray icon. The support window remains visible.","Remsup",MessageBoxButton.OK,MessageBoxImage.Warning);}}};
  Content=new TextBlock{Text="Opening Remsup…",Margin=new Thickness(24),FontSize=20};Loaded+=async(_,__)=>await InitializeAsync();Closing+=(_,e)=>{if(!exitRequested){e.Cancel=true;WindowState=WindowState.Minimized;return;}DisposeOwned();};
 }
 async System.Threading.Tasks.Task InitializeAsync(){
  try{
   assets=new EmbeddedUi();Content=view;var environment=await CoreWebView2Environment.CreateAsync(options.RuntimeFolder,options.UserData);await view.EnsureCoreWebView2Async(environment);if(closing)return;
   var core=view.CoreWebView2;
   core.Settings.AreHostObjectsAllowed=false;core.Settings.AreDevToolsEnabled=false;core.Settings.AreDefaultContextMenusEnabled=false;core.Settings.AreDefaultScriptDialogsEnabled=false;core.Settings.IsStatusBarEnabled=false;
   core.NavigationStarting+=(_,e)=>{if(e.Uri!=BridgePolicy.Document){e.Cancel=true;return;}if(navigated){e.Cancel=true;FailRenderer();return;}navigated=true;};core.NavigationCompleted+=(_,e)=>{if(!fatal&&!closing)core.PostWebMessageAsJson(JsonSerializer.Serialize(new{type="context",context=gate.Context}));};core.FrameNavigationStarting+=(_,e)=>e.Cancel=true;core.NewWindowRequested+=(_,e)=>e.Handled=true;core.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;core.DownloadStarting+=(_,e)=>e.Cancel=true;
   core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);core.WebResourceRequested+=(_,e)=>{if(e.Request.Method=="GET"&&assets.TryGet(e.Request.Uri,out var asset))e.Response=environment.CreateWebResourceResponse(new MemoryStream(asset.Bytes,false),200,"OK","Content-Type: "+asset.Mime+"\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");else e.Response=environment.CreateWebResourceResponse(new MemoryStream(),403,"Blocked","");};
   core.ProcessFailed+=(_,e)=>FailRenderer();
   core.WebMessageReceived+=async(_,e)=>await DispatchAsync(e);
   core.Navigate(BridgePolicy.Document);
  }catch(Exception ex){
   host.Dispose();ShowStopped("Remsup could not open its renderer. Receiving support is stopped.\n\nInstall the Microsoft Edge WebView2 Evergreen Runtime using your approved offline installer, then reopen Remsup. Nothing is installed automatically.\n\n"+ex.Message);
   if(options.Artifacts!=null){Directory.CreateDirectory(options.Artifacts);File.WriteAllText(Path.Combine(options.Artifacts,"failure.json"),JsonSerializer.Serialize(new{type=ex.GetType().Name,supportStarted=false,message=ex.Message}));await System.Threading.Tasks.Task.Delay(700);RequestExit();}
  }
 }
 async System.Threading.Tasks.Task DispatchAsync(CoreWebView2WebMessageReceivedEventArgs e){
  if(fatal||closing)return;string request=null;try{
   string raw=e.WebMessageAsJson;if(System.Text.Encoding.UTF8.GetByteCount(raw)>65536)throw new ArgumentException("Message exceeds limit.");using var document=JsonDocument.Parse(raw,new JsonDocumentOptions{MaxDepth=12});var envelope=document.RootElement;
   if(envelope.EnumerateObject().Any(p=>p.Name!="version"&&p.Name!="requestId"&&p.Name!="kind"&&p.Name!="payload"&&p.Name!="context"&&p.Name!="sequence")||envelope.EnumerateObject().GroupBy(p=>p.Name).Any(g=>g.Count()>1))throw new ArgumentException("Unknown or duplicate message field.");
   if(envelope.GetProperty("version").GetInt32()!=1)throw new ArgumentException("Unsupported bridge version.");request=envelope.GetProperty("requestId").GetString();if(!Guid.TryParse(request,out _))throw new ArgumentException("Invalid correlation ID.");string kind=envelope.GetProperty("kind").GetString();
   if(!BridgePolicy.Authorize(e.Source,kind,options.Client,System.Text.Encoding.UTF8.GetByteCount(raw)))throw new ArgumentException("Origin, capability or action rejected.");
   gate.Accept(envelope.GetProperty("context").GetString(),request,envelope.GetProperty("sequence").GetInt64(),kind);
   // Actions execute on the owner dispatcher; awaited connection work yields so Cancel remains reachable.
   object result=await host.HandleAsync(kind,envelope.GetProperty("payload"));if(closing||fatal)return;view.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new{type="reply",requestId=request,ok=true,result,generation=++generation}));Publish();
   if(kind=="ready"&&!ready){ready=true;if(options.Artifacts!=null){Directory.CreateDirectory(options.Artifacts);File.WriteAllText(Path.Combine(options.Artifacts,"ready.json"),JsonSerializer.Serialize(new{readyMs=clock.Elapsed.TotalMilliseconds,runtime=view.CoreWebView2.Environment.BrowserVersionString,pid=Environment.ProcessId,fixture=true}));}}
   if(kind=="snapshot"&&options.Artifacts!=null&&ready&&!dispatching){dispatching=true;await System.Threading.Tasks.Task.Delay(350,reviewCancellation.Token);if(closing||fatal)return;await FixtureReview.Capture(view,options.Artifacts,"renderer",host.Snapshot());File.WriteAllText(Path.Combine(options.Artifacts,"snapshot.json"),JsonSerializer.Serialize(host.Snapshot()));if(options.FixtureScript!=null){try{await FixtureReview.Run(view,options,()=>host.Snapshot(),reviewCancellation.Token);}catch(OperationCanceledException){return;}catch(Exception failure){if(closing||fatal)return;File.WriteAllText(Path.Combine(options.Artifacts,"review-failure.txt"),failure.Message);Environment.ExitCode=1;}if(!closing)RequestExit();}dispatching=false;}
  }catch(Exception ex){if(fatal||closing)return;string message=HostController.FriendlyError(ex);host.Fail(message);if(request!=null)view.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new{type="reply",requestId=request,ok=false,error=message}));}
 }
 void FailRenderer(){if(closing||fatal)return;fatal=true;gate.Invalidate();reviewCancellation.Cancel();if(host.UpdateHandoffActive){Content=new TextBlock{Text="Renderer stopped. Waiting for the update to restore or verify relay registration, then Remsup will close.",Margin=new Thickness(24),TextWrapping=TextWrapping.Wrap};host.UpdateSettled+=()=>{host.Dispose();RequestExit();};return;}host.Dispose();Dispatcher.BeginInvoke(new Action(()=>{if(closing)return;view.Dispose();ShowStopped("Renderer stopped or reloaded. Receiving support has stopped. Exit using the tray, then reopen Remsup.");}));}
 void Publish(){if(closing||fatal||view.CoreWebView2==null)return;view.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new{type="state",generation=++generation,state=host.Snapshot()}));}
 void ShowStopped(string message){var body=new StackPanel{Margin=new Thickness(24)};var lamp=new TextBlock{Text="● No operational transport",Foreground=System.Windows.Media.Brushes.Firebrick,FontSize=13,Margin=new Thickness(0,0,0,16)};System.Windows.Automation.AutomationProperties.SetName(lamp,"Red: no operational transport; support stopped");body.Children.Add(lamp);body.Children.Add(new TextBlock{Text=message,TextWrapping=TextWrapping.Wrap,FontSize=16});Content=body;}
 internal void Restore(){Show();WindowState=WindowState.Normal;tray?.Show();Activate();}
 internal async void RequestUninstall(){Restore();try{await host.HandleAsync("installation.uninstall",JsonSerializer.SerializeToElement(new{}));}catch(Exception error){host.Fail(error.Message);}}
 internal bool IsExiting=>exitRequested;
 internal void RequestExit(){if(closing||!host.RequestUserExit())return;exitRequested=true;Close();}
 void DisposeOwned(){if(closing)return;closing=true;reviewCancellation.Cancel();gate.Invalidate();host.Dispose();tray?.Dispose();view.Dispose();}
}
