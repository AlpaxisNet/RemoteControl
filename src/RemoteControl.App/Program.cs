using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using RemoteControl.Core;
using RemoteControl.Extensions;
using RemoteControl.Ui.Common;

namespace RemoteControl.App;

/// <summary>Where the program starts (<c>TMain.cpp:656-873</c>).</summary>
public static partial class Program
{
    /// <summary>The command line the program was started with.</summary>
    internal static StartupArguments Arguments { get; private set; } = StartupArguments.Default;

    /// <summary>The <c>--debug</c>/<c>--packetlog</c> output of this process.</summary>
    internal static DebugOutput Debug { get; private set; } = DebugOutput.None;

    /// <summary>The crash log of the start, before the application's handlers are installed.</summary>
    internal static CrashLog? EarlyCrashLog { get; private set; }

    /// <summary>The process handlers installed first; the application replaces them with <see cref="UiErrors"/>.</summary>
    internal static IDisposable? EarlyHandlers { get; set; }

    /// <summary>Starts the program: the MCP server on standard input and output with <c>--mcp</c>, the windows otherwise (none shown on the desktop with <c>--headless</c>).</summary>
    /// <param name="args">The command line.</param>
    /// <returns>The exit code.</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        AppPaths paths = AppPaths.ForCurrentUser();
        // The last-resort handlers come first: anything that fails from here on is written to the crash log.
        var crashLog = new CrashLog(paths.CrashLogFile);
        EarlyCrashLog = crashLog;
        EarlyHandlers = crashLog.InstallProcessHandlers();
        try
        {
            StartupArguments arguments = StartupArguments.Parse(args ?? Array.Empty<string>());
            return Dispatch(arguments, runMcp: parsed => RunMcpServer(paths, parsed), runWindows: parsed => RunWindows(parsed, crashLog));
        }
        catch (Exception exception)
        {
            crashLog.Write("startup", exception, true);
            ReportStartupFailure(exception, crashLog.FilePath);
            return 1;
        }
        finally
        {
            EarlyHandlers?.Dispose();
            EarlyHandlers = null;
        }
    }

    /// <summary>The decision of <see cref="Main"/>: the MCP server without any window, or the program with its windows.</summary>
    /// <param name="arguments">The command line.</param>
    /// <param name="runMcp">Runs the MCP server.</param>
    /// <param name="runWindows">Runs the program.</param>
    /// <returns>The exit code.</returns>
    internal static int Dispatch(StartupArguments arguments, Func<StartupArguments, int> runMcp, Func<StartupArguments, int> runWindows)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(runMcp);
        ArgumentNullException.ThrowIfNull(runWindows);
        return arguments.Mode == StartMode.McpServer ? runMcp(arguments) : runWindows(arguments);
    }

    /// <summary>The application as the desktop and the previewer build it.</summary>
    /// <returns>The builder.</returns>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().WithBundledFonts().LogToTrace();

    /// <summary>
    /// The application of a headless run (<c>--headless</c>): Avalonia's screenless platform, on which every window of the program exists
    /// and works (sessions, editors, the login form the MCP tools fill in) but none is shown on the desktop; no display is needed.
    /// </summary>
    /// <returns>The builder.</returns>
    internal static AppBuilder BuildHeadlessApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont()
        .WithBundledFonts()
        .LogToTrace();

    private static int RunMcpServer(AppPaths paths, StartupArguments arguments)
    {
        // The options decide whether MCP is enabled; nothing else of the program is started, and no window.
        var options = new OptionsStore(paths);
        options.Load();
        return McpStdioServer.RunAsync(options.Current, arguments.McpInstance).GetAwaiter().GetResult();
    }

    private static int RunWindows(StartupArguments arguments, CrashLog crashLog)
    {
        // A headless program has no window to show what happens: its log goes to standard error, as with --debug.
        bool debug = arguments.Debug || arguments.Headless;
        if (debug && OperatingSystem.IsWindows()) AttachParentConsole();
        Arguments = arguments;
        Debug = new DebugOutput(Console.Error, debug, arguments.PacketLog);
        Debug.Line("debug logging enabled");
        try
        {
            AppBuilder builder = arguments.Headless ? BuildHeadlessApp() : BuildAvaloniaApp();
            return builder.StartWithClassicDesktopLifetime(arguments.Remaining.ToArray(), ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception exception)
        {
            crashLog.Write("startup", exception, true);
            ReportStartupFailure(exception, crashLog.FilePath);
            return 1;
        }
    }

    private static void ReportStartupFailure(Exception exception, string crashLogFile)
    {
        string message = "Remote Control could not start:\n\n" + exception.Message + "\n\nDetails were written to " + crashLogFile + ".";
        try
        {
            Console.Error.WriteLine(message);
            Console.Error.WriteLine(exception);
        }
        catch (Exception)
        {
            // No standard error.
        }
        if (OperatingSystem.IsWindows())
        {
            try
            {
                MessageBoxW(IntPtr.Zero, message, "Remote Control", 0x10);
            }
            catch (Exception)
            {
                // Nothing more can be shown.
            }
        }
    }

    private static void AttachParentConsole()
    {
        try
        {
            if (!AttachConsole(-1)) return;
            var error = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetError(error);
            Console.SetOut(output);
        }
        catch (Exception)
        {
            // Without a console the debug lines are lost, as in the C++.
        }
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
