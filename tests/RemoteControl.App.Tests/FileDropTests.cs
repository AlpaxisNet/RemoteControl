using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using RemoteControl.App.Platform;
using RemoteControl.Core;
using RemoteControl.Ui.Common;

namespace RemoteControl.App.Tests;

/// <summary>An X display that records what the XDND receiver asks of it.</summary>
internal sealed class FakeXdndDisplay : IXdndDisplay
{
    private readonly Dictionary<string, IntPtr> _atoms = new(StringComparer.Ordinal);

    public List<(IntPtr Destination, IntPtr Type, nint[] Data)> Sent { get; } = new();

    public List<(IntPtr Selection, IntPtr Target, IntPtr Property, IntPtr Requestor, nint Time)> Conversions { get; } = new();

    public Dictionary<(IntPtr Window, IntPtr Property), IntPtr[]> AtomLists { get; } = new();

    public byte[]? Property { get; set; }

    public bool Incremental { get; set; }

    public int Aware { get; private set; } = -1;

    public IntPtr Atom(string name)
    {
        if (!_atoms.TryGetValue(name, out IntPtr atom)) _atoms[name] = atom = 100 + _atoms.Count;
        return atom;
    }

    public void SendClientMessage(IntPtr destination, IntPtr messageType, nint l0, nint l1, nint l2, nint l3, nint l4) => Sent.Add((destination, messageType, [l0, l1, l2, l3, l4]));

    public IReadOnlyList<IntPtr> ReadAtoms(IntPtr window, IntPtr property) => AtomLists.TryGetValue((window, property), out IntPtr[]? atoms) ? atoms : [];

    public void ConvertSelection(IntPtr selection, IntPtr target, IntPtr property, IntPtr requestor, nint time) => Conversions.Add((selection, target, property, requestor, time));

    public byte[]? TakeProperty(IntPtr window, IntPtr property, out bool incremental)
    {
        incremental = Incremental;
        return Incremental ? null : Property;
    }

    public (int X, int Y)? ToWindow(IntPtr window, int rootX, int rootY) => (rootX - 100, rootY - 50);

    public void SetAware(IntPtr window, int version) => Aware = version;
}

/// <summary>A drop target that records the drag and the drop.</summary>
internal sealed class RecordingDropTarget : IFileDropTarget
{
    public bool Accept { get; set; } = true;

    public List<Point> Overs { get; } = new();

    public int Ended { get; private set; }

    public List<(Point Point, IReadOnlyList<string> Paths)> Drops { get; } = new();

    public bool FileDragOver(Point point)
    {
        Overs.Add(point);
        return Accept;
    }

    public void FileDragEnded() => Ended++;

    public void FilesDropped(Point point, IReadOnlyList<string> paths) => Drops.Add((point, paths));
}

public sealed class XdndReceiverTests
{
    private const int Window = 0x4000001;
    private const int Source = 0x5000002;

    private sealed class Harness
    {
        public Harness()
        {
            Receiver = new XdndReceiver(Display, Window, Target, () => 2.0, (delay, action) =>
            {
                Timeouts.Add(action);
                return new Cancel(() => Timeouts.Remove(action));
            }, new AppLog());
        }

        public FakeXdndDisplay Display { get; } = new();

        public RecordingDropTarget Target { get; } = new();

        public List<Action> Timeouts { get; } = new();

        public XdndReceiver Receiver { get; }

        public IntPtr A(string name) => Display.Atom(name);

        public bool Message(string type, nint l0 = Source, nint l1 = 0, nint l2 = 0, nint l3 = 0, nint l4 = 0) => Receiver.HandleClientMessage(A(type), l0, l1, l2, l3, l4);

        public void Enter(params string[] types)
        {
            nint flags = (nint)5 << 24;
            if (types.Length > 3)
            {
                flags |= 1;
                Display.AtomLists[(Source, A("XdndTypeList"))] = types.Select(A).ToArray();
                Assert.True(Message("XdndEnter", l1: flags));
                return;
            }

            nint[] list = types.Select(type => (nint)A(type)).Concat([0, 0, 0]).Take(3).ToArray();
            Assert.True(Message("XdndEnter", l1: flags, l2: list[0], l3: list[1], l4: list[2]));
        }

        public void Position(int rootX, int rootY) => Assert.True(Message("XdndPosition", l2: ((nint)rootX << 16) | (nint)rootY, l3: 1000, l4: A("XdndActionCopy")));

        private sealed class Cancel(Action cancel) : IDisposable
        {
            public void Dispose() => cancel();
        }
    }

    [Fact]
    public void A_file_list_is_taken_where_it_is_dropped_and_the_source_is_told()
    {
        var h = new Harness();
        h.Receiver.Announce();
        Assert.Equal(XdndReceiver.Version, h.Display.Aware);

        h.Enter("text/plain", "text/uri-list");
        h.Position(300, 250);

        Assert.Equal([new Point(100, 100)], h.Target.Overs);
        (IntPtr destination, IntPtr type, nint[] status) = h.Display.Sent.Single();
        Assert.Equal((IntPtr)Source, destination);
        Assert.Equal(h.A("XdndStatus"), type);
        Assert.Equal([Window, 3, 0, 0, h.A("XdndActionCopy")], status);

        Assert.True(h.Message("XdndDrop", l2: 4321));
        (IntPtr selection, IntPtr target, IntPtr property, IntPtr requestor, nint time) = h.Display.Conversions.Single();
        Assert.Equal((h.A("XdndSelection"), h.A("text/uri-list"), (IntPtr)Window, (nint)4321), (selection, target, requestor, time));

        h.Display.Property = Encoding.UTF8.GetBytes("# comment\r\nfile:///home/me/start.nw\r\nfile://localhost/home/me/my%20picture.png\r\nhttp://example.com/x\r\n");
        Assert.True(h.Receiver.HandleSelectionNotify(Window, h.A("XdndSelection"), property));

        (Point point, IReadOnlyList<string> paths) = h.Target.Drops.Single();
        Assert.Equal(new Point(100, 100), point);
        Assert.Equal(["/home/me/start.nw", "/home/me/my picture.png"], paths);
        Assert.Equal(1, h.Target.Ended);
        (IntPtr finishedTo, IntPtr finishedType, nint[] finished) = h.Display.Sent[^1];
        Assert.Equal(((IntPtr)Source, h.A("XdndFinished")), (finishedTo, finishedType));
        Assert.Equal([Window, 1, h.A("XdndActionCopy"), 0, 0], finished);
        Assert.Empty(h.Timeouts);
    }

    [Fact]
    public void More_than_three_types_are_read_from_the_source()
    {
        var h = new Harness();
        h.Enter("a", "b", "c", "text/uri-list");
        h.Position(110, 60);
        Assert.Equal(3, h.Display.Sent.Single().Data[1]);
    }

    [Fact]
    public void A_drag_without_files_is_refused_and_never_asks_the_window()
    {
        var h = new Harness();
        h.Enter("text/plain", "UTF8_STRING");
        h.Position(300, 250);
        Assert.Empty(h.Target.Overs);
        Assert.Equal([Window, 2, 0, 0, 0], h.Display.Sent.Single().Data);

        Assert.True(h.Message("XdndDrop", l2: 1));
        Assert.Empty(h.Display.Conversions);
        Assert.Equal(h.A("XdndFinished"), h.Display.Sent[^1].Type);
        Assert.Equal(0, h.Display.Sent[^1].Data[1]);
        Assert.Empty(h.Target.Drops);
    }

    [Fact]
    public void A_place_the_window_refuses_is_answered_with_no()
    {
        var h = new Harness();
        h.Target.Accept = false;
        h.Enter("text/uri-list");
        h.Position(300, 250);
        Assert.Equal(2, h.Display.Sent.Single().Data[1]);
        Assert.True(h.Message("XdndDrop"));
        Assert.Empty(h.Display.Conversions);
        Assert.Equal(0, h.Display.Sent[^1].Data[1]);
    }

    [Fact]
    public void Leaving_ends_the_drag_and_other_sources_are_ignored()
    {
        var h = new Harness();
        h.Enter("text/uri-list");
        Assert.True(h.Message("XdndPosition", l0: 999, l2: (300 << 16) | 250));
        Assert.Empty(h.Display.Sent);
        Assert.True(h.Message("XdndLeave", l0: 999));
        Assert.Equal(0, h.Target.Ended);
        Assert.True(h.Message("XdndLeave"));
        Assert.Equal(1, h.Target.Ended);
        Assert.False(h.Receiver.HandleClientMessage(h.A("WM_PROTOCOLS"), 1, 2, 3, 4, 5));
    }

    [Fact]
    public void A_list_that_never_arrives_ends_the_drop_after_the_timeout()
    {
        var h = new Harness();
        h.Enter("text/uri-list");
        h.Position(300, 250);
        Assert.True(h.Message("XdndDrop"));
        Assert.False(h.Receiver.HandleSelectionNotify(Window, h.A("CLIPBOARD"), 1));
        h.Timeouts.Single()();
        Assert.Empty(h.Target.Drops);
        Assert.Equal(0, h.Display.Sent[^1].Data[1]);
        Assert.False(h.Receiver.HandleSelectionNotify(Window, h.A("XdndSelection"), 1));
    }

    [Fact]
    public void A_list_sent_in_pieces_is_not_taken_but_answered()
    {
        var h = new Harness();
        h.Enter("text/uri-list");
        h.Position(300, 250);
        h.Message("XdndDrop");
        h.Display.Incremental = true;
        Assert.True(h.Receiver.HandleSelectionNotify(Window, h.A("XdndSelection"), h.Display.Conversions[0].Property));
        Assert.Empty(h.Target.Drops);
        Assert.Equal(h.A("XdndFinished"), h.Display.Sent[^1].Type);
    }

    [Fact]
    public void Withdrawing_takes_the_mark_away()
    {
        var h = new Harness();
        h.Receiver.Announce();
        h.Receiver.Withdraw();
        Assert.Equal(0, h.Display.Aware);
    }
}

public sealed class AvaloniaX11EventsTests
{
    [StructLayout(LayoutKind.Sequential, Size = 192)]
    internal struct FakeEvent
    {
        public int Type;
    }

    internal delegate void FakeHandler(ref FakeEvent xevent);

    private sealed class Window
    {
        public List<int> Seen { get; } = new();

        public void OnEvent(ref FakeEvent xevent) => Seen.Add(xevent.Type);
    }

    [Fact]
    public void A_hook_sees_the_events_first_and_passes_on_what_it_does_not_take()
    {
        var window = new Window();
        var table = new Dictionary<IntPtr, FakeHandler> { [(IntPtr)42] = window.OnEvent };
        var taken = new List<int>();
        IDisposable? hook = AvaloniaX11Events.Intercept(table, 42, pointer =>
        {
            int type = Marshal.ReadInt32(pointer);
            if (type != 33) return false;
            taken.Add(type);
            return true;
        }, new AppLog(), out string? problem);
        Assert.NotNull(hook);
        Assert.Null(problem);

        var message = new FakeEvent { Type = 33 };
        var motion = new FakeEvent { Type = 6 };
        table[42](ref message);
        table[42](ref motion);
        Assert.Equal([33], taken);
        Assert.Equal([6], window.Seen);

        hook!.Dispose();
        table[42](ref message);
        Assert.Equal([6, 33], window.Seen);
    }

    [Fact]
    public void A_failing_hook_passes_the_event_on_and_a_removed_window_stays_removed()
    {
        var window = new Window();
        var table = new Dictionary<IntPtr, FakeHandler> { [(IntPtr)7] = window.OnEvent };
        IDisposable hook = AvaloniaX11Events.Intercept(table, 7, _ => throw new InvalidOperationException("boom"), new AppLog(), out _)!;
        var xevent = new FakeEvent { Type = 33 };
        table[7](ref xevent);
        Assert.Equal([33], window.Seen);
        table.Remove(7);
        hook.Dispose();
        Assert.Empty(table);
        Assert.Null(AvaloniaX11Events.Intercept(table, 7, _ => true, new AppLog(), out string? problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void Avalonia_still_keeps_its_x11_events_where_the_bridge_reaches_them()
    {
        // The bridge reads members of Avalonia 11's X11 platform that are not public; a new Avalonia version that moves them fails here.
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Assert.NotNull(typeof(TopLevel).GetProperty("PlatformImpl", instance));
        Type? x11Window = Type.GetType("Avalonia.X11.X11Window, Avalonia.X11");
        Assert.NotNull(x11Window);
        FieldInfo? platformField = x11Window!.GetField("_platform", instance);
        Assert.NotNull(platformField);
        PropertyInfo? windows = platformField!.FieldType.GetProperty("Windows", instance);
        Assert.NotNull(windows);
        Assert.True(typeof(IDictionary).IsAssignableFrom(windows!.PropertyType));
        Assert.Equal(typeof(IntPtr), platformField.FieldType.GetProperty("Display", instance)?.PropertyType);
        Type handler = windows.PropertyType.GetGenericArguments()[1];
        MethodInfo invoke = handler.GetMethod("Invoke")!;
        Assert.Equal(typeof(void), invoke.ReturnType);
        Type parameter = invoke.GetParameters().Single().ParameterType;
        Assert.True(parameter.IsByRef);
        Assert.True(Marshal.SizeOf(parameter.GetElementType()!) >= 24 * IntPtr.Size);
    }

    [AvaloniaFact]
    public void A_window_that_is_no_x11_window_takes_no_part()
    {
        if (!OperatingSystem.IsLinux()) return;
        var window = new Avalonia.Controls.Window();
        Assert.Null(new X11FileDropBridge(new AppLog()).Attach(window, new RecordingDropTarget()));
        Assert.False(AvaloniaX11Events.TryGet(window, out _, out _, out string? problem));
        Assert.Contains("not an Avalonia X11 window", problem, StringComparison.Ordinal);
    }
}
