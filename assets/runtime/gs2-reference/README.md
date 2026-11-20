# GraalScript2 Documentation

This folder contains documentation for the GraalScript2 language and generic Graal engine APIs.

Use scripts in `npcs/`, `classes/`, and `weapons/` only as reference for how GraalScript is written in practice.

Use docs/basics/ghidra for raw lookups of syntax and a deeper understanding of the API.

## Sections

- [Ghidra Decomp](ghidra/) - Pseudo code dump from most of the internal script objects, functions, etc.
- [Basics](basics/) - Core syntax, variables, functions, logic, strings, math, input/output, timers, and client/server basics.
- [Reference Documents](docs/) - API references, events, GUI controls, GUI profiles, particle engine, advanced features, and the machine-readable definitions database.
- [Realworld Examples](refs/) - Actual server dumps with examples of scripts to reference.

## Index

- [Start here](#start-here)
- [Basics](#basics)
- [Reference documents](#reference-documents)
- [Function and event lists](#function-and-event-lists)

## Start here

| Document | What it is |
| --- | --- |
| [CORE.md](CORE.md) | The core reference. Read it before anything else. |
| [basics/](basics/) | Short guides to the language, one topic per file. |
| [docs/GraalScript_Reference.md](docs/GraalScript_Reference.md) | The comprehensive guide to GS1 and GS2, with an index and many examples. |

## Basics

| Document | What it covers |
| --- | --- |
| [comments.md](basics/comments.md) | Comments |
| [variables.md](basics/variables.md) | Variables: numbers, text, objects and lists |
| [functions.md](basics/functions.md) | Functions |
| [logic.md](basics/logic.md) | Logic and decisions |
| [math.md](basics/math.md) | Math |
| [strings.md](basics/strings.md) | Strings |
| [timeouts.md](basics/timeouts.md) | Timeouts and looping |
| [io.md](basics/io.md) | Keyboard and mouse input |
| [server-client.md](basics/server-client.md) | Server-side and client-side code |
| [remote-control.md](basics/remote-control.md) | The Remote Control (RC) |

## Reference documents

| Document | What it covers |
| --- | --- |
| [GraalScript_Reference.md](docs/GraalScript_Reference.md) | Comprehensive guide to GS1 and GS2: syntax, built-in functions and variables, object properties, GUI controls, examples |
| [functions-reference.md](docs/functions-reference.md) | Reference of GraalScript2 functions and variables |
| [events-reference.md](docs/events-reference.md) | Events the engine calls when conditions or actions occur |
| [advanced-features.md](docs/advanced-features.md) | Advanced GS2 features and constraints |
| [gui-controls.md](docs/gui-controls.md) | The GUI controls; every control inherits from GuiControl |
| [gui-profiles.md](docs/gui-profiles.md) | GUI profiles, which define how controls look |
| [particle-engine.md](docs/particle-engine.md) | The particle engine, an extension of `showimg` |
| [docu_emitters-1.03.md](docs/docu_emitters-1.03.md) | Emitters on Graal v4+ |
| [GS2_Commands.md](docs/GS2_Commands.md) | GS2 commands |
| [TClient_Documentation.md](docs/TClient_Documentation.md) | The `TClient_*` functions, the `TClient` instance, the data handlers, the login sequences and the StartScript weapons |
| [Adventure_Functions.md](docs/Adventure_Functions.md) | The `Adventure_*` functions |

## Function and event lists

Plain lists for lookups.

| File | What it lists |
| --- | --- |
| [scriptfunctions_client.md](docs/scriptfunctions_client.md) | Client script functions and events by object |
| [scriptfunctions.txt](docs/scriptfunctions.txt) | Script functions by object |
| [scripthelp_rc.txt](docs/scripthelp_rc.txt) | Script help entries for scripts in the Remote Control, including the `$pref::` variables |
| [graal_wiki_dump.xml](docs/graal_wiki_dump.xml) | Export of the Graal Bible wiki |
