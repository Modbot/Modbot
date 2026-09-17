§0. What this file is

This is a snapshot of where MCP (Model Context Protocol) stands as of 2026-09-17: the
spec itself, how the big hosted chat apps let a person add a remote MCP server today,
the exact config shape each local tool wants, and the current state of the official
C# MCP SDK on NuGet. Written for anyone at Modbot who needs to wire a Modbot MCP
server up to Claude, ChatGPT, Gemini, or a coding tool without re-deriving all of this
from scratch.

§1. The MCP specification

§1.1 Which revision is current

The stable, "current" revision of the spec as of today is **2025-11-25**. A newer
**2026-07-28 revision** exists as a **release candidate** (announced on the MCP blog);
it had not become the current stable revision as of this writing. So "2025-11-25" is
the number to build against; treat 2026-07-28 as "coming, not yet final."

Source: [modelcontextprotocol.io/specification/2025-11-25](https://modelcontextprotocol.io/specification/2025-11-25) (fetched 2026-09-17) and [The 2026-07-28 MCP Specification Release Candidate](https://blog.modelcontextprotocol.io/posts/2026-07-28-release-candidate/) (found via search 2026-09-17, not fetched directly).

§1.2 Streamable HTTP transport (VERIFIED, fetched from the spec page itself)

- The server exposes **one HTTP path** ("the MCP endpoint") that answers both `POST`
  and `GET`, e.g. `https://example.com/mcp`.
- **Every** JSON-RPC message the client sends is a new HTTP `POST` to that endpoint.
  The client's `Accept` header on that POST **must** list both `application/json` and
  `text/event-stream`.
- If the posted body is a JSON-RPC *request* (not a response/notification), the server
  answers either with `Content-Type: application/json` (one JSON object) or
  `Content-Type: text/event-stream` (opens a Server-Sent-Events stream that eventually
  carries the JSON-RPC response for that request). The client must handle both.
- If the posted body is a JSON-RPC *response* or *notification*, the server just
  answers `202 Accepted` with no body (or an HTTP error if it can't accept it).
- The client may also send a plain `GET` to the same endpoint to open a stream the
  server can push messages on, without the client posting anything first. The `Accept`
  header on that GET must list `text/event-stream`. The server either opens the stream
  or answers `405 Method Not Allowed` if it doesn't offer one.
- **Session id**: the server *may* hand out a session id at start-up, in an
  `Mcp-Session-Id` HTTP response header on the reply to the first `initialize` call.
  If it does, the client **must** send that same header on every later request. A
  server that requires the header should answer `400 Bad Request` to a later request
  missing it. The server can end the session at any time; once ended it answers `404`
  to that id, and the client must start a fresh session (a new `initialize`, no id).
  Session ids must be plain visible ASCII (0x21–0x7E) only.
- **Ending a session**: the client, when it's done with a session, *should* send an
  HTTP `DELETE` to the endpoint carrying the `Mcp-Session-Id` header, to close it
  explicitly. A server that doesn't allow clients to close sessions this way may answer
  `405 Method Not Allowed`.
- **Protocol version header**: the client must send `MCP-Protocol-Version: <version>`
  (e.g. `MCP-Protocol-Version: 2025-11-25`) on every request after the first, using
  whichever version was agreed during `initialize`. If the header is missing and the
  server has no other way to know the version, it should assume `2025-03-26` (the old
  version) for backward compatibility. An unsupported/invalid version gets `400`.
- **Reconnecting**: to resume after a dropped stream, the client sends a `GET` with a
  `Last-Event-ID` header carrying the last SSE event id it saw; the server may replay
  what it missed on that same stream.
- **Origin header**: servers must check the `Origin` header on every connection and
  refuse (`403`) an invalid one, to block DNS-rebinding attacks; a server meant only
  for local use should bind to `127.0.0.1`, not `0.0.0.0`.

Source: [modelcontextprotocol.io/specification/2025-11-25/basic/transports](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports) (fetched 2026-09-17, full text pulled directly from the page).

§1.3 Authorization (VERIFIED, fetched from the spec page itself)

Authorization is **optional** for MCP overall, but when a server does use it, this is
the shape the spec requires. It rests on OAuth 2.1 (still an IETF draft,
`draft-ietf-oauth-v2-1-13`) plus four other standards: RFC 8414 (Authorization Server
Metadata), RFC 7591 (Dynamic Client Registration), RFC 9728 (Protected Resource
Metadata), and — new in this revision — the **Client ID Metadata Documents** draft
(`draft-ietf-oauth-client-id-metadata-document-00`).

Roles: the MCP server is the OAuth "resource server"; the MCP client is the OAuth
"client"; a separate (or co-hosted) "authorization server" issues tokens.

**Protected Resource Metadata (RFC 9728) — required.** Every MCP server that uses
authorization must publish this. Two ways a client can find it:
1. On a `401 Unauthorized` reply, the `WWW-Authenticate` header carries
   `resource_metadata="<url>"`. Clients must prefer this when present. Example from
   the spec:
   ```
   WWW-Authenticate: Bearer resource_metadata="https://mcp.example.com/.well-known/oauth-protected-resource",
                            scope="files:read"
   ```
2. Otherwise, a well-known URI: **at the server's own path**,
   e.g. `https://example.com/public/mcp` → `https://example.com/.well-known/oauth-protected-resource/public/mcp`,
   **or at the root**, `https://example.com/.well-known/oauth-protected-resource`.
   Clients must try both discovery mechanisms and fall back to the well-known URIs, in
   that order, if the header path doesn't work.

The metadata document must include `authorization_servers` (at least one entry). The
spec doesn't otherwise fix every field — it defers to RFC 9728 itself for the rest.

**Authorization Server Metadata — one of two required.** The authorization server
must publish either RFC 8414 metadata or OpenID Connect Discovery 1.0; clients must be
able to use either. For an issuer with a path (e.g. `.../tenant1`), clients try, in
order:
1. `https://auth.example.com/.well-known/oauth-authorization-server/tenant1`
2. `https://auth.example.com/.well-known/openid-configuration/tenant1`
3. `https://auth.example.com/tenant1/.well-known/openid-configuration`

For an issuer with no path, clients try
`https://auth.example.com/.well-known/oauth-authorization-server` then
`https://auth.example.com/.well-known/openid-configuration`.

**Client registration — three ways, in priority order (new in this revision).**
1. **Pre-registered client** — use it if the client already has one for this server.
2. **Client ID Metadata Documents (CIMD)** — landed in this spec revision. The client
   hosts a small JSON document at an HTTPS URL and uses *that URL itself* as its OAuth
   `client_id`; no registration call needed. The document must contain at least
   `client_id`, `client_name`, `redirect_uris`, and the `client_id` value in the
   document must match the URL exactly. Authorization servers advertise support for
   this via `"client_id_metadata_document_supported": true` in their own metadata.
   Example document from the spec:
   ```json
   {
     "client_id": "https://app.example.com/oauth/client-metadata.json",
     "client_name": "Example MCP Client",
     "client_uri": "https://app.example.com",
     "logo_uri": "https://app.example.com/logo.png",
     "redirect_uris": [
       "http://127.0.0.1:3000/callback",
       "http://localhost:3000/callback"
     ],
     "grant_types": ["authorization_code"],
     "response_types": ["code"],
     "token_endpoint_auth_method": "none"
   }
   ```
3. **Dynamic Client Registration (RFC 7591)** — kept only "for backwards
   compatibility," may-support rather than should-support now that CIMD exists.
4. If none of the above work, the client should just ask the person using it to type
   in client details by hand.

**PKCE — S256 required.** Clients must implement PKCE and must use the `S256`
challenge method whenever technically able to. Since OAuth itself has no standard way
to *ask* whether a server supports PKCE, the client must check the authorization
server's metadata for `code_challenge_methods_supported`; if that field is missing,
the client must refuse to proceed at all (for both plain OAuth metadata and OpenID
Connect metadata).

**Resource Indicators (RFC 8707) — the `resource` parameter.** The client must send a
`resource` parameter on *both* the authorization request and the token request, giving
the canonical URI of the MCP server it intends to use the token with (e.g.
`https://mcp.example.com/mcp`, no fragment, no trailing slash preferred). This binds
the token to that specific server — MCP servers must in turn check the token's
audience actually names them, and must never forward (pass through) a client's token
to some other upstream API unmodified.

**Refresh tokens.** For public clients (which most MCP clients are — CLI tools, apps
with no client secret), the authorization server must rotate refresh tokens on each
use, per OAuth 2.1 §4.3.1.

**Scopes.** `scopes_supported` in the Protected Resource Metadata document is meant to
be the bare minimum scope set. On a 401, the server should also send a `scope`
parameter in the `WWW-Authenticate` header naming exactly what's needed for *that*
request; if present, the client must treat it as authoritative over
`scopes_supported`. If no `scope` is given on the 401, the client falls back to
requesting everything in `scopes_supported`.

**Insufficient scope / step-up flow — yes, it exists.** If a client already holds a
token but it lacks a needed scope, the server should answer `403 Forbidden` (not 401)
with:
```
WWW-Authenticate: Bearer error="insufficient_scope",
                         scope="files:read files:write user:profile",
                         resource_metadata="https://mcp.example.com/.well-known/oauth-protected-resource",
                         error_description="Additional file write permission required"
```
The client should then request a fresh token with the union of its old scopes plus the
newly required ones ("step-up authorization"), retry the request a small bounded
number of times, and give up permanently after that.

Error codes overall: `401` = no/bad token, `403` = bad scope, `400` = malformed
request.

Source: [modelcontextprotocol.io/specification/2025-11-25/basic/authorization](https://modelcontextprotocol.io/specification/2025-11-25/basic/authorization) (fetched 2026-09-17, full text pulled directly from the page).

§2. Adding a remote MCP server in each hosted chat app, today (Sept 2026)

§2.1 Claude.ai (web, desktop, Cowork, mobile)

**Supported.** Called "custom connectors." When you add one, Claude's own cloud
infrastructure connects to your server — not your local device — so this works the
same way across claude.ai, Claude Desktop, Cowork, and the mobile apps.

- **Where**: Pro/Max — go to *Customize → Connectors*, click the "+" button, choose
  "Add custom connector." Team/Enterprise — an org owner goes to *Organization
  settings → Connectors*, clicks "Add," hovers "Custom," picks "Web."
- **Fields**: a required remote MCP server URL, plus an optional "Advanced settings"
  panel where you can set an OAuth Client ID and OAuth Client Secret for your server.
- **Auth supported**: OAuth (with the client id/secret you supply, or presumably
  dynamic registration/discovery if you leave those blank — the support article
  doesn't spell out the fallback), or no auth for a public server.
- **Plans**: Free, Pro, Max, Team, and Enterprise all get custom connectors. Free
  accounts are capped at **one** custom connector.
- **Constraint**: the server must be reachable over the public internet from
  Anthropic's own IP ranges — a server sitting behind a corporate VPN or firewall
  won't connect.
- No "add connector" deep-link URL was found documented (UNVERIFIED — not confirmed
  either way; the support article didn't mention one).
- **Claude Desktop config file**: for genuinely *local* MCP servers,
  `claude_desktop_config.json` still only speaks the classic stdio shape (a `command`
  and `args`). It does **not** understand a `"type": "http"` + `"url"` remote entry —
  multiple 2026 bug reports describe Claude Desktop silently corrupting the
  `mcpServers` block when a `url` field is present. The documented workaround for
  wiring a *remote* server into Desktop through the old local-config path is to run it
  through the `mcp-remote` bridge package:
  ```json
  {
    "mcpServers": {
      "my-mcp-server": {
        "command": "npx",
        "args": ["mcp-remote", "https://your-mcp-server.com"]
      }
    }
  }
  ```
  In practice, though, Desktop's own "custom connectors" UI (§2.1 above) is the
  supported way to add a remote server now — the config-file route is really for local
  stdio servers.

Sources: [Get started with custom connectors using remote MCP – Claude Help Center](https://support.claude.com/en/articles/11175166-get-started-with-custom-connectors-using-remote-mcp) (fetched 2026-09-17); GitHub issue [anthropics/claude-code#37286](https://github.com/anthropics/claude-code/issues/37286) on Desktop config corruption (found via search 2026-09-17, not fetched directly — UNVERIFIED detail, but corroborated by more than one blog).

§2.2 ChatGPT (OpenAI)

**Supported**, under "Developer mode."

- **Where**: *Settings → Security and login*, toggle "Developer mode" on. (Older
  guides describe the path as *Settings → Connectors → Advanced*, or *Settings → Apps
  → Advanced settings*; OpenAI appears to have moved this control more than once
  during 2026, so check your own account if the exact menu doesn't match — treat the
  precise path as UNVERIFIED beyond "it's somewhere in Settings.") Once on, go to
  *Settings → Connectors* (also called "Plugins" in some builds) and create a new
  developer-mode connector.
- **Fields**: name, description (the model reads this when deciding whether to use the
  tool, so it matters), and the MCP server's URL.
- **Auth supported**: **OAuth** — including support for Client ID Metadata Documents
  and Dynamic Client Registration — **or "No Authentication."** A "Mixed
  Authentication" mode (OAuth and no-auth tools coexisting on one connector) also
  exists.
- **Transport**: both SSE and Streamable HTTP are supported.
- **Plans**: Pro, Plus, Business, Enterprise, and Education accounts, on the web.
  Managed workspaces can also gate this by admin policy.
- **Tool count / deep research constraint**: Developer mode connectors do **not**
  require `search`/`fetch` tools — that requirement only applied to the older,
  narrower "custom connectors for Deep Research" feature that predates Developer mode.
  No maximum tool count is documented.
- **HTTPS**: not explicitly stated in what was fetched, but every example URL uses
  `https://`; treat "HTTPS required" as UNVERIFIED-but-likely.

Sources: [Developer mode and MCP apps in ChatGPT – OpenAI Help Center](https://help.openai.com/en/articles/12584461-developer-mode-and-mcp-apps-in-chatgpt) (found via search 2026-09-17; direct fetch returned 403, so its content here is drawn from search-result excerpts, not a full page fetch — mark as **partially unverified**); [ChatGPT Developer mode – developers.openai.com](https://developers.openai.com/api/docs/guides/developer-mode) (fetched 2026-09-17, used for the "Settings → Security and login," plan list, and the "does not require search/fetch tools" quote).

§2.3 Gemini (Google)

**Mixed — it depends which "Gemini" you mean.**

- **gemini.google.com, personal account** (the ordinary consumer chat app): custom
  remote MCP servers are supported through a feature called **Gemini Spark**, under
  *Connected Apps*. It takes any HTTPS MCP server URL. This is gated to a **personal
  Google Account** — it is **not available** if you sign in with a work or school
  Google Account. (UNVERIFIED in detail — found via search summaries, not a direct
  fetch of a Google support page; treat the "Spark" branding and exact menu path with
  caution, but the personal-account-only gating showed up consistently across
  sources.)
- **Gemini Business / Gemini Enterprise** (the paid workspace product, a different
  product from the free consumer app): custom MCP server connections are supported for
  admins, with mandatory OAuth 2.0 and TLS — this is a separate, enterprise-admin-only
  feature, documented at Google Cloud's own docs.
- **Gemini CLI**: config lives in `~/.gemini/settings.json` (user-wide) or
  `.gemini/settings.json` (per project), under an `mcpServers` map. Each server entry
  picks its transport by which one field it sets — `command` (stdio), `url` (SSE, the
  older HTTP+SSE transport), or `httpUrl` (Streamable HTTP). Example with headers and
  a bearer token:
  ```json
  {
    "mcpServers": {
      "httpServerWithAuth": {
        "httpUrl": "http://localhost:3000/mcp",
        "headers": {
          "Authorization": "Bearer your-api-token",
          "X-Custom-Header": "custom-value"
        },
        "timeout": 5000
      }
    }
  }
  ```
  OAuth is also supported for CLI-connected servers, with a `dynamic_discovery`
  provider type (the default — auto-discovers OAuth config from the server) or a
  `google_credentials` type (uses Google's own Application Default Credentials).
  `${MY_ENV_VAR}`-style variable substitution works inside `settings.json`.
- **Gemini API**: has its own `mcp` tool for wiring an MCP server into API calls
  server-side (UNVERIFIED in detail — not independently fetched this pass).
- **"Gems"**: these are Gemini's saved-persona/custom-instruction feature, a different
  thing from MCP connectors; nothing found ties Gems to remote MCP server support.

Sources: search-result summaries citing [Tallyfy – How to get your MCP server into
Google Gemini](https://tallyfy.com/how-to-list-mcp-server-google-gemini/) and Google's
own Gemini Enterprise support pages (found via search 2026-09-17, not fetched
directly — **UNVERIFIED**, corroborated across several independent write-ups but no
official Google page was itself fetched); [google-gemini/gemini-cli — docs/tools/mcp-server.md](https://github.com/google-gemini/gemini-cli/blob/main/docs/tools/mcp-server.md) (found via search 2026-09-17, JSON and field names drawn from search excerpts, not a direct fetch — treat field names as reliable, since they matched across multiple independent mirrors of the same doc, but mark as not directly fetched this pass).

§2.4 Grok (xAI)

**Not supported in the consumer chat app** (grok.com / the Grok mobile apps) as far as
any source found describes. What **is** supported is xAI's **developer-facing API**:
"Remote MCP Tools" work in the xAI native SDK, the OpenAI-compatible Responses API,
and the Speech-to-Speech / Voice Agent API — you specify a server URL and xAI's
backend manages the connection. This is a developer/API feature, not a "go to
settings and add a connector" feature in the grok.com chat UI. Grok 4.3 (on the API
since May 2026) supports this natively. No evidence was found of a grok.com Settings
page for adding a personal custom MCP connector the way Claude/ChatGPT/Gemini have —
say plainly: **not supported for the consumer chat surface**, only for developers
calling the API directly.

Sources: [Remote MCP Tools – docs.x.ai](https://docs.x.ai/developers/tools/remote-mcp) (found via search 2026-09-17, not fetched directly — UNVERIFIED in exact wording, but the "xAI SDK / Responses API / Speech-to-Speech" list was consistent across two independent docs.x.ai URLs returned by search).

§3. Local clients — exact config JSON for a remote server

§3.1 Claude Desktop (`claude_desktop_config.json`)

Local stdio servers only, in the config file itself — see §2.1 above for why. The
documented workaround for a remote URL is the `mcp-remote` bridge:
```json
{
  "mcpServers": {
    "my-mcp-server": {
      "command": "npx",
      "args": ["mcp-remote", "https://your-mcp-server.com", "--header", "Authorization: Bearer YOUR_TOKEN"]
    }
  }
}
```
(The `--header` flag on `mcp-remote` itself is the standard way to pass a bearer
token through — UNVERIFIED exact flag spelling for this specific package version, but
this is the documented convention across the write-ups found.) For an actual remote
server added the supported way, use the in-app "custom connectors" UI (§2.1), not this
file.

§3.2 Claude Code

CLI:
```bash
claude mcp add --transport http <name> <url> --header "Authorization: Bearer YOUR_TOKEN"
```
Add more headers by repeating `--header`. Scope with `--scope user` (global) or
`--scope project`. `.mcp.json` shape it writes / reads:
```json
{
  "mcpServers": {
    "my-server": {
      "type": "http",
      "url": "https://example.com/mcp",
      "headers": {
        "Authorization": "Bearer secret-token"
      }
    }
  }
}
```
SSE is also supported (`--transport sse`) but is called out in the docs as
**deprecated** in favor of Streamable HTTP; Claude Code (2.1.265+) tries HTTP first
and falls back to SSE automatically. Claude Code has full OAuth 2.0 support for remote
servers too — servers needing auth show up flagged in `/mcp`, sign-in happens via
`/mcp` or `claude mcp login <name>`, dynamic client registration is used
automatically where the server supports it, and `--client-id`/`--client-secret` flags
let you pass pre-registered credentials instead:
```bash
claude mcp add --transport http --client-id your-client-id --client-secret \
  --callback-port 8080 my-server https://mcp.example.com/mcp
```

Source: [code.claude.com/docs/en/mcp](https://code.claude.com/docs/en/mcp) (fetched 2026-09-17).

§3.3 Cursor

`~/.cursor/mcp.json` (or a project-level `.cursor/mcp.json`), using `url` and
`headers` fields under `mcpServers` (the exact per-server JSON shape mirrors Claude
Code's — `type`/`url`/`headers` — per Cursor's own install-link docs, UNVERIFIED exact
key spelling since the page itself wasn't fetched, only summarized via search).

Deep link:
```
cursor://anysphere.cursor-deeplink/mcp/install?name=<NAME>&config=<BASE64_JSON_CONFIG>
```
Build it by JSON-stringifying the server's config object (the same shape as an
`mcp.json` entry — either the stdio shape with `command`/`args`, or the HTTP shape
with `type`+`url`), base64-encoding that string, and substituting it in for
`<BASE64_JSON_CONFIG>`. Cursor's own docs site offers a small tool that generates this
link from a pasted JSON config.

Source: [Cursor Docs – MCP Install Links](https://cursor.com/docs/context/mcp/install-links) (found via search 2026-09-17, summarized from search excerpts, not independently fetched — mark the exact field names as UNVERIFIED, the deep-link format itself as reasonably solid since it was consistent across several independent write-ups).

§3.4 VS Code

`mcp.json` (workspace: `.vscode/mcp.json`, or a user-level file) — the root key is
**`servers`**, not `mcpServers` (this is a common mistake called out in VS Code's own
GitHub issue tracker: using `mcpServers` there "silently breaks everything"):
```json
{
  "servers": {
    "github": {
      "type": "http",
      "url": "https://api.githubcopilot.com/mcp"
    }
  }
}
```
A `headers` field is supported for auth (`"headers": {"Authorization": "Bearer your-token-here"}`), though as of Sept 2026 there is an open VS Code bug where `headers` is silently dropped specifically for a *workspace* `.mcp.json` file (while the same config works from `.vscode/mcp.json`) — worth testing rather than assuming, if headers seem to not be reaching the server (UNVERIFIED as still-open; found via a live GitHub issue title, not confirmed fixed or not as of today).

Deep link:
```
vscode:mcp/install?<url-encoded-JSON-config>
```
(Insiders build: `vscode-insiders:mcp/install?...`.) Built as
`` `vscode:mcp/install?${encodeURIComponent(JSON.stringify(obj))}` ``.

Sources: [VS Code Docs – Add and manage MCP servers](https://code.visualstudio.com/docs/agent-customization/mcp-servers) (fetched 2026-09-17, gave the `servers`/`type`/`url` shape; did not itself show the deep link or `headers` field — those two details come from search-result summaries of the MCP developer guide and open GitHub issues, found 2026-09-17, not independently fetched — mark as less certain than the `servers` root key, which was fetched directly).

§3.5 Windsurf

`~/.codeium/windsurf/mcp_config.json`, under `mcpServers`, using `serverUrl` (or the
alias `url` — both are accepted) plus `headers`:
```json
{
  "mcpServers": {
    "remote-http-mcp": {
      "serverUrl": "https://your-server.com/mcp",
      "headers": {
        "Authorization": "Bearer your-token"
      }
    }
  }
}
```
The file supports `${env:VAR_NAME}` and `${file:/path/to/file}` substitution so
secrets don't have to be hardcoded in the file.

Source: search-result summary citing Windsurf/Cascade docs (found 2026-09-17, not
independently fetched — UNVERIFIED in exact field spelling, though `serverUrl` +
`headers` was consistent across multiple independent write-ups).

§3.6 Codex CLI (OpenAI)

`~/.codex/config.toml` (or a per-project `.codex/config.toml` for trusted projects),
under a `[mcp_servers.<name>]` table. Giving it a `url` (instead of `command`) makes
Codex pick the Streamable HTTP transport automatically:
```toml
[mcp_servers.example]
url = "https://mcp.example.com/mcp"

[mcp_servers.example.http_headers]
x-custom-header = "value"
```
For a bearer token pulled from an environment variable rather than hardcoded:
```toml
[mcp_servers.example]
url = "https://mcp.example.com/mcp"
bearer_token_env_var = "MY_TOKEN_ENV_VAR"
```
OAuth is also supported directly: `codex mcp login <name>`.

Source: search-result summaries citing developers.openai.com/codex/mcp and several
independent Codex CLI guides (found 2026-09-17, not independently fetched — the
`http_headers` table name and `bearer_token_env_var` key are UNVERIFIED exact
spellings, consistent across sources but not confirmed against the primary doc page
itself).

§3.7 Gemini CLI

See §2.3 above — `httpUrl` + `headers` under `mcpServers` in `settings.json`, with
`oauth` config for servers needing sign-in.

§4. The official C# MCP SDK

§4.1 Package versions (VERIFIED — fetched nuget.org directly, 2026-09-17)

- **`ModelContextProtocol`** — current version **2.2.0**, published 2026-08-13, **not
  a prerelease**. Description: "The official C# SDK for the Model Context Protocol,
  enabling .NET applications, services, and libraries to implement and interact with
  MCP clients and servers." Targets **.NET 8.0, .NET 9.0, .NET 10.0, and
  .NET Standard 2.0**. It depends on a lower-level `ModelContextProtocol.Core` package
  (also seen at version 1.2.0/2.2.0 in search results — UNVERIFIED which exact number
  is current for `.Core` specifically, since that package page wasn't fetched
  directly).
- **`ModelContextProtocol.AspNetCore`** — also version **2.2.0**, published
  2026-08-13, **not a prerelease**. Targets **.NET 8.0, .NET 9.0, .NET 10.0**. This is
  the HTTP-hosting package: ASP.NET Core integration for running an MCP server over
  Streamable HTTP.

Both packages moved out of prerelease (they were 0.x-preview builds earlier in 2026 —
search results turned up old preview numbers like 0.3.0-preview.3 and
0.8.0-preview.1) and are now on a stable 2.x line as of August 2026.

Sources: [nuget.org/packages/ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol) and [nuget.org/packages/ModelContextProtocol.AspNetCore](https://www.nuget.org/packages/ModelContextProtocol.AspNetCore) (both fetched 2026-09-17).

§4.2 Basic HTTP server setup (VERIFIED — pulled from the SDK's own getting-started
page and its `ProtectedMcpServer` sample on GitHub)

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMcpServer()
    .WithHttpTransport(options =>
    {
       options.SessionMode = HttpServerSessionMode.Stateless;
    })
    .WithToolsFromAssembly();
var app = builder.Build();

app.MapMcp();

app.Run("http://localhost:3001");
```

`MapMcp()` takes an optional path argument (e.g. `app.MapMcp("/mcp")`) to serve the
endpoint somewhere other than the app's root — the fetched getting-started example
used the root, so the exact call with a path argument wasn't independently confirmed
this pass, but it follows the ordinary ASP.NET Core `Map...` convention and is
documented elsewhere in the SDK's samples (UNVERIFIED to that precision, but very low
risk of being wrong).

**Attributes.** `WithToolsFromAssembly()` scans the assembly for every class marked
`[McpServerToolType]` and registers each `[McpServerTool]`-attributed method on it as
a callable tool:
```csharp
[McpServerToolType]
public static class EchoTool
{
    [McpServerTool, Description("Echoes the message back to the client.")]
    public static string Echo(string message) => $"hello {message}";
}
```
`WithTools<T>()` (seen in the `ProtectedMcpServer` sample, `.WithTools<WeatherTools>()`)
registers just the tools on one named type, rather than scanning the whole assembly.

§4.3 `HttpServerTransportOptions` — full property list (VERIFIED, fetched from the
SDK's own source file on GitHub, `src/ModelContextProtocol.AspNetCore/HttpServerTransportOptions.cs`)

| Property | Type | What it does |
|---|---|---|
| `Stateless` | `bool` | "value that indicates whether the server runs in a stateless mode that doesn't track state between requests, allowing for load balancing without session affinity." |
| `SessionMode` | `HttpServerSessionMode` | "value that indicates how the server tracks state between requests." (an enum; `Stateless` is one of its values — the sample code sets `options.SessionMode = HttpServerSessionMode.Stateless;`, so there are two related-but-distinct ways to reach stateless mode: the plain `bool Stateless` property, and the newer `SessionMode` enum.) |
| `ConfigureSessionOptions` | `Func<HttpContext, McpServerOptions, CancellationToken, Task>?` | optional async callback to configure per-session `McpServerOptions`, with access to the `HttpContext` of the request that started the session |
| `RunSessionHandler` | `Func<HttpContext, McpServer, CancellationToken, Task>?` | optional async callback for running a new MCP session by hand |
| `EnableLegacySse` | `bool` | whether the server also maps the old (pre-Streamable-HTTP) SSE endpoints, for older clients |
| `EventStreamStore` | `ISseEventStreamStore?` | event store for resumability — when set, events are stored and can be replayed to a client that reconnects with `Last-Event-ID` |
| `SessionMigrationHandler` | `ISessionMigrationHandler?` | handler for moving a session between server instances |
| `PerSessionExecutionContext` | `bool` | whether the server uses one execution context for the whole session (note: turning this on stops you from being able to use `IHttpContextAccessor` in handlers — see §4.4) |
| `IdleTimeout` | `TimeSpan` | how long the server waits with no active requests before timing out a session |
| `MaxIdleSessionCount` | `int` | max number of idle sessions kept in memory |
| `TimeProvider` | `TimeProvider` | time source used for testing `IdleTimeout` |

The sample's own comment on `SessionMode`: "Stateless mode is the default and
recommended for servers that don't need 2025-11-25 protocol revision server-to-client
requests like sampling or elicitation. Stateless model enables horizontal scaling
without session affinity and works with clients that don't send Mcp-Session-Id."

Source: [raw.githubusercontent.com — HttpServerTransportOptions.cs](https://raw.githubusercontent.com/modelcontextprotocol/csharp-sdk/main/src/ModelContextProtocol.AspNetCore/HttpServerTransportOptions.cs) and [raw.githubusercontent.com — samples/ProtectedMcpServer/Program.cs](https://raw.githubusercontent.com/modelcontextprotocol/csharp-sdk/main/samples/ProtectedMcpServer/Program.cs) (both fetched 2026-09-17, full file contents pulled directly).

§4.4 Getting the calling user into a tool call

Two documented ways (search-result summary, not independently fetched from a single
canonical page — UNVERIFIED in exact wording, though the pattern is common and
low-risk):
- Inject `IHttpContextAccessor` into a tool class and read
  `HttpContext?.User?.FindFirst("sub")?.Value` etc. This only works for the HTTP
  transport, and stops working if `PerSessionExecutionContext` is turned on (that mode
  uses `AsyncLocal<T>` state scoped to the whole session instead, which doesn't carry
  a live `HttpContext`).
- Have the tool method take a `ClaimsPrincipal` parameter directly (or read it off
  `RequestContext<CallToolRequestParams>`) — described as the more "portable" option
  since it isn't HTTP-specific. Exact call shape not independently confirmed this
  pass.

Also confirmed (from the fetched `ProtectedMcpServer` sample, §4.5 below):
`app.MapMcp().RequireAuthorization()` — chaining the ordinary ASP.NET Core
`RequireAuthorization()` extension straight onto the MCP endpoint mapping — works, and
is the documented way to gate the whole MCP endpoint behind standard ASP.NET Core
auth. A named policy also works: `app.MapMcp("/mcp").RequireAuthorization("PolicyName")`
(UNVERIFIED exact quoting — summarized from search, not fetched from source, but the
pattern is standard ASP.NET Core and highly likely correct).

§4.5 Dynamic tool registration and the auth package

**Dynamic registration** — besides the `[McpServerToolType]`/`[McpServerTool]`
attribute approach, the SDK supports building a tool at runtime with
`McpServerTool.Create(...)`, which wraps "a large variety of .NET method signatures"
and can be given an `IServiceProvider`. For servers that want full control over what
`tools/list` and `tools/call` return, `McpServerOptions` exposes `ListToolsHandler`
and `CallToolHandler` delegates directly; when you also register a `ToolCollection`
(e.g. through `WithTools()`), the SDK auto-generates handlers that read from that
collection, and any tools added to it later can be announced to already-connected
clients with
`await server.SendNotificationAsync(NotificationMethods.ToolListChangedNotification, new ToolListChangedNotificationParams())`.
(This paragraph is a search-result summary, not independently fetched from source —
UNVERIFIED in exact method signatures.)

**Auth package** — there is **no separate NuGet package** called
`ModelContextProtocol.AspNetCore.Authentication`. Those types
(`McpAuthenticationDefaults`, `McpAuthenticationHandler`, the `.AddMcp(...)`
extension method, and `ResourceMetadata`/`ProtectedResourceMetadata`) live **inside
the main `ModelContextProtocol.AspNetCore` package**, in a namespace of that name —
confirmed directly by the fetched sample file, whose `using` list includes
`ModelContextProtocol.AspNetCore.Authentication;` alongside `ModelContextProtocol.AspNetCore;`
from the same package. `AddMcp(...)` slots into the ordinary ASP.NET Core auth
pipeline as an additional scheme, layered on top of a real token-validating scheme
(the sample uses `AddJwtBearer` for validation, then `.AddMcp(...)` after it purely to
supply the MCP-flavored challenge behavior and to publish
`ResourceMetadata`/Protected Resource Metadata). Full working example, quoted
verbatim from `samples/ProtectedMcpServer/Program.cs`:

```csharp
builder.Services.AddAuthentication(options =>
{
    options.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.Authority = inMemoryOAuthServerUrl;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidAudience = serverUrl, // Validate that the audience matches the resource metadata as suggested in RFC 8707
        ValidIssuer = inMemoryOAuthServerUrl,
        NameClaimType = "name",
        RoleClaimType = "roles"
    };
})
.AddMcp(options =>
{
    options.ResourceMetadata = new()
    {
        ResourceDocumentation = "https://docs.example.com/api/weather",
        AuthorizationServers = { inMemoryOAuthServerUrl },
        ScopesSupported = ["mcp:tools"],
    };
});

builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();
builder.Services.AddMcpServer()
    .WithTools<WeatherTools>()
    .WithHttpTransport(options =>
    {
        options.SessionMode = HttpServerSessionMode.Stateless;
    });

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapMcp().RequireAuthorization();
```

`ProtectedResourceMetadata` (in `ModelContextProtocol.Authentication`, a level up from
the ASP.NET Core-specific namespace) is the type the SDK builds the
`/.well-known/oauth-protected-resource` document from; the sample sets its
`ResourceDocumentation`, `AuthorizationServers`, and `ScopesSupported` fields.
`AddMcp` is what makes the SDK actually publish that document and handle the
`WWW-Authenticate: ... resource_metadata=...` challenge on 401 automatically — so a
Modbot server using this package gets §1.3's Protected Resource Metadata discovery
"for free" as long as `AddMcp` is wired in.

Source: everything in this subsection with a code block is quoted directly from
[raw.githubusercontent.com — samples/ProtectedMcpServer/Program.cs](https://raw.githubusercontent.com/modelcontextprotocol/csharp-sdk/main/samples/ProtectedMcpServer/Program.cs), fetched 2026-09-17.

§5. What is verified and what is not

**Verified** (fetched an official/primary page or source file directly this session,
2026-09-17, and the content above quotes or closely paraphrases what it said):
- The full text of the MCP 2025-11-25 Streamable HTTP transport page (§1.2).
- The full text of the MCP 2025-11-25 Authorization page (§1.3), including that CIMD
  has landed in this revision and that the step-up/insufficient_scope flow exists.
- `ModelContextProtocol` and `ModelContextProtocol.AspNetCore` current NuGet versions,
  prerelease status, and target frameworks (§4.1) — fetched nuget.org directly.
- The `HttpServerTransportOptions` full property list (§4.3) and the complete
  `ProtectedMcpServer` sample (§4.5, and most of §4.2) — fetched raw source files
  from GitHub directly.
- Claude custom connectors: where to add one, the field list, plan availability
  including the Free-plan one-connector cap, and the "must be reachable from
  Anthropic's IP ranges" constraint (§2.1) — fetched the Claude Help Center article
  directly.
- Claude Code's `claude mcp add` syntax, `.mcp.json` shape, SSE deprecation notice,
  and OAuth flags (§3.2) — fetched code.claude.com directly.
- ChatGPT Developer Mode's plan list and the "does not require search/fetch tools"
  statement (§2.2) — fetched developers.openai.com directly.
- VS Code's `mcp.json` root key (`servers`, not `mcpServers`) and the basic
  `type`/`url` shape (§3.4) — fetched code.visualstudio.com directly.

**Not verified — drawn from web search result summaries only, not an independently
fetched primary page**, flagged individually above at each point, but worth
restating: the exact ChatGPT Developer Mode menu path (it's moved more than once);
Gemini consumer-app ("Spark") details and menu path; Gemini Enterprise/Business
requirements; Grok/xAI's exact API support wording; Cursor's precise `mcp.json` field
names (though the deep-link format is corroborated across sources); VS Code's
`headers` field and its `vscode:mcp/install` deep-link format; Windsurf's config
shape; Codex CLI's `config.toml` shape; the C# SDK's dynamic-tool-registration method
signatures and `IHttpContextAccessor`/`ClaimsPrincipal` injection pattern; and
`ModelContextProtocol.Core`'s exact current version number.

**Explicitly could not access**: the OpenAI ChatGPT help-center article
(help.openai.com/en/articles/12584461) returned HTTP 403 to a direct fetch, so its
content in §2.2 comes only from the search engine's own excerpt of that page, not a
full read.

§6. Sources

- [modelcontextprotocol.io/specification/2025-11-25](https://modelcontextprotocol.io/specification/2025-11-25) — fetched 2026-09-17
- [modelcontextprotocol.io/specification/2025-11-25/basic/transports](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports) — fetched 2026-09-17
- [modelcontextprotocol.io/specification/2025-11-25/basic/authorization](https://modelcontextprotocol.io/specification/2025-11-25/basic/authorization) — fetched 2026-09-17
- [blog.modelcontextprotocol.io — The 2026-07-28 MCP Specification Release Candidate](https://blog.modelcontextprotocol.io/posts/2026-07-28-release-candidate/) — found via search 2026-09-17, not independently fetched
- [support.claude.com/en/articles/11175166 — Get started with custom connectors using remote MCP](https://support.claude.com/en/articles/11175166-get-started-with-custom-connectors-using-remote-mcp) — fetched 2026-09-17
- [code.claude.com/docs/en/mcp](https://code.claude.com/docs/en/mcp) — fetched 2026-09-17
- [help.openai.com/en/articles/12584461 — Developer mode and MCP apps in ChatGPT](https://help.openai.com/en/articles/12584461-developer-mode-and-mcp-apps-in-chatgpt) — attempted 2026-09-17, returned HTTP 403; used only via search-engine excerpt
- [developers.openai.com/api/docs/guides/developer-mode](https://developers.openai.com/api/docs/guides/developer-mode) — fetched 2026-09-17
- [github.com/google-gemini/gemini-cli — docs/tools/mcp-server.md](https://github.com/google-gemini/gemini-cli/blob/main/docs/tools/mcp-server.md) — found via search 2026-09-17, not independently fetched
- [tallyfy.com — How to get your MCP server into Google Gemini](https://tallyfy.com/how-to-list-mcp-server-google-gemini/) — found via search 2026-09-17, not independently fetched
- [docs.x.ai/developers/tools/remote-mcp — Remote MCP Tools](https://docs.x.ai/developers/tools/remote-mcp) — found via search 2026-09-17, not independently fetched
- [www.nuget.org/packages/ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol) — fetched 2026-09-17
- [www.nuget.org/packages/ModelContextProtocol.AspNetCore](https://www.nuget.org/packages/ModelContextProtocol.AspNetCore) — fetched 2026-09-17
- [raw.githubusercontent.com/modelcontextprotocol/csharp-sdk/main/samples/ProtectedMcpServer/Program.cs](https://raw.githubusercontent.com/modelcontextprotocol/csharp-sdk/main/samples/ProtectedMcpServer/Program.cs) — fetched 2026-09-17
- [raw.githubusercontent.com/modelcontextprotocol/csharp-sdk/main/src/ModelContextProtocol.AspNetCore/HttpServerTransportOptions.cs](https://raw.githubusercontent.com/modelcontextprotocol/csharp-sdk/main/src/ModelContextProtocol.AspNetCore/HttpServerTransportOptions.cs) — fetched 2026-09-17
- [csharp.sdk.modelcontextprotocol.io/v2/concepts/getting-started.html](https://csharp.sdk.modelcontextprotocol.io/v2/concepts/getting-started.html) — fetched 2026-09-17
- [code.visualstudio.com/docs/agent-customization/mcp-servers](https://code.visualstudio.com/docs/agent-customization/mcp-servers) — fetched 2026-09-17
- [cursor.com/docs/context/mcp/install-links](https://cursor.com/docs/context/mcp/install-links) — found via search 2026-09-17, not independently fetched
- [developer.mescius.com / docs.windsurf.com — Windsurf MCP config](https://docs.windsurf.com/windsurf/cascade/mcp) — found via search 2026-09-17, not independently fetched
- [developers.openai.com/codex/mcp](https://developers.openai.com/codex/mcp) — found via search 2026-09-17, not independently fetched
