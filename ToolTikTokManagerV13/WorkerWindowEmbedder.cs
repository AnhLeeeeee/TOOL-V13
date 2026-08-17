using System.Runtime.InteropServices;

namespace ToolTikTokManagerV13;

static class WorkerWindowEmbedder
{
    const int GWL_STYLE = -16;
    const long WS_CHILD = 0x40000000L;
    const long WS_VISIBLE = 0x10000000L;
    const long WS_CAPTION = 0x00C00000L;
    const long WS_THICKFRAME = 0x00040000L;
    const long WS_MINIMIZEBOX = 0x00020000L;
    const long WS_MAXIMIZEBOX = 0x00010000L;
    const long WS_SYSMENU = 0x00080000L;
    const long WS_OVERLAPPEDWINDOW = 0x00CF0000L;
    const int SW_SHOW = 5;

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] static extern int GetWindowLong32(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int value);
    [DllImport("user32.dll", SetLastError = true)] static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr GetParent(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    static long GetStyle(IntPtr hwnd) => IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, GWL_STYLE).ToInt64() : GetWindowLong32(hwnd, GWL_STYLE);
    static void SetStyle(IntPtr hwnd, long style)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, GWL_STYLE, new IntPtr(style));
        else SetWindowLong32(hwnd, GWL_STYLE, unchecked((int)style));
    }

    const uint SWP_NOMOVE = 0x0002;
    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOZORDER = 0x0004;
    const uint SWP_NOACTIVATE = 0x0010;
    const uint SWP_FRAMECHANGED = 0x0020;

    public static bool IsValid(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

    public static bool IsAttachedTo(IntPtr hwnd, Control? host)
    {
        if (!IsValid(hwnd) || host is null || host.IsDisposed || !host.IsHandleCreated) return false;
        return GetParent(hwnd) == host.Handle && (GetStyle(hwnd) & WS_CHILD) != 0;
    }

    public static bool Attach(IntPtr hwnd, Control host)
    {
        if (!IsValid(hwnd) || host.IsDisposed) return false;
        if (!host.IsHandleCreated) host.CreateControl();
        if (!host.IsHandleCreated) return false;

        var style = GetStyle(hwnd);
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
        style |= WS_CHILD | WS_VISIBLE;
        SetStyle(hwnd, style);
        SetParent(hwnd, host.Handle);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        Resize(hwnd, host.ClientSize);
        ShowWindow(hwnd, SW_SHOW);
        return IsAttachedTo(hwnd, host);
    }

    public static bool Detach(IntPtr hwnd)
    {
        if (!IsValid(hwnd)) return false;
        SetParent(hwnd, IntPtr.Zero);
        var style = GetStyle(hwnd);
        style &= ~WS_CHILD;
        style |= WS_OVERLAPPEDWINDOW | WS_VISIBLE;
        SetStyle(hwnd, style);
        MoveWindow(hwnd, 80, 80, 980, 760, true);
        ShowWindow(hwnd, SW_SHOW);
        return true;
    }

    public static void Resize(IntPtr hwnd, Size size)
    {
        if (!IsValid(hwnd)) return;
        MoveWindow(hwnd, 0, 0, Math.Max(100, size.Width), Math.Max(100, size.Height), true);
    }
}
