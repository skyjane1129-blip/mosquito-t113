using System.Text.Json;
using Mosquito.Client.Core;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: Mosquito.Client.IntegrationRunner SETTINGS_JSON USERNAME PASSWORD");
    return 2;
}

var settings = AppSettings.Load(args[0]);
var cloud = new CloudApiClient(settings);
var outbox = new OutboxRepository(settings.LocalDataRoot);
await outbox.InitializeAsync(CancellationToken.None);
var workflow = new CaptureWorkflow(
    settings,
    new AdbClient(settings),
    new KeyValueMetadataParser(),
    cloud,
    outbox);

await cloud.LoginAsync(args[1], args[2], CancellationToken.None);
var progress = new Progress<WorkflowProgress>(item =>
    Console.WriteLine($"PROGRESS={item.Percent}|{item.Stage}|{item.Message}"));
var artifact = await workflow.CaptureAndUploadAsync(
    UploadRoute.Windows,
    settings.DefaultFocus,
    progress,
    CancellationToken.None);

Console.WriteLine(JsonSerializer.Serialize(new
{
    result = artifact.State is CaptureState.Complete or CaptureState.Partial ? "PASS" : "FAIL",
    captureId = artifact.CaptureId,
    state = artifact.State,
    artifact.DeviceSerial,
    artifact.HostCapturedAtUtc,
    artifact.LocalPhotoPath,
    artifact.LocalMetadataPath,
    artifact.PhotoSha256,
    artifact.MetadataSha256,
    artifact.PhotoBytes,
    artifact.MetadataBytes,
    metadataVersion = artifact.Metadata.Version,
    recordStatus = artifact.Metadata.RecordStatus,
    temperatureCentiC = artifact.Metadata.Environment.TemperatureCentiC,
    humidityCentiRh = artifact.Metadata.Environment.HumidityCentiRh,
    batteryMv = artifact.Metadata.Power.BatteryMv,
    chargeState = artifact.Metadata.Power.ChargeState,
    boardTimeValid = artifact.Metadata.BoardTimeValid,
    artifact.LastError
}, new JsonSerializerOptions { WriteIndented = true }));

return artifact.State is CaptureState.Complete or CaptureState.Partial ? 0 : 1;
