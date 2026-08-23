using System.Runtime.InteropServices;

namespace ToolTikTokManagerV13;

static class WorkerWindowEmbedder
{
    const int GWL_STYLE = -16;
    const int GWL_EXSTYLE = -20;

    const long WS_CHILD = 0x40000000L;
    const long WS_VISIBLE = 0x10000000L;
    const long WS_CAPTION = 0x00C00000L;
    const long WS_THICKFRAME = 0x00040000L;
    const long WS_MINIMIZEBOX = 0x00020000L;
    const long WS_MAXIMIZEBOX = 0x00010000L;
    const long WS_SYSMENU = 0x00080000L;
    const long WS_OVERLAPPEDWINDOW = 0x00CF0000L;

    const long WS_EX_TOOLWINDOW = 0x00000080L;
    const long WS_EX_APPWINDOW = 0x00040000L;

    const int SW_HIDE = 0;
    const int SW_SHOW = 5;

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int value);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool MoveWindow(
        IntPtr hWnd,
        int x,
        int y,
        int width,
        int height,
        bool repaint);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    const uint SWP_NOMOVE = 0x0002;
    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOZORDER = 0x0004;
    const uint SWP_NOACTIVATE = 0x0010;
    const uint SWP_FRAMECHANGED = 0x0020;

    static long GetWindowBits(IntPtr hwnd, int index)
        => IntPtr.Size == 8
            ? GetWindowLongPtr64(hwnd, index).ToInt64()
            : GetWindowLong32(hwnd, index);

    static void SetWindowBits(IntPtr hwnd, int index, long value)
    {
        if (IntPtr.Size == 8)
            SetWindowLongPtr64(hwnd, index, new IntPtr(value));
        else
            SetWindowLong32(hwnd, index, unchecked((int)value));
    }

    public static bool IsValid(IntPtr hwnd)
        => hwnd != IntPtr.Zero && IsWindow(hwnd);

    public static bool IsAttachedTo(IntPtr hwnd, Control? host)
    {
        if (!IsValid(hwnd)
            || host is null
            || host.IsDisposed
            || !host.IsHandleCreated)
        {
            return false;
        }

        return GetParent(hwnd) == host.Handle
               && (GetWindowBits(hwnd, GWL_STYLE) & WS_CHILD) != 0;
    }

    public static bool Attach(IntPtr hwnd, Control host)
    {
        if (!IsValid(hwnd) || host.IsDisposed)
            return false;

        if (!host.IsHandleCreated)
            host.CreateControl();

        if (!host.IsHandleCreated)
            return false;

        // Ẩn trước khi đổi style/parent để Worker không lóe lên Task View.
        ShowWindow(hwnd, SW_HIDE);

        var style = GetWindowBits(hwnd, GWL_STYLE);
        style &= ~(
            WS_CAPTION
            | WS_THICKFRAME
            | WS_MINIMIZEBOX
            | WS_MAXIMIZEBOX
            | WS_SYSMENU);
        style |= WS_CHILD | WS_VISIBLE;
        SetWindowBits(hwnd, GWL_STYLE, style);

        var exStyle = GetWindowBits(hwnd, GWL_EXSTYLE);
        exStyle &= ~WS_EX_APPWINDOW;
        exStyle |= WS_EX_TOOLWINDOW;
        SetWindowBits(hwnd, GWL_EXSTYLE, exStyle);

        SetParent(hwnd, host.Handle);

        SetWindowPos(
            hwnd,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SWP_NOMOVE
            | SWP_NOSIZE
            | SWP_NOZORDER
            | SWP_NOACTIVATE
            | SWP_FRAMECHANGED);

        Resize(hwnd, host.ClientSize);
        ShowWindow(hwnd, SW_SHOW);

        return IsAttachedTo(hwnd, host);
    }

    public static bool Detach(IntPtr hwnd)
    {
        if (!IsValid(hwnd))
            return false;

        SetParent(hwnd, IntPtr.Zero);

        var style = GetWindowBits(hwnd, GWL_STYLE);
        style &= ~WS_CHILD;
        style |= WS_OVERLAPPEDWINDOW | WS_VISIBLE;
        SetWindowBits(hwnd, GWL_STYLE, style);

        // Chỉ khi người dùng bấm "Tách Worker" mới đưa Worker trở lại
        // Taskbar / Task View như một cửa sổ bình thường.
        var exStyle = GetWindowBits(hwnd, GWL_EXSTYLE);
        exStyle &= ~WS_EX_TOOLWINDOW;
        exStyle |= WS_EX_APPWINDOW;
        SetWindowBits(hwnd, GWL_EXSTYLE, exStyle);

        SetWindowPos(
            hwnd,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SWP_NOMOVE
            | SWP_NOSIZE
            | SWP_NOZORDER
            | SWP_NOACTIVATE
            | SWP_FRAMECHANGED);

        MoveWindow(hwnd, 80, 80, 980, 760, true);
        ShowWindow(hwnd, SW_SHOW);
        return true;
    }

    public static void Resize(IntPtr hwnd, Size size)
    {
        if (!IsValid(hwnd))
            return;

        MoveWindow(
            hwnd,
            0,
            0,
            Math.Max(100, size.Width),
            Math.Max(100, size.Height),
            true);
    }
}
