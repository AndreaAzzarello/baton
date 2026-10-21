## What and why

<!-- What does this change, and which issue does it address? (Fixes #…) -->

## How it was tested

<!-- Tests added or updated; manual checks (with separate data: BATON_DATA). -->

## Checklist

- [ ] `dotnet build Baton.slnx` with zero warnings and `dotnet test Baton.slnx` pass
- [ ] Texts changed through `tools/make_resx.py` / `tools/make_locales.py`, not in the generated files
- [ ] New controls have an accessible name and keyboard navigation
- [ ] Database schema changes have a `MigrateToN` step with backup (or: no schema change)
- [ ] No secrets, personal data, or code, names and graphics of other products
- [ ] Documentation updated if behaviour changed (README / README.it.md / CHANGELOG)
