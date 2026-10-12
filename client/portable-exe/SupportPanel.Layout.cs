using System;
using System.Drawing;
using System.Windows.Forms;
internal sealed partial class RemsupSupportPanel {
 readonly FlowLayoutPanel footer=new FlowLayoutPanel{Dock=DockStyle.Bottom,AutoSize=true,WrapContents=true,Padding=new Padding(0,12,0,0)};
 readonly Panel optionalDetails=new Panel{Dock=DockStyle.Top,AutoSize=true,Visible=false};
 internal event Action ReadinessChanged;
 readonly bool syntheticSettings;
 readonly bool isLanFixture;
 readonly Button idCopy,passwordCopy;
 internal string StatusText {get{return status.Text;}}
 internal RemsupSupportPanel(Func<RemsupSettings> settingsProvider=null,Func<SupportStatus> statusProvider=null,bool isLanFixture=false){
  syntheticSettings=settingsProvider!=null;this.isLanFixture=isLanFixture;loadSettings=settingsProvider??SupportActions.LoadSettings;getStatus=statusProvider??SupportActions.GetStatus;AutoScaleMode=AutoScaleMode.Dpi;Font=new Font("Segoe UI",10);Padding=new Padding(24);Tag="sidebar";
  details.Dock=DockStyle.Fill;details.AutoScroll=true;Controls.Add(details);var stack=WorkspaceUi.Stack();details.Controls.Add(stack);
  WorkspaceUi.Add(stack,WorkspaceUi.Text("This computer",false,14));WorkspaceUi.Add(stack,WorkspaceUi.Text("Share only with your operator.",true));
  WorkspaceUi.Add(stack,WorkspaceUi.Text("DEVICE ID",true));deviceId.ReadOnly=true;deviceId.Dock=DockStyle.Top;deviceId.Font=new Font("Segoe UI",18,FontStyle.Bold);deviceId.BorderStyle=BorderStyle.None;deviceId.AccessibleName="This computer's Device ID";WorkspaceUi.Add(stack,deviceId);idCopy=CopyAction(deviceId,"Copy ID");WorkspaceUi.Add(stack,idCopy);
  WorkspaceUi.Add(stack,WorkspaceUi.Text("CURRENT PASSWORD",true));password.ReadOnly=true;password.Dock=DockStyle.Top;password.UseSystemPasswordChar=true;password.BorderStyle=BorderStyle.None;password.Font=new Font("Segoe UI",15);password.AccessibleName="This computer's receiving password";WorkspaceUi.Add(stack,password);passwordCopy=CopyAction(password,"Copy");WorkspaceUi.Add(stack,passwordCopy);
  var expand=WorkspaceUi.Button("Connection details",()=>{optionalDetails.Visible=!optionalDetails.Visible;});expand.AccessibleDescription="Show or hide this computer's relay/LAN details.";WorkspaceUi.Add(stack,expand);
  addresses.ReadOnly=true;addresses.Multiline=true;addresses.Height=72;addresses.Dock=DockStyle.Top;addresses.BorderStyle=BorderStyle.None;addresses.AccessibleName="This computer's LAN status";optionalDetails.Controls.Add(addresses);WorkspaceUi.Add(stack,optionalDetails);
  WorkspaceUi.Add(stack,WorkspaceUi.Button("Diagnostics",()=>{using(var dialog=new Form{Text="Support diagnostics",Size=new Size(680,430),StartPosition=FormStartPosition.CenterParent}){var copy=new TextBox{Multiline=true,ReadOnly=true,Dock=DockStyle.Fill,ScrollBars=ScrollBars.Vertical,Text=log.Text,AccessibleName="Observed receiving status history"};dialog.Controls.Add(copy);Appearance.Attach(dialog);dialog.ShowDialog(this);}}));
  var readiness=new FlowLayoutPanel{Dock=DockStyle.Bottom,AutoSize=true,WrapContents=true};lamp.Text="●";lamp.AutoSize=true;lamp.FlatStyle=FlatStyle.Flat;lamp.FlatAppearance.BorderSize=0;lamp.AccessibleName="Support readiness";lamp.GotFocus+=(s,e)=>readinessTip.Show(lamp.AccessibleDescription,lamp,0,lamp.Height,5000);status.AutoSize=true;status.MaximumSize=new Size(330,0);readiness.Controls.Add(lamp);readiness.Controls.Add(status);
  footer.Controls.Add(WorkspaceUi.Button("Settings",()=>ShowSettings()));themeButton.Text="Dark mode";themeButton.AutoSize=true;themeButton.MinimumSize=new Size(100,36);themeButton.AccessibleName="Switch color theme";themeButton.Click+=(s,e)=>{try{Appearance.Toggle();}catch(Exception ex){MessageBox.Show(this,ex.Message,"Appearance");}};footer.Controls.Add(themeButton);footer.Controls.Add(WorkspaceUi.Button("Close",()=>{var close=CloseRequested;if(close!=null)close();}));
  Controls.Add(footer);Controls.Add(readiness);var hint=WorkspaceUi.Text("Close stops support.\nMinimize keeps it running.",true);hint.Dock=DockStyle.Bottom;Controls.Add(hint);
  WorkspaceUi.Help(this);GuiHelp.Set(deviceId,"Device ID identifies THIS computer, not a relay address.");GuiHelp.Set(password,"Receiving OTP/password. Share only with your authorized operator.");GuiHelp.Set(addresses,"This computer's local addresses and opt-in LAN bind status.");
  supportHost.Changed+=snapshot=>{if(!IsDisposed&&IsHandleCreated)try{BeginInvoke((Action)(()=>{if(!IsDisposed&&snapshot.Generation==supportHost.Snapshot.Generation)RenderReadiness();}));}catch(InvalidOperationException){}};
  Load+=(s,e)=>{RefreshIdentity();RenderReadiness();};Appearance.Attach(this,()=>{themeButton.Text=Appearance.Dark?"Light mode":"Dark mode";RenderReadiness();});
 }
 Button CopyAction(TextBox value,string text){var copy=WorkspaceUi.Button(text,()=>{});copy.Click+=async(s,e)=>await CopyTextAsync(value,copy);return copy;}
 internal void UseSidebar(){footer.Visible=false;status.Parent.Visible=false;Padding=new Padding(24);}
 internal void UsePortableRows(){PairIdentity(deviceId,idCopy);PairIdentity(password,passwordCopy);}
 static void PairIdentity(TextBox value,Button copy){var stack=(TableLayoutPanel)value.Parent;int row=stack.GetRow(value);stack.Controls.Remove(value);stack.Controls.Remove(copy);stack.Controls.Add(WorkspaceUi.Pair(value,copy,110),0,row);}
 internal SettingsControl CreateSettings(){var editor=new SettingsControl(loadSettings,syntheticSettings?(Action<RemsupSettings>)(_=>{}):SupportActions.SaveSettings,syntheticSettings?(Action<bool>)(_=>{}):SupportActions.SetAutoStartEnabled);editor.SettingsSaved+=async()=>await ApplySettingsAsync();return editor;}
}
