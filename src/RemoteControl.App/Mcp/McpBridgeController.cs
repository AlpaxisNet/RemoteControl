using RemoteControl.Core;
using RemoteControl.Extensions;
using RemoteControl.Ui.Common;

namespace RemoteControl.App.Mcp;

/// <summary>
/// Runs the MCP bridge (<see cref="McpBridge"/>) while the option <c>mcpenabled</c> is on and follows the option live: switching it
/// on starts listening, switching it off closes the socket (pipe) and every MCP connection. UI thread only.
/// </summary>
internal sealed class McpBridgeController : IDisposable
{
    private readonly AppServices _services;
    private readonly Func<McpBridgeOptions> _createOptions;
    private readonly EventHandler<OptionsChangedEventArgs> _optionsChanged;
    private McpBridge? _bridge;
    private bool _disposed;

    /// <summary>Creates the controller and starts the bridge when the option is on.</summary>
    /// <param name="services">The program's services.</param>
    /// <param name="host">What the tools do.</param>
    /// <param name="transport">Where to listen (the C++ names by default; tests give temporary ones).</param>
    /// <param name="deniedNote">Why approvals are denied without a question (a headless program), for the clients; or null.</param>
    public McpBridgeController(AppServices services, IMcpHost host, McpTransportOptions? transport = null, string? deniedNote = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        ArgumentNullException.ThrowIfNull(host);
        _createOptions = () => new McpBridgeOptions
        {
            Host = host,
            Options = services.Options,
            Post = action => UiDispatch.Post(action, "MCP request"),
            AuditLogFile = services.Paths.McpAuditLogFile,
            BackupDirectory = services.Paths.McpBackupDirectory,
            WorkspaceDirectory = services.Paths.McpWorkspaceDirectory,
            ReferenceDirectory = services.Paths.Gs2ReferenceDirectory,
            ScriptApiCacheFile = Path.Combine(services.Paths.EditorCacheDirectory, "scriptapi.json"),
            Transport = transport ?? new McpTransportOptions(),
            Log = services.Log,
            DeniedNote = deniedNote,
        };
        _optionsChanged = (_, e) =>
        {
            if (e.Previous.McpEnabled == e.Current.McpEnabled) return;
            UiDispatch.Post(Apply, "MCP option");
        };
        _services.Options.Changed += _optionsChanged;
        Apply();
    }

    /// <summary>The running bridge, or null.</summary>
    public McpBridge? Bridge => _bridge;

    /// <summary>Starts or stops the bridge as the option says.</summary>
    public void Apply()
    {
        if (_disposed) return;
        bool enabled = _services.Options.Current.McpEnabled;
        if (enabled && _bridge is null)
        {
            var bridge = new McpBridge(_createOptions());
            McpActionResult started = bridge.Start();
            if (started.Success)
            {
                _bridge = bridge;
                _services.Log.Info("The MCP bridge listens on " + bridge.Endpoint + ".");
            }
            else
            {
                bridge.Dispose();
                _services.Log.Warn("The MCP bridge could not start: " + started.Error);
            }
        }
        else if (!enabled && _bridge is not null)
        {
            Stop();
        }
    }

    /// <summary>Stops the bridge.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _services.Options.Changed -= _optionsChanged;
        Stop();
    }

    private void Stop()
    {
        McpBridge? bridge = _bridge;
        _bridge = null;
        if (bridge is null) return;
        _ = bridge.DisposeAsync().AsTask().ContinueWith(task => _ = task.Exception, TaskScheduler.Default);
        _services.Log.Info("The MCP bridge stopped.");
    }
}
