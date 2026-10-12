using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Independent visible local controls. There is no renderer or remote grant API.
// A future verified screen channel must marshal requests to the UI dispatcher.
internal sealed class ScreenConsentWindow:Window
{
    readonly ScreenAccess access;
    readonly DispatcherTimer timer=new DispatcherTimer();
    readonly TextBlock status=new TextBlock{FontSize=19,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,10)};
    readonly TextBlock elapsed=new TextBlock{FontSize=16,Margin=new Thickness(0,0,0,12)};
    readonly CheckBox viewing=new CheckBox{Content="Screen viewing",Margin=new Thickness(0,8,0,8)};
    readonly CheckBox mouse=new CheckBox{Content="Mouse control",Margin=new Thickness(0,8,0,8)};
    readonly CheckBox typing=new CheckBox{Content="Keyboard control",Margin=new Thickness(0,8,0,8)};
    bool refreshing;
    readonly TextBlock summary=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)};
    readonly Button view=new Button{Content="Allow view only",Margin=new Thickness(0,0,0,8)};
    readonly Button control=new Button{Content="Allow control",Margin=new Thickness(0,0,0,8)};
    readonly Button deny=new Button{Content="Deny",Margin=new Thickness(0,0,0,8)};
    readonly Button stop=new Button{Content="Disconnect",Margin=new Thickness(0,0,0,8)};
    Guid displayed;Guid minimizedGrant;
    internal string OperatorLabel="Operator identity not verified";
    internal event Action Revoked;
    internal event Action Changed;
    internal void LocalApproveForFixture(bool allowControl){(allowControl?control:view).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
    internal void CaptureOwnVisual(string path){var visual=(FrameworkElement)Content;visual.UpdateLayout();int width=(int)Math.Ceiling(visual.ActualWidth),height=(int)Math.Ceiling(visual.ActualHeight);var drawing=new DrawingVisual();using(var context=drawing.RenderOpen())context.DrawRectangle(new VisualBrush(visual),null,new Rect(0,0,width,height));var image=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);image.Render(drawing);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(path);png.Save(file);}
    internal void ShowCurrentRequest(){WindowState=WindowState.Normal;Show();Activate();Refresh();}
    internal ScreenConsentWindow(ScreenAccess owner)
    {
        if(owner==null)throw new ArgumentNullException("owner");access=owner;
        Title="Remsup screen access";Width=Math.Min(410,SystemParameters.WorkArea.Width);MaxHeight=SystemParameters.WorkArea.Height;WindowStartupLocation=WindowStartupLocation.CenterScreen;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.CanMinimize;Topmost=true;ShowInTaskbar=true;
        FontFamily=new FontFamily("Segoe UI");FontSize=14;Background=Brush("#F3F5F8");Foreground=Brush("#243247");
        var panel=new StackPanel{Margin=new Thickness(18),Background=Background};
        var connection=new StackPanel();connection.Children.Add(status);connection.Children.Add(elapsed);connection.Children.Add(summary);
        panel.Children.Add(new Border{Background=Brush("#EAF3FF"),CornerRadius=new CornerRadius(12),Padding=new Thickness(16),Margin=new Thickness(0,0,0,12),Child=connection});
        var permissions=new StackPanel();permissions.Children.Add(new TextBlock{Text="Session permissions",FontWeight=FontWeights.SemiBold});permissions.Children.Add(viewing);permissions.Children.Add(mouse);permissions.Children.Add(typing);
        panel.Children.Add(new Border{Background=Brushes.White,CornerRadius=new CornerRadius(12),Padding=new Thickness(16),Margin=new Thickness(0,0,0,12),Child=permissions});
        foreach(var button in new[]{view,control,deny,stop}){button.Background=Brush(button==stop?"#C83D48":button==deny?"#526174":"#246BCE");button.Foreground=Brushes.White;button.Padding=new Thickness(12,10,12,10);button.BorderThickness=new Thickness(0);RoundButton(button);panel.Children.Add(button);}
        Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Background=Background};
        viewing.Unchecked+=(_,__)=>{if(!refreshing){access.Stop();Revoked?.Invoke();Refresh();}};
        mouse.Click+=(_,__)=>InputChanged();typing.Click+=(_,__)=>InputChanged();
        view.Click+=(_,__)=>Approve(false);control.Click+=(_,__)=>Approve(true);
        deny.Click+=(_,__)=>{try{access.DenyLocal(displayed);}catch(InvalidOperationException){}Revoked?.Invoke();Refresh();};
        stop.Click+=(_,__)=>{access.Stop();Revoked?.Invoke();Refresh();};
        timer.Interval=TimeSpan.FromMilliseconds(500);timer.Tick+=(_,__)=>Refresh();
        IsVisibleChanged+=(_,__)=>{if(IsVisible)timer.Start();};
        Closed+=(_,__)=>{timer.Stop();access.Stop();Revoked?.Invoke();};
        Refresh();
    }
    static SolidColorBrush Brush(string color)=>new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    static void RoundButton(Button button)
    {
        var border=new FrameworkElementFactory(typeof(Border)){Name="buttonBorder"};border.SetValue(Border.CornerRadiusProperty,new CornerRadius(8));border.SetValue(Border.BorderThicknessProperty,new Thickness(2));border.SetValue(Border.BorderBrushProperty,Brushes.Transparent);border.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty));border.SetValue(Border.PaddingProperty,new TemplateBindingExtension(Control.PaddingProperty));
        var content=new FrameworkElementFactory(typeof(ContentPresenter));content.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);border.AppendChild(content);
        var template=new ControlTemplate(typeof(Button)){VisualTree=border};
        var hover=new Trigger{Property=UIElement.IsMouseOverProperty,Value=true};hover.Setters.Add(new Setter(UIElement.OpacityProperty,0.88));template.Triggers.Add(hover);
        var pressed=new Trigger{Property=Button.IsPressedProperty,Value=true};pressed.Setters.Add(new Setter(UIElement.OpacityProperty,0.72));template.Triggers.Add(pressed);
        var focus=new Trigger{Property=UIElement.IsKeyboardFocusedProperty,Value=true};focus.Setters.Add(new Setter(Border.BorderBrushProperty,Brush("#FFBD3E"),"buttonBorder"));template.Triggers.Add(focus);
        var disabled=new Trigger{Property=UIElement.IsEnabledProperty,Value=false};disabled.Setters.Add(new Setter(UIElement.OpacityProperty,0.45));template.Triggers.Add(disabled);button.Template=template;
    }
    void InputChanged(){if(refreshing)return;access.SetLocalInput(mouse.IsChecked==true,typing.IsChecked==true);Refresh();}
    internal void Approve(bool allowControl)
    {
        // A queued stale click must not approve a newer request.
        try{access.GrantLocal(displayed,allowControl);}catch(InvalidOperationException){}Refresh();
    }
    internal void Refresh()
    {
        try{access.RefreshEnvironment();}catch(InvalidOperationException){access.Stop();}
        var snapshot=access.Snapshot;
        displayed=snapshot.SessionId;
        bool pending=snapshot.State==ScreenSessionState.Pending;
        view.Visibility=deny.Visibility=pending?Visibility.Visible:Visibility.Collapsed;
        control.Visibility=pending&&snapshot.RequestedControl?Visibility.Visible:Visibility.Collapsed;
        stop.IsEnabled=pending||snapshot.CanView;
        refreshing=true;viewing.IsChecked=snapshot.CanView;viewing.IsEnabled=snapshot.CanView;
        mouse.IsChecked=access.CanMouse;typing.IsChecked=access.CanType;mouse.IsEnabled=typing.IsEnabled=snapshot.CanControl;refreshing=false;
        stop.Visibility=pending?Visibility.Collapsed:Visibility.Visible;
        status.Text=pending?"Waiting for approval":snapshot.CanView?(snapshot.AutoAccepted?"Automatically accepted session":"Screen session active"):"Screen access disconnected";
        elapsed.Text=snapshot.CanView?"Connected "+TimeSpan.FromMilliseconds(snapshot.ElapsedMs).ToString(@"hh\:mm\:ss"):pending?"Approve only an expected request":"A new session needs your approval";
        summary.Text=(pending||snapshot.CanView?"Claimed operator: "+OperatorLabel+" (not independently verified).\nMonitor: "+snapshot.MonitorId+"\n\n":"")+"Relay transport uses pinned TLS; the relay administrator can read traffic. "+"Direct LAN uses the verified receiver SPKI pin. "+"Controlled testing only."+(pending?"\nA command password does not grant screen rights.":snapshot.CanView?"\nMinimize keeps this session active. Disconnect or close this window to stop.":"");
        if(IsVisible&&snapshot.CanView&&minimizedGrant!=snapshot.SessionId){minimizedGrant=snapshot.SessionId;WindowState=WindowState.Minimized;}
        Changed?.Invoke();
    }
}
