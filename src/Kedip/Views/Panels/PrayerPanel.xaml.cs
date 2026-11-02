using System.Windows.Controls;
using Kedip.Core;

namespace Kedip.Views.Panels;

/// <summary>Next prayer countdown + today's schedule. OWNER: D.</summary>
public partial class PrayerPanel : UserControl
{
    private readonly AppServices _services;

    public PrayerPanel(AppServices services)
    {
        _services = services;
        InitializeComponent();
    }
}
