using System.Windows;
using System.Windows.Controls;

namespace Kedip.Shell.Settings;

/// <summary>A settings row: <see cref="Header"/> and optional <see cref="Description"/> on the left, the content on the right.</summary>
public sealed class SettingRow : ContentControl
{
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(SettingRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingRow), new PropertyMetadata(null));

    /// <summary>Put the content under the text instead of beside it (for wide controls such as 4-way pickers).</summary>
    public static readonly DependencyProperty StackedProperty = DependencyProperty.Register(
        nameof(Stacked), typeof(bool), typeof(SettingRow), new PropertyMetadata(false));

    public bool Stacked
    {
        get => (bool)GetValue(StackedProperty);
        set => SetValue(StackedProperty, value);
    }

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }
}
