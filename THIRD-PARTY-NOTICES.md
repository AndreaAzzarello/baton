# Third-party notices

DownloadManager is released under the MIT License (see `LICENSE`). The published program
(`DownloadManager.exe`, a self-contained .NET single file) includes the components below, each under its own license.
The licenses were checked against the package metadata of the exact versions used.

## Included in DownloadManager.exe

| Component | Version | License | Project |
|---|---|---|---|
| .NET runtime and WPF (Microsoft) | 10.0 | MIT | https://github.com/dotnet/runtime, https://github.com/dotnet/wpf |
| AngleSharp | 1.8.3 | MIT | https://github.com/AngleSharp/AngleSharp |
| FluentFTP | 55.0.0 | MIT | https://github.com/robinrodricks/FluentFTP |
| Jint | 4.17.0 | BSD-2-Clause | https://github.com/sebastienros/jint |
| Acornima (used by Jint) | 1.7.0 | BSD-3-Clause | https://github.com/adams85/acornima |
| Microsoft.Data.Sqlite, Microsoft.Data.Sqlite.Core | 10.0.13 | MIT | https://github.com/dotnet/efcore |
| System.Security.Cryptography.ProtectedData | 10.0.13 | MIT | https://github.com/dotnet/runtime |
| SQLitePCLRaw (bundle_e_sqlite3, core, provider, lib) | 2.1.13 | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| SQLite (native e_sqlite3, inside SQLitePCLRaw) | — | Public domain | https://sqlite.org/copyright.html |

The full license texts are published with each package (https://www.nuget.org) and at
https://opensource.org/licenses (MIT, BSD-2-Clause, BSD-3-Clause, Apache-2.0).
The Apache-2.0 components ship no NOTICE file that would have to be reproduced.

## Used only to build and test (not distributed)

| Component | Version | License |
|---|---|---|
| xunit, xunit.runner.visualstudio | 2.9.3, 3.1.4 | Apache-2.0 |
| Microsoft.NET.Test.Sdk (with Microsoft.CodeCoverage, TestPlatform) | 17.14.1 | MIT |
| coverlet.collector | 6.0.4 | MIT |
| Newtonsoft.Json (pulled in by the test SDK) | 13.0.3 | MIT |
| puppeteer-core, @puppeteer/browsers (`tools/trials`, real-browser trials) | 24.43.1, 2.13.2 | Apache-2.0 |
| web-ext (`tools/sign-firefox.ps1`, run through npx, never committed) | 10.7.0 | MPL-2.0 |

## Browser extension

The extension (`extension/`) contains no third-party code or libraries: it only uses the browser's extension APIs.
Its integration modes take the *idea* from "Download with…" (webextension.org, MPL-2.0); no part of that code has been
copied or adapted.

## Optional external program: ffmpeg

ffmpeg is **not** included and never downloaded by DownloadManager. If the user installs it and points the app at it
(Settings, or a copy in `PATH`), the app runs it as a separate process only to join already downloaded video and audio
tracks. ffmpeg is licensed by its authors (LGPL-2.1 or later, GPL for some builds): see https://ffmpeg.org/legal.html.
