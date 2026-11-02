using System.Windows.Controls;
using Kedip.Core;

namespace Kedip.Views.Panels;

/// <summary>Dropped files; drag out, open, remove. OWNER: E.</summary>
public partial class ShelfPanel : UserControl
{
    private readonly AppServices _services;

    public ShelfPanel(AppServices services)
    {
        _services = services;
        InitializeComponent();
    }
}
