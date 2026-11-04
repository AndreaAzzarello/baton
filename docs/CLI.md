# batonctl — the command line of Baton

`batonctl.exe` adds, lists and controls downloads of the running Baton from a terminal or a script. It ships with Baton
(next to `Baton.exe`): the installer offers to add it to your `PATH`, the portable zip and Scoop carry it, and the app's
updates keep it at the program's version.

```powershell
batonctl add https://example.com/file.zip --category Software
batonctl list --status downloading
batonctl pause all
```

It talks to the app through the same local pipe as the browser extension, restricted to your Windows user. Nothing is
sent over the network. If Baton is closed, `batonctl` starts it in the notification area and waits (up to 30 seconds)
for it to answer; `batonctl version` never starts it.

## Commands

| Command | What it does |
| --- | --- |
| `add <url…\|->` | Adds downloads. `-` reads one address per line from the input. |
| `list [--status <s>]` | Lists downloads. Status: `queued`, `downloading`, `paused`, `completed`, `failed`. |
| `status <id>` | Shows one download. |
| `pause <id\|all>` | Pauses a download, or all. |
| `resume <id\|all>` | Starts a download again, or all paused and failed ones (through the queues, in order). |
| `retry <id\|all>` | Tries a failed download again, or all failed ones. |
| `queues start\|stop [name]` | Starts or stops a queue (the main queue without a name). |
| `open <id>` | Opens the finished file with its default program. |
| `remove <id>` | Removes a download **from the list**; the file stays on the disk. |
| `remove --all-completed` | Removes all completed downloads from the list. |
| `version` | Versions of `batonctl` and of the running Baton. |

Options of `add`:

| Option | Meaning |
| --- | --- |
| `--folder <path>` | Destination folder. A relative path is taken from the current folder. |
| `--category <name>` | Category that decides the folder. |
| `--queue <name>` | Queue to put the download in. |
| `--no-start` | Adds it paused (`--start`, the default, starts it). |
| `--allow-duplicate` | Adds it even if it is a duplicate (with a numbered name). |

`--json` (any command) prints one JSON object with the same fields every time; `--help` prints the usage.

## Duplicates

`add` never asks questions. An address that is already in the list, a file with the same name in the destination folder,
or (after the server's answer) the same size and ETag as a finished or removed download is a **duplicate**: it is
skipped and reported, and the exit code is 4. The setting «If a download is a duplicate» (Settings › General › Duplicates) can never make
a command line add overwrite a file: «Ask me each time» and «Overwrite» skip, «Add anyway» adds with a numbered name. `--allow-duplicate`
forces «Add anyway» for that command.

The terminal says why, in English or, when Windows is in Italian, in Italian:

| Reason (`--json`) | English | Italian |
| --- | --- | --- |
| `sameUrl` | `skipped (duplicate: same address already in the list, #7)` | `saltato (duplicato: stesso indirizzo già in elenco, #7)` |
| `sameName` | `skipped (duplicate: a file with the same name is already in the folder)` | `saltato (duplicato: nella cartella c'è già un file con lo stesso nome)` |
| `sameFile` | `skipped (duplicate: same file from another address (same size and version))` | `saltato (duplicato: stesso file da un altro indirizzo (stessa dimensione e versione))` |

`#7` is the existing download, when it is in the list. With `--json` the words are never translated: `reason` is one of the three
codes above and stays the same across versions (a script should compare the code, not the text).

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Done. |
| 1 | The app refused or failed (unknown download, no such category…). |
| 2 | Wrong arguments. |
| 3 | Baton could not be reached or started. |
| 4 | At least one duplicate was skipped (and nothing failed). |

## JSON

Errors: `{"ok":false,"error":"…"}` (on the standard output with `--json`). Other answers:

```jsonc
// add
{"ok":true,"results":[{"url":"https://…","status":"added","id":12,"duplicate":null,"error":null}]}
// status: "added" | "duplicate" | "error"; for a duplicate, "id" is the existing download (if it is in the list) and
// "duplicate" is {"reason":"sameUrl"|"sameName"|"sameFile","existingId":7,"existingPath":"C:\\…\\file.zip"}

// list / status / pause <id> / resume <id>
{"ok":true,"downloads":[{"id":12,"name":"file.zip","url":"https://…","folder":"…","path":null,"status":"downloading",
  "downloaded":1048576,"totalSize":5242880,"error":null,"queue":"Main","category":"Archives",
  "addedUtc":"…","completedUtc":null}]}

// version
{"cli":"1.4.0","app":"1.4.0","running":true}
```

Downloads in the answers never carry cookies, passwords, tokens, form fields or the query string of the address.

## Limits that always apply

The command line can do what the app's window does with downloads and queues, and no more: it never deletes files from
the disk (`remove` only changes the list) and it cannot change settings, credentials, proxy, rules, folders of the
categories or updates. A folder can be chosen only by the command line (the browser extension, and later the local API and
MCP, go through categories).

## Examples

```powershell
# A list of links from a file, paused, into a folder
Get-Content links.txt | batonctl add - --folder D:\Downloads\Course --no-start
batonctl resume all

# In a script: wait for the exit code and read the JSON
$r = batonctl add https://example.com/a.iso --json | ConvertFrom-Json
if ($LASTEXITCODE -eq 4) { "already downloaded: #$($r.results[0].id)" }
```
