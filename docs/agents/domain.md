# Domain docs

Single-context repository.

- `GLOSSARY.md` at the root, if it exists, defines the vocabulary; use its terms.
- Architectural decisions (ADR) are NOT kept in `docs/adr/`: they live in the owner's vault
  (`%USERPROFILE%\Documents\ClaudeVault\decisioni\`, outside the repository). When a skill would write an ADR, write it there
  (one file per decision: date, context, choice, rejected alternatives) and do not create `docs/adr/`.
  If that vault does not exist (e.g. a fresh clone), skip the ADR step and do not create `docs/adr/`.
- If these files do not exist, proceed silently.
