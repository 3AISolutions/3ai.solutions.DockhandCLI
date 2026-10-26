# DockhandCLI

[![CI](https://github.com/3AISolutions/3ai.solutions.DockhandCLI/actions/workflows/ci.yml/badge.svg)](https://github.com/3AISolutions/3ai.solutions.DockhandCLI/actions/workflows/ci.yml)

`dockhand` is a cross-platform .NET 10 command-line client for [Dockhand](https://github.com/Finsys/dockhand)
Docker management servers. It manages any number of Dockhand installations by alias, checks containers
for image updates and applies them, and works both as a one-shot command for scripts and CI and as an
interactive shell.

```text
$ dockhand -srv01 check updates
── srv01 › hhdocker01 (env 1) ──────────────────────────────────────────
╭───────────┬──────────────┬────────────────────┬─────────────────────────────╮
│ Container │ Image        │ Status             │ Details                     │
├───────────┼──────────────┼────────────────────┼─────────────────────────────┤
│ nginx     │ nginx:latest │ ● update available │ 1b4f2c9e8a71 → 7d03e5a6c4b2 │
│ redis     │ redis:7      │ ↑ newer tag        │ newer tag 7.4               │
╰───────────┴──────────────┴────────────────────┴─────────────────────────────╯
1 of 2 container(s) have an image update.
Apply with: dockhand -srv01 check apply update docker <container>
```

- [Requirements](#requirements) · [Install](#install) · [Build from source](#build-from-source)
- [Endpoints](#endpoints) · [Authentication](#authentication) · [Environments](#docker-environments)
- [Direct mode](#direct-command-line-mode) · [Interactive shell](#interactive-shell)
- [Update workflow](#docker-update-workflow) · [JSON output](#json-output-and-automation)
- [Command reference](#command-reference) · [Exit codes](#exit-codes) · [Security](#security) · [Troubleshooting](#troubleshooting)

Design notes and the mapping of every command to the Dockhand REST API are in
[docs/ApiAnalysis.md](docs/ApiAnalysis.md). The original requirements are in
[docs/ProjectDescription.md](docs/ProjectDescription.md).

## Requirements

- To **run** a released binary: nothing. Releases are self-contained (no .NET runtime needed) for
  Windows x64, Linux x64, Linux ARM64 and macOS ARM64.
- To **build**: the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
- A Dockhand server (tested against API version 1.0.53) and, when authentication is enabled on it,
  a Dockhand user account or API token.
- Optional for token storage: Windows Credential Manager (built in), macOS Keychain (built in), or on
  Linux the Secret Service via `secret-tool` (package `libsecret-tools` on Debian/Ubuntu,
  `libsecret` on Fedora/Arch). Without it, tokens fall back to an owner-only file.

## Install

Download the archive for your platform from the
[releases page](https://github.com/3AISolutions/3ai.solutions.DockhandCLI/releases), extract the single
`dockhand` executable and put it on your `PATH`.

**Linux / macOS**

```bash
tar -xzf dockhand-v1.0.0-linux-x64.tar.gz
sudo install -m 0755 dockhand /usr/local/bin/dockhand
dockhand --version
```

On macOS, a downloaded binary is quarantined by Gatekeeper; clear the flag once with
`xattr -d com.apple.quarantine /usr/local/bin/dockhand`.

**Windows (PowerShell)**

```powershell
Expand-Archive dockhand-v1.0.0-win-x64.zip -DestinationPath "$env:LOCALAPPDATA\Programs\dockhand"
[Environment]::SetEnvironmentVariable("Path", "$([Environment]::GetEnvironmentVariable('Path','User'));$env:LOCALAPPDATA\Programs\dockhand", "User")
# open a new terminal, then:
dockhand --version
```

## Build from source

```bash
git clone https://github.com/3AISolutions/3ai.solutions.DockhandCLI.git
cd 3ai.solutions.DockhandCLI
dotnet build
dotnet test
dotnet run --project src/DockhandCLI -- --help
```

### Publishing a standalone executable

Publishing with a runtime identifier produces one self-contained, single-file executable:

| Platform | Command |
|---|---|
| Windows x64 | `dotnet publish src/DockhandCLI -c Release -r win-x64 -o publish/win-x64` |
| Linux x64 | `dotnet publish src/DockhandCLI -c Release -r linux-x64 -o publish/linux-x64` |
| Linux ARM64 | `dotnet publish src/DockhandCLI -c Release -r linux-arm64 -o publish/linux-arm64` |
| macOS ARM64 | `dotnet publish src/DockhandCLI -c Release -r osx-arm64 -o publish/osx-arm64` |

Each output folder contains just `dockhand` (`dockhand.exe` on Windows), about 40 MB. Cross-publishing
works from any OS. Set a version with `-p:Version=1.2.3`.

Pushing a tag such as `v1.2.3` runs the [release workflow](.github/workflows/release.yml), which tests,
publishes all four platforms, and attaches the archives plus `SHA256SUMS.txt` to a GitHub release.

## Endpoints

An **endpoint** is one Dockhand installation (its URL), saved under a short alias. One endpoint can
manage several **Docker environments**; those are separate and covered [below](#docker-environments).

```bash
dockhand endpoint add srv01 https://docker01.example.com --description "Production"
dockhand endpoint add srv02 https://docker02.example.com --default-env 1
dockhand endpoint list
dockhand endpoint show srv01
dockhand endpoint default srv02
dockhand endpoint test srv01
dockhand endpoint remove srv02
```

The first endpoint you add becomes the default. Options for `endpoint add`:

| Option | Meaning |
|---|---|
| `-d, --description <text>` | Friendly description |
| `--default-env <id>` | Default Docker environment for this endpoint |
| `--request-timeout <seconds>` | Timeout for ordinary requests to this endpoint (default 100) |
| `--default` | Make it the default endpoint |
| `--allow-http` | Allow a plain `http://` URL (the token then travels unencrypted) |

A URL may include a path when Dockhand sits behind a reverse proxy, e.g. `https://tools.example.com/dockhand`.

Aliases are 2–32 characters (letter first; letters, digits, `-`, `_`, `.`), compared case-insensitively.
Names that would clash with options (`help`, `version`, `verbose`, `server`, `env`, `output`, `timeout`, …) are reserved.

### Choosing the endpoint for a command

These are equivalent:

```bash
dockhand -srv01 check updates
dockhand --server srv01 check updates
dockhand check updates --server srv01
```

Without a selection, the default endpoint is used (or the only one, if just one is registered).
`-<alias>` is only recognised for **registered** aliases of two or more characters, so `-h`, `-v`
and `-y` keep their usual meaning; `-unknown` is an error that lists your aliases. Giving two different
endpoints (e.g. `-srv01 --server srv02`) is an error.

### Where configuration is stored

| OS | Configuration directory |
|---|---|
| Windows | `%APPDATA%\DockhandCLI` |
| macOS | `~/Library/Application Support/DockhandCLI` |
| Linux | `$XDG_CONFIG_HOME/dockhand` (default `~/.config/dockhand`) |

Override it with the `DOCKHAND_CONFIG_DIR` environment variable. `config.json` holds aliases, URLs,
default environments, timeouts and a *reference* to where each token is stored, never a token.

## Authentication

Dockhand accepts user-scoped API tokens (`dh_…`) as `Authorization: Bearer` headers. Store one per endpoint:

```bash
dockhand endpoint auth srv01
```

You are asked to either **paste an existing token** (create one in Dockhand under *Profile → API tokens*)
or **sign in with username and password**, in which case `dockhand` logs in, creates a token named
`dockhand-cli@<machine>`, and logs out again. The password is never stored. MFA codes are supported.

Non-interactive forms:

```bash
# import a token
printf '%s' "$DOCKHAND_TOKEN" | dockhand endpoint auth srv01 --token-stdin

# sign in and create a token (password on stdin, optional expiry)
printf '%s\n' "$PASSWORD" | dockhand endpoint auth srv01 --login --username admin --password-stdin --expires 2027-12-31
```

Every token is verified against the server before it is stored. Tokens are stored in:

| OS | Store |
|---|---|
| Windows | Windows Credential Manager (`DockhandCLI:<alias>`) |
| macOS | login Keychain (service `DockhandCLI`) |
| Linux | Secret Service through `secret-tool` (service `DockhandCLI`) |
| any, if the above is unavailable | `secrets.json` in the configuration directory, mode `0600` (DPAPI-encrypted on Windows), with a warning |

**CI and containers:** set `DOCKHAND_TOKEN_<ALIAS>` (alias upper-cased, other characters as `_`), e.g.
`DOCKHAND_TOKEN_SRV01`. It overrides any stored token, so no keyring is needed.

`dockhand endpoint logout srv01` removes the stored token. Tokens stay valid on the server until you
revoke them in the Dockhand web UI.

Single sign-on (OIDC) and passkey logins are browser-only; users of those create a token in the web UI
and import it.

## Docker environments

```bash
dockhand -srv01 env list
dockhand -srv01 env show 1
dockhand endpoint set-env srv01 1          # or by name: hhdocker01
dockhand -srv01 --env 2 containers list    # one-off override, id or name
dockhand -srv01 --all-envs check updates   # update checks across all environments
```

The environment is chosen in this order: `--env`, the endpoint's saved default, the only environment
if exactly one exists. Otherwise the command stops and asks for `--env`; Dockhand has no
"default environment" of its own.

## Direct command-line mode

With arguments, `dockhand` runs one command and exits with a meaningful [exit code](#exit-codes).
Global options work before or after the command:

| Option | Meaning |
|---|---|
| `--server <alias>`, `-<alias>` | Endpoint to use |
| `--env <id\|name>` | Docker environment |
| `--all-envs` | All environments (`check updates` only) |
| `-o, --output table\|json` | Output format (default `table`) |
| `--timeout <time>` | Request timeout: `30`, `90s`, `5m`. Overrides everything, including long operations |
| `--verbose` | Diagnostic output (requests, timings) on stderr |
| `--no-color` | No colors or styling (also honoured: `NO_COLOR`) |
| `-h, --help` | Help for any command, e.g. `dockhand check updates --help` |
| `-v, --version` | Version |

Long operations (update checks, container updates, stack jobs) may take up to 30 minutes by default;
ordinary requests time out after 100 seconds or the endpoint's `--request-timeout`.

## Interactive shell

Running `dockhand` without a command starts a shell (`dockhand -srv02` starts it with srv02 active):

```text
╭────────────────────────────────────────────────────────────╮
│ ✻ Dockhand CLI v1.0.0                                      │
│                                                            │
│ endpoint: srv01                                            │
│                                                            │
│ / for commands · ↑↓ history · Tab completes · Ctrl+D exits │
╰────────────────────────────────────────────────────────────╯

dockhand [srv01] > /ch
❯ /check updates                          Check containers for image updates (--pending, --cached,…
  /check apply update docker <container>  Update a container to its latest image (--dry-run, --yes)

dockhand [srv01] > /command
dockhand [srv01] > endpoint list
dockhand [srv01] > use srv02
Active endpoint: srv02 (https://docker02.example.com; the saved default is unchanged)
dockhand [srv02] > /check updates
dockhand [srv02] > /containers list --env 2
dockhand [srv02] > -srv01 health
dockhand [srv02] > /check apply update docker nginx
dockhand [srv02] > /exit
```

- Typing `/` opens a command picker that filters as you type (`/logs` finds `containers logs`).
  Up/Down select, Tab completes, Enter runs the command (or fills it in when it still needs
  arguments), Esc closes the picker.
- Every command from direct mode works, with or without the leading `/`; they run the same code.
- `/command` (or `/help`) lists commands; `/command check updates` shows the detailed help.
- `use <alias>` switches the endpoint for this session only; `-<alias>` targets another endpoint for one command.
- `/history`, `/clear`, `/exit` or `/quit` (or Ctrl+D).
- Arguments can be quoted (`"two words"`, `'literal'`) and escaped (`\"`, `\ `).
- Up/Down browse history, Tab completes commands, aliases and options, Ctrl+C cancels the running
  command (or clears the line) without leaving the shell.
- Piping commands in works too: `printf 'env list\ncheck updates\n' | dockhand -srv01`.
  The shell then exits with the status of the last command.

## Docker update workflow

### Find a container and update it

After pushing a new image to your registry (under the tag the container already uses, e.g. `:latest`),
find the container and run the command `dockhand` prints for it:

```text
$ dockhand containers search myapp
╭─────────────────┬───────────┬──────────────────────────────────────┬───────────╮
│ Environment     │ Container │ Image                                │ State     │
├─────────────────┼───────────┼──────────────────────────────────────┼───────────┤
│ hhdocker01 (1)  │ myapp     │ registry.example.com/myapp:latest    │ ● running │
╰─────────────────┴───────────┴──────────────────────────────────────┴───────────╯
1 container(s) match 'myapp'.

Check for a new image and update (add --check-only to only check, --yes to skip the question):
  dockhand -srv01 --env hhdocker01 update container myapp
```

- `containers search <text>` (alias `containers find`) matches part of the container name **or image**,
  case-insensitively, in every environment of the endpoint. `--env` limits it to one environment;
  `--all-endpoints` searches every registered endpoint. In the shell, type `/search`.
- `update container <c>` runs a fresh update check of the container's environment (Dockhand has no
  single-container check, so the whole environment is checked), shows the result for that container,
  and updates it only when Dockhand reports a new image. It asks first unless `--yes` is given.
  - `--check-only` reports and stops; `--force` updates even when no new image is found.
  - It ends by printing the command line to use in scripts.

From a PowerShell script in your own solution, use [samples/Update-DockhandContainer.ps1](samples/Update-DockhandContainer.ps1):

```powershell
./Update-DockhandContainer.ps1 -Server srv01 -Environment hhdocker01 -Container myapp
./Update-DockhandContainer.ps1 -Server srv01 -Environment hhdocker01 -Container myapp -Image registry.example.com/myapp:latest  # docker push first
```

or call the CLI directly: `dockhand -srv01 --env hhdocker01 update container myapp --yes --output json`
returns `{ "updateAvailable": …, "updated": …, "check": {…}, "command": "…" }` and a non-zero exit code on failure.

### All update commands

```bash
dockhand -srv01 check updates                 # fresh check (POST /api/containers/check-updates)
dockhand -srv01 check updates --cached        # results of the last check, no new check
dockhand -srv01 check updates --pending       # pending records, including held updates
dockhand -srv01 check updates --updates-only  # hide up-to-date containers
dockhand -srv01 --all-envs check updates      # every environment, grouped by server and environment

dockhand -srv01 check apply update docker nginx --dry-run
dockhand -srv01 check apply update docker nginx           # asks for confirmation
dockhand -srv01 check apply update docker nginx --yes     # no question (automation)
dockhand -srv01 update apply nginx                        # short form, same code path
dockhand -srv01 update container nginx --yes              # check first; update only if there is a new image
```

Statuses: **update available** (a new image for the current tag), **held** (waiting for Dockhand's
minimum release age), **newer tag** (a higher version tag exists; advisory, never applied automatically),
**pinned digest moved**, **updates disabled** (label `dockhand.update=false`), **system container**
(Dockhand/Hawser, which update themselves), **error**.

Applying an update:

1. Resolves the container by **exact name, exact id, or an unambiguous id prefix of at least 12
   characters**. Anything else is refused with suggestions; ambiguous matches are never guessed.
2. Shows a plan: endpoint, environment, container, image, state and whether Dockhand recorded a pending update.
3. Asks for confirmation unless `--yes` is given. Without a terminal and without `--yes` it refuses.
   `--dry-run` stops before any change.
4. Calls `POST /api/containers/batch-update` with that one container id. Dockhand pulls the latest image
   for the container's tag and recreates it; Dockhand states that the container settings are preserved.
   The request is never retried automatically.
5. Reports success only when Dockhand's per-container result says so, including any pull warnings.

## JSON output and automation

`--output json` writes exactly one JSON document to stdout: no colors, spinners or banners. Messages and
errors go to stderr; failures leave stdout empty and return a non-zero exit code.

```bash
dockhand -srv01 --all-envs check updates --output json
```

```json
{
  "endpoint": "srv01",
  "mode": "fresh",
  "environments": [
    {
      "environmentId": 1,
      "environmentName": "hhdocker01",
      "total": 2,
      "updatesFound": 1,
      "containers": [
        {
          "containerId": "a1b2c3d4e5f6a7b8c9d0a1b2c3d4e5f6",
          "containerName": "nginx",
          "image": "nginx:latest",
          "status": "update-available",
          "currentDigest": "sha256:1b4f2c9e8a71…",
          "newDigest": "sha256:7d03e5a6c4b2…"
        }
      ]
    },
    {
      "environmentId": 2,
      "environmentName": "hhdocker02",
      "containers": [],
      "error": "Could not connect to 'srv01' (https://docker01.example.com): …"
    }
  ]
}
```

A nightly job that updates everything with a pending update:

```bash
export DOCKHAND_TOKEN_SRV01="$SECRET_FROM_CI"
dockhand endpoint add srv01 https://docker01.example.com --default-env 1
dockhand -srv01 check updates -o json \
  | jq -r '.environments[].containers[] | select(.status=="update-available") | .containerName' \
  | while read -r name; do dockhand -srv01 update apply "$name" --yes; done
```

`dockhand -srv01 containers list -o json`:

```json
[
  { "id": "a1b2c3d4e5f6…", "name": "nginx", "image": "nginx:latest", "state": "running", "status": "Up 2 hours" }
]
```

## Command reference

| Command | Description |
|---|---|
| `endpoint add <alias> <url>` | Register an installation (`-d`, `--default-env`, `--request-timeout`, `--default`, `--allow-http`) |
| `endpoint list` | List endpoints (★ marks the default) |
| `endpoint show <alias>` | Details including the masked token and where it is stored |
| `endpoint remove <alias>` | Remove an endpoint and its stored token (`-y`) |
| `endpoint default <alias>` | Set the default endpoint |
| `endpoint set-env <alias> <env>` | Save the default environment (`--clear`, `--no-verify`) |
| `endpoint test [alias]` | Reachability, authentication and environments |
| `endpoint auth <alias>` | Store a token (`--token-stdin`, `--login`, `-u`, `--password-stdin`, `--mfa-code`, `--token-name`, `--expires`) |
| `endpoint logout <alias>` | Remove the stored token |
| `env list` / `env show <env>` | Docker environments of the endpoint |
| `containers list` | Containers (`--running`) |
| `containers search <text>` | Find containers by name or image in every environment, with the command to update each (`--all-endpoints`) |
| `containers inspect <c>` | Raw Docker inspect JSON |
| `containers logs <c>` | Logs (`-n/--tail`, `--since`, `--until`) |
| `containers start\|stop\|restart\|pause\|unpause <c>` | Lifecycle; stop, restart and pause ask for confirmation (`-y`) |
| `check updates` | Update check (`--pending`, `--cached`, `--updates-only`, `--all-envs`) |
| `check apply update docker <c>` | Apply an update (`--dry-run`, `-y`) |
| `update apply <c>` | Same as above |
| `update container <c>` | Check one container and update it if there is a new image (`--check-only`, `--force`, `-y`) |
| `images list` | Images with tags, size, creation date |
| `stacks list` / `stacks show <s>` | Compose stacks; `show` prints the compose file |
| `stacks start <s>` | Start a stack |
| `stacks stop\|restart\|deploy\|down <s>` | Confirmed actions (`-y`; deploy: `--pull --build --force-recreate`; restart: `--mode restart\|ordered\|recreate`; down: `--remove-volumes`) |
| `jobs show <id>` / `jobs cancel <id>` | Inspect or cancel a background job |
| `health` | Service and database health (no token needed) |
| `system` | Host, runtime, database and Docker information (`--env` adds Docker details) |

Every command accepts the [global options](#direct-command-line-mode) and `--help`.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Unexpected error, or unreadable configuration |
| 2 | Usage error: bad arguments, unknown alias, confirmation needed (`--yes`), ambiguous name |
| 3 | Not found: endpoint, environment, container, stack |
| 4 | Authentication or permission failure (HTTP 401/403) |
| 5 | API error, or the operation was reported as failed |
| 6 | Partial failure: some environments in `--all-envs` failed |
| 7 | Network error or timeout |
| 8 | Aborted at the confirmation prompt |
| 130 | Cancelled with Ctrl+C |

## Security

- TLS certificates are always validated. There is no option to skip validation; trust a private CA
  through your OS certificate store.
- `http://` endpoints need an explicit `--allow-http`.
- Tokens live in the OS credential store (or an owner-only fallback file), never in `config.json`,
  never on the command line, and are passed to keyring helpers on stdin, not as arguments.
- Tokens are masked everywhere they are shown (`dh_…x9Qa`); log and error text is scrubbed of anything
  token-shaped. HTTP headers are never logged, even with `--verbose`.
- Redirects are not followed, so a token is never forwarded to another host.
- Configuration and secret files are written atomically (temporary file + rename) under a lock, with
  mode `0600` (directory `0700`) on Linux and macOS. A failed request never rewrites the configuration.
- Mutating commands confirm first; `--dry-run` on updates sends no mutating request; failed updates are
  not retried. Dockhand blocks an IP for 5 minutes after 10 failed token attempts, so `dockhand` never
  retries a 401.

## Troubleshooting

| Symptom | What to do |
|---|---|
| `requires authentication and no API token is configured` | `dockhand endpoint auth <alias>` |
| `rejected the credentials (HTTP 401)` | The token was revoked or expired; run `endpoint auth` again |
| `rate limiting this client (HTTP 429)` | Too many failed token attempts from your IP; wait 5 minutes |
| `has N environments … Choose one with --env` | `dockhand endpoint set-env <alias> <id>` |
| `TLS connection … failed` | Install the server's CA certificate in the OS trust store; check the hostname matches the certificate |
| `redirected … Update the endpoint URL` | Use the final URL (often `https://` instead of `http://`, or a trailing path) |
| `returned an unexpected response (content type text/html)` | The URL points to a login page or proxy, not Dockhand's API |
| `No OS keyring is available` (Linux) | Install `secret-tool`, or use `DOCKHAND_TOKEN_<ALIAS>`; otherwise the 0600 file is used |
| `needs confirmation, but no interactive terminal` | Add `--yes` in scripts |
| Anything else | Re-run with `--verbose` to see each request and its timing on stderr |

## Project layout

```text
DockhandCLI.slnx
├── src/
│   ├── DockhandCLI/                  # executable "dockhand": commands, shell, presentation, hosting
│   ├── DockhandCLI.Core/             # models, interfaces, services (parsing, resolution, updates)
│   └── DockhandCLI.Infrastructure/   # Dockhand API client, configuration and secret storage
├── tests/
│   ├── DockhandCLI.UnitTests/
│   └── DockhandCLI.IntegrationTests/ # full CLI runs against a mocked HttpMessageHandler
├── docs/                             # requirements, API analysis, OpenAPI snapshot
├── samples/                          # Update-DockhandContainer.ps1 for use from other solutions
└── .github/workflows/                # CI and release
```

## License

[MIT](LICENSE)
