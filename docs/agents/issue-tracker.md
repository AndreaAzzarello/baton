# Issue tracker: GitHub

Issues live in GitHub Issues of `AndreaAzzarello/baton` (public repository). Use the `gh` CLI.

**Every write needs the owner's explicit confirmation, each time**: `gh issue create/edit/comment/close/reopen`,
`gh pr create/edit/comment/close/merge`, `gh label *`, `gh release *`, `gh api` with a non-GET method.
`.claude/settings.json` has `ask` rules for these. Reads (`gh issue list/view`, `gh pr view`) are free.

## Conventions

- **Read an issue**: `gh issue view <number> --json number,title,body,labels,comments`.
- **List issues**: `gh issue list --state open --json number,title,labels`.
- **Publish to the tracker** (a skill says so): draft the title and body, show them, and run `gh issue create` only after approval.
- Labels: do not create labels on GitHub unless asked; triage labels are not set up.
- PRs as a request surface: no.
