using System.IO;
using System.Windows;
using Mosquito.Client.Core;

namespace Mosquito.Client;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var settings = AppSettings.Load(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
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
            MessageBox.Show(
                $"客户端初始化失败：{exception.Message}",
                "Mosquito 采集客户端",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
