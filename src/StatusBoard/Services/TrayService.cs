using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StatusBoard.Core;
using StatusBoard.ViewModels;

namespace StatusBoard.Services;

/// <summary>
/// Notification-area icon coloured by overall status, with a menu (Open / Refresh now / Exit) and balloon notifications for
/// status changes. Talks to Shell_NotifyIcon directly (no WinForms); balloons need no AppUserModelID or registry entries.
/// </summary>
public sealed class TrayService : IDisposable
{
    private const int CallbackMessage = 0x8001, MenuOpen = 1, MenuRefresh = 2, MenuExit = 3;
    private readonly HwndSource _window;
    private readonly Action _show, _refresh, _exit;
    private readonly Dictionary<ServiceStatus, nint> _icons = [];
    private readonly int _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private NotifyIconData _data;
    private bool _visible;

    public TrayService(Action show, Action refresh, Action exit)
    {
        (_show, _refresh, _exit) = (show, refresh, exit);
        // A hidden top-level window receives the icon's messages (and can own the menu, which a message-only window can't).
        _window = new HwndSource(new HwndSourceParameters("Status Board tray") { Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000) });
        _window.AddHook(WndProc);
        _data = new NotifyIconData
        {
            Size = Marshal.SizeOf<NotifyIconData>(), Window = _window.Handle, Id = 1, CallbackMessage = CallbackMessage,
            Flags = Nif.Message | Nif.Icon | Nif.Tip, Tip = "Status Board",
        };
        SetStatus(ServiceStatus.Unknown, "Status Board – checking…");
    }

    /// <summary>Whether Windows accepted the last add/update/balloon (checked by the self-test).</summary>
    public bool LastCallAccepted { get; private set; }

    public bool Visible
    {
        get => _visible;
        set
        {
            if (value == _visible) return;
            _visible = value;
            LastCallAccepted = Shell_NotifyIcon(value ? NimAdd : NimDelete, ref _data);
        }
    }

    public void SetStatus(ServiceStatus status, string tooltip)
    {
        if (!_icons.TryGetValue(status, out var icon)) _icons[status] = icon = Draw(status);
        _data.Icon = icon;
        _data.Tip = tooltip.Length > 127 ? tooltip[..124] + "…" : tooltip; // the shell's limit
        _data.Flags = Nif.Message | Nif.Icon | Nif.Tip;
        if (_visible) LastCallAccepted = Shell_NotifyIcon(NimModify, ref _data);
    }


    private static string Clip(string s, int max) => s.Length > max ? s[..(max - 1)] + "…" : s;

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == _taskbarCreated && _visible) Shell_NotifyIcon(NimAdd, ref _data); // Explorer restarted
        if (msg != CallbackMessage) return 0;
        switch ((int)lParam & 0xFFFF)
        {
            case 0x0202 or 0x0405: _show(); break; // left click, balloon click
            case 0x0205: ShowMenu(); break;       // right click
        }
        handled = true;
        return 0;
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, 0, MenuOpen, "Open Status Board");
        AppendMenu(menu, 0, MenuRefresh, "Refresh now");
        AppendMenu(menu, 0x800, 0, null); // separator
        AppendMenu(menu, 0, MenuExit, "Exit");
        GetCursorPos(out var pt);
        SetForegroundWindow(_window.Handle); // so the menu closes when clicking elsewhere
        var cmd = TrackPopupMenu(menu, 0x0100 | 0x0002, pt.X, pt.Y, 0, _window.Handle, 0); // TPM_RETURNCMD | TPM_RIGHTBUTTON
        DestroyMenu(menu);
        switch (cmd)
        {
            case MenuOpen: _show(); break;
            case MenuRefresh: _refresh(); break;
            case MenuExit: _exit(); break;
        }
    }

    /// <summary>A 32 px status-coloured dot with a white mark, so it isn't colour-only.</summary>
    private static nint Draw(ServiceStatus s)
    {
        var (color, mark) = s switch
        {
            ServiceStatus.Operational => (Color.FromRgb(22, 163, 74), "✓"),
            ServiceStatus.Maintenance => (Color.FromRgb(37, 99, 235), "i"),
            ServiceStatus.Degraded => (Color.FromRgb(217, 119, 6), "!"),
            ServiceStatus.PartialOutage => (Color.FromRgb(234, 88, 12), "!"),
            ServiceStatus.MajorOutage => (Color.FromRgb(220, 38, 38), "×"),
            _ => (Color.FromRgb(107, 114, 128), "?"),
        };
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawEllipse(new SolidColorBrush(color), null, new Point(16, 16), 15, 15);
            var text = new FormattedText(mark, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 17, Brushes.White, 1.0);
            dc.DrawText(text, new Point(16 - text.Width / 2, 16.5 - text.Height / 2));
        }
        var bmp = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        png.Save(ms);
        var bytes = ms.ToArray();
        return CreateIconFromResourceEx(bytes, bytes.Length, true, 0x00030000, 32, 32, 0);
    }

    public void Dispose()
    {
        Visible = false;
        foreach (var i in _icons.Values) DestroyIcon(i);
        _window.Dispose();
    }

    // ---- Win32 ----
    private const int NimAdd = 0, NimModify = 1, NimDelete = 2;
    [Flags] private enum Nif { Message = 1, Icon = 2, Tip = 4, Info = 0x10 }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public nint Window;
        public int Id;
        public Nif Flags;
        public int CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public int State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public int TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public int InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Pt { public int X, Y; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, int flags, nint id, string? text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(nint menu, int flags, int x, int y, int reserved, nint window, nint rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Pt point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint CreateIconFromResourceEx(byte[] bits, int size, bool icon, int version, int cx, int cy, int flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
}
