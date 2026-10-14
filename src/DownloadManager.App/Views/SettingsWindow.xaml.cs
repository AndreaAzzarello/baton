using System.ComponentModel;
using System.Windows;
using DownloadManager.App.ViewModels;

namespace DownloadManager.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _vm = viewModel;
        // Password boxes cannot be bound (the password must not live in a bindable string); they are filled here.
        ProxyPasswordBox.Password = viewModel.ProxyPassword;
        viewModel.PropertyChanged += OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Choosing another saved login clears the box: a saved password is never shown, only replaced.
        if (e.PropertyName == nameof(SettingsViewModel.SelectedLogin))
            LoginPasswordBox.Password = "";
    }

    private void OnProxyPasswordChanged(object sender, RoutedEventArgs e) => _vm.ProxyPassword = ProxyPasswordBox.Password;

    private void OnLoginPasswordChanged(object sender, RoutedEventArgs e) => _vm.LoginPassword = LoginPasswordBox.Password;

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_vm.TrySave())
            DialogResult = true;
    }
}
