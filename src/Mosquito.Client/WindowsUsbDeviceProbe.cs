using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Mosquito.Client.Core;

namespace Mosquito.Client;

// SetupAPI uses the Windows device tree; no adb executable, PowerShell or elevation is required.
public sealed class WindowsUsbDeviceProbe : IUsbDeviceProbe
{
    public Task<IReadOnlyList<UsbBoard>> FindBoardsAsync(CancellationToken token) => Task.Run<IReadOnlyList<UsbBoard>>(() =>
    {
        var list = new List<UsbBoard>();
        var handle = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, 2 | 4);
        if (handle == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            for (uint index = 0; ; index++)
            {
                token.ThrowIfCancellationRequested();
                var info = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                if (!SetupDiEnumDeviceInfo(handle, index, ref info))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 259) break;
                    throw new Win32Exception(error);
                }
                var id = new StringBuilder(1024);
                if (!SetupDiGetDeviceInstanceId(handle, ref info, id, id.Capacity, out _)) continue;
                if (!id.ToString().StartsWith(@"USB\VID_18D1&PID_D002", StringComparison.OrdinalIgnoreCase)) continue;
                var problem = CM_Get_DevNode_Status(out _, out var code, info.DevInst, 0) == 0 ? code : uint.MaxValue;
                list.Add(new(id.ToString(), ReadProperty(handle, ref info, 12) ?? ReadProperty(handle, ref info, 0) ?? "Mosquito USB 设备",
                    ReadProperty(handle, ref info, 4) ?? "", problem));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(handle); }
        return list;
    }, token);

    private static string? ReadProperty(IntPtr handle, ref DeviceInfo info, uint property)
    {
        var bytes = new byte[4096];
        return SetupDiGetDeviceRegistryProperty(handle, ref info, property, out _, bytes, bytes.Length, out var size)
            ? Encoding.Unicode.GetString(bytes, 0, Math.Min((int)size, bytes.Length)).TrimEnd('\0') : null;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo { public uint Size; public Guid ClassGuid; public uint DevInst; public UIntPtr Reserved; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfo info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref DeviceInfo info, StringBuilder id, int size, out int required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref DeviceInfo info, uint property, out uint type, byte[] buffer, int size, out uint required);
    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint device, uint flags);
}
