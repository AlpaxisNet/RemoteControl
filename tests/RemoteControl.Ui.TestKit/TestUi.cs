using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RemoteControl.Ui.Common;

namespace RemoteControl.Ui.TestKit;

/// <summary>
/// Input and lookup helpers for headless window tests: find controls, click, type, press keys, and wait for the dispatcher. Every
/// method runs on the headless UI thread (inside an <c>[AvaloniaFact]</c>).
/// </summary>
/// <example>
/// <code>
/// var ok = TestUi.Find&lt;Button&gt;(window, "OkButton");
/// TestUi.Click(ok);
/// TestUi.TypeInto(TestUi.Find&lt;TextBox&gt;(window, "Nickname"), "Graal123");
/// TestUi.PressKey(window, Key.Enter);
/// TestUi.WaitUntil(() =&gt; list.ItemCount == 3);
/// RcDialog dialog = TestUi.FindDialog();
/// </code>
/// </example>
public static class TestUi
{
    /// <summary>The default time <see cref="WaitUntil"/> waits.</summary>
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(30);

    /// <summary>The shortest time a wait lasts before it gives up, whatever limit a test names.</summary>
    public static TimeSpan MinimumTimeout { get; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The limit of a wait. A wait ends as soon as its condition holds, so a generous limit costs a passing test nothing, while a
    /// short one made tests fail on build machines that are several times slower than a desktop: a limit below
    /// <see cref="MinimumTimeout"/> is raised to it.
    /// </summary>
    private static TimeSpan Limit(TimeSpan? timeout)
    {
        TimeSpan limit = timeout ?? DefaultTimeout;
        return limit < MinimumTimeout ? MinimumTimeout : limit;
    }

    /// <summary>Runs the queued dispatcher jobs and a render tick a few times, so that layout, bindings and posted work are done.</summary>
    public static void Settle()
    {
        for (int pass = 0; pass < 3; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Runs the dispatcher until <paramref name="condition"/> holds. Dispatcher timers (animations, debouncers) only fire while the
    /// test awaits; wait for them with <see cref="WaitUntilAsync"/>.
    /// </summary>
    /// <param name="condition">The condition.</param>
    /// <param name="timeout">How long to wait; <see cref="DefaultTimeout"/> when <c>null</c>.</param>
    /// <param name="what">What is waited for, for the failure message.</param>
    /// <exception cref="TimeoutException">The condition did not hold in time.</exception>
    public static void WaitUntil(Func<bool> condition, TimeSpan? timeout = null, string? what = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var watch = Stopwatch.StartNew();
        TimeSpan limit = Limit(timeout);
        while (true)
        {
            Settle();
            if (condition()) return;
            if (watch.Elapsed > limit) throw new TimeoutException("Timed out waiting" + (what is null ? "." : " for " + what + "."));
            Thread.Sleep(5);
        }
    }

    /// <summary>Awaits until <paramref name="condition"/> holds, letting asynchronous work continue in between.</summary>
    /// <param name="condition">The condition.</param>
    /// <param name="timeout">How long to wait; <see cref="DefaultTimeout"/> when <c>null</c>.</param>
    /// <param name="what">What is waited for, for the failure message.</param>
    /// <returns>Completes when the condition holds.</returns>
    /// <exception cref="TimeoutException">The condition did not hold in time.</exception>
    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null, string? what = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var watch = Stopwatch.StartNew();
        TimeSpan limit = Limit(timeout);
        while (true)
        {
            Settle();
            if (condition()) return;
            if (watch.Elapsed > limit) throw new TimeoutException("Timed out waiting" + (what is null ? "." : " for " + what + "."));
            await Task.Delay(5).ConfigureAwait(true);
        }
    }

    /// <summary>The control named <paramref name="name"/> below <paramref name="root"/> (logical tree first, then visual tree).</summary>
    /// <typeparam name="T">The control type.</typeparam>
    /// <param name="root">Where to search.</param>
    /// <param name="name">The control's <c>Name</c>.</param>
    /// <returns>The control.</returns>
    /// <exception cref="InvalidOperationException">There is no such control.</exception>
    public static T Find<T>(Visual root, string name) where T : Control =>
        TryFind<T>(root, name) ?? throw new InvalidOperationException($"No {typeof(T).Name} named '{name}' in {root.GetType().Name}.");

    /// <summary>The control named <paramref name="name"/>, or <c>null</c>.</summary>
    /// <typeparam name="T">The control type.</typeparam>
    /// <param name="root">Where to search.</param>
    /// <param name="name">The control's <c>Name</c>.</param>
    /// <returns>The control or <c>null</c>.</returns>
    public static T? TryFind<T>(Visual root, string name) where T : Control
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root is ILogical logical)
            foreach (ILogical item in logical.GetSelfAndLogicalDescendants())
                if (item is T control && control.Name == name) return control;
        foreach (Visual item in root.GetSelfAndVisualDescendants())
            if (item is T control && control.Name == name) return control;
        return null;
    }

    /// <summary>Every control of a type below <paramref name="root"/> (visual tree), in tree order.</summary>
    /// <typeparam name="T">The control type.</typeparam>
    /// <param name="root">Where to search.</param>
    /// <returns>The controls.</returns>
    public static IReadOnlyList<T> FindAll<T>(Visual root) where T : Visual
    {
        ArgumentNullException.ThrowIfNull(root);
        return root.GetSelfAndVisualDescendants().OfType<T>().ToArray();
    }

    /// <summary>The first control of a type below <paramref name="root"/>.</summary>
    /// <typeparam name="T">The control type.</typeparam>
    /// <param name="root">Where to search.</param>
    /// <returns>The control.</returns>
    /// <exception cref="InvalidOperationException">There is none.</exception>
    public static T First<T>(Visual root) where T : Visual =>
        FindAll<T>(root).FirstOrDefault() ?? throw new InvalidOperationException($"No {typeof(T).Name} in {root.GetType().Name}.");

    /// <summary>The open dialog of <see cref="Dialogs"/> (the newest one), waiting for it to open.</summary>
    /// <param name="timeout">How long to wait.</param>
    /// <returns>The dialog.</returns>
    public static RcDialog FindDialog(TimeSpan? timeout = null)
    {
        RcDialog? dialog = null;
        WaitUntil(() => (dialog = RcWindow.OpenWindows.OfType<RcDialog>().LastOrDefault(d => d.IsVisible)) is not null, timeout, "a dialog");
        return dialog!;
    }

    /// <summary>The centre of a control in its window's coordinates.</summary>
    /// <param name="control">The control (laid out in a shown window).</param>
    /// <returns>The point.</returns>
    public static Point CenterOf(Control control)
    {
        ArgumentNullException.ThrowIfNull(control);
        TopLevel top = TopLevelOf(control);
        var center = new Point(control.Bounds.Width / 2, control.Bounds.Height / 2);
        return control.TranslatePoint(center, top) ?? throw new InvalidOperationException("The control is not in its window's visual tree.");
    }

    /// <summary>Clicks a control with the left mouse button at its centre (press and release, as a user does).</summary>
    /// <param name="control">The control.</param>
    public static void Click(Control control)
    {
        Settle();
        TopLevel top = TopLevelOf(control);
        Point point = CenterOf(control);
        top.MouseMove(point);
        top.MouseDown(point, MouseButton.Left);
        if (top.PlatformImpl is not null) top.MouseUp(point, MouseButton.Left);
        Settle();
    }

    /// <summary>Presses the left mouse button at a point of a control, without releasing it.</summary>
    /// <param name="control">The control.</param>
    /// <param name="point">The point in the control's coordinates; its centre when <c>null</c>.</param>
    public static void MouseDown(Control control, Point? point = null)
    {
        TopLevel top = TopLevelOf(control);
        Point at = point is { } p ? control.TranslatePoint(p, top) ?? default : CenterOf(control);
        top.MouseMove(at);
        top.MouseDown(at, MouseButton.Left);
        Settle();
    }

    /// <summary>Releases the left mouse button at a point of a control.</summary>
    /// <param name="control">The control.</param>
    /// <param name="point">The point in the control's coordinates (may be outside it); its centre when <c>null</c>.</param>
    public static void MouseUp(Control control, Point? point = null)
    {
        TopLevel top = TopLevelOf(control);
        Point at = point is { } p ? control.TranslatePoint(p, top) ?? default : CenterOf(control);
        top.MouseMove(at);
        top.MouseUp(at, MouseButton.Left);
        Settle();
    }

    /// <summary>Types text into whatever has the keyboard focus in the window.</summary>
    /// <param name="topLevel">The window.</param>
    /// <param name="text">The text.</param>
    public static void TypeText(TopLevel topLevel, string text)
    {
        ArgumentNullException.ThrowIfNull(topLevel);
        topLevel.KeyTextInput(text);
        Settle();
    }

    /// <summary>Focuses a text box, moves the caret to its end and types text into it.</summary>
    /// <param name="box">The text box.</param>
    /// <param name="text">The text.</param>
    public static void TypeInto(TextBox box, string text)
    {
        ArgumentNullException.ThrowIfNull(box);
        box.Focus();
        box.CaretIndex = box.Text?.Length ?? 0;
        Settle();
        TypeText(TopLevelOf(box), text);
    }

    /// <summary>Presses and releases a key in the window (sent to the focused control).</summary>
    /// <param name="topLevel">The window.</param>
    /// <param name="key">The key.</param>
    /// <param name="modifiers">Modifier keys held.</param>
    public static void PressKey(TopLevel topLevel, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        ArgumentNullException.ThrowIfNull(topLevel);
        topLevel.KeyPress(key, modifiers, PhysicalKey.None, null);
        if (topLevel.PlatformImpl is not null) topLevel.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Settle();
    }

    /// <summary>The window a control is in.</summary>
    /// <param name="control">The control.</param>
    /// <returns>The window.</returns>
    public static TopLevel TopLevelOf(Visual control) =>
        TopLevel.GetTopLevel(control) ?? throw new InvalidOperationException("The control is not in a shown window.");
}
