using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
internal static class InstallationDialogTests {
 static void Check(bool value,string message){if(!value)throw new Exception(message);}
 static IEnumerable<T> Children<T>(DependencyObject parent) where T:DependencyObject {for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is T match)yield return match;foreach(var nested in Children<T>(child))yield return nested;}}
 [STAThread] static void Main(){new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};var dialogs=new NativeDialogs(null);string path=@"C:\Synthetic\Programs\Remsup";
  NativeTheme.ReviewShown=window=>window.Dispatcher.BeginInvoke(new Action(()=>{Check(Children<TextBlock>(window).Any(x=>x.Text.Contains(path)),"install path is not visible");var icons=Children<CheckBox>(window).ToArray();Check(icons.Length==2&&icons.All(x=>x.IsChecked==true),"icons are not opt-out defaults");foreach(var icon in icons)icon.IsChecked=false;Children<Button>(window).Single(x=>(string)x.Content=="Install").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}));
  var choice=dialogs.Install(path);Check(choice!=null&&!choice.Desktop&&!choice.StartMenu,"icon choice not returned");
  NativeTheme.ReviewShown=window=>window.Dispatcher.BeginInvoke(new Action(()=>Children<Button>(window).Single(x=>(string)x.Content=="Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));Check(dialogs.Install(path)==null,"cancel accepted installation");
  NativeTheme.ReviewShown=window=>window.Dispatcher.BeginInvoke(new Action(()=>{Check(Children<CheckBox>(window).Single().IsChecked==false,"uninstall deletes data by default");Children<Button>(window).Single(x=>(string)x.Content=="Uninstall").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}));Check(dialogs.Uninstall()==false,"default uninstall does not retain data");NativeTheme.ReviewShown=null;Console.WriteLine("Visible install path/default icons/cancel and uninstall retention choice PASS");
 }
}
