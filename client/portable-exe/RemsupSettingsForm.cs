using System;
using System.Drawing;
using System.Windows.Forms;
internal sealed class RemsupSettingsForm:Form {
 internal event Action SettingsSaved;
 internal readonly SettingsControl Editor;
 readonly TextBox relayHostBox,relayPortBox,permanentPasswordBox;readonly Label saveStatus;
 internal RemsupSettingsForm(RemsupSettings suppliedSettings=null,Action<RemsupSettings> saveProvider=null,Action<bool> startupProvider=null){
  Text="RemSup Settings";Size=new Size(760,640);MinimumSize=new Size(650,520);StartPosition=FormStartPosition.CenterParent;Font=new Font("Segoe UI",10);AutoScaleMode=AutoScaleMode.Dpi;
  Editor=new SettingsControl(suppliedSettings==null?null:(Func<RemsupSettings>)(()=>suppliedSettings),saveProvider??(suppliedSettings==null?null:(Action<RemsupSettings>)(_=>{})),startupProvider??(suppliedSettings==null?null:(Action<bool>)(_=>{})));
  Editor.SettingsSaved+=()=>{var saved=SettingsSaved;if(saved!=null)saved();};Editor.CategoryPanel.Dock=DockStyle.Left;Editor.CategoryPanel.Width=170;Controls.Add(Editor);Controls.Add(Editor.CategoryPanel);
  relayHostBox=Editor.relayHostBox;relayPortBox=Editor.relayPortBox;permanentPasswordBox=Editor.permanentPasswordBox;saveStatus=Editor.saveStatus;
  var close=WorkspaceUi.Button("Close",()=>Close());close.Dock=DockStyle.Bottom;Controls.Add(close);WorkspaceUi.Help(this);GuiHelp.Set(close,"Close Settings; the app and support remain running.");Appearance.Attach(this);
 }
 void SaveChanges(RemsupSettings settings,bool close){if(Editor.SaveValues(settings)&&close)Close();}
}
