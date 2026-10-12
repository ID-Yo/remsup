using System;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
internal static class GuiHelp {
 static readonly ConditionalWeakTable<Control,ToolTip> tips=new ConditionalWeakTable<Control,ToolTip>();
 internal static void Attach(Control root){var tip=new ToolTip{AutoPopDelay=12000,InitialDelay=400,ReshowDelay=100,ShowAlways=true};tips.Add(root,tip);root.Disposed+=(s,e)=>tip.Dispose();}
 internal static void Set(Control control,string text){control.AccessibleDescription=text;for(Control root=control;root!=null;root=root.Parent){ToolTip tip;if(tips.TryGetValue(root,out tip)){tip.SetToolTip(control,text);return;}}}
 internal static void Buttons(Control root){foreach(Control child in root.Controls){if(child is ButtonBase)Set(child,string.IsNullOrEmpty(child.AccessibleDescription)?ActionText(child.Text):child.AccessibleDescription);Buttons(child);}}
 static string ActionText(string text){switch(text){case "Close":case "Exit":return "Exit the application and stop support. Minimize keeps support running.";case "Settings":return "Configure relay, optional LAN, password and Windows startup. Saved support settings apply while this app is open.";case "Copy ID":return "Copy this computer's Device ID for an authorized operator; it is not an IP address.";case "Copy":return "Copy the current OTP/password for an authorized operator. Never share it publicly.";case "Show":case "Hide":return "Reveal or hide the password field while authorized.";case "Save and close":return "Save these settings and apply them to support in the running application.";case "Clear":return "Clear and save the receiving permanent password; the app uses a fresh OTP.";default:return text;}}
}
