using System.Net;
using System.Text;
using RemoteControl.Scripting;

namespace RemoteControl.Scripting.Tests;

public sealed class ApiDefinitionParserTests
{
    [Fact]
    public void ArrayShape()
    {
        IReadOnlyList<ApiDefinition> definitions = ApiDefinitionParser.Parse("""
            [
              {"name": "setTimer", "type": "function", "params": ["seconds"], "returns": "void", "scope": "script",
               "description": "setTimer(seconds) - Starts the timer.", "example": "setTimer(1);", "extra": {"nested": [1, 2, {"x": null}]}},
              {"name": "echo", "parameters": "text", "scope": "server"},
              {"type": "nameless"},
              {"name": "ECHO", "description": "duplicate"},
            ]
            """);
        Assert.Equal(new[] { "echo", "setTimer" }, definitions.Select(definition => definition.Name));
        ApiDefinition timer = definitions[1];
        Assert.Equal("function", timer.Type);
        Assert.Equal(new[] { "seconds" }, timer.Parameters);
        Assert.Equal("void", timer.Returns);
        Assert.Equal("setTimer(1);", timer.Example);
        Assert.Equal(string.Empty, definitions[0].Description);
        Assert.Equal(new[] { "text" }, definitions[0].Parameters);
        Assert.Equal("server", definitions[0].Scope);
    }

    [Fact]
    public void ObjectMapShape()
    {
        IReadOnlyList<ApiDefinition> definitions = ApiDefinitionParser.Parse("""
            { "player.chat": {"type": "property", "returns": "string"}, "named": {"name": "realName", "args": "a, b"}, "bad": 5 }
            """);
        Assert.Equal(new[] { "player.chat", "realName" }, definitions.Select(definition => definition.Name));
        Assert.Equal(new[] { "a", "b" }, definitions[1].Parameters);
    }

    [Fact]
    public void EveryParameterSpellingAndForm()
    {
        IReadOnlyList<ApiDefinition> definitions = ApiDefinitionParser.Parse("""
            [
              {"name": "a", "params": ["x", "", "y"]},
              {"name": "b", "parameters": [{"name": "n", "label": "l"}, {"label": "l2", "type": "t"}, {"type": "t3"}, {"other": 1}, 5, null]},
              {"name": "c", "args": "first, second = f(1, 2), \"x,y\", [p, q]"},
              {"name": "d", "arguments": " spaced ,  , last "},
              {"name": "e", "params": ["ignored"], "arguments": ["wins"]},
              {"name": "f", "params": 7},
              {"name": "g", "params": "a, b(c"}
            ]
            """);
        Dictionary<string, IReadOnlyList<string>> parameters = definitions.ToDictionary(definition => definition.Name, definition => definition.Parameters);
        Assert.Equal(new[] { "x", "y" }, parameters["a"]);
        Assert.Equal(new[] { "n", "l2", "t3" }, parameters["b"]);
        Assert.Equal(new[] { "first", "second = f(1, 2)", "\"x,y\"", "[p, q]" }, parameters["c"]);
        Assert.Equal(new[] { "spaced", "last" }, parameters["d"]);
        Assert.Equal(new[] { "wins" }, parameters["e"]);
        Assert.Empty(parameters["f"]);
        Assert.Equal(new[] { "a" }, parameters["g"]);
    }

    [Fact]
    public void KeysAreCaseSensitiveAndWrongTypesAreIgnored()
    {
        ApiDefinition definition = Assert.Single(ApiDefinitionParser.Parse("""[{"Name": "x", "name": "y", "returns": null, "type": 3, "description": "d"}]"""));
        Assert.Equal("y", definition.Name);
        Assert.Equal(string.Empty, definition.Returns);
        Assert.Equal(string.Empty, definition.Type);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[{\"name\": \"x\"")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    [InlineData("[]")]
    public void InvalidOrEmptyGivesNothing(string json) => Assert.Empty(ApiDefinitionParser.Parse(json));

    [Fact]
    public void DeepJsonIsRejected()
    {
        string deep = "[{\"name\": \"x\", \"extra\": " + new string('[', 5000) + new string(']', 5000) + "}]";
        Assert.Empty(ApiDefinitionParser.Parse(deep));
    }

    [Fact]
    public void Utf8BytesWithByteOrderMark()
    {
        byte[] bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("[{\"name\": \"caf\\u00e9\", \"description\": \"\\ud83d\\ude00\"}]")).ToArray();
        ApiDefinition definition = Assert.Single(ApiDefinitionParser.Parse(bytes));
        Assert.Equal("café", definition.Name);
        Assert.Equal("😀", definition.Description);
    }

    [Fact]
    public void DisplayFields()
    {
        var definition = new ApiDefinition("setTimer", "function", new[] { "seconds" }, "void", "script", "setTimer(seconds) - Starts the timer.", "setTimer(1);");
        Assert.Equal("settimer", definition.SearchName);
        Assert.Equal("setTimer(seconds)", definition.CompletionLabel);
        Assert.Equal("setTimer(seconds)", definition.Signature);
        Assert.Equal("function  SCRIPT\nParameters: seconds\nReturns: void", definition.CompletionInfo);
        Assert.Equal("setTimer(seconds)  →  void\nStarts the timer.", definition.CompletionSummary);
        Assert.Equal("GLOBAL", definition.ScopeLabel);
        Assert.Equal("function  SCRIPT\nParameters: seconds\nReturns: void\nsetTimer(seconds) - Starts the timer.\n\nExample:\nsetTimer(1);", definition.Info);

        var bare = new ApiDefinition("visible", "property");
        Assert.Equal("visible", bare.CompletionLabel);
        Assert.Equal("visible()", bare.Signature);
        Assert.Equal("property", bare.CompletionInfo);
        Assert.Equal(string.Empty, bare.ScopeLabel);
        Assert.False(bare.HasSignatureDetails);

        Assert.Equal("CLIENTSIDE", new ApiDefinition("a", scope: "Client").ScopeLabel);
        Assert.Equal("SERVERSIDE", new ApiDefinition("a", scope: "server").ScopeLabel);
        Assert.Equal("WEAPON", new ApiDefinition("a", scope: "weapon").ScopeLabel);
        Assert.Equal("f()  →  int", new ApiDefinition("f", returns: "int").CompletionSummary);
        Assert.Equal("f()\nreturns an int", new ApiDefinition("f", returns: "int", description: "returns an int").CompletionSummary);

        var longOne = new ApiDefinition("f", parameters: Enumerable.Range(0, 30).Select(index => "parameter" + index));
        Assert.Equal(84, longOne.CompletionLabel.Length);
        Assert.EndsWith("...", longOne.CompletionLabel, StringComparison.Ordinal);
    }

    [Fact]
    public void SetMergesBuiltInsAndSorts()
    {
        ApiDefinitionSet set = ApiDefinitionSet.Create(new[] { new ApiDefinition("zeta"), new ApiDefinition("Alpha"), new ApiDefinition("GUICONTROL", "function"), new ApiDefinition("alpha", "dup") });
        Assert.True(set.RemoteLoaded);
        Assert.Equal(set.Definitions.Select(definition => definition.SearchName).OrderBy(name => name, StringComparer.Ordinal), set.Definitions.Select(definition => definition.SearchName));
        Assert.Equal("function", set.Find("guicontrol")!.Type);
        Assert.Equal(string.Empty, set.Find("ALPHA")!.Type);
        Assert.Equal("class", set.Find("GuiRadioCtrl")!.Type);
        Assert.Equal(new[] { "name" }, set.Find("GuiRadioCtrl")!.Parameters);
        Assert.Equal("property", set.Find("visible")!.Type);
        Assert.Null(set.Find("missing"));
        Assert.Equal(20, ApiDefinitionSet.BuiltInDefinitions.Count);
        Assert.False(ApiDefinitionSet.BuiltInOnly.RemoteLoaded);
        Assert.Equal(20, ApiDefinitionSet.BuiltInOnly.Definitions.Count);
    }

    [Fact]
    public void ResolveFallsBackToMemberAndLocalFunctions()
    {
        ApiDefinitionSet set = ApiDefinitionSet.Create(new[] { new ApiDefinition("chat", "property") });
        var local = new[] { new ApiDefinition("myHelper", "Function") };
        Assert.Equal("chat", set.Resolve("player.chat", local)!.Name);
        Assert.Equal("myHelper", set.Resolve("MYHELPER", local)!.Name);
        Assert.Null(set.Resolve("other", local));
        Assert.Null(set.Resolve("", local));
    }

    [Fact]
    public void ScriptHelp()
    {
        var definitions = new[] { new ApiDefinition("setTimer", parameters: new[] { "s" }, returns: "void", scope: "script", description: "Starts.", example: "setTimer(1);"), new ApiDefinition("setTimerMs", description: "Ms.") };
        Assert.Equal(new[] { "[Script Help] setTimer(s) [SCRIPT]", "Returns: void", "Starts.", "Example: setTimer(1);" }, RemoteControl.Scripting.ScriptHelp.Format(definitions, "SETTIMER"));
        Assert.Equal(new[] { "[Script Help] 2 entries matching 'timer':", "setTimer(s) [SCRIPT] - Starts.", "setTimerMs() - Ms." }, RemoteControl.Scripting.ScriptHelp.Format(definitions, "timer"));
        Assert.Equal(new[] { "[Script Help] No API entries match 'zz'." }, RemoteControl.Scripting.ScriptHelp.Format(definitions, "zz"));
        var many = Enumerable.Range(0, 40).Select(index => new ApiDefinition("fn" + index)).ToArray();
        IReadOnlyList<string> lines = RemoteControl.Scripting.ScriptHelp.Format(many, "f");
        Assert.Equal(32, lines.Count);
        Assert.Equal("Use a longer /scripthelp2 query to narrow the result.", lines[^1]);
    }
}

public sealed class ApiDefinitionLoaderTests : IDisposable
{
    private const string Json = "[{\"name\": \"echo\", \"params\": [\"text\"]}]";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "rc-scripting-tests-" + Guid.NewGuid().ToString("N"));

    public ApiDefinitionLoaderTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond;

        public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => this.respond = respond;

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return respond(request, cancellationToken);
        }
    }

    private static Handler Answer(HttpStatusCode status, string body) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));

    [Fact]
    public async Task HttpSuccessIsCachedAtomically()
    {
        Handler handler = Answer(HttpStatusCode.OK, Json);
        string cache = Path.Combine(directory, "editors", "scriptapi.json");
        ApiLoadResult result = await new ApiDefinitionLoader(handler).LoadAsync("https://api.example.test/v1?x=1", cache);
        Assert.Equal(ApiDefinitionOrigin.Source, result.Origin);
        Assert.Null(result.SourceError);
        Assert.True(result.Definitions.RemoteLoaded);
        Assert.NotNull(result.Definitions.Find("echo"));
        Assert.NotNull(result.Definitions.Find("GuiControl"));
        Assert.Equal(Json, File.ReadAllText(cache));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(cache)!));
        HttpRequestMessage request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.example.test/v1?x=1", request.RequestUri!.ToString());
        Assert.Contains(request.Headers.Accept, value => value.MediaType == "application/json");
        Assert.Contains("RemoteControl/1.0", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task OnlyStatus200IsAccepted(HttpStatusCode status)
    {
        string cache = Path.Combine(directory, "cache.json");
        File.WriteAllText(cache, "[{\"name\": \"cached\"}]");
        ApiLoadResult result = await new ApiDefinitionLoader(Answer(status, Json)).LoadAsync("http://api.example.test/", cache);
        Assert.Equal(ApiDefinitionOrigin.Cache, result.Origin);
        Assert.NotNull(result.SourceError);
        Assert.NotNull(result.Definitions.Find("cached"));
        Assert.Null(result.Definitions.Find("echo"));
    }

    [Fact]
    public async Task ResponsesOverEightMegabytesAreRefused()
    {
        string big = "[" + string.Join(",", Enumerable.Repeat("{\"name\": \"x\", \"description\": \"" + new string('a', 1000) + "\"}", 9000)) + "]";
        var handler = new Handler((_, _) =>
        {
            var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(big)));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        ApiLoadResult result = await new ApiDefinitionLoader(handler).LoadAsync("https://api.example.test/", null);
        Assert.Equal(ApiDefinitionOrigin.None, result.Origin);
        Assert.Contains("8 MiB", result.SourceError, StringComparison.Ordinal);
        Assert.False(result.Definitions.RemoteLoaded);
        Assert.NotNull(result.Definitions.Find("GuiControl"));
    }

    [Fact]
    public async Task NetworkFailureFallsBackToCache()
    {
        string cache = Path.Combine(directory, "cache.json");
        File.WriteAllText(cache, "[{\"name\": \"cached\"}]");
        var handler = new Handler((_, _) => throw new HttpRequestException("no route"));
        ApiLoadResult result = await new ApiDefinitionLoader(handler).LoadAsync("https://api.example.test/", cache);
        Assert.Equal(ApiDefinitionOrigin.Cache, result.Origin);
        Assert.Contains("no route", result.SourceError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyOrInvalidSourceKeepsTheCache()
    {
        string cache = Path.Combine(directory, "cache.json");
        File.WriteAllText(cache, "[{\"name\": \"cached\"}]");
        ApiLoadResult result = await new ApiDefinitionLoader(Answer(HttpStatusCode.OK, "[]")).LoadAsync("https://api.example.test/", cache);
        Assert.Equal(ApiDefinitionOrigin.Cache, result.Origin);
        Assert.Equal("[{\"name\": \"cached\"}]", File.ReadAllText(cache));
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        var handler = new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ApiDefinitionLoader(handler).LoadAsync("https://api.example.test/", null, cancellation.Token));
    }

    [Fact]
    public async Task FileUriAndPlainPath()
    {
        string file = Path.Combine(directory, "api file.json");
        File.WriteAllText(file, Json);
        string cache = Path.Combine(directory, "cache.json");
        ApiLoadResult byUri = await new ApiDefinitionLoader().LoadAsync(new Uri(file).AbsoluteUri, cache);
        Assert.Equal(ApiDefinitionOrigin.Source, byUri.Origin);
        Assert.NotNull(byUri.Definitions.Find("echo"));
        Assert.True(File.Exists(cache));
        ApiLoadResult byPath = await new ApiDefinitionLoader().LoadAsync(file, null);
        Assert.Equal(ApiDefinitionOrigin.Source, byPath.Origin);
        ApiLoadResult missing = await new ApiDefinitionLoader().LoadAsync(Path.Combine(directory, "missing.json"), null);
        Assert.Equal(ApiDefinitionOrigin.None, missing.Origin);
        ApiLoadResult none = await new ApiDefinitionLoader().LoadAsync(null, null);
        Assert.Equal(ApiDefinitionOrigin.None, none.Origin);
    }

    [Fact]
    public async Task OversizedFileIsRefused()
    {
        string file = Path.Combine(directory, "huge.json");
        using (var stream = new FileStream(file, FileMode.Create)) stream.SetLength(ApiDefinitionLoader.MaximumBytes + 1);
        ApiLoadResult result = await new ApiDefinitionLoader().LoadAsync(file, null);
        Assert.Equal(ApiDefinitionOrigin.None, result.Origin);
    }

    [Fact]
    public async Task InvalidUrlIsAnError()
    {
        ApiLoadResult result = await new ApiDefinitionLoader(Answer(HttpStatusCode.OK, Json)).LoadAsync("http://", null);
        Assert.Equal(ApiDefinitionOrigin.None, result.Origin);
        Assert.NotNull(result.SourceError);
    }
}

public sealed class CompletionEngineTests
{
    private static readonly ApiDefinitionSet Api = ApiDefinitionSet.Create(new[]
    {
        new ApiDefinition("setTimer", "function", new[] { "seconds" }, "void", "script", "Starts the timer."),
        new ApiDefinition("setTimerMs", "function", new[] { "ms" }),
        new ApiDefinition("timer", "event"),
        new ApiDefinition("resetTimers", "function"),
        new ApiDefinition("sendToRC", "function", new[] { "text" }, scope: "server"),
        new ApiDefinition("chat", "property", scope: "client"),
    });

    [Theory]
    [InlineData("settimer", "settimer", 0)]
    [InlineData("settimer", "", 0)]
    [InlineData("settimer", "set", 10)]
    [InlineData("settimer", "tim", 23)]
    [InlineData("settimer", "stm", 103)]
    [InlineData("settimer", "xyz", -1)]
    [InlineData("SetTimer", "SETT", 10)]
    public void Ranking(string candidate, string needle, int score) => Assert.Equal(score, CompletionRanking.Score(candidate, needle));

    [Fact]
    public void OrderIsScoreThenName()
    {
        CompletionResult result = new CompletionEngine(Api, LocalSymbols.Empty).Propose("x = timer", 9, null);
        Assert.Equal("timer", result.Prefix);
        Assert.Equal(4, result.ReplaceStart);
        Assert.Equal(5, result.ReplaceLength);
        // exact (0), then substrings by position: settimer and settimerms (23, by name), resettimers (25).
        Assert.Equal(new[] { "timer", "setTimer", "setTimerMs", "resetTimers" }, result.Items.Select(item => item.InsertText));
    }

    [Fact]
    public void ItemFields()
    {
        CompletionItem item = new CompletionEngine(Api, LocalSymbols.Empty).Propose("setTimer", 8, null).Items[0];
        Assert.Equal("setTimer(seconds)", item.Label);
        Assert.Equal("setTimer", item.InsertText);
        Assert.Equal(CompletionItemKind.Function, item.Kind);
        Assert.Equal("function", item.KindLabel);
        Assert.Equal("GLOBAL", item.Scope);
        Assert.Equal("function  SCRIPT\nParameters: seconds\nReturns: void", item.Info);
        CompletionItem chat = new CompletionEngine(Api, LocalSymbols.Empty).Propose("chat", 4, null).Items[0];
        Assert.Equal(CompletionItemKind.Property, chat.Kind);
        Assert.Equal("CLIENTSIDE", chat.Scope);
        CompletionItem gui = new CompletionEngine(Api, LocalSymbols.Empty).Propose("GuiRadio", 8, null).Items[0];
        Assert.Equal(CompletionItemKind.Class, gui.Kind);
    }

    [Fact]
    public void AtMostSixteenAndDefinitionsFirst()
    {
        var many = ApiDefinitionSet.Create(Enumerable.Range(0, 40).Select(index => new ApiDefinition("value" + index.ToString("00"))));
        const string text = "valueLocal = 1; valueOther = 2; value";
        CompletionResult result = new CompletionEngine(many).Propose(text, text.Length, null);
        Assert.Equal(CompletionEngine.MaximumProposals, result.Items.Count);
        Assert.All(result.Items, item => Assert.Equal(CompletionItemKind.Function, item.Kind));
        Assert.Equal("value00", result.Items[0].InsertText);
        Assert.Equal("value15", result.Items[15].InsertText);
    }

    [Fact]
    public void InteractiveNeedsTwoCharacters()
    {
        var engine = new CompletionEngine(Api, LocalSymbols.Empty);
        Assert.Empty(engine.Propose("s", 1, null, CompletionTrigger.Interactive).Items);
        Assert.NotEmpty(engine.Propose("se", 2, null, CompletionTrigger.Interactive).Items);
        Assert.NotEmpty(engine.Propose("s", 1, null, CompletionTrigger.UserRequested).Items);
        Assert.Equal(CompletionEngine.MaximumProposals, engine.Propose(string.Empty, 0, null).Items.Count);
    }

    [Fact]
    public void LocalFunctionsAndIdentifiers()
    {
        const string text = "function myHelper(a, b) {}\nfunction setTimer() {}\nplayer.chatmsg = \"x\";\nmyhelpervalue = 1;\nmyH";
        var engine = new CompletionEngine(Api);
        CompletionResult result = engine.Propose(text, text.Length, null);
        CompletionItem helper = result.Items[0];
        Assert.Equal("myHelper(a, b)", helper.Label);
        Assert.Equal("Function", helper.KindLabel);
        Assert.Equal("GLOBAL", helper.Scope);
        Assert.Equal("Function defined in the current script.", helper.Definition!.Description);
        Assert.Contains(result.Items, item => item.InsertText == "myhelpervalue" && item.Kind == CompletionItemKind.Identifier && item.Info == "Current script identifier");
        Assert.DoesNotContain(result.Items, item => item.InsertText.Equals("myHelper", StringComparison.Ordinal) && item.Kind == CompletionItemKind.Identifier);
    }

    [Fact]
    public void ObjectPrefixIsStrippedFromIdentifiers()
    {
        const string text = "player.chatmsg = 1; player.chatcolor = 2; other.chatter = 3;\nplayer.cha";
        CompletionResult result = new CompletionEngine(Api).Propose(text, text.Length, null);
        Assert.Equal("cha", result.Prefix);
        Assert.Equal("player.", CompletionEngine.ObjectPrefix(text, text.Length));
        Assert.Equal("chat", result.Items[0].InsertText);
        string[] identifiers = result.Items.Where(item => item.Kind == CompletionItemKind.Identifier).Select(item => item.InsertText).ToArray();
        // Like the C++, the chain being typed is itself an identifier of the text and is offered too.
        Assert.Equal(new[] { "chatmsg", "chatcolor", "cha" }, identifiers);
    }

    [Fact]
    public void PlayersOnlyInsideStrings()
    {
        var players = new[] { new OnlinePlayer("Graal1234", "Stefan", "Stefan_C"), new OnlinePlayer("guest", "Guest", "") };
        var engine = new CompletionEngine(ApiDefinitionSet.Create(Array.Empty<ApiDefinition>()), LocalSymbols.Empty);
        const string text = "findplayer(\"ste";
        CompletionItem[] inside = engine.Propose(text, text.Length, players).Items.Where(item => item.Kind == CompletionItemKind.Player).ToArray();
        Assert.Equal(new[] { "Stefan  [NICK]", "Stefan_C  [COMMUNITY]" }, inside.Select(item => item.Label));
        Assert.All(inside, item => Assert.Equal("Graal1234", item.InsertText));
        Assert.Equal("Online player — Graal1234", inside[0].Info);
        Assert.Equal("Player", inside[0].KindLabel);
        Assert.DoesNotContain(engine.Propose("findplayer(ste", 14, players).Items, item => item.Kind == CompletionItemKind.Player);
        Assert.DoesNotContain(engine.Propose("findplayer(\"", 12, players).Items, item => item.Kind == CompletionItemKind.Player);
        Assert.Single(engine.Propose("x(\"gue", 6, players).Items, item => item.Kind == CompletionItemKind.Player);
    }

    [Fact]
    public void InsideString()
    {
        Assert.True(CompletionEngine.IsInsideString("a = \"b", 6));
        Assert.False(CompletionEngine.IsInsideString("a = \"b\" ", 8));
        Assert.True(CompletionEngine.IsInsideString("a = 'b\\'", 8));
        Assert.False(CompletionEngine.IsInsideString("a = \"b\nc", 8));
        Assert.True(CompletionEngine.IsInsideString("\"it's", 5));
    }

    [Fact]
    public void LocalSymbolExtraction()
    {
        const string text = "//#CLIENTSIDE\nfunction onCreated() {}\n//function commented()\n  public function Foo.bar ( x ,y, ) {\nfunction noparen\nfunction ONCREATED() {}\nfunction $d0($x) {}";
        LocalSymbols symbols = LocalSymbols.Extract(text);
        Assert.Equal(new[] { "onCreated", "Foo", "$d0" }, symbols.Functions.Select(function => function.Name));
        Assert.Equal(new[] { "x", "y" }, symbols.Functions[1].Parameters);
        Assert.All(symbols.Functions, function => Assert.Equal("clientside", function.Scope));
        Assert.Equal("script", LocalSymbols.Extract("//#CLIENTSIDE\n//#SERVERSIDE\nfunction a() {}").Functions[0].Scope);
        Assert.Contains("Foo.bar", symbols.Identifiers);
        Assert.Contains("CLIENTSIDE", symbols.Identifiers);
        Assert.DoesNotContain("ONCREATED", symbols.Identifiers);
        Assert.Equal(new[] { "a.b", "c", "\U0001D49Cx", "é1" }, LocalSymbols.ExtractIdentifiers("a.b. 1c c. A.B \U0001D49Cx é1"));
    }

    [Fact]
    public void PrefixStopsAtLineStart()
    {
        Assert.Equal(3, CompletionEngine.PrefixStart("ab\nfoo", 6));
        Assert.Equal(0, CompletionEngine.PrefixStart("$x_1", 4));
        Assert.Equal(2, CompletionEngine.PrefixStart("a.\U0001D49C", 4));
        CompletionResult result = new CompletionEngine(Api, LocalSymbols.Empty).Propose("abc", 99, null);
        Assert.Equal("abc", result.Prefix);
    }

    [Fact]
    public async Task ProposeFromManyThreads()
    {
        var engine = new CompletionEngine(Api, LocalSymbols.Extract("someIdentifier = 1;"));
        CompletionResult[] results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => engine.Propose("set", 3, null))));
        Assert.All(results, result => Assert.Equal("setTimer", result.Items[0].InsertText));
    }
}
