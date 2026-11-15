using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RemoteControl.App.Platform;

namespace RemoteControl.App.Hotkeys;

/// <summary>
/// The global hotkey on X11: <c>XGrabKey</c> on the root window, for the four Num Lock / Caps Lock combinations as the C++ did, on a
/// display connection and thread of the program's own. A grab another program owns answers <c>BadAccess</c>, which is recorded by
/// <see cref="X11"/>'s error handler and reported as a failure instead of aborting the process (defect D4).
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed unsafe class X11HotkeyBackend : IHotkeyBackend
{
    private static readonly uint[] IgnoredModifiers = [0, X11.LockMask, X11.Mod2Mask, X11.LockMask | X11.Mod2Mask];

    private readonly ConcurrentQueue<(Hotkey? Hotkey, TaskCompletionSource<HotkeyResult> Done)> _requests = new();
    private readonly Thread _thread;
    private readonly IntPtr _display;
    private int _keycode;
    private uint _modifiers;
    private volatile bool _stop;

    /// <summary>Opens the display and starts the event thread; <see cref="UnsupportedReason"/> says when there is none.</summary>
    public X11HotkeyBackend()
    {
        UnsupportedReason = X11.Unavailable();
        if (UnsupportedReason is null)
        {
            _display = X11.Open();
            if (_display == IntPtr.Zero) UnsupportedReason = "The X display could not be opened.";
        }
        _thread = new Thread(Loop) { IsBackground = true, Name = "Global hotkey" };
        if (UnsupportedReason is null) _thread.Start();
    }

    public string? UnsupportedReason { get; }

    public event EventHandler? Pressed;

    public Task<HotkeyResult> SetAsync(Hotkey? hotkey)
    {
        if (UnsupportedReason is not null) return Task.FromResult(hotkey is null ? HotkeyResult.Ok : HotkeyResult.Fail(UnsupportedReason));
        if (_stop) return Task.FromResult(HotkeyResult.Fail("The hotkey is released."));
        var done = new TaskCompletionSource<HotkeyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests.Enqueue((hotkey, done));
        return done.Task;
    }

    public void Dispose()
    {
        if (_stop) return;
        _stop = true;
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
        else if (_display != IntPtr.Zero) X11.Close(_display);
    }

    private void Loop()
    {
        byte* xevent = stackalloc byte[192];
        IntPtr root = X11.XDefaultRootWindow(_display);
        var poll = new X11.PollFd { Fd = X11.XConnectionNumber(_display), Events = 1 };
        try
        {
            while (!_stop)
            {
                while (_requests.TryDequeue(out var request)) request.Done.TrySetResult(Grab(root, request.Hotkey));
                while (X11.XPending(_display) > 0)
                {
                    X11.XNextEvent(_display, (IntPtr)xevent);
                    if (*(int*)xevent != X11.KeyPress) continue;
                    uint state = *(uint*)(xevent + 80);
                    uint keycode = *(uint*)(xevent + 84);
                    if (keycode == _keycode && (state & (X11.ShiftMask | X11.ControlMask | X11.Mod1Mask | X11.Mod4Mask)) == _modifiers) RaisePressed();
                }
                poll.Revents = 0;
                X11.poll(ref poll, 1, 100);
            }
        }
        catch (Exception)
        {
            // An Xlib failure ends the hotkey, never the program.
        }
        finally
        {
            try
            {
                Ungrab(root);
                X11.XSync(_display, 0);
            }
            catch (Exception)
            {
                // The display is closed next.
            }
            X11.Close(_display);
            while (_requests.TryDequeue(out var request)) request.Done.TrySetResult(HotkeyResult.Fail("The hotkey is released."));
        }
    }

    private HotkeyResult Grab(IntPtr root, Hotkey? hotkey)
    {
        try
        {
            Ungrab(root);
            X11.SyncAndTakeError(_display);
            if (hotkey is null) return HotkeyResult.Ok;
            IntPtr keysym = hotkey.X11KeysymName is { } name ? X11.XStringToKeysym(name) : (IntPtr)hotkey.X11CharacterKeysym;
            if (keysym == IntPtr.Zero) return HotkeyResult.Fail("X11 does not know the key \"" + (hotkey.X11KeysymName ?? hotkey.Character.ToString()) + "\".");
            int keycode = X11.XKeysymToKeycode(_display, keysym);
            if (keycode == 0) return HotkeyResult.Fail("The keyboard has no such key.");
            uint modifiers = 0;
            if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Control)) modifiers |= X11.ControlMask;
            if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt)) modifiers |= X11.Mod1Mask;
            if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Shift)) modifiers |= X11.ShiftMask;
            if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Super)) modifiers |= X11.Mod4Mask;
            foreach (uint ignored in IgnoredModifiers) X11.XGrabKey(_display, keycode, modifiers | ignored, root, 1, 1, 1);
            byte error = X11.SyncAndTakeError(_display);
            _keycode = keycode;
            _modifiers = modifiers;
            if (error == 0) return HotkeyResult.Ok;
            Ungrab(root);
            X11.SyncAndTakeError(_display);
            return HotkeyResult.Fail(error == X11.BadAccess
                ? "Another program already uses this key combination."
                : "The X server refused the hotkey (error " + error + ").");
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return HotkeyResult.Fail(exception.Message);
        }
    }

    private void Ungrab(IntPtr root)
    {
        if (_keycode == 0) return;
        foreach (uint ignored in IgnoredModifiers) X11.XUngrabKey(_display, _keycode, _modifiers | ignored, root);
        _keycode = 0;
        _modifiers = 0;
    }

    private void RaisePressed()
    {
        try
        {
            Pressed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // The handler posts to the UI thread.
        }
    }
}
