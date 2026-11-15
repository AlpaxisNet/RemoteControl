using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Common.Tests;

public sealed class UiSafeTests
{
    [AvaloniaFact]
    public void Run_catches_logs_writes_the_crash_log_and_shows_one_window()
    {
        using var env = new TestEnvironment();
        var reported = new List<UiErrorEventArgs>();
        EventHandler<UiErrorEventArgs> handler = (_, e) => reported.Add(e);
        UiSafe.ErrorReported += handler;
        try
        {
            bool finished = UiSafe.Run(() => throw new InvalidOperationException("boom"), "Test.Run");
            TestUi.Settle();
            Assert.False(finished);
            Assert.Equal(1, UiSafe.ReportedCount);
            Assert.Equal(1, UiSafe.WindowCount);
            UiErrorEventArgs error = Assert.Single(reported);
            Assert.Equal("Test.Run", error.Context);
            Assert.True(error.WindowShown);
            Assert.Contains(env.Log.Snapshot(), line => line.Contains("Test.Run", StringComparison.Ordinal) && line.Contains("boom", StringComparison.Ordinal));
            Assert.Contains("boom", File.ReadAllText(env.Paths.CrashLogFile));
            RcDialog window = TestUi.FindDialog();
            Assert.Equal(DialogKind.Error, window.Kind);
            Assert.Null(window.Owner);
            Assert.Contains("boom", window.Message);
        }
        finally
        {
            UiSafe.ErrorReported -= handler;
        }
    }

    [AvaloniaFact]
    public void A_failing_timer_cannot_open_hundreds_of_windows()
    {
        using var env = new TestEnvironment();
        for (int index = 0; index < 200; index++) UiSafe.Report(new InvalidOperationException("again " + index), "Timer.Tick");
        TestUi.Settle();
        Assert.Equal(200, UiSafe.ReportedCount);
        Assert.Equal(1, UiSafe.WindowCount);
        Assert.Equal(199, UiSafe.SuppressedCount);
        Assert.Single(RcWindow.OpenWindows.OfType<RcDialog>());

        TestUi.FindDialog().Close();
        TestUi.Settle();
        UiSafe.Report(new InvalidOperationException("soon after"), "Timer.Tick");
        Assert.Equal(1, UiSafe.WindowCount);
    }

    [AvaloniaFact]
    public void Run_returns_values_and_fallbacks()
    {
        using var env = new TestEnvironment();
        UiSafe.ShowErrorWindows = false;
        try
        {
            Assert.Equal(5, UiSafe.Run(() => 5, "Value", -1));
            Assert.Equal(-1, UiSafe.Run<int>(() => throw new FormatException(), "Value", -1));
            Assert.True(UiSafe.Run(() => { }, "Nothing"));
            Assert.Equal(0, UiSafe.WindowCount);
        }
        finally
        {
            UiSafe.ShowErrorWindows = true;
        }
    }

    [AvaloniaFact]
    public async Task Async_work_and_handlers_are_caught()
    {
        using var env = new TestEnvironment();
        UiSafe.ShowErrorWindows = false;
        try
        {
            Assert.False(await UiSafe.RunAsync(async () => { await Task.Yield(); throw new IOException("disk"); }, "Async.Work"));
            Assert.True(await UiSafe.RunAsync(() => Task.CompletedTask, "Async.Nothing"));
            Assert.False(await UiSafe.RunAsync(() => Task.FromCanceled(new CancellationToken(true)), "Async.Cancelled"));
            Assert.Equal(1, UiSafe.ReportedCount);
            Assert.Equal("x", await UiSafe.RunAsync<string>(() => throw new IOException(), "Async.Value", "x"));

            EventHandler handler = UiSafe.AsyncHandler(async () => { await Task.Yield(); throw new InvalidOperationException("handler"); }, "Async.Handler");
            handler(this, EventArgs.Empty);
            await TestUi.WaitUntilAsync(() => UiSafe.ReportedCount == 3);

            EventHandler<string> typed = UiSafe.Handler<string>((_, text) => throw new ArgumentException(text), "Typed.Handler");
            typed(this, "bad");
            Assert.Equal(4, UiSafe.ReportedCount);
            UiSafe.Wrap(() => throw new InvalidOperationException(), "Wrapped")();
            Assert.Equal(5, UiSafe.ReportedCount);
        }
        finally
        {
            UiSafe.ShowErrorWindows = true;
        }
    }

    [AvaloniaFact]
    public void Dispatcher_exceptions_are_handled_and_the_program_keeps_running()
    {
        using var env = new TestEnvironment();
        UiSafe.ShowErrorWindows = false;
        using (UiErrors.Install(env.Log, env.Services.CrashLog))
        {
            Dispatcher.UIThread.Post(() => throw new InvalidOperationException("posted failure"));
            TestUi.Settle();
            Assert.Equal(1, UiSafe.ReportedCount);
            Assert.Contains("ui_dispatcher", File.ReadAllText(env.Paths.CrashLogFile));
            bool ranAfter = false;
            Dispatcher.UIThread.Post(() => ranAfter = true);
            TestUi.Settle();
            Assert.True(ranAfter);
        }
        UiSafe.ShowErrorWindows = true;
    }

    [AvaloniaFact]
    public void A_timer_that_keeps_failing_stops()
    {
        using var env = new TestEnvironment();
        UiSafe.ShowErrorWindows = false;
        try
        {
            int ticks = 0;
            RepeatingTimer timer = UiDispatch.Repeat(TimeSpan.FromHours(1), () => { ticks++; throw new InvalidOperationException(); }, "Failing.Timer");
            Assert.True(timer.IsRunning);
            for (int index = 0; index < 5; index++) timer.TickNow();
            Assert.Equal(RepeatingTimer.MaximumFailures, ticks);
            Assert.False(timer.IsRunning);
            Assert.Contains(env.Log.Snapshot(), line => line.Contains("was stopped", StringComparison.Ordinal));
        }
        finally
        {
            UiSafe.ShowErrorWindows = true;
        }
    }

    [AvaloniaFact]
    public async Task Debounce_runs_once_after_the_last_trigger()
    {
        using var env = new TestEnvironment();
        int runs = 0;
        using Debouncer debouncer = UiDispatch.Debounce(TimeSpan.FromMilliseconds(30), () => runs++, "Debounce");
        debouncer.Trigger();
        debouncer.Trigger();
        debouncer.Trigger();
        Assert.True(debouncer.IsPending);
        await TestUi.WaitUntilAsync(() => runs > 0);
        await Task.Delay(80);
        TestUi.Settle();
        Assert.Equal(1, runs);
        debouncer.Trigger();
        debouncer.Flush();
        Assert.Equal(2, runs);
        debouncer.Trigger();
        debouncer.Cancel();
        Assert.False(debouncer.IsPending);
    }

    [AvaloniaFact]
    public async Task InvokeAsync_and_Post_run_on_the_ui_thread()
    {
        using var env = new TestEnvironment();
        int value = await Task.Run(() => UiDispatch.InvokeAsync(() => Dispatcher.UIThread.CheckAccess() ? 42 : 0));
        Assert.Equal(42, value);
        await Assert.ThrowsAsync<FormatException>(() => UiDispatch.InvokeAsync(new Action(() => throw new FormatException())));
        bool posted = false;
        await Task.Run(() => UiDispatch.Post(() => posted = Dispatcher.UIThread.CheckAccess()));
        await TestUi.WaitUntilAsync(() => posted);
    }
}
