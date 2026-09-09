using System.Text.RegularExpressions;

namespace Mosquito.Client.Core;

public enum DetectionState { Passed, Warning, Failed, Skipped }
public sealed record DetectionCheck(string Name, DetectionState State, string Detail)
{
    public string Display => $"{State switch { DetectionState.Passed => "通过", DetectionState.Warning => "待确认", DetectionState.Failed => "异常", _ => "未检查" }} · {Name}\n{Detail}";
}
public sealed record UsbBoard(string InstanceId, string Name, string Service, uint ProblemCode)
{
    public string Serial => InstanceId.Split('\\').Last();
}
public interface IUsbDeviceProbe
{
    Task<IReadOnlyList<UsbBoard>> FindBoardsAsync(CancellationToken token);
}
public sealed record DeviceDetectionResult(string Summary, IReadOnlyList<DetectionCheck> Checks,
    bool Connected = false, bool CaptureReady = false, string? DeviceId = null, string? Version = null)
{
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;
}
public interface IDeviceDetectionService
{
    Task<DeviceDetectionResult> DetectAsync(CancellationToken token);
}

public sealed class DeviceDetectionService(AppSettings settings, IAdbClient adb, IUsbDeviceProbe usb,
    Func<CancellationToken, Task<string>> adbVersion) : IDeviceDetectionService
{
    public async Task<DeviceDetectionResult> DetectAsync(CancellationToken token)
    {
        var checks = new List<DetectionCheck>();
        IReadOnlyList<UsbBoard>? boards = null;
        // Windows enumeration is independent of adb, including on a machine missing adb.exe.
        try { boards = await usb.FindBoardsAsync(token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { checks.Add(new("USB 与驱动", DetectionState.Warning, "Windows 设备检查失败：" + ex.Message)); }
        var targets = boards?.Where(x => x.Serial.Equals(settings.DeviceSerial, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (boards is not null)
        {
            checks.Add(new("USB 设备", targets?.Length == 1 ? DetectionState.Passed : DetectionState.Warning,
                boards.Count == 0 ? "没有发现 Mosquito USB 设备，请检查数据线、供电与接口。" :
                targets?.Length != 1 ? $"发现 {boards.Count} 个 Mosquito USB 接口，未能唯一匹配配置的 ADB 序列号 {settings.DeviceSerial}。" : targets[0].Name));
            if (targets?.Length == 1)
                checks.Add(new("USB 驱动", targets[0].ProblemCode == 0 && targets[0].Service.Equals("WinUSB", StringComparison.OrdinalIgnoreCase) ? DetectionState.Passed : DetectionState.Warning,
                    $"驱动：{(targets[0].Service.Length == 0 ? "未绑定" : targets[0].Service)}；Windows 问题代码：{targets[0].ProblemCode}"));
        }
        try { checks.Insert(0, new("ADB 组件", DetectionState.Passed, await adbVersion(token))); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            checks.Insert(0, new("ADB 组件", DetectionState.Failed, "通信组件缺失或不可运行：" + ex.Message));
            checks.Add(new("ADB 通信与板端接口", DetectionState.Skipped, "请修复客户端通信组件后重新检测。"));
            return new("通信组件未就绪" + (boards?.Count > 0 ? " · 已发现 USB 设备" : ""), checks);
        }
        IReadOnlyList<AdbDevice> devices;
        try { devices = await adb.GetDevicesAsync(token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { checks.Add(new("ADB 通信", DetectionState.Failed, ex.Message)); return new("ADB 通信检查失败", checks); }
        var matches = devices.Where(x => x.Serial == settings.DeviceSerial).ToArray();
        if (matches.Length != 1 || targets?.Length > 1)
        {
            checks.Add(new("ADB 通信", DetectionState.Failed, matches.Length > 1 || targets?.Length > 1 ? "目标设备不唯一，请只连接一台目标板后重试。" : "未发现配置的目标设备；请检查连接及板端 ADB 是否启动。"));
            return new(matches.Length > 1 || targets?.Length > 1 ? "目标设备需确认" : boards?.Count > 0 ? "已发现 USB · ADB 尚未连接" : "未发现目标设备", checks);
        }
        if (matches[0].State != "device")
        {
            checks.Add(new("ADB 通信", DetectionState.Failed, "设备状态：" + matches[0].State + "；请检查 USB 连接与板端通信服务。"));
            return new("发现设备 · ADB " + matches[0].State, checks);
        }
        try
        {
            if (!Regex.IsMatch(settings.BoardCommandPath, "^[A-Za-z0-9_./:-]+$", RegexOptions.CultureInvariant))
                throw new InvalidOperationException("板端命令路径配置无效。");
            // Presence checks only: never invoke capture, sensor, reboot or installation commands.
            var command = $"export PATH={settings.BoardCommandPath}; echo MOSQUITO_PROBE=1; " +
                "mosquito-version 2>/dev/null; for c in mosquito-capture mosquito-environment mosquito-power; do " +
                "if command -v \"$c\" >/dev/null 2>&1; then echo \"CAP:$c=1\"; else echo \"CAP:$c=0\"; fi; done";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var result = await adb.ShellAsync(settings.DeviceSerial, command, timeout.Token);
            if (result.ExitCode != 0 || !result.StandardOutput.Contains("MOSQUITO_PROBE=1", StringComparison.Ordinal))
                throw new InvalidOperationException("设备没有返回有效的只读探测响应。");
            checks.Add(new("ADB 通信", DetectionState.Passed, "目标板已响应只读探测。"));
            var lines = result.StandardOutput.Replace("\r", "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var version = lines.FirstOrDefault(x => x.StartsWith("MOSQUITO_IMAGE="))?["MOSQUITO_IMAGE=".Length..];
            var hasCommands = new[] { "mosquito-capture", "mosquito-environment", "mosquito-power" }.All(x => lines.Contains("CAP:" + x + "=1"));
            checks.Add(new("板端接口", hasCommands ? DetectionState.Passed : DetectionState.Warning,
                hasCommands ? "采集、环境、电源命令均存在；实际采集结果以执行时为准。" : "采集接口未就绪：" + string.Join("、", new[] { "mosquito-capture", "mosquito-environment", "mosquito-power" }.Where(x => !lines.Contains("CAP:" + x + "=1")))));
            var metadataVersion = lines.FirstOrDefault(x => x.StartsWith("MOSQUITO_PHOTO_METADATA="))?["MOSQUITO_PHOTO_METADATA=".Length..];
            var metadataCompatible = metadataVersion == "3";
            checks.Add(new("元数据兼容性", metadataCompatible ? DetectionState.Passed : DetectionState.Warning,
                metadataCompatible ? "镜像声明 metadata v3；采集时仍校验实际文件。" : $"镜像声明的照片元数据版本：{metadataVersion ?? "未提供"}。客户端需要 v3，临时接口兼容性需单独验证。"));
            checks.Add(new("设备身份", string.IsNullOrWhiteSpace(settings.DeviceId) || version is null ? DetectionState.Warning : DetectionState.Passed,
                $"设备编号：{settings.DeviceId ?? "待配置"}（本机配置）；镜像：{version ?? "未提供"}；ADB：{settings.DeviceSerial}"));
            var summary = !hasCommands ? "已连接 · 采集接口未就绪" : checks.Any(x => x.State != DetectionState.Passed) ? "已连接 · 存在待确认项" : "已连接 · 检测通过";
            return new(summary, checks, true, hasCommands && metadataCompatible, settings.DeviceId, version);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            checks.Add(new("板端响应", DetectionState.Failed, ex is OperationCanceledException ? "只读探测超时，请等待板子启动完成后重试。" : ex.Message));
            return new("ADB 在线 · 板端响应异常", checks);
        }
    }
}
