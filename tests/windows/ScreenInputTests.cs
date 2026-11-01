using System;
internal static class ScreenInputTests
{
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    static void Refused(Action action){try{action();throw new Exception("Expected frame refusal");}catch(InvalidOperationException){}}
    public static int Main(){try{Run();return 0;}catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
    public static void Run()
    {
        long now=0;var session=new ScreenSession(()=>now);var frames=new ScreenFrames(session,()=>now);
        var geometry=new ScreenGeometry("left",1,-1920,-200,1920,1080);
        Guid id=session.Request("test",geometry.MonitorId,true);session.GrantLocal(id,true);
        var first=frames.Record("test",id,geometry,960,540);
        var point=frames.ValidateMouse("test",id,first.FrameId,geometry,"click",0,0,0);
        Check(point.X==-1920&&point.Y==-200,"negative monitor origin");
        now=200;point=frames.ValidateMouse("test",id,first.FrameId,geometry,"right-click",959,539,0);
        Check(point.X==-1&&point.Y==879,"scaled endpoints");
        now=400;Refused(()=>frames.ValidateMouse("test",id,first.FrameId,geometry,"click",960,0,0));
        Refused(()=>frames.ValidateMouse("test",id,first.FrameId,geometry,"click",-1,0,0));
        Refused(()=>frames.ValidateMouse("test",id,first.FrameId,geometry,"drag",1,1,0));
        Refused(()=>frames.ValidateMouse("test",id,first.FrameId,geometry,"scroll",1,1,0));
        frames.ValidateMouse("test",id,first.FrameId,geometry,"scroll",1,1,-10);
        Refused(()=>frames.ValidateMouse("test",id,first.FrameId,geometry,"click",1,1,0));
        now=1000;var second=frames.Record("test",id,geometry,960,540);
        Refused(()=>frames.ValidateMouse("test",id,first.FrameId,geometry,"click",1,1,0));
        now=9000;frames.ValidateMouse("test",id,second.FrameId,geometry,"click",1,1,0);
        now=31001;Refused(()=>frames.ValidateMouse("test",id,second.FrameId,geometry,"click",1,1,0));
        var continuousFrame=frames.Record("test",id,geometry,960,540);
        // Continuous move testing: 15ms interval allowed, <15ms rejected
        now+=1000;
        var movePoint=frames.ValidateMouse("test",id,continuousFrame.FrameId,geometry,"move",100,100,0);
        Check(movePoint!=null,"move validation failed");
        now+=10;
        Refused(()=>frames.ValidateMouse("test",id,continuousFrame.FrameId,geometry,"move",105,105,0)); // 10ms < 15ms
        now+=15;
        movePoint=frames.ValidateMouse("test",id,continuousFrame.FrameId,geometry,"move",110,110,0);
        Check(movePoint!=null,"subsequent move validation failed");
        // Button down / up testing: 50ms interval required for down, up is never rate-blocked
        now+=50;
        var downPoint=frames.ValidateMouse("test",id,continuousFrame.FrameId,geometry,"down",110,110,0);
        Check(downPoint!=null,"down validation failed");
        now+=20;
        Refused(()=>frames.ValidateMouse("test",id,continuousFrame.FrameId,geometry,"down",110,110,0)); // 20ms < 50ms for down
        now+=10;
        var upPoint=frames.ValidateMouse("test",id,continuousFrame.FrameId,geometry,"up",110,110,0); // up release is never rate-blocked
        Check(upPoint!=null,"up validation failed");
        var third=frames.Record("test",id,geometry,960,540);
        var changed=new ScreenGeometry("left",2,-1920,-200,1920,1080);
        Refused(()=>frames.ValidateMouse("test",id,third.FrameId,changed,"click",1,1,0));
        Check(!session.Snapshot.CanView,"geometry change revokes");
        id=session.Request("test",geometry.MonitorId,false);session.GrantLocal(id,false);
        now+=1000;var fourth=frames.Record("test",id,geometry,960,540);
        Refused(()=>frames.ValidateMouse("test",id,fourth.FrameId,geometry,"click",1,1,0));
        session.Revoke();Refused(()=>frames.Record("test",id,geometry,960,540));
        Console.WriteLine("PASS synthetic frame scaling/negative-origin/bounds/actions/rate/age/geometry/permission");
    }
}
