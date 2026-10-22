# Security policy

## Supported versions

Only the latest release receives security fixes. Updates are signed (see [Verify a release](README.md#verify-a-release)): install
them from the app (Impostazioni › Aggiornamenti, Settings › Updates) or from the GitHub Releases page of this repository.

## Reporting a vulnerability

Please **do not open a public issue** for a vulnerability. Use GitHub's private reporting instead:
**Security › Report a vulnerability** on this repository
(https://github.com/AndreaAzzarello/baton/security/advisories/new).

Include, if you can: the version (file properties of `Baton.exe` › Details), Windows version and
browser, steps to reproduce, and what an attacker gains. Never include real passwords, cookies or personal files: test
data is enough.

You should get a first answer within 14 days. Once a fix is released, the advisory is published with credit to the
reporter, unless they prefer otherwise. Reports in Italian are welcome too.

## Scope

In scope: the app, the native messaging host, the browser extension, the update and signing mechanism and the scripts
in `tools/`. Out of scope: vulnerabilities of third-party sites, of ffmpeg or of an antivirus the user configures, and
attacks that already need administrator rights or a malicious extension the user loaded in developer mode (see [Known
issues](README.md#known-issues)).

## Privacy

What Baton stores, where, and which servers it talks to: [PRIVACY.md](PRIVACY.md) (no telemetry).

## Known open leads

The README lists, under [Known issues](README.md#known-issues), the limits and the unconfirmed leads of the latest internal security audit.
They are public on purpose; reports that confirm or rule one out are welcome through the channel above.
