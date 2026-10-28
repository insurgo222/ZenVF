using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ZenVF.Interop;

/// <summary>
/// Minimal managed wrapper around the PawnIO kernel driver. It mirrors the
/// IOCTL protocol used by LibreHardwareMonitor and ZenStates-Core.
/// </summary>
public sealed class PawnIoNative : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80;

    private const uint DeviceType = 41394u << 16;
    private const uint IoctlLoadBinary = DeviceType | (0x821 << 2);
    private const uint IoctlExecuteFn = DeviceType | (0x841 << 2);
    private const int FunctionNameLength = 32;

    private SafeFileHandle? _handle;
    private bool _disposed;

    public bool IsLoaded => _handle is { IsInvalid: false, IsClosed: false };

    public static bool IsInstalled => FindInstalledVersion() is not null || ServiceExists();

    public static string? InstalledVersion
    {
        get
        {
            string? version = FindInstalledVersion();
            if (version is not null)
            {
                return version;
            }

            return ServiceExists() ? "已安装" : null;
        }
    }

    private static string? FindInstalledVersion()
    {
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using RegistryKey? key = baseKey.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
                if (key?.GetValue("DisplayVersion") is string version && !string.IsNullOrWhiteSpace(version))
                {
                    return version;
                }
            }
            catch
            {
                // Try the next registry view.
            }
        }

        return null;
    }

    private static bool ServiceExists()
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    public void LoadEmbeddedModule(byte[] module)
    {
        ThrowIfDisposed();
        CloseHandle();

        _handle = CreateFile(@"\\?\GLOBALROOT\Device\PawnIO",
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal,
            IntPtr.Zero);

        if (_handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            _handle.Dispose();
            _handle = null;
            throw new Win32Exception(error,
                error == 5
                    ? "PawnIO 设备拒绝访问，请以管理员身份运行。"
                    : "无法打开 PawnIO 设备，请确认 PawnIO 驱动已安装。");
        }

        if (!DeviceIoControl(_handle, IoctlLoadBinary, module, (uint)module.Length, null, 0, out _, IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            CloseHandle();
            throw new Win32Exception(error, "PawnIO 模块加载失败。");
        }
    }

    public long[] Execute(string function, long[] input, int outputLength)
    {
        ThrowIfDisposed();
        if (!IsLoaded)
        {
            return Array.Empty<long>();
        }

        byte[] inputBuffer = new byte[FunctionNameLength + (input.Length * sizeof(long))];
        byte[] name = Encoding.ASCII.GetBytes(function);
        Array.Copy(name, 0, inputBuffer, 0, Math.Min(name.Length, FunctionNameLength - 1));
        for (int i = 0; i < input.Length; i++)
        {
            BitConverter.TryWriteBytes(inputBuffer.AsSpan(FunctionNameLength + (i * sizeof(long)), sizeof(long)), input[i]);
        }

        byte[] outputBuffer = new byte[outputLength * sizeof(long)];
        bool ok = DeviceIoControl(
            _handle!,
            IoctlExecuteFn,
            inputBuffer,
            (uint)inputBuffer.Length,
            outputBuffer,
            (uint)outputBuffer.Length,
            out uint bytesReturned,
            IntPtr.Zero);

        if (!ok)
        {
            return Array.Empty<long>();
        }

        int count = (int)(bytesReturned / sizeof(long));
        long[] result = new long[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = BitConverter.ToInt64(outputBuffer, i * sizeof(long));
        }

        return result;
    }

    public int ExecuteHr(string function, long[] input, uint inputLength, long[] output, uint outputLength, out uint returnedLength)
    {
        ThrowIfDisposed();
        returnedLength = 0;
        if (!IsLoaded)
        {
            return unchecked((int)0x80070005);
        }

        int count = (int)Math.Min(inputLength, (uint)input.Length);
        byte[] inputBuffer = new byte[FunctionNameLength + (count * sizeof(long))];
        byte[] name = Encoding.ASCII.GetBytes(function);
        Array.Copy(name, 0, inputBuffer, 0, Math.Min(name.Length, FunctionNameLength - 1));
        for (int i = 0; i < count; i++)
        {
            BitConverter.TryWriteBytes(inputBuffer.AsSpan(FunctionNameLength + (i * sizeof(long)), sizeof(long)), input[i]);
        }

        int outCount = (int)Math.Min(outputLength, (uint)output.Length);
        byte[] outputBuffer = new byte[outCount * sizeof(long)];
        bool ok = DeviceIoControl(
            _handle!,
            IoctlExecuteFn,
            inputBuffer,
            (uint)inputBuffer.Length,
            outputBuffer,
            (uint)outputBuffer.Length,
            out uint bytesReturned,
            IntPtr.Zero);

        if (!ok)
        {
            return Marshal.GetHRForLastWin32Error();
        }

        returnedLength = bytesReturned / sizeof(long);
        for (int i = 0; i < Math.Min(outCount, (int)returnedLength); i++)
        {
            output[i] = BitConverter.ToInt64(outputBuffer, i * sizeof(long));
        }

        return 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseHandle();
    }

    private void CloseHandle()
    {
        _handle?.Dispose();
        _handle = null;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        byte[]? inBuffer,
        uint inBufferSize,
        byte[]? outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);
}
