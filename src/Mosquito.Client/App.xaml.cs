using System.IO;
using System.Windows;
using System.Windows.Threading;
using Mosquito.Client.Core;

namespace Mosquito.Client;

public partial class App : Application
{
    private string? _errorLog;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Last line of defence for the demo: an unexpected exception on the UI thread or in a forgotten task
        // is logged and shown, but never takes the whole client down.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => { Log("后台任务", args.Exception); args.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log("进程级", args.ExceptionObject as Exception);
        try
        {
            var settings = AppSettings.Load(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
            _errorLog = Path.Combine(settings.LocalDataRoot, "client-errors.log");
            var cloud = new CloudApiClient(settings);
            var outbox = new OutboxRepository(settings.LocalDataRoot);
            await outbox.InitializeAsync(CancellationToken.None);
            var workflow = new CaptureWorkflow(
                settings,
                new AdbClient(settings),
                new KeyValueMetadataParser(),
                cloud,
                outbox);
            var viewModel = new MainViewModel(settings, workflow, cloud, outbox);
            var window = new MainWindow(viewModel);
            MainWindow = window;
            window.Show();
            await viewModel.InitializeAsync();
        }
        catch (Exception exception)
        {
            Log("启动", exception);
            MessageBox.Show(
                $"客户端初始化失败：{exception.Message}",
                "Mosquito 采集客户端",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("界面", e.Exception);
        e.Handled = true;
        MessageBox.Show(
            $"操作未完成：{e.Exception.Message}\n\n程序可以继续使用；详情已记录到 client-errors.log。",
            "Mosquito 采集客户端",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void Log(string source, Exception? exception)
    {
        try
        {
            var path = _errorLog ?? Path.Combine(AppContext.BaseDirectory, "client-errors.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} [{source}] {exception}\n\n");
        }
        catch
        {
            // Logging must never throw from an exception handler.
        }
    }
}
