# Remote Control (C#): guide for contributors and agents

Remote Control is a staff tool for Graal Online style game servers: the RC chat, the player list, accounts and rights, bans, private
messages, the file browser, sync, and script editing over the NPC-Control (NC) connection. It is written in C# on .NET 11 with
Avalonia. It was rewritten in November 2026 from a C++20/GTK3 program and the C library RClib, which crashed at random; that code
is at [MorenoLand/GScript.RemoteControl](https://github.com/MorenoLand/GScript.RemoteControl) (commit `6b73e05`) and
[MorenoLand/GScript.RClib](https://github.com/MorenoLand/GScript.RClib), and is the reference for behaviour where a comment names
a C++ file and line.
[README.md](README.md) is for users: what the program does, where its files are, what is different from the C++ version.

## Layout

| Path | What it is |
| --- | --- |
| `src/RemoteControl.Protocol/` | the network: list server, the RC and NC connections, the texts they exchange. No user interface |
| `src/RemoteControl.Core/` | settings and storage: options, saved accounts, list-server profiles, paths, the crash log. No user interface, no network |
| `src/RemoteControl.Scripting/` | the script language: checking, formatting, completion, find and replace, highlighting definitions. No user interface |
| `src/RemoteControl.Extensions/` | extension packages and the MCP server, without their windows |
| `src/RemoteControl.Sync/` | folder and server synchronisation and git backups, without their window |
| `src/RemoteControl.Ui.Common/` | what every window shares: themes, pictures, controls, dialogs, error handling, the application's services |
| `src/RemoteControl.Ui.Shell/` | what the groups of windows agree on: a session and its windows, the tool registry, the services they offer each other |
| `src/RemoteControl.Ui.*/` | one library per group of windows: Start, Session, Players, Scripts, Files, Extensions |
| `src/RemoteControl.App/` | the program (`RemoteControl`): start-up, composition, tray, crash handling |
| `tests/RemoteControl.*.Tests/` | xUnit tests; the window tests run without a screen (Avalonia.Headless) |
| `tests/RemoteControl.Protocol.Testing/`, `tests/RemoteControl.Ui.TestKit/` | what tests share: fake servers and a recording session; the screenless application and input helpers |
| `assets/runtime/` | pictures, sounds, fonts, language files, the GS2 reference (`gs2-reference/`, a copy of GScript.GS2Taco's documentation) and the default options, copied beside the executable |

A library depends only on the ones above it in this table. A group of windows never references another group: it opens a window of
another group through the tool registry of `Ui.Shell`.

## Rules that keep it from crashing

The C++ crashed because of what these rules forbid; they are not style.

1. **One thread owns the state.** A session reads its sockets on background tasks, but every change of its state and every event
   it raises happens on the context it was given (the UI thread in the program, an inline context in tests). User-interface code
   therefore needs no lock, and never touches a control from another thread. Work that takes time (network, files, git, child
   processes) is `async` and never blocks the UI thread.
2. **Nothing a server sends can throw out of a receive loop.** Packets are read through a bounds-checked reader; a packet that is
   too short, has a negative length or does not parse is dropped with a log line. A frame is different: one whose header announces
   an impossible length (zero after the `FF FF` marker, over 8 MiB) or whose body cannot be decoded ends the connection with a
   "Protocol error" reason and is never skipped, because the bytes after it cannot be trusted (decoded as packets they show windows
   and players that do not exist). A connection that fails ends with one `Disconnected` event carrying the reason.
3. **Sends are ordered and atomic.** One writer per connection (a queue or a lock) so that two callers can never interleave the
   bytes or the cipher state of two frames.
4. **Every handler ends in a catch.** No `async void` except event handlers, and those go through the shared helper that logs
   the exception and shows it. An exception in one window never takes the program down; the program's last-resort handlers
   (`AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`, the dispatcher's) write the crash log.
5. **What is disposed is unsubscribed.** A window that closes removes its handlers from the session and stops its timers; an event
   that arrives for a closed window is ignored. A session that is replaced (reconnect) can no longer raise events.
6. **Files are written whole.** Options, accounts and profiles are written to a temporary file and moved into place. A file the
   program cannot read is never overwritten with defaults: it is kept (renamed aside) and the user is told.
7. **Paths from a server are untrusted.** A downloaded file is written only inside the folder chosen for it; `..`, absolute paths
   and drive letters in a server's file name are refused.
8. **Text has an encoding.** The servers speak Windows-1252 bytes; the program converts at the connection and uses `string`
   everywhere else.

## Conventions

- C# 15, nullable enabled, warnings are errors, file-scoped namespaces, four spaces. Public types and members of `Protocol` and
  `Core` have XML doc comments in plain English.
- No `TODO`, no stub, no `NotImplementedException` in finished work. What is deliberately left out is written down in `README.md`.
- Windows are Avalonia XAML with code-behind; a list a window shows is an `ObservableCollection` of a small view-model class.
  No MVVM framework. New NuGet packages only with a reason (the versions are in `Directory.Packages.props`).
- Existing users keep their files: the options file (`control2config.txt`), the saved accounts (`accounts.key`, `accounts.dat`)
  and the list-server profiles (`listservers.conf`) are read in the formats the C++ wrote.
- The built-in list server is Alpaxis (`listserver.alpaxis.net`, port 14922). Users add their own profiles.
- Avalonia's non-public parts are used in one place only: on X11 the drop of files from a file manager (Avalonia 11 has no XDND),
  `RemoteControl.App/Platform/X11FileDrop.cs`, which wraps a window's entry of Avalonia's X11 event table. A test
  (`AvaloniaX11EventsTests`) fails when a new Avalonia version moves those members; without them windows only stop taking drops.
- Tests: logic is tested directly; a window is tested headless (open it, type and click, read its controls, save a picture when
  `RC_TEST_SHOTS` names a folder). A test never needs a network, a screen or the user's files, and it never starts a program on the
  desktop: in a test run `ExternalPrograms.Disabled` is set, so a link or a sound is only recorded (`ExternalPrograms.Suppressed`).
  URLs are opened through `AppServices.Urls` only.

## Build and test

```bash
dotnet build RemoteControl.sln -c Release
dotnet test RemoteControl.sln -c Release --no-build
scripts/publish.sh                      # publish/linux-x64 and publish/win-x64, each a folder and an archive
```

Expected: 0 warnings, every test passes (two tests that need a real network are skipped unless `RC_LIVE_LISTSERVER`,
`RC_LIVE_ACCOUNT` and `RC_LIVE_PASSWORD_FILE` are set: only ever against a test network of your own).

## How the groups of windows fit together

- A window gets a `SessionContext` (`Ui.Shell`). It never keeps the connection: it reads `context.Session` when it sends
  something and subscribes through `context.Attach(...)`, so a reconnect (a new connection object in the same context) and closing
  the window are both handled.
- A group registers its windows by name (`ToolIds`) in `<Group>Feature.Register(ToolRegistry)`; another group opens one with
  `context.OpenTool(ToolIds.X, argument)`. What a group needs from another goes through the interfaces of `Ui.Shell/Services.cs`.
- The program (`RemoteControl.App`) implements `IAppShell`: sessions, login and server list, reconnect, sign-out, the tray, the
  hotkey, attention, and the MCP host.

## The MCP server

- `Extensions/Mcp/` holds the protocol (`McpRequestProcessor`), the tool list (`McpToolCatalog.cs`: name, permission group, schema) and
  the server tools (`McpServerTools.*.cs`). A server tool gets an `McpToolContext` and runs on the host's context, like a window: it
  reads the session there, sends with the session's methods, and waits for the answering event with `RequestAsync` (subscribe, send,
  wait with a time limit). It returns JSON text or throws `McpToolException` (a tool error the client reads) or `McpDeniedException`.
- A tool that changes something asks `ApproveAsync` (or `ApproveScriptAsync`) first, and keeps the old version with the
  `McpBackupStore` before it overwrites or deletes. Listings and transfers go through the session's `McpFileBrowser`, never the windows'.
- Every question goes through `ApproveAsync`/`ApproveScriptAsync`, which `mcpbypass` skips; a tool that reads again what it changes
  after the question checks `AsksApproval`, not `mcpapprove`. A server answer a tool waits for, which a window would show by itself,
  is claimed first (`Claim`), so that a tool opens no window.
- The program also runs headless (`--headless`, Avalonia's screenless platform): every window exists but no one sees it. Dialogs then
  only log and return their cancel answer (`Dialogs.Unattended`), approvals are denied (`UnattendedApprovals`), and no tray, hotkey or
  external program is started. Code an MCP tool reaches must never wait for a click.
- A new tool is one `Tool(...)` entry in `McpToolCatalog.cs` and a method; tests drive it through the bridge against `FakeRcSession`
  (`McpServerToolsTests`). [docs/MCP.md](docs/MCP.md) lists every tool.
