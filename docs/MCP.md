# Baton as an MCP server

`batonctl mcp` lets AI agents (Claude Code, Claude Desktop, any client of the [Model Context Protocol](https://modelcontextprotocol.io))
list and control the downloads of the running Baton. It is part of `batonctl.exe`, which ships with Baton. Nothing is sent over
the network: the agent talks to `batonctl` on standard input and output, and `batonctl` talks to Baton through the same local pipe
as the command line, restricted to your Windows user.

It is **off by default**. Turn it on in Settings › Automation › AI agents (MCP) and choose how much an agent may do.

## Add Baton to your agent

Settings › Automation › AI agents (MCP) › **Copy the MCP configuration** copies the text below with the real path of `batonctl.exe`
(installed or portable). Paste it in the MCP servers file of your client.

```json
{
  "mcpServers": {
    "baton": {
      "command": "C:\\Users\\you\\AppData\\Local\\Programs\\Baton\\batonctl.exe",
      "args": ["mcp"]
    }
  }
}
```

- **Claude Code**: `claude mcp add baton -- "C:\Users\you\AppData\Local\Programs\Baton\batonctl.exe" mcp`, or paste the text in `.mcp.json`.
- **Claude Desktop**: paste it in `claude_desktop_config.json` (*not verified with the real Claude Desktop*, only with the SDK client and `claude -p`).

Baton is started in the notification area the first time a tool is listed or called, not when the client connects.

## Levels

The level is chosen in the settings, separately for MCP and for the local API. The app checks it on every call; the tool list
`batonctl mcp` shows is only a courtesy and follows the level as the app reports it each time the list is asked.

| Level | Tools |
| --- | --- |
| Off | none |
| Read only | `list_downloads`, `get_download`, `list_queues` |
| Read and add | the above, `add_downloads`, `probe_url` |
| Extended | the above, `pause_downloads`, `resume_downloads`, `retry_downloads`, `remove_from_list`, `start_queue`, `stop_queue` |

`probe_url` sends a request to the address it is given, so it needs "Read and add" and not "Read only".

While Baton runs **with administrator rights** MCP and the local API are off whatever the setting says: any program of the same user could
send them orders, and an elevated process must not take them. The pipe also refuses connections that arrive over the network.

## Tools

Results are structured data (with an output schema) and the same JSON as text. Keys and codes are English and stable. Names,
addresses and error texts that come from websites are cleaned (no control, bidirectional, zero-width or tag characters, a bounded
length) and are only ever the value of a field.

| Tool | Arguments | Notes |
| --- | --- | --- |
| `list_downloads` | `limit` (1–200, default 50), `cursor` | In order of number; pass `nextCursor` back as `cursor`. |
| `get_download` | `id` | |
| `list_queues` | | Names, the main queue first. |
| `add_downloads` | `urls` (1–50), `category`, `queue`, `start`, `allowDuplicate` | One result per address: `added`, `duplicate`, `failed` or `notAttempted` (a budget of 60 seconds). No folder: only categories. |
| `probe_url` | `url` | Name, type, size and whether the server can resume; nothing is downloaded. |
| `pause_downloads`, `resume_downloads`, `retry_downloads`, `remove_from_list` | `ids` (1–50) | Each download is named; there is no "all". One result per number. `remove_from_list` removes finished downloads from the list only (unfinished ones are refused: `notFinished`). |
| `start_queue`, `stop_queue` | `queue` | The main queue without a name. A queue an agent started never runs its end action (shut down, sleep…) by itself: Baton asks for a click. |

Addresses must be http, https, ftp, ftps or ftpes, and never on this computer or a private network unless "Allow addresses of the local
network" is on; the download itself is checked again at every connection and redirect. The address in a result is reduced to
scheme, host and file name. Downloads an agent asks for never use the logins you saved in Baton, and a name that does not
resolve within 5 seconds is refused (`unresolvable`): it cannot be shown to be public.

## Fixed limits

These do not depend on the level and are not settings:

- no tool opens a file or a program, deletes files from the disk, or changes settings, credentials, proxy, rules, folders or updates;
- at most 50 items per call, at most 2 calls in flight, 120 requests per minute (every call counts; the rate limit is written to
  the activity log at most once a minute);
- timeouts: 10 seconds per call, 30 seconds for `probe_url`, 60 seconds in total for `add_downloads`;
- what an agent changes through MCP is in Settings › Automation › Activity log (origin `mcp`), refused calls included. A read that
  worked (`list_downloads`, `get_download`) is not written, and refusals are written once per reason in a minute, then counted in
  one line ("×N"), so a loop cannot empty the log.

## Errors

A failed call returns `isError` with `{"error":{"code","message"}}`. Codes: `automationOff`, `levelTooLow`, `notAllowed`,
`tooManyRequests`, `noDownload`, `noQueue`, `noCategory`, `privateNetwork`, `unresolvable`, `notFinished`, `badScheme`, `invalid`, `invalidArguments`, `timeout`,
`appUnavailable`, `appElevated`, `appTooOld`, `unknownTool`, `failed`.

- `timeout`: Baton is busy; a change may have been made, so check with `list_downloads` before trying again.
- `appUnavailable`: Baton is not running and could not be started, or closed the connection without answering. A call is never sent
  twice when the connection broke after it was sent.
- `appElevated`: Baton was started with administrator rights. Its pipe then belongs to the Administrators group and a normal program
  (such as `batonctl mcp` started by your agent) refuses it; MCP is off in that state anyway. Start Baton normally, or run `batonctl` from an
  administrator terminal. (How the browser's native host behaves toward an elevated Baton is not verified.)
- `appTooOld`: Baton is older than 1.4.2; update it. With the app unreachable, `tools/list` shows every tool (a shorter list
  would stay shorter for the whole session) and each call says why it fails.

## What this does not protect against

The pipe is reachable by every program of your Windows user and the origin of a message is only declared, so the levels are rules
for well-behaved clients, not a barrier against code that already runs as you. In particular **an agent that has a shell can run
`batonctl` itself**, which is the command line and is not subject to the levels. If you give an agent a shell, deny `batonctl` in its
own permission rules (for Claude Code, a `deny` rule such as `Bash(batonctl:*)`), and keep the MCP level as low as you need.

## Not verified

Claude Desktop (real), and a Baton older than 1.4.2 beyond the `appTooOld` answer.
