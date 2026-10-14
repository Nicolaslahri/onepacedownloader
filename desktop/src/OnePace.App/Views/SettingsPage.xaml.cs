using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using OnePace.App.ViewModels;

namespace OnePace.App;

public partial class SettingsPage : UserControl
{
    SettingsViewModel? _vm;

    public SettingsPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as SettingsViewModel);
    }

    void Attach(SettingsViewModel? vm)
    {
        if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
        _vm = vm;
        if (vm == null) return;
        vm.PropertyChanged += OnVmChanged;
        ApiKeyBox.Password = vm.UsenetKey;
        UsenetCard.IsExpanded = vm.UsenetKey.Length == 0;
    }

    void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.Section) && _vm?.Section == "usenet")
        {
            UsenetCard.IsExpanded = true;
            UsenetCard.BringIntoView();
        }
    }

    void OnApiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (_vm != null && _vm.UsenetKey != ApiKeyBox.Password) _vm.UsenetKey = ApiKeyBox.Password;
    }
}
