using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
internal static class ImageLifetimeTests {
 internal static void Run(){
  string root=Path.Combine(Path.GetTempPath(),"RemsupImages-"+Guid.NewGuid().ToString("N"));bool dark=Appearance.Dark;
  try{
   using(var form=new DesktopForm(new DeviceStore(Path.Combine(root,"devices-v2.json")),new RemsupSupportPanel(()=>new RemsupSettings(),()=>new SupportStatus{DeviceId="111222333"}),()=>new RelayConfiguration("relay.example.test",8443,RelayTls.DefaultPin),false)){
    var images=(ImageList)typeof(DesktopForm).GetField("cardImages",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form);
    if(images.Handle==IntPtr.Zero)throw new Exception("Deferred native image handle missing");
    form.Show();Application.DoEvents();var list=(ListView)typeof(DesktopForm).GetField("devices",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form);
    for(int i=0;i<12;i++){Appearance.Dark=i%2==1;Appearance.Apply(form);images.ColorDepth=i%2==0?ColorDepth.Depth24Bit:ColorDepth.Depth32Bit; if(images.Images.Count==0)images.Images.Add((Bitmap)typeof(DesktopForm).GetField("cardImage",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(form));typeof(Control).GetMethod("RecreateHandle",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(list,null);form.Size=i%2==0?form.MinimumSize:new Size(1120,760);form.Refresh();Application.DoEvents();if(images.Handle==IntPtr.Zero)throw new Exception("Recreated native image handle missing");using(var copy=images.Images[0]){if(copy.Width<1)throw new Exception("Invalid native image copy");}}
    form.Close();
   }
   Console.WriteLine("PASS deferred ImageList handle and 12 native handle/theme/resize/repaint lifetimes");
  }finally{Appearance.Dark=dark;if(Directory.Exists(root))Directory.Delete(root,true);}
 }
}
