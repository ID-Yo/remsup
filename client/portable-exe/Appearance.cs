using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

internal static class Appearance
{
    internal static string PreferencePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Remsup", "appearance.txt");
    internal static bool Dark = Load(PreferencePath);
    internal static event Action Changed;
    internal static Color Border { get { return SystemInformation.HighContrast ? SystemColors.WindowText : Dark ? Color.FromArgb(69,87,117) : Color.FromArgb(202,212,225); } }
    internal static Color Header { get { return SystemInformation.HighContrast ? SystemColors.Control : Dark ? Color.FromArgb(25,37,55) : Color.FromArgb(237,242,248); } }
    internal static Color Surface {get{return SystemInformation.HighContrast?SystemColors.Window:Dark?Color.FromArgb(32,45,65):Color.White;}}
    internal static Color Ink {get{return SystemInformation.HighContrast?SystemColors.WindowText:Dark?Color.FromArgb(240,244,250):Color.FromArgb(25,40,62);}}
    internal static Color Muted {get{return SystemInformation.HighContrast?SystemColors.WindowText:Dark?Color.FromArgb(181,195,214):Color.FromArgb(83,100,123);}}
    internal static Color Accent {get{return SystemInformation.HighContrast?SystemColors.Highlight:Dark?Color.FromArgb(183,208,255):Color.FromArgb(30,86,190);}}
    internal static Color Selection {get{return SystemInformation.HighContrast?SystemColors.Highlight:Dark?Color.FromArgb(43,64,95):Color.FromArgb(232,240,255);}}

    internal static bool Load(string path)
    {
        try { return File.Exists(path) && File.ReadAllText(path).Trim() == "dark"; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    internal static void Toggle()
    {
        bool next = !Dark;
        Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath));
        File.WriteAllText(PreferencePath, next ? "dark" : "light");
        Dark = next;
        var changed = Changed;
        if (changed != null) changed();
    }

    internal static void Attach(Control root, Action updated = null)
    {
        var form = root as Form;
        if (form != null)
        {
            var icon = AppIdentity.CreateIcon();
            form.Icon = icon;
            form.Disposed += (s, e) => icon.Dispose();
            form.Load += (s,e) => WorkspaceUi.FitWindow(form);
        }
        Action refresh = () => { Apply(root); if (updated != null) updated(); };
        refresh();
        Changed += refresh;
        root.Disposed += (s, e) => Changed -= refresh;
    }

    internal static void Apply(Control root)
    {
        bool highContrast = SystemInformation.HighContrast;
        bool dark = Dark && !highContrast;
        Color background = highContrast ? SystemColors.Window : dark ? Color.FromArgb(19,28,43) : Color.FromArgb(245,247,251);
        Color surface = Surface;
        Color text = Ink;
        bool input = root is TextBoxBase || root is ListView || root is ComboBox || root is NumericUpDown;
        bool inCard = root is CardPanel;
        for (Control parent = root.Parent; parent != null && !inCard; parent = parent.Parent) inCard = parent is CardPanel;
        bool sidebar=(root.Tag as string)=="sidebar";for(Control parent=root.Parent;parent!=null&&!sidebar;parent=parent.Parent)sidebar=(parent.Tag as string)=="sidebar";
        root.BackColor = input || inCard ? surface : sidebar?Header:background;
        if (root.AccessibleName != "Support readiness") root.ForeColor = text;
        if (!highContrast && (root.Tag as string) == "muted") root.ForeColor = Muted;
        // Settings still uses native tabs; Desktop view selectors are themed radio buttons.
        if (root is TabControl) root.ForeColor = SystemColors.ControlText;
        var button = root as ButtonBase;
        if (button != null)
        {
            if(root.AccessibleName=="Switch color theme")root.Text=Dark?"Light mode":"Dark mode";
            button.UseVisualStyleBackColor = highContrast;
            button.FlatStyle = highContrast ? FlatStyle.System : FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Border;
            if (!highContrast && root.AccessibleName != "Support readiness")
            {
                bool primary = (button.Tag as string) == "primary";
                bool selected=button is RadioButton&&((RadioButton)button).Checked;
                button.BackColor = primary ? Color.FromArgb(35,96,211) : selected?Selection:surface;
                button.ForeColor = primary ? Color.White : text;
                button.FlatAppearance.CheckedBackColor = Selection;
                button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(29, 73, 156) : dark ? Color.FromArgb(46, 62, 84) : Color.FromArgb(232, 241, 255);
                button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(25, 62, 139) : dark ? Color.FromArgb(56, 74, 100) : Color.FromArgb(216, 231, 253);
            }
            if (button is Button) {button.Paint -= PaintDisabledButton;button.Paint += PaintDisabledButton;}
        }
        var check = root as CheckBox;
        if (check != null) check.UseVisualStyleBackColor = highContrast;
        var combo = root as ComboBox;
        if (combo != null) combo.FlatStyle = highContrast ? FlatStyle.Standard : FlatStyle.Flat;
        var page = root as TabPage;
        if (page != null) page.UseVisualStyleBackColor = highContrast;
        foreach (Control child in root.Controls) Apply(child);
        root.Invalidate();
    }

    private static void PaintDisabledButton(object sender, PaintEventArgs e)
    {
        var button = (Button)sender;
        if (button.Enabled || SystemInformation.HighContrast) return;
        var bounds = Rectangle.Inflate(button.ClientRectangle, -2, -2);
        Color fill = Dark ? Color.FromArgb(42, 55, 74) : Color.FromArgb(235, 241, 249);
        Color text = Dark ? Color.FromArgb(173, 192, 218) : Color.FromArgb(82, 105, 137);
        using (var brush = new SolidBrush(fill)) e.Graphics.FillRectangle(brush, bounds);
        TextRenderer.DrawText(e.Graphics, button.Text, button.Font, bounds, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

}
