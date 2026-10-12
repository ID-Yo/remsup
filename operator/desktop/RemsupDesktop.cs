using System;
using System.Windows.Forms;
internal static class RemsupDesktop {
 [STAThread] public static void Main(string[] args) {
  Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
  Application.ThreadException+=(s,e)=>MessageBox.Show(e.Exception.Message,"Remsup",MessageBoxButtons.OK,MessageBoxIcon.Error);
  try{if(RemsupClientProgram.HandleLanHelper(args))return;using(var instance=new ApplicationInstance())Application.Run(new DesktopForm());}
  catch(Exception ex){Environment.ExitCode=1;MessageBox.Show(ex.Message,"Remsup Desktop",MessageBoxButtons.OK,MessageBoxIcon.Error);}
 }
}
