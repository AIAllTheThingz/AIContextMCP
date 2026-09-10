# Architecture

The local server uses separate source, build, runtime, data/artifact, and log roots under one trusted Windows account. A generic layout is `<mcp-root>/{AIContextMCP,build/AIContextMCP,runtime/AIContextMCP,data/AIContextMCP,logs/AIContextMCP}`. Published output belongs in the runtime root; public documentation uses generic equivalents.

Core provides typed application contracts, bounded verified repository handles, and shared freshness state without invoking a Git process. SQLite V4 provides parameterized persistence, explicit migrations, metadata bounds, and atomic durable mutation receipts.

Server uses SDK 2.2 stdio, initializes storage before accepting requests, exposes exactly eight tools, applies strict schemas and typed error envelopes, and writes structured diagnostics to stderr. Runtime tokens are process-local, expire after 15 minutes, and are invalid after restart; receipts are retained for 30 days.

Current bounded controls include 512 index entries, 512 worktree files, and 512 worktree directories; 1 MiB index, loose-object, and worktree-file reads; 8 MiB aggregate worktree reads; a version-2 SHA-1 Git index and verified loose objects that produce a SHA-256 working-tree fingerprint; a root `.gitattributes` file is supported only when it is UTF-8, LF-only, at most 32 KiB, and limited to `*`/`*.<extension>` rules using `text` or `text=auto` with optional `eol=lf`; nested, info, and global attributes are unsupported, and every observed worktree file must be CR-free; `Unknown` for packed or unsupported objects; rejection of linked metadata; and a 32 KiB artifact file limit. Requests are limited to 64 KiB, responses to 32 KiB, and bootstrap responses to 16 KiB.

Global registration is implemented for the configured client and the controlled Chunk 8 pilot is complete. External build, publish, and two fresh CLI launches from the runtime preserved database records and all eight tools. Desktop GUI restart validation is not claimed. Automated backup and organization-wide onboarding remain future work.
