using Avalonia.Controls;
using RemoteControl.Core;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Scripts.Tests;

internal static class Helpers
{
    /// <summary>A session with the NC connected and the script tools registered.</summary>
    public static TestSession Session(string? theme = null, Func<RcOptions, RcOptions>? options = null, bool nc = true)
    {
        var test = new TestSession(theme, options: options);
        test.Fake.IsNcConnected = nc;
        test.Fake.IsNcAuthenticated = nc;
        ScriptsFeature.Register(test.Tools);
        return test;
    }

    public static T Open<T>(TestSession test, string tool, object? argument = null) where T : Window
    {
        Window? window = test.Context.OpenTool(tool, argument);
        Assert.NotNull(window);
        TestUi.Settle();
        return Assert.IsType<T>(window);
    }

    public static EditorWindow OpenEditor(TestSession test, ScriptRequest request) => Open<EditorWindow>(test, ToolIds.ScriptEditor, request);

    public static T FindWindow<T>() where T : RcWindow
    {
        T? found = null;
        TestUi.WaitUntil(() => (found = RcWindow.OpenWindows.OfType<T>().LastOrDefault(window => window.IsVisible)) is not null, what: typeof(T).Name);
        return found!;
    }

    public static async Task<T> FindWindowAsync<T>() where T : RcWindow
    {
        T? found = null;
        await TestUi.WaitUntilAsync(() => (found = RcWindow.OpenWindows.OfType<T>().LastOrDefault(window => window.IsVisible)) is not null, what: typeof(T).Name);
        return found!;
    }

    public static IReadOnlyList<EditorWindow> Editors(TestSession test) => test.Context.Windows.All.OfType<EditorWindow>().ToArray();
}
