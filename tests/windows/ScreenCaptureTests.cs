using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
internal static class ScreenCaptureTests
{
    sealed class SyntheticSurface:IScreenSurface
    {
        public bool Interactive=true,Protected;public int Captures,Inputs,Version=1;public bool Changed;public Action BeforeProtected,BeforeMouse;
        public bool IsInteractive{get{return Interactive;}}
        public ScreenGeometry[] Monitors(){return new[]{Geometry("synthetic")};}
        public ScreenGeometry Geometry(string monitor){if(monitor!="synthetic")throw new InvalidOperationException("monitor-unavailable");return new ScreenGeometry(monitor,Version,-100,0,100,100);}
        public ScreenImage Capture(ScreenGeometry geometry)
        {
            Captures++;
            using(var bitmap=new Bitmap(100,100))using(var graphics=Graphics.FromImage(bitmap))using(var bytes=new MemoryStream())
            {
                graphics.Clear(Changed?Color.Green:Color.Red);bitmap.Save(bytes,ImageFormat.Png);return new ScreenImage(bytes.ToArray(),100,100);
            }
        }
        public bool IsProtectedPoint(ScreenPoint point){if(BeforeProtected!=null)BeforeProtected();return Protected;}
        public string Typed="",Key="";
        public void Keyboard(string text,string key,ScreenGeometry geometry,Action authorize){authorize();Typed+=text;Key=key;}
        public void Mouse(string action,ScreenPoint point,int steps,Action authorize){if(BeforeMouse!=null)BeforeMouse();authorize();Inputs++;Changed=true;}
    }
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    static void Refused(Action operation){try{operation();throw new Exception("Expected rejection");}catch(InvalidOperationException){}}
    public static int Main(){try{Run();return 0;}catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
    public static void Run()
    {
        long now=0;var provider=new SyntheticSurface();var access=new ScreenAccess(provider,()=>now);
        Refused(()=>access.Capture("test",Guid.NewGuid()));Check(provider.Captures==0,"no capture without permission");
        Guid id=access.Request("test","synthetic",true);Refused(()=>access.Capture("test",id));
        access.GrantLocal(id,true);var first=access.Capture("test",id);
        using(var bytes=new MemoryStream(first.Image.Png))using(var bitmap=new Bitmap(bytes))Check(bitmap.GetPixel(50,50).R==255,"synthetic PNG red");
        Refused(()=>access.Capture("test",id));Check(provider.Captures==1,"rate checked before provider");
        access.Mouse("test",id,first.Frame.FrameId,"click",50,50,0);Check(provider.Inputs==1,"synthetic sink receives input");
        now=200;access.Keyboard("test",id,first.Frame.FrameId,1,"Тест 😀 test1_remote.txt",null);Check(provider.Typed=="Тест 😀 test1_remote.txt","synthetic Unicode sink");
        now=400;access.Keyboard("test",id,first.Frame.FrameId,1,null,"ctrl+shift+s");Check(provider.Key=="ctrl+shift+s","bounded save-as key sink");
        Refused(()=>access.Keyboard("test",id,first.Frame.FrameId,1,null,"win+r"));
        Refused(()=>access.Keyboard("test",id,first.Frame.FrameId,1,new string('x',129),null));
        Refused(()=>access.Keyboard("test",id,first.Frame.FrameId,1,"\uD800",null));
        Refused(()=>access.Keyboard("test",id,first.Frame.FrameId,1,"\0",null));
        Refused(()=>access.Keyboard("test",id,Guid.NewGuid(),1,"invalid",null));
        now=1000;var second=access.Capture("test",id);
        using(var bytes=new MemoryStream(second.Image.Png))using(var bitmap=new Bitmap(bytes))Check(bitmap.GetPixel(50,50).G==128,"synthetic PNG changed");
        now=1200;Refused(()=>access.Keyboard("test",id,second.Frame.FrameId,2,"invalid",null));
        provider.Protected=true;Refused(()=>access.Mouse("test",id,second.Frame.FrameId,"click",50,50,0));Check(provider.Inputs==1,"protected controls refused");
        provider.Protected=false;provider.Interactive=false;now=2000;Refused(()=>access.Capture("test",id));Check(provider.Captures==2&&!access.Snapshot.CanView,"desktop unavailable revokes before capture");
        provider.Interactive=true;id=access.Request("test","synthetic",true);access.GrantLocal(id,true);access.Capture("test",id);
        provider.Version++;now=3000;Refused(()=>access.Capture("test",id));Check(provider.Captures==3,"topology checked before capture");
        id=access.Request("test","synthetic",true);access.GrantLocal(id,true);access.Stop();Refused(()=>access.Capture("test",id));Check(provider.Captures==3,"stop prevents capture");
        id=access.Request("test","synthetic",true);access.GrantLocal(id,true);now=602000;var deadlineFrame=access.Capture("test",id);
        provider.BeforeProtected=()=>now=603000;Refused(()=>access.Mouse("test",id,deadlineFrame.Frame.FrameId,"click",50,50,0));Check(provider.Inputs==1,"lease expiry during protected check blocks provider input");
        provider.BeforeProtected=null;id=access.Request("test","synthetic",true);access.GrantLocal(id,true);now=1202000;deadlineFrame=access.Capture("test",id);
        provider.BeforeMouse=()=>now=1203000;Refused(()=>access.Mouse("test",id,deadlineFrame.Frame.FrameId,"click",50,50,0));Check(provider.Inputs==1,"submission authorization blocks expiry inside provider");
        provider.BeforeMouse=null;id=access.Request("test","synthetic",false);access.GrantLocal(id,false);now+=1000;var view=access.Capture("test",id);Refused(()=>access.Keyboard("test",id,view.Frame.FrameId,1,"invalid",null));
        access.Stop();Refused(()=>access.Keyboard("test",id,view.Frame.FrameId,1,null,"enter"));
        id=access.Request("test","synthetic",true);access.GrantLocal(id,true);now+=1000;var keyboardFrame=access.Capture("test",id);now+=31000;Refused(()=>access.Keyboard("test",id,keyboardFrame.Frame.FrameId,provider.Version,"invalid",null));
        now+=570000;Refused(()=>access.Keyboard("test",id,keyboardFrame.Frame.FrameId,provider.Version,null,"enter"));
        ScreenKeyboard.Validate("test",null);foreach(string key in new[]{"ctrl+shift+s","ctrl+a","enter","escape","tab"})ScreenKeyboard.Validate(null,key);
        Console.WriteLine("PASS memory-only synthetic PNG -> input sink -> changed PNG; protected/locked/changed/stopped providers not invoked");
    }
}
