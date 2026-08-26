using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ProjectFileHub.App.WindowsIntegration;

internal sealed class MinimumWindowSizeService : IDisposable
{
    private const int WindowLongPointerIndex = -4;
    private const uint GetMinimumMaximumInfoMessage = 0x0024;
    private const uint DefaultDpi = 96;

    private readonly nint _windowHandle;
    private readonly int _minimumWidth;
    private readonly int _minimumHeight;
    private readonly WindowProcedure _windowProcedure;
    private nint _previousWindowProcedure;
    private bool _disposed;

    public MinimumWindowSizeService(nint windowHandle, int minimumWidth, int minimumHeight)
    {
        _windowHandle = windowHandle;
        _minimumWidth = minimumWidth;
        _minimumHeight = minimumHeight;
        _windowProcedure = WindowProcedureCallback;

        Marshal.SetLastPInvokeError(0);
        _previousWindowProcedure = SetWindowLongPointer(
            _windowHandle,
            WindowLongPointerIndex,
            Marshal.GetFunctionPointerForDelegate(_windowProcedure));
        var error = Marshal.GetLastPInvokeError();
        if (_previousWindowProcedure == 0 && error != 0)
        {
            throw new Win32Exception(error, "无法设置程序窗口最小尺寸。");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_previousWindowProcedure != 0)
        {
            SetWindowLongPointer(_windowHandle, WindowLongPointerIndex, _previousWindowProcedure);
            _previousWindowProcedure = 0;
        }

        GC.SuppressFinalize(this);
    }

    private nint WindowProcedureCallback(nint windowHandle, uint message, nint wParam, nint lParam)
    {
        var result = CallWindowProcedure(_previousWindowProcedure, windowHandle, message, wParam, lParam);
        if (message != GetMinimumMaximumInfoMessage || lParam == 0)
        {
            return result;
        }

        var info = Marshal.PtrToStructure<MinimumMaximumInfo>(lParam);
        var dpi = GetDpiForWindow(windowHandle);
        if (dpi == 0)
        {
            dpi = DefaultDpi;
        }

        info.MinimumTrackSize.X = ScaleForDpi(_minimumWidth, dpi);
        info.MinimumTrackSize.Y = ScaleForDpi(_minimumHeight, dpi);
        Marshal.StructureToPtr(info, lParam, false);
        return result;
    }

    private static int ScaleForDpi(int value, uint dpi) =>
        (int)Math.Ceiling(value * dpi / (double)DefaultDpi);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinimumMaximumInfo
    {
        public NativePoint Reserved;
        public NativePoint MaximumSize;
        public NativePoint MaximumPosition;
        public NativePoint MinimumTrackSize;
        public NativePoint MaximumTrackSize;
    }

    private delegate nint WindowProcedure(nint windowHandle, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPointer(nint windowHandle, int index, nint newValue);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern nint CallWindowProcedure(
        nint previousWindowProcedure,
        nint windowHandle,
        uint message,
        nint wParam,
        nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);
}
