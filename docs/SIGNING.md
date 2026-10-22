# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

*Until SignPath Foundation approves the project, releases carry only the release-key signature described below, and
Windows SmartScreen may warn on first run.*

## What is signed

| File | Signature | By |
| --- | --- | --- |
| `Baton.exe` | Authenticode | SignPath Foundation, through SignPath.io |
| `Baton-Setup-<version>.exe` (installer) | Authenticode | SignPath Foundation, through SignPath.io |
| `Baton.exe`, `Baton-Setup-<version>.exe`, `baton-extension-<version>.zip` | release key (ECDSA P-256), files `.sha256` and `.sig` | the author, on the author's own PC |

- Only binaries built from this repository, [github.com/AndreaAzzarello/baton](https://github.com/AndreaAzzarello/baton),
  are signed: Baton's own code, built by [`.github/workflows/release.yml`](../.github/workflows/release.yml) on GitHub-hosted
  runners from a version tag (`v*`) on the `main` branch. Nothing built elsewhere and no third-party program is signed.
- The installer is made with [Inno Setup](https://jrsoftware.org/isinfo.php) (its compiler is downloaded by the workflow
  and checked against a fixed SHA-256); it contains the signed `Baton.exe` and Inno Setup's uninstaller.
- The portable zip contains the same signed `Baton.exe`. The browser extension packages are not Windows binaries and are
  not part of this signing (the Firefox package is signed by Mozilla's add-on service).
- The product name in the files' metadata is always *Baton*, and the product version is the version of the tag.

## Team and roles

| Role | Members |
| --- | --- |
| Committers and reviewers | [Andrea Azzarello](https://github.com/AndreaAzzarello) |
| Approvers | [Andrea Azzarello](https://github.com/AndreaAzzarello) |

- Committers may change the source without further review; changes from anyone else arrive as pull requests and are
  reviewed by a reviewer before they are merged.
- Every signing request is approved by hand by an approver in SignPath, after checking that it comes from the release
  workflow of a version tag.
- Every team member uses multi-factor authentication for GitHub and for SignPath.

## Privacy

Baton does not transfer any information to other networked systems unless specifically requested by the user or the
person installing or operating it. It connects to the servers of the downloads, pages and sites the user asks for, and,
only when the user turns on "Check for updates at startup" (off by default), to the update address shown in the settings
(the project's GitHub releases by default). The browser extension talks only to the Baton app on the same computer.

## How a release is made

1. **CI builds.** A tag `v<version>` starts the release workflow: build with zero warnings, all tests, then `Baton.exe`
   (reproducible: SDK pinned in `global.json`, LF line endings, `ContinuousIntegrationBuild`: the same commit gives the
   same `Baton.exe` byte for byte on a PC and on GitHub Actions; the installer and the zip differ between machines).
2. **SignPath signs** `Baton.exe` with Authenticode (artifact configuration `baton-exe`); the installer and the portable
   zip are built from the signed program, then the installer is signed too (`baton-setup`). Each request waits for the
   approver. The workflow uploads everything as the artifact `baton-<version>-release`, with `SHA256SUMS.txt`.
3. **The author downloads** that artifact.
4. **Checks the Authenticode signatures** (`Get-AuthenticodeSignature`, status *Valid*, signer SignPath Foundation), then
   signs the **already signed** files with the release key: `tools\sign-release.ps1 -Exe <file> -Version <version>` for
   `Baton.exe`, for the installer and for the extension's package `baton-extension-<version>.zip` (from 1.3.3 the app's
   updater installs it next to `Baton.exe` only with a valid signature). The order matters: Authenticode changes the file,
   and the release key signs its final SHA-256 (the one the app's updater checks).
5. **Publishes** the GitHub release with those files.

The private release key never leaves the author's PC and never reaches CI (it is encrypted with Windows DPAPI in
`%LOCALAPPDATA%`); the public keys are built into the app (`src/Baton.Core/Updates/release-keys.txt`).

Until SignPath Foundation approves the project, step 2 is skipped (no SignPath variables): the workflow produces the
same files without Authenticode, and steps 3–5 are the same.

## Verifying a download

```powershell
.\tools\verify-release.ps1 -Exe "$HOME\Downloads\Baton-Setup-1.3.3.exe"   # .sha256 and .sig next to it
```

It checks the SHA-256, the release-key signature (version + file name + hash) and, when the file carries one, the
Authenticode signature (`-RequireAuthenticode` to insist on it). Windows shows the Authenticode signer in the file's
Properties › Digital signatures.

## Setting up SignPath (maintainer notes)

- SignPath project `baton` with the GitHub trusted build system, origin verification on, signing policy
  `release-signing` with manual approval.
- Artifact configurations `baton-exe` and `baton-setup`: copies in [`.signpath/artifact-configurations`](../.signpath/artifact-configurations).
- In the GitHub repository: secret `SIGNPATH_API_TOKEN` (CI user's token), variables `SIGNPATH_ORGANIZATION_ID`,
  `SIGNPATH_PROJECT_SLUG` (`baton`), `SIGNPATH_SIGNING_POLICY_SLUG` (`release-signing`). With
  `SIGNPATH_ORGANIZATION_ID` empty the workflow builds without signing.
- `main` protected against force pushes and deletion; tags `v*` created only by the maintainer.
