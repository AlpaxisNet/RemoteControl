using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using RemoteControl.Ui.Common;

namespace RemoteControl.App.Platform;

/// <summary>
/// The real desktop: the windows of the Avalonia application, the task-bar flash (Windows <c>FlashWindowEx</c>; X11
/// <c>_NET_WM_STATE_DEMANDS_ATTENTION</c>; nothing elsewhere), the alert sound, and the application's end.
/// </summary>
internal sealed partial class AvaloniaDesktop : IDesktop
{
    private readonly IClassicDesktopStyleApplicationLifetime? _lifetime;
    private readonly AppServices _services;

    /// <summary>Creates it.</summary>
    /// <param name="lifetime">The application's lifetime (null without one).</param>
    /// <param name="services">The program's services (sounds, log).</param>
    public AvaloniaDesktop(IClassicDesktopStyleApplicationLifetime? lifetime, AppServices services)
    {
        _lifetime = lifetime;
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public IReadOnlyList<Window> Windows => _lifetime?.Windows ?? (IReadOnlyList<Window>)RcWindow.OpenWindows;

    public bool AnyWindowActive => Windows.Any(window => window.IsActive);

    public void Flash(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        Avalonia.Platform.IPlatformHandle? platform = window.TryGetPlatformHandle();
        IntPtr handle = platform?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;
        if (OperatingSystem.IsWindows())
        {
            var info = new FlashInfo
            {
                Size = (uint)Marshal.SizeOf<FlashInfo>(),
                Window = handle,
                Flags = FlashTray | FlashTimerNoForeground,
                Count = 0,
                Timeout = 0,
            };
            FlashWindowEx(ref info);
        }
        else if (OperatingSystem.IsLinux() && string.Equals(platform?.HandleDescriptor, "XID", StringComparison.Ordinal))
        {
            X11.DemandAttention(handle);
        }
    }

    public void Beep() => _services.Sounds.Beep();

    public void Shutdown()
    {
        if (_lifetime is IControlledApplicationLifetime controlled) controlled.Shutdown();
    }

    private const uint FlashTray = 0x2;
    private const uint FlashTimerNoForeground = 0xC;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FlashInfo info);
}
