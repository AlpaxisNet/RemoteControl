using Avalonia.Headless.XUnit;
using RemoteControl.Protocol.Session;
using RemoteControl.Ui.Common;
using RemoteControl.Ui.Shell;
using RemoteControl.Ui.TestKit;
using ProtocolKind = RemoteControl.Protocol.Session.ScriptKind;
using ShellKind = RemoteControl.Ui.Shell.ScriptKind;

namespace RemoteControl.Ui.Scripts.Tests;

public sealed class ExternalEditorTests
{
    [AvaloniaFact]
    public void File_names_are_made_safe_and_never_shared()
    {
        // The workspace is a real folder path of the system the test runs on (a drive letter on Windows).
        string workspace = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "w"));
        string a = ExternalEditor.PathFor(workspace, "My:Server", "weapons", "a/b");
        string b = ExternalEditor.PathFor(workspace, "My:Server", "weapons", "a_b");
        Assert.NotEqual(a, b);
        Assert.Equal(Path.Combine(workspace, "My_Server", "weapons", "a_b.gs2"), b);
        Assert.StartsWith(Path.Combine(workspace, "My_Server", "weapons", "a_b-"), a);
        Assert.Equal(Path.Combine(workspace, "s", "text", "serveroptions.txt"), ExternalEditor.PathFor(workspace, "s", "text", "serveroptions.txt"));
        Assert.Equal("__", ExternalEditor.SafePart(".."));
        Assert.Equal("untitled", ExternalEditor.SafePart(""));
        Assert.Equal(["code", "--wait", "/f/x y.gs2"], ExternalEditor.CommandLine("code --wait \"{file}\"", "/f/x y.gs2"));
        Assert.Equal(["my editor", "-n", "/f.gs2"], ExternalEditor.CommandLine("\"my editor\" -n", "/f.gs2"));
        Assert.True(ExternalEditor.Covers("scripts", true));
        Assert.False(ExternalEditor.Covers("scripts", false));
        Assert.True(ExternalEditor.Covers("text", false));
        Assert.False(ExternalEditor.Covers("off", true));
    }

    [AvaloniaFact]
    public async Task A_saved_file_is_uploaded_through_the_save_flow_and_watching_stops_on_close()
    {
        if (OperatingSystem.IsWindows()) return;
        string folder = Path.Combine(Path.GetTempPath(), "rc-external-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string script = Path.Combine(folder, "fake-editor.sh");
        await File.WriteAllTextAsync(script, "#!/bin/sh\nsleep 0.3\nprintf 'echo(\"edited\");' > \"$1\"\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string workspace = Path.Combine(folder, "workspace");
        ExternalEditor.TestCommandFolder = folder;
        try
        {
            using TestSession test = Helpers.Session(options: options => options with { ExternalEditorScope = "scripts", ExternalEditorWorkspace = workspace, ExternalEditorCommand = script + " {file}" });
            EditorWindow window = Helpers.OpenEditor(test, new ScriptRequest(ShellKind.Weapon, "Sword"));
            test.Fake.RaiseScript(ProtocolKind.Weapon, "Sword", "echo(\"server\");", image: "s.png");
            await TestUi.WaitUntilAsync(() => window.External is not null, what: "the external editor");
            string path = window.External!.Path;
            Assert.StartsWith(workspace, path);
            Assert.Contains("Test Server", path);
            await TestUi.WaitUntilAsync(() => test.Fake.WasCalled(nameof(IRcSession.UpdateWeaponAsync)), TimeSpan.FromSeconds(5), "the upload");
            Assert.Equal(["Sword", "s.png", "echo(\"edited\");"], test.Fake.LastCall(nameof(IRcSession.UpdateWeaponAsync))!.Arguments.Cast<string>());
            Assert.Equal("echo(\"edited\");", window.Editor.Text);
            Assert.False(window.IsDirty);
            Assert.Contains("external editor", window.InfoMessage);

            window.Close();
            TestUi.Settle();
            test.Fake.ClearCalls();
            await File.WriteAllTextAsync(path, "echo(\"after close\");");
            await Task.Delay(500);
            TestUi.Settle();
            Assert.False(test.Fake.WasCalled(nameof(IRcSession.UpdateWeaponAsync)));
        }
        finally
        {
            ExternalEditor.TestCommandFolder = null;
            Directory.Delete(folder, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Without_a_command_nothing_is_started_in_a_test_and_local_changes_are_not_overwritten_silently()
    {
        string folder = Path.Combine(Path.GetTempPath(), "rc-external-" + Guid.NewGuid().ToString("N"));
        try
        {
            using TestSession test = Helpers.Session(options: options => options with { ExternalEditorScope = "text", ExternalEditorWorkspace = folder, ExternalEditorCommand = "" });
            string path = ExternalEditor.PathFor(folder, "Test Server", "text", "serveroptions.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "local=1");
            ExternalPrograms.ClearSuppressed();
            EditorWindow window = Helpers.Open<EditorWindow>(test, ToolIds.ServerOptions);
            test.Fake.RaiseServerOptions("server=1");
            RcDialog question = TestUi.FindDialog();
            Assert.Contains(path, question.Message);
            question.Accept();
            await TestUi.WaitUntilAsync(() => window.External is not null, what: "the external editor");
            Assert.Equal("local=1", await File.ReadAllTextAsync(path));
            Assert.Equal("local=1", window.Editor.Text);
            Assert.True(window.IsDirty);
            Assert.Contains("file " + path, ExternalPrograms.Suppressed);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task A_command_outside_the_test_folder_is_not_started_in_a_test()
    {
        string folder = Path.Combine(Path.GetTempPath(), "rc-external-" + Guid.NewGuid().ToString("N"));
        try
        {
            using TestSession test = Helpers.Session(options: options => options with { ExternalEditorScope = "scripts", ExternalEditorWorkspace = folder, ExternalEditorCommand = "/bin/false {file}" });
            EditorWindow window = Helpers.OpenEditor(test, new ScriptRequest(ShellKind.Class, "Lib"));
            test.Fake.RaiseScript(ProtocolKind.Class, "Lib", "x");
            await TestUi.WaitUntilAsync(() => window.External is not null, what: "the external editor");
            Assert.Equal("x", await File.ReadAllTextAsync(window.External!.Path));
            Assert.DoesNotContain("could not be started", window.StatusMessage);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
