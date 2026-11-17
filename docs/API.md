# Local REST API

Baton can serve a small HTTP API on your own computer, so scripts and agents can list and add downloads. It is **off by default**: turn it on in Settings › Automation by choosing a level. The machine-readable description is [openapi.json](openapi.json), also served at `/api/v1/openapi.json`.

Base address: `http://127.0.0.1:7745/api/v1` (the port is a setting, 1024-65535; if it is in use Baton says so in Settings and does not pick another).

## Access

- Every request needs `Authorization: Bearer <token>` (the token is in Settings › Automation: Show, Copy, New token; a new token stops the old one at once). The `openapi.json` endpoint needs it too.
- The caller must be on this computer, the `Host` header must be exactly `127.0.0.1:<port>` or `localhost:<port>`, and a request with an `Origin` header is refused with 403 (so a web page cannot call the API; there is no CORS).
- Limits: body 1 MB, 120 requests per minute (429 beyond that).
- Levels: *Off*, *Read only* (list, status, version, queues), *Read and add downloads*, *Extended* (also pause, resume, retry, remove from the list, start/stop a queue).
- `open` (opening a finished file) is never available through the API, at any level. `folder` is not accepted when adding: choose a `category` and the app's own folder for it is used.

Refusals and every request that changes something are written to the activity log (Settings › Automation › Activity log); addresses are logged without query string, fragment or user info.

## Routes

| Method and path | Level | Meaning |
|---|---|---|
| `GET /version` | read | App and protocol version |
| `GET /downloads`, `GET /downloads/{id}` | read | List, status of one |
| `POST /downloads` | read and add | Add `{ "url": ..., "category": ..., "allowDuplicate": false }`; 201 added, 409 `duplicate`, 400 invalid, 403 refused |
| `POST /downloads/{id}/pause` `resume` `retry` | extended | Control |
| `DELETE /downloads/{id}` | extended | Remove a **finished** download from the list (the file stays on the disk); an unfinished one is refused with 400, because removing it would delete its partial file |
| `GET /queues` | read | Queues |
| `POST /queues/{name}/start` `stop` | extended | Control a queue; a queue started this way never runs its end action (shut down, sleep…) by itself, Baton asks for a click |

Unknown JSON fields are rejected with 400.

## Addresses an agent may ask for

Only `http`, `https`, `ftp`, `ftps` and `ftpes`. Addresses on this computer or a private network (loopback, RFC 1918, link-local, shared address space, unique-local IPv6) are refused unless Settings › Automation › "Allow addresses of the local network" is on (off by default).

Downloads asked for through the API never use the logins saved in Baton. A name that does not resolve within 5 seconds is refused (HTTP 400, as it cannot be shown to be public).

The check is not only made when the download is added: it is made again every time a connection is made, so a name whose DNS answer changes afterwards (DNS rebinding) or a redirect cannot lead to a private address.

- **HTTP(S)**: every request and every redirect is checked, and the connection goes only to public addresses.
- **FTP(S)**: Baton resolves the name itself, refuses it when no address is public, and connects to the address it checked (without TLS) or checks the remote address before the login during the TLS handshake and again after it (with TLS the certificate has to match the name). The data connection is always passive and always goes to the **same address as the control connection**: the address a server names in `PASV`/`EPSV` is ignored, so a server cannot send Baton to another host. Active mode is not used for these downloads. Through a proxy the checks are limited to the name, because the proxy you configured does the connecting.

## The pipe and the token

The command line (`batonctl`) talks to the app through a named pipe with the message `origin` set to `cli`. The API server runs inside the app and turns each HTTP request into the same kind of message with `origin=api`, which it hands to the same handler (it does not go through the pipe). A message with `origin=api` that arrives on the pipe does **not** need the token: the pipe is reachable only by the current Windows user (its access list, which also refuses network clients), and any program of that user can already do everything the token protects. The token protects the HTTP port, which any local program and any browser tab could otherwise reach. The levels are a rule for well-behaved clients, not a barrier against the user's own programs.
