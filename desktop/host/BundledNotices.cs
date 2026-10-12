using System;
using System.Linq;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
internal static class BundledNotices {
 internal static string Text(){var assembly=typeof(BundledNotices).Assembly;var text=new StringBuilder();foreach(string name in assembly.GetManifestResourceNames().Where(n=>n.StartsWith("Remsup.Notices.",StringComparison.Ordinal)).OrderBy(n=>n,StringComparer.Ordinal)){using var resource=assembly.GetManifestResourceStream(name);using var reader=new StreamReader(resource);text.AppendLine(name.Substring(15)).AppendLine().AppendLine(reader.ReadToEnd()).AppendLine();}if(text.Length==0)throw new InvalidOperationException("Required bundled license notices missing.");return text.ToString();}
 internal static void Show(Window owner,bool dark){var window=NativeTheme.Dialog(owner,"Third-party licenses",dark);window.Width=Math.Min(760,SystemParameters.WorkArea.Width);window.Height=Math.Min(600,SystemParameters.WorkArea.Height-32);window.SizeToContent=SizeToContent.Manual;var grid=new Grid{Margin=new Thickness(24)};grid.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});grid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});var text=new TextBox{Text=Text(),IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Background=window.Background,Foreground=window.Foreground,FontSize=13};grid.Children.Add(text);var row=NativeTheme.Row();row.Margin=new Thickness(0,16,0,0);row.Children.Add(NativeTheme.Cancel(window,"Close"));Grid.SetRow(row,1);grid.Children.Add(row);window.Content=grid;window.ShowDialog();}
}
