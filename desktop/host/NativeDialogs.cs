using System;
using System.Windows;
using System.Windows.Controls;
internal interface IHostDialogs {
 InstallChoice Install(string path)=>null;
 bool? Uninstall()=>null;
 bool Confirm(string title,string text);
 string Credential(string title,string existing);
 void Reveal(string title,string value);
 void Copy(string value);
 void Error(string message);
}
internal sealed class NativeDialogs:IHostDialogs {
 readonly Window owner;readonly Func<bool> dark;
 internal NativeDialogs(Window window,Func<bool> theme=null){owner=window;dark=theme??(()=>false);}
 public bool? Uninstall(){
  var window=NativeTheme.Dialog(owner,"Uninstall Remsup",dark());var body=NativeTheme.Body();body.Children.Add(NativeTheme.Label("Stop Remsup and remove its installed app, startup entry and shortcuts. Settings and saved devices are kept by default."));var remove=new CheckBox{Content="Also delete settings and saved devices",IsChecked=false,MinHeight=44,Foreground=window.Foreground};body.Children.Add(remove);var row=NativeTheme.Row();row.Children.Add(NativeTheme.Cancel(window));row.Children.Add(NativeTheme.Primary(window,"Uninstall"));NativeTheme.Layout(window,body,row);return window.ShowDialog()==true?remove.IsChecked==true:null;
 }
 public InstallChoice Install(string path){
  var window=NativeTheme.Dialog(owner,"Install Remsup",dark());var body=NativeTheme.Body();body.Children.Add(NativeTheme.Label("Remsup will install for this Windows user in:\n"+path+"\nThe downloaded file stays in its original location; you can delete it later."));
  var desktop=new CheckBox{Content="Desktop icon",IsChecked=true,Margin=new Thickness(0,12,0,0),MinHeight=44,Foreground=window.Foreground};var start=new CheckBox{Content="Start menu icon",IsChecked=true,MinHeight=44,Foreground=window.Foreground};body.Children.Add(desktop);body.Children.Add(start);var row=NativeTheme.Row();row.Children.Add(NativeTheme.Cancel(window));row.Children.Add(NativeTheme.Primary(window,"Install"));NativeTheme.Layout(window,body,row);
  return window.ShowDialog()==true?new InstallChoice{Desktop=desktop.IsChecked==true,StartMenu=start.IsChecked==true}:null;
 }
 public bool Confirm(string title,string text){
  var window=NativeTheme.Dialog(owner,title,dark());var body=NativeTheme.Body();body.Children.Add(NativeTheme.Label(text));var row=NativeTheme.Row();bool close=title=="Close active session";string action=close?"Disconnect and close":title=="Remove saved device"?"Remove device":title=="Scoped LAN firewall"?(text.StartsWith("Remove")?"Remove rule":"Prepare rule"):title=="Connect to device"?"Connect":title=="Uninstall Remsup"?"Uninstall":"Run command";var cancel=NativeTheme.Cancel(window,close?"Keep open":"Cancel");row.Children.Add(cancel);row.Children.Add(NativeTheme.Primary(window,action));NativeTheme.Layout(window,body,row);window.Loaded+=(_,__)=>cancel.Focus();return window.ShowDialog()==true;
 }
 public string Credential(string title,string existing){
  var window=NativeTheme.Dialog(owner,title,dark());var body=NativeTheme.Body();body.Children.Add(NativeTheme.Label(title.StartsWith("Password for ")?"Enter the password for "+title.Substring(13)+".":title=="Receiving password"?"Set a permanent password for receiving support.":"Enter the device's permanent password."));var box=new PasswordBox{Password=existing??""};body.Children.Add(NativeTheme.Input(window,box));var row=NativeTheme.Row();row.Children.Add(NativeTheme.Cancel(window));var use=NativeTheme.Primary(window,"Use password");use.IsDefault=true;row.Children.Add(use);NativeTheme.Layout(window,body,row);window.Loaded+=(_,__)=>box.Focus();bool accepted=window.ShowDialog()==true;string result=accepted?box.Password:null;box.Clear();return result;
 }
 public void Reveal(string title,string value){
  var window=NativeTheme.Dialog(owner,title,dark());var body=NativeTheme.Body();body.Children.Add(NativeTheme.Label("Share only with your authorized operator."));var box=new PasswordBox{Password=value};var visible=new TextBox{Text=value,IsReadOnly=true};var masked=NativeTheme.Input(window,box);var shown=NativeTheme.Input(window,visible);shown.Visibility=Visibility.Collapsed;body.Children.Add(masked);body.Children.Add(shown);var toggle=new CheckBox{Content="Show password",Margin=new Thickness(0,16,0,0),MinHeight=44,Foreground=window.Foreground};toggle.Checked+=(_,__)=>{shown.Visibility=Visibility.Visible;masked.Visibility=Visibility.Collapsed;};toggle.Unchecked+=(_,__)=>{shown.Visibility=Visibility.Collapsed;masked.Visibility=Visibility.Visible;};window.Deactivated+=(_,__)=>toggle.IsChecked=false;body.Children.Add(toggle);var row=NativeTheme.Row();row.Children.Add(NativeTheme.Cancel(window,"Close"));NativeTheme.Layout(window,body,row);window.ShowDialog();visible.Clear();box.Clear();
 }
 public void Copy(string value){if(string.IsNullOrEmpty(value))throw new InvalidOperationException("Nothing to copy.");Exception error=null;for(int i=0;i<4;i++){try{Clipboard.SetText(value);return;}catch(Exception e){error=e;System.Threading.Thread.Sleep(25);}}throw new InvalidOperationException("Clipboard is busy. Please try again.",error);}
 public void Error(string message){var window=NativeTheme.Dialog(owner,"Remsup",dark());var body=NativeTheme.Body();body.Children.Add(NativeTheme.Label(message));var row=NativeTheme.Row();row.Children.Add(NativeTheme.Cancel(window,"Close"));NativeTheme.Layout(window,body,row);window.ShowDialog();}
}
