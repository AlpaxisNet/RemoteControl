using System.Text.Json;
using RemoteControl.Core;

namespace RemoteControl.Extensions;

/// <summary>Runs <see cref="IMcpHost"/> calls on the host's context.</summary>
internal interface IMcpHostInvoker
{
    /// <summary>Runs <paramref name="call"/> on the host's context and returns its result.</summary>
    Task<T> InvokeAsync<T>(Func<IMcpHost, Task<T>> call);

    /// <summary>Cancelled when the bridge stops: server tools end their waits.</summary>
    CancellationToken Stopping { get; }
}

/// <summary>What one request line produced: the response line (or none) and the audit line (or none).</summary>
/// <param name="Response">The JSON-RPC response, or <c>null</c> for a notification.</param>
/// <param name="AuditLine">The audit log line (without line break), or <c>null</c>.</param>
internal sealed record McpOutcome(string? Response, string? AuditLine);

/// <summary>
/// The JSON-RPC request handling of the program's MCP bridge (<c>processRequest</c>, <c>TMcpServer.cpp:100-172</c>). Runs on a
/// background thread; only the <see cref="IMcpHost"/> calls go to the host's context.
/// </summary>
/// <remarks>
/// <para>Methods: <c>rc/instance</c> (the probe of the <c>--mcp</c> process), <c>initialize</c>, <c>notifications/initialized</c>,
/// <c>ping</c> (added: an empty result, which MCP clients expect), <c>tools/list</c>, <c>tools/call</c>, and (added) <c>resources/list</c>,
/// <c>resources/read</c> and <c>resources/templates/list</c> for the bundled GS2 reference; anything else is -32601.</para>
/// <para>Error codes: -32700 parse error and -32600 invalid request (added: the C++ guessed fields in any text), -32601 method not
/// found, -32001 MCP disabled, -32002 mutation denied by the user, -32003 outside the server scope, -32602 invalid arguments or a
/// tool that is unknown or not allowed, -32603 an internal error of the program.</para>
/// <para>Differences from the C++: a request is parsed as JSON; the <c>id</c> is the request's own top-level id (the C++ took the first
/// <c>"id"</c> anywhere, D-38); a notification (a request without <c>id</c>) never gets a response, so responses cannot shift (D-38);
/// tool arguments are read from <c>params.arguments</c> (or <c>params</c>); every denied approval is audited.</para>
/// </remarks>
internal sealed class McpRequestProcessor
{
    /// <summary>The MCP protocol revision the C++ answered with.</summary>
    public const string ProtocolVersion = "2025-06-18";

    private static readonly HashSet<string> ConnectionTools = new(StringComparer.Ordinal)
    {
        "rc_status", "rc_list_connections", "rc_select_connection", "rc_login_status", "rc_submit_login", "rc_list_servers", "rc_connect_server", "rc_list_windows", "rc_read_window",
    };

    private readonly IMcpHostInvoker _host;
    private readonly Func<RcOptions> _options;
    private readonly Action<string> _saveScriptTypeApproval;
    private readonly int _processId;
    private readonly Func<DateTimeOffset> _clock;
    private readonly McpToolServices _services;

    /// <summary>Creates a processor.</summary>
    /// <param name="host">Runs host calls on the host's context.</param>
    /// <param name="options">The current options (read for every request).</param>
    /// <param name="saveScriptTypeApproval">Saves "always allow" for a script type (<c>Weapon</c>, <c>Class</c>, <c>NPC</c>).</param>
    /// <param name="processId">The process id reported to clients.</param>
    /// <param name="clock">The clock of the audit log.</param>
    /// <param name="services">What the server tools share (backups, the local folders); by default in a temporary folder.</param>
    public McpRequestProcessor(IMcpHostInvoker host, Func<RcOptions> options, Action<string> saveScriptTypeApproval, int processId, Func<DateTimeOffset>? clock = null, McpToolServices? services = null)
    {
        _host = host;
        _options = options;
        _saveScriptTypeApproval = saveScriptTypeApproval;
        _processId = processId;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        string temporary = Path.Combine(Path.GetTempPath(), "rc-mcp-" + processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _services = services ?? new McpToolServices(new McpBackupStore(Path.Combine(temporary, "backups"), AppLog.Default), Path.Combine(temporary, "files"), saveScriptTypeApproval, processId, AppLog.Default);
    }

    /// <summary>Whether the bridge answers <paramref name="line"/>: every non-blank line except a JSON object without <c>id</c> (a notification).</summary>
    /// <param name="line">One request line.</param>
    /// <returns>Whether one response line follows.</returns>
    public static bool ExpectsResponse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            // A request that is no valid text (a lone surrogate) is answered with a parse error, so it expects a response.
            if (!McpArguments.IsWellFormed(document.RootElement)) return true;
            return document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.TryGetProperty("id", out _);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>Handles one request line.</summary>
    /// <param name="line">The line.</param>
    /// <returns>The response and the audit line.</returns>
    public async Task<McpOutcome> ProcessAsync(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return new McpOutcome(null, null);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return new McpOutcome(Error(null, -32700, "Parse error"), null);
        }
        // JSON lets a string or a property name hold an escaped lone surrogate, which no .NET string can: such a request is refused
        // whole, here, so that nothing later (the id, the method, a tool's arguments) can throw on it.
        if (!McpArguments.IsWellFormed(document.RootElement))
        {
            document.Dispose();
            return new McpOutcome(Error(null, -32700, "Parse error: the request has text that is not valid UTF-16 (a lone surrogate)"), null);
        }
        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new McpOutcome(Error(null, -32600, "Invalid Request"), null);
            bool hasId = root.TryGetProperty("id", out JsonElement idElement);
            string? id = null;
            if (hasId)
            {
                if (idElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null)) return new McpOutcome(Error(null, -32600, "Invalid Request"), null);
                id = idElement.GetRawText();
            }
            string method = root.TryGetProperty("method", out JsonElement methodElement) && methodElement.ValueKind == JsonValueKind.String ? McpArguments.Text(methodElement) ?? string.Empty : string.Empty;
            JsonElement parameters = root.TryGetProperty("params", out JsonElement value) && value.ValueKind == JsonValueKind.Object ? value : default;
            var arguments = new McpArguments(parameters);
            string tool = method == "tools/call" ? arguments.Name : string.Empty;
            RcOptions options = _options();
            var context = new RequestContext(id, method, tool, options);
            if (!options.McpEnabled)
            {
                context.Audit("disabled");
                return context.Outcome(hasId ? Error(id, -32001, "MCP integration is disabled in RC Options") : null, _clock);
            }
            if (!hasId)
            {
                if (method == "notifications/initialized") context.Audit("ok");
                return context.Outcome(null, _clock);
            }
            try
            {
                string response = await DispatchAsync(context, arguments).ConfigureAwait(false);
                return context.Outcome(response, _clock);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                context.Audit("failed");
                return context.Outcome(Error(id, -32603, "Internal error: " + exception.Message), _clock);
            }
        }
    }

    private async Task<string> DispatchAsync(RequestContext context, McpArguments arguments)
    {
        string? id = context.Id;
        switch (context.Method)
        {
            case "rc/instance":
                return Response(id, await InstanceTextAsync().ConfigureAwait(false));
            case "initialize":
                context.Audit("ok");
                return Response(id, "{\"protocolVersion\":\"" + ProtocolVersion + "\",\"capabilities\":{\"tools\":{\"listChanged\":false},\"resources\":{\"listChanged\":false}},\"serverInfo\":{\"name\":\"rc\",\"version\":\"3\"}}");
            case "ping":
                return Response(id, "{}");
            case "tools/list":
                context.Audit("ok");
                return Response(id, "{\"tools\":" + McpTools.ListJson(context.Options) + "}");
            case "tools/call":
                return await CallToolAsync(context, arguments.ToolArguments).ConfigureAwait(false);
            case "resources/list" or "resources/read" or "resources/templates/list":
                return await ResourcesAsync(context, arguments).ConfigureAwait(false);
            default:
                context.Audit("method-not-found");
                return Error(id, -32601, "Method not found");
        }
    }

    private async Task<string> CallToolAsync(RequestContext context, McpArguments arguments)
    {
        string? id = context.Id;
        string tool = context.Tool;
        RcOptions options = context.Options;
        bool full = options.McpFullControl;
        bool canRead = full || options.McpRead;
        bool canWrite = full || options.McpWrite;
        bool canServer = full || options.McpServer;
        bool canLogin = full || options.McpLogin;
        bool canWindows = full || options.McpWindows;
        bool canAdmin = full || options.McpAdmin;
        if (McpTools.TryGet(tool, out McpToolDefinition? definition) && definition.Run is not null)
        {
            if (!McpTools.Allowed(definition.Group, options))
            {
                context.Audit("scope-disabled");
                return Error(id, -32602, "Tool unavailable or disabled by MCP scope");
            }
            return await RunServerToolAsync(context, definition, arguments).ConfigureAwait(false);
        }
        if (!ConnectionTools.Contains(tool) && options.McpServerScope.Length > 0)
        {
            McpServerState server = await _host.InvokeAsync(host => host.GetServerStateAsync()).ConfigureAwait(false);
            if (!string.Equals(server.Name, options.McpServerScope, StringComparison.OrdinalIgnoreCase))
            {
                context.Audit("server-scope-denied");
                return Error(id, -32003, "Current server is outside configured MCP server scope");
            }
        }
        switch (tool)
        {
            case "rc_status" when canRead:
            {
                McpServerState server = await _host.InvokeAsync(host => host.GetServerStateAsync()).ConfigureAwait(false);
                string text = JsonText.Write(writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("bridge", "connected");
                    writer.WriteNumber("processId", _processId);
                    writer.WriteBoolean("serverConnected", server.Connected);
                    writer.WriteString("server", JsonText.Clean(server.Name));
                    writer.WriteString("serverScope", JsonText.Clean(options.McpServerScope));
                    McpLocalFiles local = _services.LocalFiles(options);
                    writer.WriteString("workspace", JsonText.Clean(local.Workspace));
                    writer.WriteStartArray("localRoots");
                    foreach (string root in local.Roots) writer.WriteStringValue(JsonText.Clean(root));
                    writer.WriteEndArray();
                    writer.WriteString("backups", JsonText.Clean(_services.Backups.Root));
                    writer.WriteBoolean("approvals", options.McpApprove);
                    writer.WriteEndObject();
                });
                return Ok(context, text);
            }
            case "rc_list_connections" when canRead:
                return Ok(context, StringArray(await _host.InvokeAsync(host => host.ListConnectionsAsync()).ConfigureAwait(false)));
            case "rc_login_status" when canRead:
            {
                McpLoginState login = await _host.InvokeAsync(host => host.GetLoginStateAsync()).ConfigureAwait(false);
                return Ok(context, JsonText.Write(writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteBoolean("visible", login.Visible);
                    writer.WriteString("account", JsonText.Clean(login.Account));
                    writer.WriteString("nickname", JsonText.Clean(login.Nickname));
                    writer.WriteBoolean("hasPassword", login.HasPassword);
                    writer.WriteEndObject();
                }));
            }
            case "rc_list_servers" when canRead:
                return Ok(context, StringArray(await _host.InvokeAsync(host => host.ListServersAsync()).ConfigureAwait(false)));
            case "rc_list_windows" when canRead:
                return Ok(context, WindowsText(await _host.InvokeAsync(host => host.ListWindowsAsync()).ConfigureAwait(false), string.Empty));
            case "rc_read_window" when canRead:
                return Ok(context, WindowsText(await _host.InvokeAsync(host => host.ListWindowsAsync()).ConfigureAwait(false), arguments.String("title")));
            case "rc_list_script_editors" when canRead:
                return Ok(context, EditorsText(await ListEditorsAsync().ConfigureAwait(false), string.Empty));
            case "rc_read_script_editor" when canRead:
                return Ok(context, EditorsText(await ListEditorsAsync().ConfigureAwait(false), arguments.String("editor")));
            case "rc_read_script_lines" when canRead:
            {
                McpScriptEditorInfo? editor = FindEditor(await ListEditorsAsync().ConfigureAwait(false), arguments.String("editor"));
                context.Audit(editor is null ? "failed" : "ok");
                return Response(id, ToolResult(editor is null ? "Script editor is not open" : McpProtocol.EditorLineRange(editor.Text, arguments.Int("start", 1), arguments.Int("end", 200)), editor is null));
            }
            case "rc_search_script_editor" when canRead:
            {
                McpScriptEditorInfo? editor = FindEditor(await ListEditorsAsync().ConfigureAwait(false), arguments.String("editor"));
                context.Audit(editor is null ? "failed" : "ok");
                string result = editor is null ? "Script editor is not open" : McpProtocol.EditorSearch(editor.Text, arguments.String("query"), arguments.Bool("caseSensitive", false), arguments.Int("contextLines", 0));
                return Response(id, ToolResult(result, editor is null));
            }
            case "rc_replace_script_text" when canWrite:
            {
                if (!arguments.TryString("editor", out string name) || !arguments.TryString("expected", out string expected) || !arguments.TryString("replacement", out string replacement))
                {
                    context.Audit("invalid-json-string");
                    return Error(id, -32602, "Partial edit requires valid JSON editor, expected, and replacement strings");
                }
                if (!await ApproveScriptAsync(McpScriptAction.Edit, name).ConfigureAwait(false)) return Denied(context);
                McpActionResult result = await _host.InvokeAsync(host => host.UpdateScriptEditorAsync(name, current =>
                    McpProtocol.TryGuardedReplace(current, expected, replacement, out string edited, out string error) ? McpTextEdit.Replace(edited) : McpTextEdit.Refuse(error))).ConfigureAwait(false);
                return Done(context, result, "Open script buffer partially edited; changes remain unsaved");
            }
            case "rc_write_script_editor" when canWrite:
            {
                if (!arguments.TryString("editor", out string name) || !arguments.TryString("text", out string text))
                {
                    context.Audit("invalid-json-string");
                    return Error(id, -32602, "Edit requires valid JSON editor and text strings");
                }
                if (!await ApproveScriptAsync(McpScriptAction.Edit, name).ConfigureAwait(false)) return Denied(context);
                McpActionResult result = await _host.InvokeAsync(host => host.UpdateScriptEditorAsync(name, _ => McpTextEdit.Replace(text))).ConfigureAwait(false);
                return Done(context, result, "Open script buffer edited; changes remain unsaved");
            }
            case "rc_save_script_editor" when canWrite:
            {
                string name = arguments.String("editor");
                if (!await ApproveScriptAsync(McpScriptAction.SaveApply, name).ConfigureAwait(false)) return Denied(context);
                return Done(context, await _host.InvokeAsync(host => host.SaveScriptEditorAsync(name)).ConfigureAwait(false), "Script Save/Apply action invoked");
            }
            case "rc_submit_login" when canLogin:
            {
                if (!await ApproveAsync("Submit the native RC login form").ConfigureAwait(false)) return Denied(context);
                string account = arguments.String("account");
                string nickname = arguments.String("nickname");
                return Done(context, await _host.InvokeAsync(host => host.SubmitLoginAsync(account, nickname)).ConfigureAwait(false), "Native login form submitted");
            }
            case "rc_connect_server" when canLogin:
            {
                string server = arguments.String("server");
                if (!await ApproveAsync("Connect to RC server " + server).ConfigureAwait(false)) return Denied(context);
                return Done(context, await _host.InvokeAsync(host => host.ConnectServerAsync(server)).ConfigureAwait(false), "Native server connection invoked");
            }
            case "rc_select_connection" when canWindows:
            {
                string server = arguments.String("server");
                if (!await ApproveAsync("Select RC server session " + server).ConfigureAwait(false)) return Denied(context);
                return Done(context, await _host.InvokeAsync(host => host.SelectConnectionAsync(server)).ConfigureAwait(false), "RC server session selected");
            }
            case "rc_open_window" when canWindows:
            {
                string view = arguments.String("view");
                if (!McpProtocol.Views.Contains(view)) return Done(context, McpActionResult.Fail("Unsupported managed RC view"), string.Empty);
                if (!await ApproveAsync("Open managed RC view " + view).ConfigureAwait(false)) return Denied(context);
                return Done(context, await _host.InvokeAsync(host => host.OpenViewAsync(view)).ConfigureAwait(false), "Managed RC view opened");
            }
            case "rc_send_chat" when canServer:
            {
                string text = arguments.String("text");
                if (!await ApproveAsync("Send RC chat text").ConfigureAwait(false)) return Denied(context);
                return Done(context, await _host.InvokeAsync(host => host.SendChatAsync(text)).ConfigureAwait(false), "RC chat text sent");
            }
            case "rc_focused_window_action" when canAdmin:
            {
                string action = arguments.String("action");
                if (!await ApproveAsync("Focused window action: " + action).ConfigureAwait(false)) return Denied(context);
                if (action is not ("present" or "close")) return Done(context, McpActionResult.Fail("No focused window or invalid action"), string.Empty);
                return Done(context, await _host.InvokeAsync(host => host.FocusedWindowActionAsync(action)).ConfigureAwait(false), "Focused window action invoked");
            }
            default:
                context.Audit("scope-disabled");
                return Error(id, -32602, "Tool unavailable or disabled by MCP scope");
        }
    }

    /// <summary>
    /// Runs a server tool on the host's context: finds its session (the <c>server</c> argument, or the selected session), checks the
    /// server scope against it, then runs it. A <see cref="McpToolException"/> is a tool error the client reads; a denied approval is the
    /// JSON-RPC error -32002; a session outside the scope is -32003.
    /// </summary>
    private async Task<string> RunServerToolAsync(RequestContext context, McpToolDefinition definition, McpArguments arguments)
    {
        RcOptions options = context.Options;
        string server = arguments.String("server");
        CancellationToken token = _host.Stopping;
        try
        {
            string text = await _host.InvokeAsync(async host =>
            {
                McpSessionTarget? target = definition.Session == McpSessionUse.None ? null : await host.FindSessionAsync(server).ConfigureAwait(true);
                if (definition.Session == McpSessionUse.Required && target is null)
                    throw new McpToolException(server.Length == 0 ? "No connected RC server session: connect one (rc_connect_server) or name it in server." : "Connected RC server session not found: " + server + " (rc_list_connections lists them).");
                // A tool that needs its session is refused at once outside the scope; one that may work without it is refused when it uses it.
                if (definition.Session == McpSessionUse.Required && target is not null && options.McpServerScope.Length > 0 && !string.Equals(target.ServerName, options.McpServerScope, StringComparison.OrdinalIgnoreCase))
                    throw new McpScopeException();
                var call = new McpToolContext(host, arguments, options, target, _services, token, TimeSpan.FromSeconds(definition.TimeoutSeconds));
                return await definition.Run!(call).ConfigureAwait(true);
            }).ConfigureAwait(false);
            context.Audit("ok");
            return Response(context.Id, ToolResult(text, false));
        }
        catch (McpToolException exception)
        {
            context.Audit("failed");
            return Response(context.Id, ToolResult(exception.Message, true));
        }
        catch (McpDeniedException)
        {
            return Denied(context);
        }
        catch (McpScopeException)
        {
            context.Audit("server-scope-denied");
            return Error(context.Id, -32003, "Current server is outside configured MCP server scope");
        }
    }

    /// <summary>The prefix of the resources: the documents of the bundled GS2 reference.</summary>
    internal const string ResourcePrefix = "gs2ref://reference/";

    /// <summary>
    /// <c>resources/list</c>, <c>resources/read</c> and <c>resources/templates/list</c>: the documents of the GS2 reference beside the program
    /// (the verified guide first, with the highest priority), for clients that attach documents. Needs the read permission.
    /// </summary>
    private async Task<string> ResourcesAsync(RequestContext context, McpArguments arguments)
    {
        if (!McpTools.Allowed(McpToolGroup.Read, context.Options))
        {
            context.Audit("scope-disabled");
            return Error(context.Id, -32602, "Resources unavailable or disabled by MCP scope");
        }
        if (context.Method == "resources/templates/list")
        {
            context.Audit("ok");
            return Response(context.Id, "{\"resourceTemplates\":[]}");
        }
        McpReference reference = _services.Reference(context.Options);
        IReadOnlyList<McpReferenceDocument> documents = await Task.Run(() => reference.Documents().Where(document => document.Bundled).ToArray()).ConfigureAwait(false);
        if (context.Method == "resources/list")
        {
            context.Audit("ok");
            return Response(context.Id, JsonText.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteStartArray("resources");
                foreach (McpReferenceDocument document in documents)
                {
                    writer.WriteStartObject();
                    writer.WriteString("uri", JsonText.Clean(ResourcePrefix + document.Id));
                    writer.WriteString("name", JsonText.Clean(document.Id));
                    writer.WriteString("description", JsonText.Clean((document.Verified ? "Verified by people. " : string.Empty) + (document.Description.Length > 0 ? document.Description : "GS2 reference document.")));
                    writer.WriteString("mimeType", MimeType(document.Id));
                    writer.WriteNumber("size", document.Size);
                    writer.WriteStartObject("annotations");
                    writer.WriteNumber("priority", document.Verified ? 1.0 : document.Rank <= 2 ? 0.8 : 0.4);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }));
        }
        string uri = arguments.String("uri");
        McpReferenceDocument? found = uri.StartsWith(ResourcePrefix, StringComparison.Ordinal) ? McpReference.Find(documents, uri[ResourcePrefix.Length..]) : null;
        string? text = found is null ? null : McpReference.Read(found);
        if (found is null || text is null)
        {
            context.Audit("failed");
            return Error(context.Id, -32602, "Resource not found: " + uri);
        }
        context.Audit("ok");
        return Response(context.Id, JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("contents");
            writer.WriteStartObject();
            writer.WriteString("uri", JsonText.Clean(ResourcePrefix + found.Id));
            writer.WriteString("mimeType", MimeType(found.Id));
            writer.WriteString("text", JsonText.Clean(text));
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }));

        static string MimeType(string id) => Path.GetExtension(id).ToLowerInvariant() switch
        {
            ".md" => "text/markdown",
            ".xml" => "application/xml",
            ".json" => "application/json",
            _ => "text/plain",
        };
    }

    private async Task<string> InstanceTextAsync()
    {
        IReadOnlyList<McpScriptEditorInfo> editors = await ListEditorsAsync().ConfigureAwait(false);
        IReadOnlyList<McpWindowInfo> windows = await _host.InvokeAsync(host => host.ListWindowsAsync()).ConfigureAwait(false);
        McpServerState server = await _host.InvokeAsync(host => host.GetServerStateAsync()).ConfigureAwait(false);
        return JsonText.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("processId", _processId);
            writer.WriteBoolean("active", windows.Any(window => window.Active));
            writer.WriteBoolean("serverConnected", server.Connected);
            writer.WriteNumber("editorCount", editors.Count);
            writer.WriteString("server", JsonText.Clean(server.Name));
            writer.WriteEndObject();
        });
    }

    private Task<IReadOnlyList<McpScriptEditorInfo>> ListEditorsAsync() => _host.InvokeAsync(host => host.ListScriptEditorsAsync());

    private async Task<bool> ApproveAsync(string description)
    {
        if (!_options().McpApprove) return true;
        return await _host.InvokeAsync(host => host.ApproveAsync(description)).ConfigureAwait(false);
    }

    private async Task<bool> ApproveScriptAsync(McpScriptAction action, string editor)
    {
        RcOptions options = _options();
        if (!options.McpApprove) return true;
        string type = McpProtocol.ScriptTypeOf(editor);
        bool saved = type switch
        {
            "Weapon" => options.McpApproveWeapon,
            "Class" => options.McpApproveClass,
            "NPC" => options.McpApproveNpc,
            _ => false,
        };
        if (saved) return true;
        McpApproval answer = await _host.InvokeAsync(host => host.ApproveScriptAsync(action, editor, type)).ConfigureAwait(false);
        if (answer == McpApproval.AlwaysAllow && type.Length > 0) _saveScriptTypeApproval(type);
        return answer is McpApproval.AllowOnce or McpApproval.AlwaysAllow;
    }

    private static McpScriptEditorInfo? FindEditor(IReadOnlyList<McpScriptEditorInfo> editors, string name) =>
        editors.FirstOrDefault(editor => string.Equals(editor.Name, name, StringComparison.Ordinal));

    private static string Ok(RequestContext context, string text)
    {
        context.Audit("ok");
        return Response(context.Id, ToolResult(text, false));
    }

    private static string Done(RequestContext context, McpActionResult result, string success)
    {
        context.Audit(result.Success ? "ok" : "failed");
        return Response(context.Id, ToolResult(result.Success ? success : result.Error, !result.Success));
    }

    private static string Denied(RequestContext context)
    {
        context.Audit("approval-denied");
        return Error(context.Id, -32002, "Mutation denied by user");
    }

    private static string StringArray(IReadOnlyList<string> values) => JsonText.Write(writer =>
    {
        writer.WriteStartArray();
        foreach (string value in values) writer.WriteStringValue(JsonText.Clean(value));
        writer.WriteEndArray();
    });

    private static string WindowsText(IReadOnlyList<McpWindowInfo> windows, string requested) => JsonText.Write(writer =>
    {
        writer.WriteStartArray();
        foreach (McpWindowInfo window in windows)
        {
            if (requested.Length > 0 && !string.Equals(window.Title, requested, StringComparison.OrdinalIgnoreCase)) continue;
            writer.WriteStartObject();
            writer.WriteString("title", JsonText.Clean(window.Title));
            writer.WriteBoolean("active", window.Active);
            writer.WriteStartArray("tabs");
            foreach (string tab in window.Tabs) writer.WriteStringValue(JsonText.Clean(tab));
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    });

    private static string EditorsText(IReadOnlyList<McpScriptEditorInfo> editors, string requested) => JsonText.Write(writer =>
    {
        writer.WriteStartArray();
        foreach (McpScriptEditorInfo editor in editors)
        {
            if (requested.Length > 0 && !string.Equals(editor.Name, requested, StringComparison.Ordinal)) continue;
            writer.WriteStartObject();
            writer.WriteString("name", JsonText.Clean(editor.Name));
            writer.WriteBoolean("active", editor.Active);
            writer.WriteBoolean("modified", editor.Modified);
            writer.WriteString("text", JsonText.Clean(editor.Text));
            writer.WriteString("selection", JsonText.Clean(editor.Selection));
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    });

    /// <summary>An MCP tool result: <c>{"content":[{"type":"text","text":...}],"isError":...}</c>.</summary>
    internal static string ToolResult(string text, bool isError) => JsonText.Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartArray("content");
        writer.WriteStartObject();
        writer.WriteString("type", "text");
        writer.WriteString("text", JsonText.Clean(text));
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteBoolean("isError", isError);
        writer.WriteEndObject();
    });

    /// <summary>A JSON-RPC result response; <paramref name="result"/> is JSON text.</summary>
    internal static string Response(string? id, string result) => "{\"jsonrpc\":\"2.0\",\"id\":" + (id ?? "null") + ",\"result\":" + result + "}";

    /// <summary>A JSON-RPC error response.</summary>
    internal static string Error(string? id, int code, string message) => JsonText.Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        writer.WritePropertyName("id");
        if (id is null) writer.WriteNullValue();
        else writer.WriteRawValue(id, skipInputValidation: false);
        writer.WriteStartObject("error");
        writer.WriteNumber("code", code);
        writer.WriteString("message", JsonText.Clean(message));
        writer.WriteEndObject();
        writer.WriteEndObject();
    });

    /// <summary>The request being handled and its audit outcome.</summary>
    private sealed class RequestContext
    {
        private string? _outcome;

        public RequestContext(string? id, string method, string tool, RcOptions options)
        {
            Id = id;
            Method = method;
            Tool = tool;
            Options = options;
        }

        public string? Id { get; }

        public string Method { get; }

        public string Tool { get; }

        public RcOptions Options { get; }

        public void Audit(string outcome) => _outcome = outcome;

        public McpOutcome Outcome(string? response, Func<DateTimeOffset> clock)
        {
            if (_outcome is null || !Options.McpAudit) return new McpOutcome(response, null);
            string line = JsonText.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("time", clock().ToUnixTimeSeconds());
                writer.WriteString("method", JsonText.Clean(Method));
                writer.WriteString("tool", JsonText.Clean(Tool));
                writer.WriteString("outcome", _outcome);
                writer.WriteEndObject();
            });
            return new McpOutcome(response, line);
        }
    }
}
