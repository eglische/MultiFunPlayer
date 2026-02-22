using System.Windows;
using System.Windows.Controls;
using MultiFunPlayer.UI.Controls.ViewModels;

namespace MultiFunPlayer.UI.Controls.Views;

internal partial class RemoteSettingsView : UserControl
{
    public RemoteSettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (DataContext is RemoteSettingsViewModel vm)
        {
            HttpPasswordBox.Password = vm.HttpPassword ?? string.Empty;
            MqttPasswordBox.Password = vm.MqttPassword ?? string.Empty;
        }
    }

    private void HttpPasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is RemoteSettingsViewModel vm)
            vm.HttpPassword = HttpPasswordBox.Password;
    }

    private void MqttPasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is RemoteSettingsViewModel vm)
            vm.MqttPassword = MqttPasswordBox.Password;
    }
}
