using System.Windows.Controls;
using Kedip.Core;

namespace Kedip.Views.Panels;

/// <summary>Now playing + controls + lyric lines. OWNER: C.</summary>
public partial class MusicPanel : UserControl
{
    private readonly AppServices _services;

    public MusicPanel(AppServices services)
    {
        _services = services;
        InitializeComponent();
    }
}
