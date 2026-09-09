using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Interop;

namespace Mosquito.Client;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _statusTimer;
    private HwndSource? _source;
    private DateTimeOffset _lastPoll = DateTimeOffset.UtcNow;

    public MainWindow(MainViewModel viewModel, bool enablePolling = true)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.ClearPasswordRequested += ClearPassword;
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += StatusTimerOnTick;
        if (enablePolling)
        {
            _statusTimer.Start();
            SourceInitialized += (_, _) => { _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle); _source?.AddHook(DeviceMessage); };
        }
        Closed += (_, _) =>
        {
            _statusTimer.Stop(); _source?.RemoveHook(DeviceMessage);
            _viewModel.ClearPasswordRequested -= ClearPassword; _viewModel.Close();
        };
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
        try
        {
            await _viewModel.TickAsync();
            if (DateTimeOffset.UtcNow - _lastPoll >= TimeSpan.FromSeconds(60))
            {
                _lastPoll = DateTimeOffset.UtcNow; await _viewModel.PollAsync();
            }
        }
        catch (OperationCanceledException) { }
    }
    private void ClearPassword() => PasswordInput.Clear();
    private void LiveReadings_OnExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is Expander expander)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => expander.BringIntoView()));
    }
    private IntPtr DeviceMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0219) _viewModel.DeviceChanged(); // WM_DEVICECHANGE; debounce in the view model.
        return IntPtr.Zero;
    }
}
