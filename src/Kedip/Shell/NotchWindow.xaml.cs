using System.Windows;
using Kedip.Core;

namespace Kedip.Shell;

/// <summary>
/// The notch overlay window. See DESIGN.md "Notch shell".
/// OWNER: A. Scaffold stub — replace the implementation, keep the public API.
/// </summary>
public partial class NotchWindow : Window
{
    private readonly AppServices _services;

    public NotchWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        ClockText.Text = DateTime.Now.ToString("HH:mm");
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;
    }
}
