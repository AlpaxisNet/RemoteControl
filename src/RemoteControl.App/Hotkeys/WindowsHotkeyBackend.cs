using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RemoteControl.App.Hotkeys;

/// <summary>
/// The global hotkey on Windows: <c>RegisterHotKey</c> with <c>MOD_NOREPEAT</c> on a thread of its own that runs a message loop (the C++
/// registered it on the login window's handle). <c>WM_HOTKEY</c> raises <see cref="Pressed"/>; everything is released when disposed.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsHotkeyBackend : IHotkeyBackend
{
    private const int HotkeyId = 0x5244;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const uint WmHotkey = 0x0312;
    private const uint WmQuit = 0x0012;
    private const uint WmCommand = 0x8000 + 0x52;

    private readonly ConcurrentQueue<(Hotkey? Hotkey, TaskCompletionSource<HotkeyResult> Done)> _requests = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _thread;
    private uint _threadId;
    private bool _registered;
    private volatile bool _disposed;

    /// <summary>Starts the message thread.</summary>
    public WindowsHotkeyBackend()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Global hotkey" };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    public string? UnsupportedReason => null;

    public event EventHandler? Pressed;

    public Task<HotkeyResult> SetAsync(Hotkey? hotkey)
    {
        if (_disposed) return Task.FromResult(HotkeyResult.Fail("The hotkey is released."));
        var done = new TaskCompletionSource<HotkeyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests.Enqueue((hotkey, done));
        if (_threadId == 0 || !PostThreadMessageW(_threadId, WmCommand, 0, 0)) done.TrySetResult(HotkeyResult.Fail("The hotkey thread does not run."));
        return done.Task;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_threadId != 0) PostThreadMessageW(_threadId, WmQuit, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    private void Loop()
    {
        _threadId = GetCurrentThreadId();
        // Makes the thread's message queue before anyone posts to it.
        PeekMessageW(out _, IntPtr.Zero, 0, 0, 0);
        _ready.Set();
        try
        {
            while (GetMessageW(out Msg message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.Message == WmHotkey && message.WParam == HotkeyId) RaisePressed();
                else if (message.Message == WmCommand) Process();
            }
        }
        finally
        {
            if (_registered) UnregisterHotKey(IntPtr.Zero, HotkeyId);
            _registered = false;
            while (_requests.TryDequeue(out var request)) request.Done.TrySetResult(HotkeyResult.Fail("The hotkey is released."));
        }
    }

    private void Process()
    {
        while (_requests.TryDequeue(out var request))
        {
            try
            {
                request.Done.TrySetResult(Register(request.Hotkey));
            }
            catch (Exception exception)
            {
                request.Done.TrySetResult(HotkeyResult.Fail(exception.Message));
            }
        }
    }

    private HotkeyResult Register(Hotkey? hotkey)
    {
        if (_registered) UnregisterHotKey(IntPtr.Zero, HotkeyId);
        _registered = false;
        if (hotkey is null) return HotkeyResult.Ok;
        uint key = (uint)hotkey.WindowsVirtualKey;
        if (hotkey.Kind == HotkeyKeyKind.Character)
        {
            short scan = VkKeyScanW(char.ToUpperInvariant(hotkey.Character));
            key = scan == -1 ? 0 : (uint)(scan & 0xff);
        }
        if (key == 0) return HotkeyResult.Fail("This key is not known on Windows.");
        uint modifiers = ModNoRepeat;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Control)) modifiers |= ModControl;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt)) modifiers |= ModAlt;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Shift)) modifiers |= ModShift;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Super)) modifiers |= ModWin;
        if (!RegisterHotKey(IntPtr.Zero, HotkeyId, modifiers, key))
        {
            int error = Marshal.GetLastPInvokeError();
            return HotkeyResult.Fail(error == 1409 ? "Another program already uses this key combination." : "Windows refused the hotkey (error " + error + ").");
        }
        _registered = true;
        return HotkeyResult.Ok;
    }

    private void RaisePressed()
    {
        try
        {
            Pressed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // The handler posts to the UI thread; nothing here may end the message loop.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Window;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr window, int id);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(out Msg message, IntPtr window, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessageW(out Msg message, IntPtr window, uint min, uint max, uint remove);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint thread, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial short VkKeyScanW(ushort character);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
