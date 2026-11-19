using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Threading;
using RemoteControl.Core;
using RemoteControl.Ui.Common;

namespace RemoteControl.App.Platform;

/// <summary>
/// Files dropped from a file manager onto the program's windows on Linux (X11, and XWayland under a Wayland desktop). Avalonia 11
/// does not speak XDND: a window never says it takes drops, and the messages a file manager sends it are thrown away. This bridge
/// marks each window that asks (<see cref="FileDrop.Enable"/>) as a drop target and takes its XDND messages out of Avalonia's event
/// table before Avalonia ignores them (<see cref="AvaloniaX11Events"/>), handing them to an <see cref="XdndReceiver"/>. Where that
/// is not possible (another Avalonia version, not X11) it logs why once and the window simply takes no drops, as before.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class X11FileDropBridge : IFileDropBridge
{
    private readonly AppLog _log;
    private bool _warned;

    /// <summary>Creates the bridge.</summary>
    /// <param name="log">Where problems are logged.</param>
    public X11FileDropBridge(AppLog log) => _log = log ?? throw new ArgumentNullException(nameof(log));

    /// <inheritdoc />
    public IDisposable? Attach(Window window, IFileDropTarget target)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(target);
        Avalonia.Platform.IPlatformHandle? handle = window.TryGetPlatformHandle();
        if (handle is null || handle.Handle == IntPtr.Zero || !string.Equals(handle.HandleDescriptor, "XID", StringComparison.Ordinal)) return null;
        if (!AvaloniaX11Events.TryGet(window, out IntPtr display, out IDictionary? handlers, out string? problem))
        {
            Warn(problem ?? "Avalonia's X11 platform was not found.");
            return null;
        }

        IntPtr xid = handle.Handle;
        var receiver = new XdndReceiver(new XlibXdndDisplay(display), xid, target, () => window.RenderScaling,
            (delay, action) => DispatcherTimer.RunOnce(() => UiSafe.Run(action, "FileDrop.Timeout"), delay), _log);
        IDisposable? hook = AvaloniaX11Events.Intercept(handlers!, xid, xevent => Handle(receiver, xid, xevent), _log, out problem);
        if (hook is null)
        {
            Warn(problem ?? "The window's X11 events could not be read.");
            return null;
        }

        receiver.Announce();
        return new Detach(() =>
        {
            bool stillThere = handlers!.Contains(xid);
            hook.Dispose();
            if (stillThere) receiver.Withdraw();
        });
    }

    private void Warn(string problem)
    {
        if (_warned) return;
        _warned = true;
        _log.Warn("Files cannot be dropped onto windows from the file manager: " + problem);
    }

    /// <summary>Reads the XDND events of one window from a raw <c>XEvent</c> (Xlib's layout, the long fields as wide as a pointer).</summary>
    private static unsafe bool Handle(XdndReceiver receiver, IntPtr xid, IntPtr xevent)
    {
        int size = IntPtr.Size;
        byte* raw = (byte*)xevent;
        int type = *(int*)raw;
        IntPtr window = *(IntPtr*)(raw + 4 * size);
        if (window != xid) return false;
        if (type == X11.ClientMessage)
        {
            if (*(int*)(raw + 6 * size) != 32) return false;
            nint* data = (nint*)(raw + 7 * size);
            return receiver.HandleClientMessage(*(IntPtr*)(raw + 5 * size), data[0], data[1], data[2], data[3], data[4]);
        }

        // XSelectionEvent: requestor (the window field), selection, target, property, time.
        if (type == X11.SelectionNotify) return receiver.HandleSelectionNotify(window, *(IntPtr*)(raw + 5 * size), *(IntPtr*)(raw + 7 * size));
        return false;
    }

    private sealed class Detach(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

/// <summary>
/// Reaches into Avalonia 11's X11 platform (not public API; checked by a test against the Avalonia version in use): the display
/// connection of the UI thread, and the table that hands each X event to the window it is for, keyed by X window id. A window's
/// entry is wrapped so that a handler sees the event first; whatever it does not take goes on to Avalonia unchanged.
/// </summary>
internal static class AvaloniaX11Events
{
    private delegate void RefHandler<T>(ref T xevent) where T : struct;

    /// <summary>Finds Avalonia's X11 platform of a window.</summary>
    /// <param name="window">The window.</param>
    /// <param name="display">The display connection Avalonia uses on the UI thread.</param>
    /// <param name="handlers">The event table (<c>Dictionary&lt;IntPtr, X11EventDispatcher.EventHandler&gt;</c>).</param>
    /// <param name="problem">Why not, when it fails.</param>
    /// <returns>Whether it was found.</returns>
    public static bool TryGet(TopLevel window, out IntPtr display, out IDictionary? handlers, out string? problem)
    {
        display = IntPtr.Zero;
        handlers = null;
        problem = null;
        try
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            object? impl = typeof(TopLevel).GetProperty("PlatformImpl", instance)?.GetValue(window);
            if (impl is null || impl.GetType().FullName != "Avalonia.X11.X11Window")
            {
                problem = "the window is not an Avalonia X11 window (" + (impl?.GetType().FullName ?? "none") + ").";
                return false;
            }

            object? platform = impl.GetType().GetField("_platform", instance)?.GetValue(impl);
            handlers = platform?.GetType().GetProperty("Windows", instance)?.GetValue(platform) as IDictionary;
            object? connection = platform?.GetType().GetProperty("Display", instance)?.GetValue(platform);
            if (handlers is null || connection is not IntPtr pointer || pointer == IntPtr.Zero)
            {
                problem = "this Avalonia version keeps its X11 events elsewhere.";
                handlers = null;
                return false;
            }

            display = pointer;
            return true;
        }
        catch (Exception exception) when (exception is TargetException or TargetInvocationException or MemberAccessException or InvalidCastException or NotSupportedException)
        {
            problem = exception.Message;
            return false;
        }
    }

    /// <summary>Puts a handler in front of a window's entry of the event table, until the result is disposed.</summary>
    /// <param name="handlers">The table.</param>
    /// <param name="xid">The window's X id.</param>
    /// <param name="handler">Gets a pointer to each <c>XEvent</c> of the window; true takes it (Avalonia does not see it).</param>
    /// <param name="log">Where a failing handler is logged (the event then goes on to Avalonia).</param>
    /// <param name="problem">Why not, when it fails.</param>
    /// <returns>The registration, or null.</returns>
    public static IDisposable? Intercept(IDictionary handlers, IntPtr xid, Func<IntPtr, bool> handler, AppLog log, out string? problem)
    {
        problem = null;
        try
        {
            if (handlers[xid] is not Delegate original || original.GetInvocationList().Length != 1 || original.Target is null)
            {
                problem = "the window has no X11 event handler.";
                return null;
            }

            Type delegateType = original.GetType();
            MethodInfo? invoke = delegateType.GetMethod("Invoke");
            ParameterInfo[] parameters = invoke?.GetParameters() ?? Array.Empty<ParameterInfo>();
            Type? eventType = parameters.Length == 1 && parameters[0].ParameterType.IsByRef ? parameters[0].ParameterType.GetElementType() : null;
            if (invoke is null || invoke.ReturnType != typeof(void) || eventType is not { IsValueType: true } || Marshal.SizeOf(eventType) < 24 * IntPtr.Size)
            {
                problem = "the X11 event handler has another shape in this Avalonia version.";
                return null;
            }

            Delegate forward = Delegate.CreateDelegate(typeof(RefHandler<>).MakeGenericType(eventType), original.Target, original.Method);
            var hook = new Hook(handler, forward, log);
            MethodInfo dispatch = typeof(AvaloniaX11Events).GetMethod(nameof(Dispatch), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(eventType);
            Delegate wrapper = Delegate.CreateDelegate(delegateType, hook, dispatch);
            handlers[xid] = wrapper;
            return new Unhook(() =>
            {
                // Avalonia removes the entry when the window is destroyed; only an entry that is still ours is given back.
                if (handlers.Contains(xid) && ReferenceEquals(handlers[xid], wrapper)) handlers[xid] = original;
            });
        }
        catch (Exception exception) when (exception is ArgumentException or MemberAccessException or InvalidOperationException or NotSupportedException or TargetInvocationException)
        {
            problem = exception.Message;
            return null;
        }
    }

    private static unsafe void Dispatch<T>(Hook hook, ref T xevent) where T : struct
    {
        bool taken = false;
        try
        {
            taken = hook.Handler((IntPtr)Unsafe.AsPointer(ref xevent));
        }
        catch (Exception exception)
        {
            hook.Log.Warn("An X11 event of a window could not be read; it goes on to Avalonia.", exception);
        }

        if (!taken) ((RefHandler<T>)hook.Forward)(ref xevent);
    }

    private sealed class Hook(Func<IntPtr, bool> handler, Delegate forward, AppLog log)
    {
        public Func<IntPtr, bool> Handler { get; } = handler;

        public Delegate Forward { get; } = forward;

        public AppLog Log { get; } = log;
    }

    private sealed class Unhook(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

/// <summary>The XDND requests on Avalonia's display connection (on its UI thread, where the events arrive).</summary>
[SupportedOSPlatform("linux")]
internal sealed unsafe class XlibXdndDisplay(IntPtr display) : IXdndDisplay
{
    private const int PropModeReplace = 0;
    private static readonly IntPtr XaAtom = 4;
    private readonly Dictionary<string, IntPtr> _atoms = new(StringComparer.Ordinal);

    public IntPtr Atom(string name)
    {
        if (!_atoms.TryGetValue(name, out IntPtr atom)) _atoms[name] = atom = X11.XInternAtom(display, name, 0);
        return atom;
    }

    public void SendClientMessage(IntPtr destination, IntPtr messageType, nint l0, nint l1, nint l2, nint l3, nint l4)
    {
        int size = IntPtr.Size;
        byte* message = stackalloc byte[24 * 8];
        new Span<byte>(message, 24 * 8).Clear();
        *(int*)message = X11.ClientMessage;
        *(int*)(message + 2 * size) = 1;
        *(IntPtr*)(message + 3 * size) = display;
        *(IntPtr*)(message + 4 * size) = destination;
        *(IntPtr*)(message + 5 * size) = messageType;
        *(int*)(message + 6 * size) = 32;
        nint* data = (nint*)(message + 7 * size);
        data[0] = l0;
        data[1] = l1;
        data[2] = l2;
        data[3] = l3;
        data[4] = l4;
        X11.XSendEvent(display, destination, 0, IntPtr.Zero, (IntPtr)message);
        X11.XFlush(display);
    }

    public IReadOnlyList<IntPtr> ReadAtoms(IntPtr window, IntPtr property)
    {
        if (X11.XGetWindowProperty(display, window, property, 0, 1024, 0, XaAtom, out _, out int format, out nuint count, out _, out IntPtr data) != 0 || data == IntPtr.Zero)
            return Array.Empty<IntPtr>();
        try
        {
            if (format != 32) return Array.Empty<IntPtr>();
            var atoms = new IntPtr[(int)Math.Min(count, 1024)];
            for (int index = 0; index < atoms.Length; index++) atoms[index] = ((IntPtr*)data)[index];
            return atoms;
        }
        finally
        {
            X11.XFree(data);
        }
    }

    public void ConvertSelection(IntPtr selection, IntPtr target, IntPtr property, IntPtr requestor, nint time)
    {
        X11.XConvertSelection(display, selection, target, property, requestor, time);
        X11.XFlush(display);
    }

    public byte[]? TakeProperty(IntPtr window, IntPtr property, out bool incremental)
    {
        incremental = false;
        // Up to 16 MiB (the length counts 32-bit units); a file list is far smaller.
        if (X11.XGetWindowProperty(display, window, property, 0, 4 * 1024 * 1024, 1, IntPtr.Zero, out IntPtr type, out int format, out nuint count, out _, out IntPtr data) != 0)
            return null;
        try
        {
            if (type == Atom("INCR"))
            {
                incremental = true;
                return null;
            }

            if (data == IntPtr.Zero || format != 8 || count > int.MaxValue) return null;
            return new ReadOnlySpan<byte>((void*)data, (int)count).ToArray();
        }
        finally
        {
            if (data != IntPtr.Zero) X11.XFree(data);
        }
    }

    public (int X, int Y)? ToWindow(IntPtr window, int rootX, int rootY)
    {
        IntPtr root = X11.XDefaultRootWindow(display);
        return X11.XTranslateCoordinates(display, root, window, rootX, rootY, out int x, out int y, out _) != 0 ? (x, y) : null;
    }

    public void SetAware(IntPtr window, int version)
    {
        IntPtr aware = Atom("XdndAware");
        if (version <= 0) X11.XDeleteProperty(display, window, aware);
        else
        {
            nint value = version;
            X11.XChangeProperty(display, window, aware, XaAtom, 32, PropModeReplace, (IntPtr)(&value), 1);
        }

        X11.XFlush(display);
    }
}

/// <summary>The Xlib calls of the XDND bridge.</summary>
internal static partial class X11
{
    /// <summary>The event type of a <c>SelectionNotify</c>.</summary>
    public const int SelectionNotify = 31;

    [LibraryImport(Library)]
    internal static partial int XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property, nint offset, nint length, int delete, IntPtr requestedType,
        out IntPtr actualType, out int actualFormat, out nuint count, out nuint bytesAfter, out IntPtr data);

    [LibraryImport(Library)]
    internal static partial int XChangeProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr type, int format, int mode, IntPtr data, int count);

    [LibraryImport(Library)]
    internal static partial int XDeleteProperty(IntPtr display, IntPtr window, IntPtr property);

    [LibraryImport(Library)]
    internal static partial int XConvertSelection(IntPtr display, IntPtr selection, IntPtr target, IntPtr property, IntPtr requestor, nint time);

    [LibraryImport(Library)]
    internal static partial int XTranslateCoordinates(IntPtr display, IntPtr source, IntPtr destination, int sourceX, int sourceY, out int x, out int y, out IntPtr child);

    [LibraryImport(Library)]
    internal static partial int XFree(IntPtr data);
}
