using System.Text;
using Avalonia;
using RemoteControl.Core;
using RemoteControl.Ui.Common;

namespace RemoteControl.App.Platform;

/// <summary>What <see cref="XdndReceiver"/> needs of an X display: atoms, messages, properties, the selection and coordinates.</summary>
internal interface IXdndDisplay
{
    /// <summary>The atom of a name (made when missing).</summary>
    IntPtr Atom(string name);

    /// <summary>Sends a 32-bit client message to a window (its <c>window</c> field is that window, no event mask).</summary>
    void SendClientMessage(IntPtr destination, IntPtr messageType, nint l0, nint l1, nint l2, nint l3, nint l4);

    /// <summary>A property of a window as a list of atoms (the source's <c>XdndTypeList</c>); empty when it is missing.</summary>
    IReadOnlyList<IntPtr> ReadAtoms(IntPtr window, IntPtr property);

    /// <summary>Asks the owner of a selection to convert it into a property of a window (<c>XConvertSelection</c>).</summary>
    void ConvertSelection(IntPtr selection, IntPtr target, IntPtr property, IntPtr requestor, nint time);

    /// <summary>Reads and deletes a property of 8-bit data; null when it is missing or comes in pieces (<c>INCR</c>, then <paramref name="incremental"/>).</summary>
    byte[]? TakeProperty(IntPtr window, IntPtr property, out bool incremental);

    /// <summary>A point of the root window in a window's pixels, or null.</summary>
    (int X, int Y)? ToWindow(IntPtr window, int rootX, int rootY);

    /// <summary>Marks a window as a drop target of an XDND version (<c>XdndAware</c>), or removes the mark with 0.</summary>
    void SetAware(IntPtr window, int version);
}

/// <summary>
/// The target side of the X drag-and-drop protocol (XDND, freedesktop.org, version 5) for one top-level window: it takes the
/// <c>XdndEnter</c>, <c>XdndPosition</c>, <c>XdndLeave</c> and <c>XdndDrop</c> messages a file manager sends, answers with
/// <c>XdndStatus</c> and <c>XdndFinished</c>, fetches the dropped files as <c>text/uri-list</c> through the <c>XdndSelection</c>, and hands
/// the drag and the drop to an <see cref="IFileDropTarget"/>. Only file lists are taken; the action is always copy. Used on the thread
/// that receives the window's X events (Avalonia's UI thread); nothing it receives can throw out of it.
/// </summary>
internal sealed class XdndReceiver
{
    /// <summary>The protocol version announced in <c>XdndAware</c>.</summary>
    public const int Version = 5;

    /// <summary>How long the dropped list may take to arrive.</summary>
    public static readonly TimeSpan SelectionTimeout = TimeSpan.FromSeconds(5);

    private readonly IXdndDisplay _display;
    private readonly IntPtr _window;
    private readonly IFileDropTarget _target;
    private readonly Func<double> _scaling;
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private readonly AppLog _log;
    private readonly IntPtr _enter, _position, _status, _leave, _drop, _finished, _actionCopy, _selection, _typeList, _uriList, _property;
    private IntPtr _source;
    private int _version;
    private bool _offersFiles;
    private bool _accepted;
    private Point _point;
    private IntPtr _dropSource;
    private Point _dropPoint;
    private IDisposable? _dropTimer;

    /// <summary>Creates the receiver of a window.</summary>
    /// <param name="display">The display.</param>
    /// <param name="window">The window's X id.</param>
    /// <param name="target">Where the drag and the drop go.</param>
    /// <param name="scaling">The window's pixels per device-independent pixel.</param>
    /// <param name="schedule">Runs an action once after a time (on the same thread); the result cancels it.</param>
    /// <param name="log">The program log.</param>
    public XdndReceiver(IXdndDisplay display, IntPtr window, IFileDropTarget target, Func<double> scaling, Func<TimeSpan, Action, IDisposable> schedule, AppLog log)
    {
        _display = display ?? throw new ArgumentNullException(nameof(display));
        _window = window;
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _scaling = scaling ?? throw new ArgumentNullException(nameof(scaling));
        _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _enter = display.Atom("XdndEnter");
        _position = display.Atom("XdndPosition");
        _status = display.Atom("XdndStatus");
        _leave = display.Atom("XdndLeave");
        _drop = display.Atom("XdndDrop");
        _finished = display.Atom("XdndFinished");
        _actionCopy = display.Atom("XdndActionCopy");
        _selection = display.Atom("XdndSelection");
        _typeList = display.Atom("XdndTypeList");
        _uriList = display.Atom("text/uri-list");
        _property = display.Atom("REMOTECONTROL_XDND_FILES");
    }

    /// <summary>Marks the window as a drop target.</summary>
    public void Announce() => _display.SetAware(_window, Version);

    /// <summary>Takes the mark away and forgets a drag or drop on its way (the window closes).</summary>
    public void Withdraw()
    {
        _dropTimer?.Dispose();
        _dropTimer = null;
        _dropSource = IntPtr.Zero;
        _source = IntPtr.Zero;
        Guard(() =>
        {
            _display.SetAware(_window, 0);
            return true;
        }, false);
    }

    /// <summary>Takes a client message of the window.</summary>
    /// <returns>Whether it was an XDND message (and so is not passed on).</returns>
    public bool HandleClientMessage(IntPtr messageType, nint l0, nint l1, nint l2, nint l3, nint l4)
    {
        if (messageType == _enter) OnEnter(l0, l1, l2, l3, l4);
        else if (messageType == _position) OnPosition(l0, l2);
        else if (messageType == _leave) OnLeave(l0);
        else if (messageType == _drop) OnDrop(l0, l2);
        else return false;
        return true;
    }

    /// <summary>Takes a <c>SelectionNotify</c> event of the window.</summary>
    /// <returns>Whether it answered this receiver's request.</returns>
    public bool HandleSelectionNotify(IntPtr requestor, IntPtr selection, IntPtr property)
    {
        if (requestor != _window || selection != _selection || _dropSource == IntPtr.Zero) return false;
        byte[]? data = null;
        bool incremental = false;
        if (property != IntPtr.Zero) data = Guard(() => _display.TakeProperty(_window, property, out incremental), null);
        if (incremental) _log.Warn("Dropped files: the list was too long to be sent at once and was not taken.");
        FinishDrop(data);
        return true;
    }

    private void OnEnter(nint source, nint flags, nint type1, nint type2, nint type3)
    {
        EndDrag();
        _source = source;
        _version = (int)((flags >> 24) & 0xFF);
        IReadOnlyList<IntPtr> types = (flags & 1) != 0
            ? Guard(() => _display.ReadAtoms(source, _typeList), Array.Empty<IntPtr>())
            : new IntPtr[] { type1, type2, type3 };
        _offersFiles = types.Contains(_uriList);
        _accepted = false;
    }

    private void OnPosition(nint source, nint coordinates)
    {
        if (source == IntPtr.Zero || source != _source) return;
        int rootX = (int)((coordinates >> 16) & 0xFFFF), rootY = (int)(coordinates & 0xFFFF);
        (int X, int Y)? local = _offersFiles ? Guard(() => _display.ToWindow(_window, rootX, rootY), null) : null;
        _accepted = false;
        if (local is { } pixels)
        {
            double scaling = _scaling();
            if (!(scaling > 0)) scaling = 1;
            _point = new Point(pixels.X / scaling, pixels.Y / scaling);
            _accepted = Guard(() => _target.FileDragOver(_point), false);
        }

        // Bit 1: send a position on every move (the window says per folder where the files would go), an empty rectangle.
        Send(source, _status, _window, (_accepted ? 1 : 0) | 2, 0, 0, _accepted ? _actionCopy : 0);
    }

    private void OnLeave(nint source)
    {
        if (source != _source) return;
        EndDrag();
    }

    private void OnDrop(nint source, nint time)
    {
        if (source == IntPtr.Zero || source != _source) return;
        if (!_accepted || !_offersFiles || _dropSource != IntPtr.Zero)
        {
            Send(source, _finished, _window, 0, 0, 0, 0);
            EndDrag();
            return;
        }

        _dropSource = source;
        _dropPoint = _point;
        _source = IntPtr.Zero;
        Guard(() =>
        {
            _display.ConvertSelection(_selection, _uriList, _property, _window, _version >= 1 ? time : 0);
            return true;
        }, false);
        _dropTimer = _schedule(SelectionTimeout, () =>
        {
            if (_dropSource == IntPtr.Zero) return;
            _log.Warn("Dropped files: the file manager did not send the list of files.");
            FinishDrop(null);
        });
    }

    private void FinishDrop(byte[]? data)
    {
        IntPtr source = _dropSource;
        Point point = _dropPoint;
        _dropSource = IntPtr.Zero;
        _dropTimer?.Dispose();
        _dropTimer = null;
        IReadOnlyList<string> paths = data is null ? Array.Empty<string>() : FileDrop.ParseUriList(Encoding.UTF8.GetString(data));
        Guard(() =>
        {
            _target.FileDragEnded();
            if (paths.Count > 0) _target.FilesDropped(point, paths);
            return true;
        }, false);
        bool taken = paths.Count > 0;
        Send(source, _finished, _window, taken ? 1 : 0, taken ? _actionCopy : 0, 0, 0);
        EndDrag();
    }

    private void EndDrag()
    {
        bool dragging = _source != IntPtr.Zero;
        _source = IntPtr.Zero;
        _offersFiles = false;
        _accepted = false;
        if (dragging) Guard(() =>
        {
            _target.FileDragEnded();
            return true;
        }, false);
    }

    private void Send(IntPtr destination, IntPtr type, nint l0, nint l1, nint l2, nint l3, nint l4)
    {
        if (destination == IntPtr.Zero) return;
        Guard(() =>
        {
            _display.SendClientMessage(destination, type, l0, l1, l2, l3, l4);
            return true;
        }, false);
    }

    private T Guard<T>(Func<T> action, T fallback)
    {
        try
        {
            return action();
        }
        catch (Exception exception)
        {
            // A drop must always be answered: what fails here is logged and the protocol goes on.
            _log.Warn("Dropped files: a step of the drag and drop failed.", exception);
            return fallback;
        }
    }
}
