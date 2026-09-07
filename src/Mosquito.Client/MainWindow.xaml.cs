using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Mosquito.Client;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _statusTimer;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _statusTimer.Tick += StatusTimerOnTick;
        _statusTimer.Start();
        Closed += (_, _) => _statusTimer.Stop();
    }

    private void PasswordInput_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
        {
            _viewModel.Password = passwordBox.Password;
        }
    }

    private async void StatusTimerOnTick(object? sender, EventArgs e)
    {
        await _viewModel.RefreshDeviceAsync();
    }
}
