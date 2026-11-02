# Claude Code tooling (maintainer notes)

## One tool per task
| Task | Tool |
|---|---|
| Bugs in the diff, before a commit | `/code-review` (official) |
| Simplicity / superfluous code, at the end of a version | `/ponytail-review` (whole repo: `/ponytail-audit`) |
| Security (protocol, DPAPI, grabber, end of phase) | `security-audit` |
| Stress-test the plan of a new feature | `/grill-me` |
| Hard bug | `diagnosing-bugs` |
| Test-first | `tdd` |
| Close a phase | project skill `fine-fase` |
| Context above ~60% | `/handoff` (saved in the ClaudeVault, see CLAUDE.md) |

Pocock's `code-review`, `pr`, `wizard`, `prototype`, `research`, `codebase-design`, `domain-modeling`, `grilling` and
`writing-for-agents` are denied for this project (`permissions.deny` in `.claude/settings.json`; plugin skills cannot be
switched off with `skillOverrides`). A deny blocks the call but the description may stay in context.

## Memory
ClaudeVault (`lezioni/`, `decisioni/`, `prompt/`) plus the CLAUDE.md files. No claude-mem (it would duplicate the vault).

## Graphify (local, code only)
- Installed with `uv tool install graphifyy` (official uv: `winget install astral-sh.uv`). Never Graphify Cloud.
- `GRAPHIFY_BACKEND=none`, `GRAPHIFY_QUERY_LOG_DISABLE=1`, `GRAPHIFY_NO_CTA=1` are set in `.claude/settings.json`; in a plain
  PowerShell set them yourself and always pass `--code-only`.
- Build: `graphify extract . --code-only`; incremental: `graphify update .`; forced: add `--force`.
- Query by symbol name: `graphify affected "MessageValidator" --depth 1`, `graphify explain "MessageValidator"`,
  `graphify path "A" "B"`. Query the graph by symbol name only (`explain` / `affected` / `path`); for free-text
  questions use grep (`graphify query` is noisy on this codebase).
- The always-on PreToolUse hooks (`graphify install` adds them) are NOT used: `graphify install --project` rewrites
  `.claude/settings.json` and CLAUDE.md; after running it, undo those edits.
- Not installed: csharp-lsp (the Stop hook already builds; revisit if reference searches prove imprecise).
