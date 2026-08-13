using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CodexHud.Infrastructure;

public static class WindowInterop
{
    private const int GwlExStyle = -20;
    private const int WmHotKey = 0x0312;
    private const int WmMouseActivate = 0x0021;
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;
    private const int MaNoActivate = 3;
    private const int PositionEditHotKeyId = 0xC0DE;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint VirtualKeyL = 0x4C;

    private const long WsExNoActivate = 0x08000000L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExTransparent = 0x00000020L;

    public static void ConfigureHudWindow(Window window, Action onPositionEditHotKey)
    {
        if (PresentationSource.FromVisual(window) is not HwndSource source)
        {
            throw new InvalidOperationException("The window source is not available.");
        }

        source.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (message == WmHotKey && wParam.ToInt32() == PositionEditHotKeyId)
            {
                handled = true;
                onPositionEditHotKey();
                return IntPtr.Zero;
            }

            return WindowProcedure(hwnd, message, wParam, lParam, ref handled);
        });

        SetClickThrough(source.Handle, enabled: true);
        RegisterHotKey(
            source.Handle,
            PositionEditHotKeyId,
            ModControl | ModAlt | ModShift | ModNoRepeat,
            VirtualKeyL);
    }

    public static void SetPositionEditing(Window window, bool enabled)
    {
        if (PresentationSource.FromVisual(window) is not HwndSource source)
        {
            return;
        }

        SetClickThrough(source.Handle, enabled: !enabled);
    }

    public static void ReleaseHudWindow(Window window)
    {
        if (PresentationSource.FromVisual(window) is HwndSource source)
        {
            UnregisterHotKey(source.Handle, PositionEditHotKeyId);
        }
    }

    private static IntPtr WindowProcedure(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();

        if (message == WmNcHitTest && (style & WsExTransparent) != 0)
        {
            handled = true;
            return new IntPtr(HtTransparent);
        }

        if (message == WmMouseActivate && (style & WsExNoActivate) != 0)
        {
            handled = true;
            return new IntPtr(MaNoActivate);
        }

        return IntPtr.Zero;
    }

    private static void SetClickThrough(IntPtr hwnd, bool enabled)
    {
        var currentStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        var updatedStyle = currentStyle | WsExToolWindow;
        if (enabled)
        {
            updatedStyle |= WsExNoActivate | WsExTransparent;
        }
        else
        {
            updatedStyle &= ~(WsExNoActivate | WsExTransparent);
        }

        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(updatedStyle));
    }

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hwnd, index)
            : new IntPtr(GetWindowLong32(hwnd, index));
    }

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hwnd, index, value)
            : new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(
        IntPtr hwnd,
        int id,
        uint modifiers,
        uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
