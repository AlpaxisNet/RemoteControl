using System.Collections.Immutable;
using System.Text.Json;
using RemoteControl.Core;

namespace RemoteControl.Extensions.Tests;

public sealed class StateFileTests
{
    [Fact]
    public void RoundTripInTheCppFormat()
    {
        using var folder = new TempFolder();
        string path = folder.Combine("sub", ExtensionStateFile.FileName);
        var entries = new[]
        {
            new ExtensionSavedState("b.ext", true, false, ImmutableSortedSet.Create(StringComparer.Ordinal, "script-read", "file-read")),
            new ExtensionSavedState("a.ext", false, true, ImmutableSortedSet.Create<string>(StringComparer.Ordinal)),
            new ExtensionSavedState("bad id", true, true, ImmutableSortedSet.Create<string>(StringComparer.Ordinal)),
        };
        ExtensionStateFile.Write(path, entries, new[] { "garbage line" });
        Assert.Equal("b.ext 1 0 file-read script-read\na.ext 0 1\ngarbage line\n", File.ReadAllText(path));
        ExtensionStateContent read = ExtensionStateFile.Read(path);
        Assert.Equal(2, read.Entries.Count);
        Assert.True(read.Entries["b.ext"].Enabled);
        Assert.False(read.Entries["b.ext"].OutputToTab);
        Assert.Equal(new[] { "file-read", "script-read" }, read.Entries["b.ext"].ApprovedCapabilities);
        Assert.True(read.Entries["a.ext"].OutputToTab);
        Assert.Equal(new[] { "garbage line" }, read.UnreadableLines);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void ReadsWhatTheCppWrote()
    {
        ExtensionStateContent content = ExtensionStateFile.Parse("gs2engine 1 1 script-read,file-read net\r\nother 0\nnumbers 2 0\n\nthird 1 x\nsame 0 0\nsame 1 1 later\n");
        Assert.True(content.Entries["gs2engine"].Enabled);
        Assert.True(content.Entries["gs2engine"].OutputToTab);
        Assert.Equal(new[] { "file-read", "net", "script-read" }, content.Entries["gs2engine"].ApprovedCapabilities);
        Assert.False(content.Entries["other"].Enabled);
        Assert.False(content.Entries["other"].OutputToTab);
        Assert.True(content.Entries["numbers"].Enabled);
        Assert.True(content.Entries["same"].Enabled);
        Assert.Equal(new[] { "later" }, content.Entries["same"].ApprovedCapabilities);
        Assert.Equal(new[] { "third 1 x" }, content.UnreadableLines);
    }

    [Fact]
    public void AMissingFileIsEmptyAndThePathIsInTheUserFolder()
    {
        using var folder = new TempFolder();
        Assert.Empty(ExtensionStateFile.Read(folder.Combine("none")).Entries);
        var paths = new AppPaths(folder.Combine("user"), folder.Combine("program"));
        Assert.Equal(folder.Combine("user", "extensions.state"), ExtensionStateFile.PathFor(paths));
        ExtensionManagerOptions options = ExtensionManagerOptions.ForUser(paths);
        Assert.Equal(folder.Combine("program", "extensions"), options.PackageRoots[0]);
        Assert.Equal(folder.Combine("user", "extensions"), options.PackageRoots[^1]);
    }
}

public sealed class ExtensionProtocolTests
{
    [Theory]
    [InlineData("{\"display\":\"Shown\",\"text\":\"t\",\"message\":\"m\"}", "Shown")]
    [InlineData("{\"display\":\"\",\"text\":\"Text wins\",\"message\":\"m\"}", "Text wins")]
    [InlineData("{\"type\":\"log\",\"message\":\"Message\"}", "Message")]
    [InlineData("{\"type\":\"output\",\"params\":{\"text\":\"nested text\"}}", "nested text")]
    [InlineData("{\"type\":\"ready\"}", "")]
    [InlineData("{\"type\":\"status\",\"state\":\"idle\"}", "")]
    [InlineData("{\"type\":\"response\",\"id\":\"1\",\"result\":{}}", "")]
    [InlineData("{\"type\":\"status\",\"display\":\"Status shown\"}", "Status shown")]
    [InlineData("{\"type\":\"progress\",\"value\":5,\"done\":false,\"empty\":\"\",\"none\":null}", "type=progress, value=5, done=false, none=null")]
    [InlineData("{\"a\":{\"b\":{\"c\":{\"d\":1}}},\"list\":[1, 2]}", "a={b={c={\"d\":1}}}, list=[1, 2]")]
    [InlineData("  plain text line \r", "plain text line")]
    [InlineData("{not json", "{not json")]
    [InlineData("[1,2,3]", "[1,2,3]")]
    [InlineData("{}", "{}")]
    public void DisplayLines(string line, string expected) => Assert.Equal(expected, ExtensionProtocol.DisplayLine(line));

    [Fact]
    public void WindowActionRequestsAreValidJson()
    {
        var context = ExtensionWindowContext.ForScriptEditor("weapons", "bow \"x\"", "Weapon: bow", "line1\nline2\t\"q\" \\ \u0001 é 😀 \ud800", "sel\"");
        string request = ExtensionProtocol.WindowActionRequest("format-script", context);
        Assert.DoesNotContain('\n', request);
        using JsonDocument document = JsonDocument.Parse(request);
        JsonElement root = document.RootElement;
        Assert.StartsWith("window-action-", root.GetProperty("id").GetString());
        Assert.Equal("window/action", root.GetProperty("method").GetString());
        JsonElement parameters = root.GetProperty("params");
        Assert.Equal("format-script", parameters.GetProperty("action").GetString());
        JsonElement value = parameters.GetProperty("context");
        Assert.Equal("weapons:bow \"x\"", value.GetProperty("windowId").GetString());
        Assert.Equal("script-editor", value.GetProperty("kind").GetString());
        Assert.Equal("Weapon: bow", value.GetProperty("title").GetString());
        Assert.Equal("weapons", value.GetProperty("scriptType").GetString());
        Assert.Equal("bow \"x\"", value.GetProperty("scriptName").GetString());
        Assert.Equal("line1\nline2\t\"q\" \\ \u0001 é 😀 �", value.GetProperty("text").GetString());
        Assert.Equal("sel\"", value.GetProperty("selection").GetString());
        string next = ExtensionProtocol.WindowActionRequest("a", context);
        Assert.NotEqual(root.GetProperty("id").GetString(), JsonDocument.Parse(next).RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public void HostMessages()
    {
        using var root = new TempFolder();
        root.Write("e/main.gs2", "");
        ExtensionManifest withEntry = ExtensionManifestLoader.Load(root.Write("e/extension.json", "{\"id\":\"e.x\",\"name\":\"E\",\"version\":\"1\",\"runtime\":\"gs2engine-stdio\",\"entry\":\"main.gs2\"}"));
        using (JsonDocument start = JsonDocument.Parse(ExtensionProtocol.HostStartRequest(withEntry)))
        {
            Assert.Equal("1", start.RootElement.GetProperty("id").GetString());
            Assert.Equal("start", start.RootElement.GetProperty("method").GetString());
            Assert.Equal("e.x", start.RootElement.GetProperty("params").GetProperty("extensionId").GetString());
            Assert.Equal(root.Combine("e", "main.gs2"), start.RootElement.GetProperty("params").GetProperty("entry").GetString());
            Assert.False(start.RootElement.GetProperty("params").TryGetProperty("packageRoot", out _));
        }
        ExtensionManifest auto = ExtensionManifestLoader.Load(root.Write("a/extension.json", "{\"id\":\"a\",\"name\":\"A\",\"version\":\"1\",\"runtime\":\"gs2engine-stdio\",\"mode\":\"auto\"}"));
        using (JsonDocument start = JsonDocument.Parse(ExtensionProtocol.HostStartRequest(auto)))
        {
            Assert.Equal(root.Combine("a"), start.RootElement.GetProperty("params").GetProperty("packageRoot").GetString());
        }
        Assert.Equal("{\"type\":\"start\",\"protocol\":\"json-lines\"}", ExtensionProtocol.StartMessage);
        Assert.Equal("{\"type\":\"stop\"}", ExtensionProtocol.StopMessage);
        Assert.Equal("{\"id\":\"2\",\"method\":\"stop\"}", ExtensionProtocol.HostStopRequest);
        Assert.Equal("{\"id\":\"3\",\"method\":\"shutdown\"}", ExtensionProtocol.HostShutdownRequest);
        Assert.True(ExtensionProtocol.IsReady("{\"type\":\"ready\",\"version\":1}"));
        Assert.False(ExtensionProtocol.IsReady("{\"type\":\"readyish\"}"));
        Assert.False(ExtensionProtocol.IsReady("ready"));
    }

    [Fact]
    public void TheLogKeepsItsNewestPart()
    {
        string log = string.Empty;
        for (int index = 0; index < 100; index++) log = ExtensionProtocol.AppendLog(log, "line " + index, 50);
        Assert.True(log.Length <= 50);
        Assert.EndsWith("line 99", log);
        Assert.Equal("a", ExtensionProtocol.AppendLog("a", "", 10));
        Assert.Equal("😀", ExtensionProtocol.AppendLog("x", "😀", 2));
        Assert.Equal(string.Empty, ExtensionProtocol.AppendLog("ab", "😀", 1));
    }

    [Fact]
    public void ContextAndBindingTexts()
    {
        var binding = new ExtensionWindowActionBinding("id", "My Ext", "/x", new ExtensionWindowAction("script-editor", "lint", "Lint", ExtensionActionPlacement.Top, ""), ExtensionIcon.None);
        Assert.Equal("Run Lint from My Ext", binding.Tooltip);
        var manifest = new ExtensionManifest { Directory = "/x", Id = "id", Name = "My Ext", Version = "1.2", Runtime = "python3", Entry = "main.py", Publisher = "Pub" };
        var info = new ExtensionInfo(manifest, true, false, ImmutableSortedSet<string>.Empty, ExtensionStatus.Failed, "Runtime exited unexpectedly", true, null);
        Assert.Equal("My Ext 1.2 (failed) - Runtime exited unexpectedly", info.DisplayText);
        Assert.Equal("ID: id\nVersion: 1.2\nStatus: failed\nRuntime: python3\nEntry: main.py\nPublisher: Pub", info.DetailsText);
        Assert.Equal("My Ext 1.2 (running)", (info with { Status = ExtensionStatus.Running, Error = "" }).DisplayText);
    }
}
