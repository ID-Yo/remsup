using System;
using System.Drawing;
using System.Windows.Forms;
internal static class WorkspaceUi {
 internal static Label Text(string text,bool muted=false,int size=10){return new Label{Text=text,AutoSize=true,Dock=DockStyle.Top,Font=new Font("Segoe UI",size,size>=14?FontStyle.Bold:FontStyle.Regular),Tag=muted?"muted":null,Margin=new Padding(0,0,0,8),MaximumSize=new Size(720,0)};}
 internal static Button Button(string text,Action action,bool primary=false){var button=new Button{Text=text,AutoSize=true,MinimumSize=new Size(90,36),Padding=new Padding(10,4,10,4),Tag=primary?"primary":null};button.Click+=(s,e)=>action();return button;}
 internal static RadioButton Nav(string text){return new RadioButton{Text=text,Appearance=System.Windows.Forms.Appearance.Button,AutoSize=true,MinimumSize=new Size(100,38),TextAlign=ContentAlignment.MiddleCenter,Padding=new Padding(8),Tag="nav",AccessibleName=text};}
 internal static TableLayoutPanel Stack(){var panel=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=1,Margin=Padding.Empty};panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));return panel;}
 internal static void Add(TableLayoutPanel panel,Control child){int row=panel.RowCount++;panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));panel.Controls.Add(child,0,row);}
 internal static Control Field(string label,Control input){var panel=Stack();panel.Margin=new Padding(0,2,0,6);var caption=Text(label);caption.Margin=new Padding(0,0,0,4);Add(panel,caption);input.Dock=DockStyle.Top;input.AccessibleName=label;Add(panel,input);return panel;}
 internal static TableLayoutPanel Pair(Control left,Control right,int rightWidth=120){var row=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=2};row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,rightWidth));row.Controls.Add(left,0,0);row.Controls.Add(right,1,0);left.Dock=right.Dock=DockStyle.Top;right.Margin=new Padding(12,3,0,3);return row;}
 internal static CardPanel Card(string title,params Control[] children){var card=new CardPanel{Dock=DockStyle.Top,AutoSize=true,Padding=new Padding(24),Margin=new Padding(0,0,0,16)};var stack=Stack();Add(stack,Text(title,false,14));foreach(var child in children)Add(stack,child);card.Controls.Add(stack);return card;}
 internal static void Help(Control root){GuiHelp.Attach(root);GuiHelp.Buttons(root);}
 internal static void FitWindow(Form form){var area=Screen.FromControl(form).WorkingArea;var available=new Size(Math.Max(300,area.Width-16),Math.Max(250,area.Height-16));form.MinimumSize=new Size(Math.Min(form.MinimumSize.Width,available.Width),Math.Min(form.MinimumSize.Height,available.Height));form.Size=new Size(Math.Min(form.Width,available.Width),Math.Min(form.Height,available.Height));form.Location=new Point(Math.Max(area.Left+8,Math.Min(form.Left,area.Right-form.Width-8)),Math.Max(area.Top+8,Math.Min(form.Top,area.Bottom-form.Height-8)));}
}
