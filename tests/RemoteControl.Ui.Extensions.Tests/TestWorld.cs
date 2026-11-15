using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using RemoteControl.Extensions;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;
using RemoteControl.Ui.TestKit;

namespace RemoteControl.Ui.Extensions.Tests;

/// <summary>
/// One test's extensions: a <see cref="TestSession"/>, a program and a user extensions folder and a state file in its temporary
/// folder (never the user's), and the host over them. Disposing it stops the host and waits until every runtime is gone.
/// </summary>
internal sealed class TestWorld : IAsyncDisposable
{
    /// <summary>The shell script every runnable test package uses: it says hello, writes every line it reads to <c>received.txt</c>, answers actions and ends on stop.</summary>
    public const string EchoScript = """
        echo '{"display":"ready to go"}'
        while IFS= read -r line; do
          printf '%s\n' "$line" >> received.txt
          case "$line" in
            *window/action*) echo "action received" ;;
            *'"type":"stop"'*) exit 0 ;;
          esac
        done
        """;

    private ExtensionsHost? _host;

    public TestWorld(bool enabled = true, string? theme = null)
    {
        Test = new TestSession(theme: theme, options: o => o with { ExtensionsEnabled = enabled });
        string root = Path.Combine(Test.Environment.Root, "ext");
        AppRoot = Path.Combine(root, "app");
        UserRoot = Path.Combine(root, "user");
        StateFile = Path.Combine(root, "extensions.state");
        Directory.CreateDirectory(AppRoot);
        Directory.CreateDirectory(UserRoot);
        ExternalPrograms.ClearSuppressed();
    }

    public TestSession Test { get; }

    public string AppRoot { get; }

    public string UserRoot { get; }

    public string StateFile { get; }

    public ExtensionsHost Host => _host ??= CreateHost();

    public static bool ShellAvailable => !OperatingSystem.IsWindows() && File.Exists("/bin/sh");

    public ExtensionManagerOptions Options() => new()
    {
        PackageRoots = [AppRoot, UserRoot],
        StateFile = StateFile,
        StopTimeout = TimeSpan.FromMilliseconds(300),
        Log = Test.Environment.Log,
    };

    public ExtensionsHost CreateHost()
    {
        var host = new ExtensionsHost(Test.Services, Test.Shell, Options);
        Test.Shell.Provide<IExtensionActions>(host);
        _host = host;
        return host;
    }

    /// <summary>Writes a package folder with its manifest and other files.</summary>
    public string WritePackage(string root, string folder, string manifest, params (string Name, string Text)[] files)
    {
        string directory = Path.Combine(root, folder);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "extension.json"), manifest);
        foreach ((string name, string text) in files)
        {
            string path = Path.Combine(directory, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        return directory;
    }

    /// <summary>A package with the <see cref="EchoScript"/> run by <c>/bin/sh</c>, two window actions (Lint at the bottom with a picture, Format at the top), two capabilities and an output tab slot.</summary>
    public string WriteEcho(string root = "", string folder = "echo", string id = "test.echo", string name = "Echo") =>
        WritePackage(root.Length == 0 ? UserRoot : root, folder, $$"""
            {
              "id": "{{id}}", "name": "{{name}}", "version": "1.2.0", "publisher": "Test Publisher",
              "runtime": "/bin/sh", "entry": "main.sh", "api": 1,
              "capabilities": ["script-read", "file-read"],
              "ui": ["output-tab"], "themes": ["night"], "commands": ["lint-all"], "readOnlyViews": ["report"],
              "windowActions": [
                { "kind": "script-editor", "id": "lint", "label": "Lint", "placement": "bottom", "icon": "file:lint.png" },
                { "id": "fmt", "label": "Format" }
              ]
            }
            """, ("main.sh", EchoScript), ("images/lint.png", "not really a picture"));

    /// <summary>A valid package that is never launched in the test (it stays disabled).</summary>
    public string WriteIdle(string root, string folder, string id, string name) =>
        WritePackage(root, folder, $$"""{ "id": "{{id}}", "name": "{{name}}", "version": "0.1", "runtime": "/bin/sh", "entry": "main.sh" }""", ("main.sh", "exit 0"));

    /// <summary>A package whose manifest has no version (invalid).</summary>
    public string WriteInvalid(string root, string folder = "broken") =>
        WritePackage(root, folder, """{ "id": "test.broken", "name": "Broken", "runtime": "/bin/sh", "entry": "main.sh" }""");

    public void WriteState(string text) => File.WriteAllText(StateFile, text);

    /// <summary>The saved state line of an extension, or null.</summary>
    public string? StateLine(string id)
    {
        try
        {
            if (!File.Exists(StateFile)) return null;
            return File.ReadAllLines(StateFile).FirstOrDefault(line => line.StartsWith(id + " ", StringComparison.Ordinal));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public ExtensionsWindow ShowWindow()
    {
        var window = (ExtensionsWindow)Host.ShowWindow(null);
        window.Width = 620;
        window.Height = 440;
        TestUi.Settle();
        return window;
    }

    public async Task WaitForStatusAsync(string id, ExtensionStatus status) =>
        await TestUi.WaitUntilAsync(() => Host.Find(id)?.Status == status, TimeSpan.FromSeconds(5), id + " " + status).ConfigureAwait(true);

    public static RcDialog? OpenDialog() => RcWindow.OpenWindows.OfType<RcDialog>().LastOrDefault(dialog => dialog.IsVisible && !dialog.IsClosed);

    public static async Task<RcDialog> WaitForDialogAsync()
    {
        await TestUi.WaitUntilAsync(() => OpenDialog() is not null, what: "a dialog").ConfigureAwait(true);
        return OpenDialog()!;
    }

    public static T ControlOfRow<T>(ExtensionsWindow window, ExtensionRow row, Func<T, bool>? match = null) where T : Control =>
        TestUi.FindAll<T>(window.ExtensionList).First(control => ReferenceEquals(control.DataContext, row) && (match is null || match(control)));

    public static Button ButtonOfRow(ExtensionsWindow window, ExtensionRow row, string text) =>
        ControlOfRow<Button>(window, row, button => button is not ToggleButton && Equals(button.Content, text));

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            _host.Dispose();
            await Task.WhenAny(_host.WhenStoppedAsync(), Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(true);
        }
        Test.Dispose();
    }
}

/// <summary>A main window's chat as the extensions see it: records what it was asked.</summary>
internal sealed class FakeChat : ISessionChat
{
    private readonly List<string> _tabs = ["RC Chat"];

    public List<(string Tab, string Text)> Appended { get; } = [];

    public List<string> Removed { get; } = [];

    public IReadOnlyList<string> Tabs => _tabs;

    public Window? Window => null;

    public IEnumerable<string> LinesOf(string tab) => Appended.Where(item => item.Tab == tab).Select(item => item.Text);

    public void AppendLine(string text) => Appended.Add(("RC Chat", text));

    public void AppendToTab(string tab, string text)
    {
        if (!_tabs.Contains(tab)) _tabs.Add(tab);
        Appended.Add((tab, text));
    }

    public void RemoveTab(string tab)
    {
        _tabs.Remove(tab);
        Removed.Add(tab);
    }

    public bool Send(string text) => false;

    public void Activate()
    {
    }
}
