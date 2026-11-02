using System.Windows;
using System.Windows.Media;
using Kedip.Core;

namespace Kedip.Shell.Settings;

/// <summary>Small "Tentang Kedip" window: logo, version, tagline and the data folder.</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        DwmStyling.UseDarkFrame(this);
        VersionText.Text = $"Versi {AppInfo.Version}";
        DataPathText.Text = AppPaths.DataDir;
        DataPathText.ToolTip = AppPaths.DataDir;
        Loaded += (_, _) =>
        {
            Motion.FromTo(Root, OpacityProperty, 0, 1, 220);
            if (Root.RenderTransform is TranslateTransform shift)
            {
                Motion.FromTo(shift, TranslateTransform.YProperty, 8, 0, 280);
            }
        };
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e) => AppInfo.OpenDataFolder();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
