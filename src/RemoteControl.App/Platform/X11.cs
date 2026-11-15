using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RemoteControl.App.Platform;

/// <summary>
/// The few Xlib calls the program makes itself (global hotkey, task-bar attention), each on a display connection of its own. An X
/// error on one of these connections is recorded instead of ending the process: Xlib's default error handler exits, which is what
/// made the C++ abort when another program already owned the hotkey (defect D4). Errors of other connections (Avalonia's) go to the
/// handler that was installed before.
/// </summary>
[SupportedOSPlatform("linux")]
internal static unsafe partial class X11
{
    private const string Library = "libX11.so.6";

    /// <summary>The event type of a key press.</summary>
    public const int KeyPress = 2;

    /// <summary>The event type of a client message.</summary>
    public const int ClientMessage = 33;

    /// <summary><c>BadAccess</c>: another client owns the grab.</summary>
    public const byte BadAccess = 10;

    /// <summary><c>ShiftMask</c>.</summary>
    public const uint ShiftMask = 1;

    /// <summary><c>LockMask</c> (Caps Lock).</summary>
    public const uint LockMask = 2;

    /// <summary><c>ControlMask</c>.</summary>
    public const uint ControlMask = 4;

    /// <summary><c>Mod1Mask</c> (Alt).</summary>
    public const uint Mod1Mask = 8;

    /// <summary><c>Mod2Mask</c> (Num Lock).</summary>
    public const uint Mod2Mask = 16;

    /// <summary><c>Mod4Mask</c> (Super).</summary>
    public const uint Mod4Mask = 64;

    private static readonly ConcurrentDictionary<IntPtr, byte> s_errors = new();
    private static readonly object s_gate = new();
    private static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int> s_previous;
    private static bool s_installed;

    /// <summary>Whether an X display can be used (the session is not Wayland and <c>DISPLAY</c> is set).</summary>
    /// <returns>Null when it can, else why not.</returns>
    public static string? Unavailable()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            return "Global hotkeys are not supported on Wayland (the desktop does not let a program grab a key).";
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return "There is no X display.";
        return null;
    }

    /// <summary>Opens a display connection of the program's own, with the recording error handler installed.</summary>
    /// <returns>The display, or zero.</returns>
    public static IntPtr Open()
    {
        try
        {
            InstallErrorHandler();
            IntPtr display = XOpenDisplay(IntPtr.Zero);
            if (display != IntPtr.Zero) s_errors[display] = 0;
            return display;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>Closes a display opened by <see cref="Open"/>.</summary>
    /// <param name="display">The display.</param>
    public static void Close(IntPtr display)
    {
        if (display == IntPtr.Zero) return;
        XCloseDisplay(display);
        s_errors.TryRemove(display, out _);
    }

    /// <summary>Waits for the server to process every request and returns the first X error since the last call (0 for none).</summary>
    /// <param name="display">The display.</param>
    /// <returns>The error code.</returns>
    public static byte SyncAndTakeError(IntPtr display)
    {
        XSync(display, 0);
        if (!s_errors.TryGetValue(display, out byte error)) return 0;
        s_errors[display] = 0;
        return error;
    }

    /// <summary>
    /// Asks the window manager to mark a window as wanting attention (<c>_NET_WM_STATE_DEMANDS_ATTENTION</c>, which the window manager
    /// clears when the window is activated). Best effort; never throws.
    /// </summary>
    /// <param name="window">The X window id of a top-level window.</param>
    /// <returns>Whether the request was sent without an error.</returns>
    public static bool DemandAttention(IntPtr window)
    {
        // Works on an X server and on XWayland alike (the window is an X window either way).
        if (window == IntPtr.Zero || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return false;
        IntPtr display = Open();
        if (display == IntPtr.Zero) return false;
        try
        {
            IntPtr root = XDefaultRootWindow(display);
            IntPtr state = XInternAtom(display, "_NET_WM_STATE", 0);
            IntPtr attention = XInternAtom(display, "_NET_WM_STATE_DEMANDS_ATTENTION", 0);
            byte* message = stackalloc byte[192];
            new Span<byte>(message, 192).Clear();
            *(int*)message = ClientMessage;
            *(int*)(message + 16) = 1;
            *(IntPtr*)(message + 24) = display;
            *(IntPtr*)(message + 32) = window;
            *(IntPtr*)(message + 40) = state;
            *(int*)(message + 48) = 32;
            long* data = (long*)(message + 56);
            data[0] = 1;
            data[1] = (long)attention;
            data[2] = 0;
            data[3] = 1;
            const long substructureMasks = (1L << 19) | (1L << 20);
            XSendEvent(display, root, 0, (IntPtr)substructureMasks, (IntPtr)message);
            return SyncAndTakeError(display) == 0;
        }
        finally
        {
            Close(display);
        }
    }

    private static void InstallErrorHandler()
    {
        lock (s_gate)
        {
            if (s_installed) return;
            s_previous = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)XSetErrorHandler((IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)&OnError);
            s_installed = true;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OnError(IntPtr display, IntPtr errorEvent)
    {
        try
        {
            if (s_errors.ContainsKey(display))
            {
                byte code = *((byte*)errorEvent + 32);
                if (s_errors.TryGetValue(display, out byte existing) && existing == 0) s_errors[display] = code;
                return 0;
            }
            delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int> previous = s_previous;
            return previous != null ? previous(display, errorEvent) : 0;
        }
        catch
        {
            return 0;
        }
    }

    [LibraryImport(Library)]
    internal static partial IntPtr XOpenDisplay(IntPtr name);

    [LibraryImport(Library)]
    internal static partial int XCloseDisplay(IntPtr display);

    [LibraryImport(Library)]
    internal static partial IntPtr XSetErrorHandler(IntPtr handler);

    [LibraryImport(Library)]
    internal static partial IntPtr XDefaultRootWindow(IntPtr display);

    [LibraryImport(Library)]
    internal static partial int XSync(IntPtr display, int discard);

    [LibraryImport(Library)]
    internal static partial int XFlush(IntPtr display);

    [LibraryImport(Library)]
    internal static partial int XPending(IntPtr display);

    [LibraryImport(Library)]
    internal static partial int XNextEvent(IntPtr display, IntPtr eventReturn);

    [LibraryImport(Library)]
    internal static partial int XConnectionNumber(IntPtr display);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr XStringToKeysym(string name);

    [LibraryImport(Library)]
    internal static partial byte XKeysymToKeycode(IntPtr display, IntPtr keysym);

    [LibraryImport(Library)]
    internal static partial int XGrabKey(IntPtr display, int keycode, uint modifiers, IntPtr window, int ownerEvents, int pointerMode, int keyboardMode);

    [LibraryImport(Library)]
    internal static partial int XUngrabKey(IntPtr display, int keycode, uint modifiers, IntPtr window);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr XInternAtom(IntPtr display, string name, int onlyIfExists);

    [LibraryImport(Library)]
    internal static partial int XSendEvent(IntPtr display, IntPtr window, int propagate, IntPtr eventMask, IntPtr eventSend);

    [StructLayout(LayoutKind.Sequential)]
    internal struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int poll(ref PollFd fds, nuint count, int timeout);
}
