using System.Windows;
using Kedip.Core;

namespace Kedip.Shell;

/// <summary>Settings UI; every change applies live via SettingsStore.Update. OWNER: F.</summary>
public partial class SettingsWindow : Window
{
    private readonly AppServices _services;

    public SettingsWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
    }
}
