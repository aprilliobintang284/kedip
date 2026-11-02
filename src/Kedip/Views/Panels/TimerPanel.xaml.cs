using System.Windows.Controls;
using Kedip.Core;

namespace Kedip.Views.Panels;

/// <summary>Pomodoro controls + water intake. OWNER: E.</summary>
public partial class TimerPanel : UserControl
{
    private readonly AppServices _services;

    public TimerPanel(AppServices services)
    {
        _services = services;
        InitializeComponent();
    }
}
