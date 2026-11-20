# Where this reference comes from

This folder is a copy of the documentation of [GScript.GS2Taco](https://github.com/MorenoLand/GScript.GS2Taco) (commit `b211b601`):
`README.md`, `CORE.md`, `basics/` and `docs/`. The MCP tools `rc_gs2_docs` and `rc_gs2_search` and the MCP resources
`gs2ref://` read it, so an agent working on GS2 scripts has the language reference at hand.

`docs/GraalScript_Reference.md` is verified by people and comes first in every search. The other files are generated or collected
references: trust `GraalScript_Reference.md` where they disagree.

Not copied: `ghidra/` (decompiled engine code) and `refs/` (script dumps of other servers). To let the MCP tools search them on your
own computer, put the folder of your GS2Taco checkout into the option `mcpreferencefolders` (RC Options, MCP tab).
