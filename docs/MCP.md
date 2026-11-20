# The MCP server

`RemoteControl --mcp` is an [MCP](https://modelcontextprotocol.io) server on standard input and output. An MCP client (Claude Code,
Claude Desktop, any other) starts it; it finds the running Remote Control and forwards every request to it. Through it an agent does
what a user does in the program's windows, on any server the program is signed in to: read and write weapons, classes and NPCs, edit
files raw, look after players, accounts, rights and bans, change the server's settings, run RC commands, and copy scripts and files
from one server to another. It also gets what an agent needs around that: the session's events (script compiler output, server
answers), a backup of everything it replaces, local folders to edit in with its own tools, a script checker, the script API of
[api.gscript.dev](https://api.gscript.dev/), and the GS2 language reference.

It is off until you switch it on, and every change can be made to wait for your approval.

## Switching it on

1. Start Remote Control and sign in to a server.
2. Options → MCP: tick **Enable local MCP integration** and the permissions you want (below).
3. Add the server to your MCP client. With Claude Code:

   ```bash
   claude mcp add remotecontrol -- /path/to/RemoteControl --mcp
   ```

   With several programs running, `--mcp-instance <process id>` picks one; otherwise the one with open script editors, the focused
   one, or a connected one is taken.

Nothing listens on the network: the `--mcp` process talks to the program over a socket (a named pipe on Windows) only your user can
open.

## Permissions and safety

| Option | Tools |
| --- | --- |
| **Allow read-only tools** (`mcpread`) | everything that only reads: sessions and events, players and accounts, scripts, files, server settings, the reference, the script API, backups |
| **Allow file and script writes** (`mcpwrite`) | writing, deleting, moving, copying and importing scripts and files, local export, restoring backups, the open editors |
| **Allow server and game mutations** (`mcpserver`) | RC and NC commands, messages, IRC, warping and disconnecting players, NPCs, reloading levels |
| **Allow login and connection control** (`mcplogin`) | the login form, connecting a server, the NC connection |
| **Allow window and UI control** (`mcpwindows`) | opening windows and script editors, selecting the session |
| **Allow administrative actions** (`mcpadmin`) | accounts, rights, attributes, comments, profiles, bans, server options, flags and folder configuration, raw packets |
| **Full native RC control** (`mcpfullcontrol`) | all of the above |

- **Require approval for mutations** (`mcpapprove`, on by default) asks you before every change, with what it will do: a command,
  message or text is shown whole, every line of it. Scripts have "Always allow Weapon/Class/NPC scripts"; deleting a script is asked
  every time. A ban on every server of the network (`world: all`) is asked every time, even with approvals off.
- **Allowed server scope** (`mcpserverscope`) limits every tool to one server.
- **The audit log** (`mcpaudit`) records every request with its outcome (Options → MCP → View audit log).
- **Backups.** Before a tool overwrites or deletes anything on a server (a script, a file, a server text, NPC flags, a player's
  texts) it keeps the old version in `mcp-backups/` of your user folder. When the old version cannot be read or the backup cannot be
  written, nothing is changed, unless the call gives `force: true` (a file over 64 MiB is deleted or replaced only with it). A forced
  change is always asked when approvals are on, even for a script type you always allow, and the question says when what it replaces
  could not be read; a weapon whose script could not be read keeps the image of the weapon list, or needs `image`. Results say
  `replaced` (not `created`) for a file that was there but could not be read. With approvals on, what a change replaces is looked at
  again after you answer (a file, a script, the files a move would replace, NPC flags): when it changed meanwhile, nothing is done.
  `rc_backups` lists and shows them, `rc_restore_backup` puts one back (and keeps what it replaces, so a restore can be undone too). A
  restore needs the permission of the change it makes: player texts and server settings `mcpadmin`, NPC flags `mcpserver`. The server
  scope covers the backups too.
- **Local folders** (`mcpfileroots`, default `.`): the only folders `rc_export` and `rc_import` read and write. A relative entry is
  inside the MCP workspace, `mcp-files/` of your user folder; add your own folders separated by `;`. Paths that leave a root, through
  `..` or a link, are refused, for the folder given and for every file written or read inside it: a path is followed link by link as
  the system follows it (links inside a link's target included) and must really end inside the root; links inside an export folder are
  not followed when it is scanned. A root inside another root counts only while it really lies inside that one (a link there is judged
  by the outer root). A file is read through one open handle whose real path is checked again (Linux and Windows), but a write is
  checked just before it is made, so a link made in that instant is not caught, and a hard link to a file outside the roots is a file
  of the folder like any other: give the agent's own file tools no more than the roots it may use. Local file names are compared as
  the system compares them (with case on Linux). In an export folder an NPC file is `<id>_<name>.gs2`; one without the id is reported
  as skipped, and a renamed one keeps the server its NPC came from.
- **Requests** whose JSON holds text that is not valid UTF-16 (an escaped lone surrogate, in a value or a key) are refused with a parse
  error.

## How the tools work

- **Sessions.** Every server tool takes `server`: the name of a connected server (`rc_list_connections`). Without it the selected
  session is used. Two servers can be used in one call: `rc_copy` takes `from` and `server`.
- **Answers.** The RC protocol answers later, in another packet. The tools send the request and wait for the answer
  (`timeoutSeconds`, 20 by default). After a change they gather what the server says for a moment (`collectMilliseconds`) and return it
  as `events`: the NC's compiler output after a script write, the server's answer after an upload.
- **Events.** `rc_read_events` reads the session's recent events (RC and NC chat, private, admin and to-all messages, IRC, server texts,
  file-browser messages, refusals, connects and disconnects, players coming and going) after a number (`since`), and can wait for new
  ones (`waitSeconds`). Pass the returned `next` back as `since`.
- **Editor texts.** Account, rights, attributes, comments and profile are read (`rc_get_player_info`) and written
  (`rc_write_player_info`) in the same text the RC's windows show: read it, change it, send it back.
- **Guarded edits.** `rc_write_script`, `rc_write_file`, `rc_write_server_text` and NPC flags take `edits`: `expected`/`replacement` pairs
  applied to the server's current text. Each expected text must occur exactly once (or every occurrence with `all: true`), otherwise
  nothing is written. Settings and flags also take `set` (keys and values) and `remove` (keys).
- **Raw files.** `rc_read_file` and `rc_write_file` move bytes. Text is read as UTF-8 when it is valid UTF-8, otherwise as Windows-1252
  (the servers' encoding), and written back in the encoding the file had; binary files go as Base64.
- **The MCP's own file-browser view.** Listings and transfers of the tools never move the file browser window, the sync or a backup.
- **Quiet answers.** The windows of the program open a dialog for every account, rights, ban or level-NPC answer the server sends; the
  answers a tool asked for are claimed first, so reading a hundred players opens no window and leaves an Edit Access window you are
  working in as it is.
- **Fresh lists.** The NC tells the program its weapons when it connects, not when one is added or deleted: before a weapon counts as
  new (or after one was deleted) a tool asks for the weapon list again.
- **Moves.** `rc_move_file` moves first and renames in the new folder; it refuses to replace a file of either name it takes (names
  compared exactly, so `Start.nw` and `start.nw` are two files) unless `overwrite: true` is given, and then keeps it first.
- **Changes made meanwhile.** With approvals on, a write reads the server's version again once you have answered; when another staff
  member changed it while the question was open, nothing is sent and the tool says so.
- **NPCs.** Deleting a database NPC keeps its script, flags and attributes (level, position, type: what `rc_npc_action create` needs to
  make it again). `rc_import` and `rc_copy` find an NPC of another server by its name, never by its id (ids are per server), and on the
  NPC's own server refuse an id that now belongs to an NPC of another name.
- **Command output.** `rc_server_commands`, `serverSide` searches and `fromServer` script help wait for the server's first line (up to
  `collectMilliseconds`), then until it has been quiet for a moment, and say `complete: false` with the reason (`cut`) when nothing
  came in time, when they stopped while it still wrote (a minute after its first line), or when lines were lost or left out; a pause
  in the middle of an answer longer than that moment also ends it. What came later is in `rc_events`.
- **Searches** with a regular expression stop with a message when the expression backtracks for too long on a line; searching runs
  beside the user interface, which stays responsive.

## The tools

The program tools of the C++ version (`rc_status`, `rc_list_connections`, the login, the windows, the open script editors,
`rc_send_chat`) are still there under their old names. The server tools:

### Reading and diagnosing (`mcpread`)

| Tool | What it does |
| --- | --- |
| `rc_session_info` | account, RC and NC state, upload limit, counts, the network's other servers, the last event number |
| `rc_read_events` | the session's events after a number, optionally waiting for new ones |
| `rc_server_commands` | the RC commands the server knows (its `/help`) |
| `rc_list_players` | the player list; `pmServer` first fetches another server's players |
| `rc_get_player_info` | player entry, account, rights, attributes, comments, profile, ban, ban history, staff activity |
| `rc_list_accounts`, `rc_list_ban_types` | the accounts list, the ban types |
| `rc_read_server_text` | server options, flags or folder configuration |
| `rc_list_scripts`, `rc_read_script` | weapons, classes, NPCs; a script whole, in lines, or the lines matching a query |
| `rc_search_scripts` | every script searched for a text or regular expression; `serverSide` uses `/scriptscan` (level scripts too) |
| `rc_read_npc`, `rc_list_levels`, `rc_read_local_npcs` | NPC attributes, flags and script; the level list; a level's NPCs |
| `rc_list_files`, `rc_read_file`, `rc_search_files` | the folders and their files; a file raw; a folder's text files searched |
| `rc_find_missing_files` | the files the server's scripts, levels and animations name but it lacks |
| `rc_capture_packets` | the packets the server sends for some seconds |
| `rc_check_script` | a script checked as the editor does, with the script API: syntax, unknown functions, argument counts |
| `rc_script_help` | a function, variable or event of the script API, or names matching a query; `fromServer` uses `/scripthelp` |
| `rc_gs2_docs`, `rc_gs2_search` | the GS2 reference: its documents, a section or outline, a search across all of it |
| `rc_backups` | what MCP replaced, and its content |

### Writing scripts and files (`mcpwrite`)

| Tool | What it does |
| --- | --- |
| `rc_write_script` | a weapon, class or NPC script, whole or by guarded edits (adds a weapon or class that does not exist) |
| `rc_delete_script` | deletes a weapon, class or database NPC |
| `rc_write_file`, `rc_delete_file`, `rc_move_file` | a server file written raw (text, Base64 or edits), deleted, renamed or moved |
| `rc_copy` | scripts and files from one connected server to another (or the same one under new names) |
| `rc_export`, `rc_import` | scripts and files into a local folder and back, sending only what changed |
| `rc_restore_backup` | puts a backup back |

### Game actions (`mcpserver`), connections (`mcplogin`), windows (`mcpwindows`)

| Tool | What it does |
| --- | --- |
| `rc_command` | RC chat (the server's commands), NC chat, or the program's chat field with its own commands (always to the server, never to a selected IRC tab) |
| `rc_send_message` | private message, mass message, admin message (to some or all), to-all message |
| `rc_player_action` | warp or disconnect a player |
| `rc_npc_action` | create, reset or warp a database NPC, or change its flags (a script given with create also needs `mcpwrite`) |
| `rc_update_levels` | make the server reload levels |
| `rc_irc` | the network chat: log in, join, leave, send |
| `rc_nc_connect` | connect or disconnect the NC |
| `rc_open_script_editor` | open the program's editor of a script, so you see it |

### Administration (`mcpadmin`)

| Tool | What it does |
| --- | --- |
| `rc_write_player_info` | account (or a new one), rights, attributes (or a reset), comments, profile |
| `rc_set_ban` | ban or unban an account or computer, here or on every server, by type, length or without end |
| `rc_write_server_text` | server options, flags or folder configuration: whole, by edits, or by keys |
| `rc_send_raw_packet` | any RC or NC packet |

## The GS2 reference and the script API

Beside the program, `gs2-reference/` holds the documentation of [GScript.GS2Taco](https://github.com/MorenoLand/GScript.GS2Taco):
`docs/GraalScript_Reference.md`, which people verified and which comes first in every search, `CORE.md`, the basics, and the
reference documents (functions, events, GUI controls and profiles, particles, the RC script help). `rc_gs2_docs` lists and reads
them (a long document gives its outline first; read a section by its heading), `rc_gs2_search` searches all of them, and MCP clients
that attach documents find them as resources (`gs2ref://reference/...`).

The decompiled engine code (`ghidra/`) and the server dumps (`refs/`) of GS2Taco are not shipped. To let the tools search them on your
computer, put the folder of your GS2Taco checkout into **More GS2 reference folders** (`mcpreferencefolders`, separated by `;`; `~` is
your home folder, a relative folder is inside the MCP workspace). A document such a folder has as well as the bundled reference is
read from the bundled copy, the one people verified, so it is never listed twice.

`rc_script_help` reads the script API of the option `autocompletesource`, by default `https://api.gscript.dev/`, the definitions the
script editor completes with (same cache file, so it works offline once loaded). `rc_check_script` uses it to report unknown functions
and wrong argument counts. `fromServer: true` asks the connected NPC-Server's own `/scripthelp` instead.

## Recipes

**Find and fix a script error.** `rc_read_events` (kinds `nc`) shows the compiler's message → `rc_search_scripts` finds the lines →
`rc_read_script` with `start`/`end` → `rc_check_script` on the fix → `rc_write_script` with `edits`; its `events` show whether the NC
compiled it.

**Bring systems over from another server.** Sign in to both servers. `rc_list_scripts` with `server` set to the source → `rc_copy`
with `from` (the source), `weapons`, `classes`, `scripts` and `files` (`levels/*.nw`, `images/`) → `rc_find_missing_files` on the
destination names the images and animations the new scripts use → `rc_copy` those files too → `rc_update_levels`. Everything replaced
on the destination is in `rc_backups`.

**Large edits with your own tools.** `rc_export` (scripts, and files such as `levels/*.nw`) into the workspace → edit the `.gs2` and
other files with your editor or the agent's own file tools → `rc_import` with `dryRun` to see what would go → `rc_import`. Importing the
folder into another server sends everything: the same move works between servers.

**Administration.** `rc_get_player_info` (parts `account`, `rights`, `ban`, `banhistory`) → change the rights text →
`rc_write_player_info` → `rc_set_ban` with a `type` from `rc_list_ban_types`.
