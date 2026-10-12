using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Captures only this explicitly created visual; never a monitor/desktop/other app.
internal sealed class SyntheticScreenSurface:IScreenSurface,IDisposable
{
    readonly Canvas canvas=new Canvas{Width=640,Height=360,Background=Brushes.WhiteSmoke};readonly Button button=new Button{Width=160,Height=70,Content="Test button · 0"};readonly Window window;
    int clicks;byte[] cached;internal bool Locked{get;set;}internal int GeometryVersion=1;internal Func<Guid> CurrentLease;internal Func<bool> CanInput;
    internal SyntheticScreenSurface()
    {
        var title=new TextBlock{Text="REMSUP SYNTHETIC TEST APPLICATION\nOnly this visual is captured. No desktop capture or OS input.",FontSize=16,Foreground=Brushes.Black};Canvas.SetLeft(title,24);Canvas.SetTop(title,24);canvas.Children.Add(title);
        Canvas.SetLeft(button,240);Canvas.SetTop(button,140);button.Background=Brushes.IndianRed;button.Click+=(_,__)=>{clicks++;button.Content="Test button · "+clicks;button.Background=clicks%2==1?Brushes.LightGreen:Brushes.IndianRed;};canvas.Children.Add(button);
        window=new Window{Title="Remsup synthetic test application",Content=canvas,SizeToContent=SizeToContent.WidthAndHeight,ResizeMode=ResizeMode.NoResize};window.Show();Render();
    }
    public bool IsInteractive=>!Locked;
    public ScreenGeometry[] Monitors()=>new[]{Geometry("synthetic")};
    public ScreenGeometry Geometry(string id)=>id=="synthetic"?new ScreenGeometry(id,GeometryVersion,0,0,640,360):throw new InvalidOperationException("monitor-unavailable");
    public ScreenImage Capture(ScreenGeometry geometry)
    {
        return new ScreenImage((byte[])System.Threading.Volatile.Read(ref cached).Clone(),640,360);
    }
    void Render(){canvas.UpdateLayout();var bitmap=new RenderTargetBitmap(640,360,96,96,PixelFormats.Pbgra32);bitmap.Render(canvas);bitmap.Freeze();var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var bytes=new MemoryStream();encoder.Save(bytes);System.Threading.Volatile.Write(ref cached,bytes.ToArray());}
    public bool IsProtectedPoint(ScreenPoint point)=>point.X<240||point.X>=400||point.Y<140||point.Y>=210;
    public void Mouse(string action,ScreenPoint point,int scrollSteps,Action authorize){Guid lease=CurrentLease?.Invoke()??Guid.Empty;int version=GeometryVersion;canvas.Dispatcher.BeginInvoke(new Action(()=>{if(Locked||GeometryVersion!=version||CurrentLease?.Invoke()!=lease||CanInput?.Invoke()!=true)return;try{authorize();}catch(InvalidOperationException){return;}button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Render();}));}
    internal string Typed="",LastKey="";
    public void Keyboard(string text,string key,ScreenGeometry geometry,Action authorize){canvas.Dispatcher.BeginInvoke(new Action(()=>{if(Locked||!geometry.Matches(Geometry("synthetic")))return;try{authorize();}catch(InvalidOperationException){return;}Typed+=text;LastKey=key;}));}
    public void Dispose(){window.Close();}
}
