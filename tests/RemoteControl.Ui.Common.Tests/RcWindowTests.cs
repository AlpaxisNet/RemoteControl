using Avalonia;
using Avalonia.Headless.XUnit;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Common.Tests;

public sealed class RcWindowTests
{
    private sealed class FakeSource
    {
        public event EventHandler? Fired;

        public event EventHandler<string>? Said;

        public int Subscribers => (Fired?.GetInvocationList().Length ?? 0) + (Said?.GetInvocationList().Length ?? 0);

        public void Fire() => Fired?.Invoke(this, EventArgs.Empty);

        public void Say(string text) => Said?.Invoke(this, text);
    }

    private sealed class Probe : IDisposable
    {
        public int Disposed { get; private set; }

        public void Dispose() => Disposed++;
    }

    [AvaloniaFact]
    public void Releases_subscriptions_timers_and_resources_on_close()
    {
        using var env = new TestEnvironment();
        var source = new FakeSource();
        var window = env.Show(new RcWindow(env.Services), 200, 100);
        int fired = 0;
        var said = new List<string>();
        window.Listen(h => source.Fired += h, h => source.Fired -= h, (_, _) => fired++);
        window.Listen<string>(h => source.Said += h, h => source.Said -= h, (_, text) => said.Add(text));
        RepeatingTimer timer = window.Every(TimeSpan.FromHours(1), () => { });
        var probe = window.Track(new Probe());
        int closedActions = 0;
        window.OnClosedOnce(() => closedActions++);
        CancellationToken token = window.ClosedToken;

        source.Fire();
        source.Say("hello");
        Assert.Equal(1, fired);
        Assert.Equal(["hello"], said);
        Assert.Equal(2, source.Subscribers);
        Assert.True(timer.IsRunning);
        Assert.Contains(window, RcWindow.OpenWindows);

        window.Close();
        TestUi.Settle();

        Assert.True(window.IsClosed);
        Assert.Equal(0, source.Subscribers);
        Assert.False(timer.IsRunning);
        Assert.Equal(1, probe.Disposed);
        Assert.Equal(1, closedActions);
        Assert.True(token.IsCancellationRequested);
        Assert.True(window.ClosedToken.IsCancellationRequested);
        Assert.Equal(0, window.HeldCount);
        Assert.DoesNotContain(window, RcWindow.OpenWindows);
        source.Fire();
        Assert.Equal(1, fired);

        var late = window.Track(new Probe());
        Assert.Equal(1, late.Disposed);
        window.OnClosedOnce(() => closedActions++);
        Assert.Equal(2, closedActions);
    }

    [AvaloniaFact]
    public void A_handler_that_throws_is_reported_and_others_keep_running()
    {
        using var env = new TestEnvironment();
        UiSafe.ShowErrorWindows = false;
        try
        {
            var source = new FakeSource();
            var window = env.Show(new RcWindow(env.Services), 200, 100);
            int second = 0;
            window.Listen(h => source.Fired += h, h => source.Fired -= h, (_, _) => throw new InvalidOperationException("listener"));
            window.Listen(h => source.Fired += h, h => source.Fired -= h, (_, _) => second++);
            source.Fire();
            Assert.Equal(1, second);
            Assert.Equal(1, UiSafe.ReportedCount);
        }
        finally
        {
            UiSafe.ShowErrorWindows = true;
        }
    }

    [AvaloniaFact]
    public async Task Events_raised_on_another_thread_run_on_the_ui_thread()
    {
        using var env = new TestEnvironment();
        var source = new FakeSource();
        var window = env.Show(new RcWindow(env.Services), 200, 100);
        bool onUiThread = false;
        window.Listen(h => source.Fired += h, h => source.Fired -= h, (_, _) => onUiThread = Avalonia.Threading.Dispatcher.UIThread.CheckAccess());
        await Task.Run(source.Fire);
        await TestUi.WaitUntilAsync(() => onUiThread);
    }

    [AvaloniaFact]
    public void Remembers_position_and_size_while_hidden()
    {
        using var env = new TestEnvironment();
        var window = env.Show(new RcWindow(env.Services) { HideOnClose = true }, 320, 240);
        window.Position = new PixelPoint(123, 45);
        TestUi.Settle();
        window.HideAndRemember();
        Assert.False(window.IsVisible);
        Assert.Equal(new PixelPoint(123, 45), window.RememberedPosition);
        Assert.Equal(new Size(320, 240), window.RememberedSize);

        window.Width = 50;
        window.ShowAndActivate();
        TestUi.Settle();
        Assert.True(window.IsVisible);
        Assert.Equal(new PixelPoint(123, 45), window.Position);
        Assert.Equal(320, window.Width);
        Assert.False(window.IsClosed);

        window.Close();
        TestUi.Settle();
        Assert.True(window.IsClosed);
        window.ShowAndActivate();
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public void Takes_the_application_icon()
    {
        using var env = new TestEnvironment();
        var window = new RcWindow(env.Services);
        Assert.NotNull(window.Icon);
        Assert.NotNull(env.Services.Assets.AppIcon);
        Assert.NotNull(RcWindow.DefaultIcon);
        Assert.NotNull(new RcWindow().Icon);
    }
}
