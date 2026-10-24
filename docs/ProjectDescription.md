# Project: DockCLI — .NET 10 CLI for Dockerhand API

You are a senior .NET architect and C# developer.

Your task is to design, implement, test, and document a production-ready command-line application named **DockCLI**, targeting **.NET 10**.

DockCLI is a cross-platform CLI for managing multiple Dockerhand API installations through their REST APIs.

**API documentation:**
https://dockermanager.beluggaservices.com/api/docs

## 1. Primary objectives

Build a modern CLI that supports:

- Executing commands directly from a terminal with arguments.
- Running an interactive shell with commands prefixed by `/`.
- Managing multiple Dockerhand API endpoints using friendly aliases.
- Selecting an API endpoint using shorthand syntax such as `-srv01`.
- Managing authentication independently for each endpoint.
- Discovering Docker environments and executing operations against them.
- Checking Docker container image updates and applying updates.
- Displaying attractive terminal output using Spectre.Console.
- Providing comprehensive built-in command help.
- Supporting automation and CI/CD through noninteractive execution.

The application must work on Windows, Linux, and macOS.

## 2. Technology requirements

Use:

- .NET 10
- C# with nullable reference types enabled
- Spectre.Console
- Spectre.Console.Cli
- Microsoft.Extensions.DependencyInjection
- Microsoft.Extensions.Http
- Microsoft.Extensions.Configuration
- Microsoft.Extensions.Logging
- System.Text.Json
- xUnit for automated testing

Use async/await for all network operations and support CancellationToken.

Prefer typed services, dependency injection, and testable abstractions.

Avoid unnecessary dependencies and excessive architectural complexity.

## 3. API discovery — mandatory first step

Before implementing API operations:

1. Retrieve the OpenAPI specification from the supplied documentation URL.
2. Identify the actual OpenAPI JSON endpoint if the URL serves an interactive documentation page.
3. Inspect authentication schemes, operations, schemas, parameters, response types, and error formats.
4. Identify operations for:
   - Authentication and API tokens
   - Docker environments
   - Containers
   - Container update checks
   - Pending updates
   - Applying container updates
   - Container lifecycle operations
   - Images
   - Stacks
   - Jobs
   - Health and server status
5. Generate a typed API client from OpenAPI where practical, or implement a typed HttpClient wrapper.
6. Keep generated code separate from application logic.

**Do not invent API endpoints, request bodies, or response properties.**

If an operation is missing or unclear, document the limitation rather than fabricating an implementation.

Known update-check routes to verify against the live specification:

- `POST /api/containers/check-updates?env=1`
- `GET /api/containers/check-updates?env=1`
- `GET /api/containers/pending-updates?env=1`

Determine the exact API operation required to apply a Docker container update.

## 4. Endpoint management

Users must be able to register multiple Dockerhand API installations.

Required commands:

```bash
dockcli endpoint add srv01 https://docker01.example.com
dockcli endpoint add srv02 https://docker02.example.com

dockcli endpoint list
dockcli endpoint show srv01
dockcli endpoint remove srv01
dockcli endpoint default srv01
dockcli endpoint test srv01
dockcli endpoint auth srv01
```

Endpoint configuration should support:

- Alias
- Base URL
- Default environment
- Authentication credential reference
- Optional request timeout
- Optional friendly description

Store nonsecret configuration in a user-specific JSON configuration file.

Use appropriate operating-system configuration directories.

Store tokens securely using an OS credential store or another suitable secure mechanism. Do not store credentials as plaintext in configuration files.

Support importing an existing API token and use Bearer authentication where specified by the API.

Never display complete tokens in logs, exception messages, or normal terminal output.

## 5. Endpoint selection syntax

Support these equivalent commands:

```bash
dockcli -srv01 check updates
dockcli --server srv01 check updates
dockcli check updates --server srv01
```

When no server is specified, use the configured default endpoint.

The shorthand `-srv01` must be resolved against registered endpoint aliases.

Do not interpret unrelated command-line options such as `-h` or `-v` as endpoint aliases.

Handle missing, invalid, and ambiguous aliases gracefully.

The server-selection logic must be reusable by both direct execution and interactive mode.

## 6. Docker environment support

A Dockerhand API installation may manage multiple Docker environments.

Do not confuse a saved API endpoint with a Docker environment.

Support:

```bash
dockcli -srv01 env list
dockcli -srv01 env show 1
dockcli endpoint set-env srv01 1
dockcli -srv01 --env 1 containers list
dockcli -srv01 --all-envs check updates
```

Use the API's actual environment identifiers and schemas.

For normal commands, use the endpoint's configured default environment when one exists.

If no environment is configured, resolve an unambiguous API default if documented; otherwise require explicit selection.

For `check updates`, support checking all environments through `--all-envs`.

When checking multiple environments, display results grouped by server and environment.

## 7. Docker update commands

Support the following command structure:

```bash
dockcli -srv01 check updates

dockcli -srv01 check updates --pending

dockcli -srv01 check updates --cached

dockcli -srv01 --env 1 check updates

dockcli -srv01 check apply update docker nginx

dockcli -srv01 check apply update docker nginx --dry-run

dockcli -srv01 check apply update docker nginx --yes
```

The exact syntax `check apply update docker <container>` is required for compatibility with the intended workflow.

You may additionally provide a shorter alias such as:

```bash
dockcli -srv01 update apply nginx
```

Both commands must invoke the same underlying application service.

### Update checking

- Invoke the actual Dockerhand update-check API.
- Display container name, environment, current image, available image, and update status when the API provides those values.
- Support cached results where the API provides them.
- Support pending updates.
- Support progress reporting when Server-Sent Events are available.
- Handle API errors and partial failures gracefully.

### Applying updates

- Verify the exact documented update API operation.
- Resolve container identifiers safely.
- Never guess a container ID when names are ambiguous.
- Display the intended action before executing.
- Require interactive confirmation unless `--yes` is supplied.
- Implement `--dry-run` without performing a mutating API request.
- Report the operation's actual outcome.
- Support cancellation and appropriate timeouts.
- Do not automatically retry non-idempotent update operations.

Do not assume that an image update preserves volumes, networking, or container configuration unless the API explicitly guarantees this behavior.

## 8. Interactive shell

Running the application without arguments must launch an interactive shell.

Example:

```text
Dockerhand CLI v1.0

Default endpoint: srv01
Type /command for help.

dockcli [srv01] > /command
dockcli [srv01] > endpoint list
dockcli [srv01] > use srv02

Active endpoint: srv02

dockcli [srv02] > /check updates
dockcli [srv02] > /containers list
dockcli [srv02] > /check apply update docker nginx
dockcli [srv02] > /exit
```

Required shell capabilities:

- `/command` displays available commands.
- `/help` is an alias for help.
- `/command <name>` displays detailed command help.
- `/exit` and `/quit` exit the shell.
- `use <alias>` changes the active endpoint for the session.
- Commands may be entered with or without the leading `/`.
- Support quoted arguments and escaped characters.
- Support command history using arrow keys if practical.
- Support command completion where practical.
- Handle Ctrl+C without corrupting the terminal.
- Reuse the same command handlers as direct CLI execution.

Changing the active endpoint in the interactive shell must not silently modify the saved default endpoint.

## 9. Direct command-line mode

Support:

```bash
dockcli --help
dockcli --version
dockcli endpoint list
dockcli -srv01 check updates
dockcli -srv01 containers list
```

When arguments are provided, execute the requested command and terminate with an appropriate process exit code.

Do not start the interactive shell when a direct command was supplied.

Support the following global options:

- `--server`
- `--env`
- `--output`
- `--timeout`
- `--verbose`
- `--no-color`
- `--help`
- `--version`

Use conventional CLI exit codes, with zero indicating success and nonzero indicating failure.

For automation, ensure machine-readable output goes to stdout and diagnostics go to stderr.

## 10. Output formatting

Use Spectre.Console for interactive terminal presentation.

Support:

```bash
dockcli -srv01 containers list
dockcli -srv01 containers list --output json
dockcli -srv01 containers list --output table
```

Default to readable tables when appropriate.

Support:

- Tables
- Status indicators
- Progress bars
- Spinners
- Colored messages
- Helpful error output

JSON output must be valid JSON without ANSI escape sequences, banners, spinners, or decorative text.

Respect redirected output and terminals without color support.

## 11. Proposed solution structure

```text
DockCLI.sln
│
├── src/
│   ├── DockCLI/
│   │   ├── Program.cs
│   │   ├── Commands/
│   │   ├── Shell/
│   │   └── Presentation/
│   │
│   ├── DockCLI.Core/
│   │   ├── Models/
│   │   ├── Interfaces/
│   │   └── Services/
│   │
│   └── DockCLI.Infrastructure/
│       ├── Api/
│       ├── Configuration/
│       ├── Authentication/
│       └── Persistence/
│
├── tests/
│   ├── DockCLI.UnitTests/
│   └── DockCLI.IntegrationTests/
│
├── README.md
└── global.json
```

You may simplify this structure if a smaller architecture is easier to maintain without sacrificing testability.

## 12. Security and reliability

Implement:

- HTTPS certificate validation enabled by default.
- Secure API token handling.
- Token redaction in diagnostic output.
- Configurable request timeouts.
- CancellationToken propagation.
- Meaningful HTTP error handling.
- Validation of endpoint URLs.
- Protection against accidental destructive operations.
- No automatic insecure TLS bypass.
- No logging of sensitive authorization headers.
- Safe configuration writes to avoid corruption.
- Appropriate file permissions for local configuration.

Ensure that a failed request against one server does not corrupt another server's configuration.

## 13. Automated tests

Create unit tests covering:

- Endpoint registration and removal
- Duplicate aliases
- Default endpoint resolution
- `-srv01` shorthand parsing
- `--server` option parsing
- Invalid endpoint aliases
- Environment selection
- Interactive command tokenization
- Command dispatch
- API error handling
- Authentication header injection
- JSON output
- Update dry-run behavior
- Update confirmation requirements
- Exit codes

Use mocked HttpMessageHandler instances for API integration tests.

Do not execute destructive operations against live Dockerhand servers during automated tests.

## 14. Packaging and distribution

Configure publishing so the application can be distributed as a standalone executable.

Desired usage:

```bash
dockcli -srv01 check updates
```

Users should not need to type `dotnet DockCLI.dll`.

Provide publish instructions for:

- Windows x64
- Linux x64
- Linux ARM64
- macOS ARM64

Use .NET 10 self-contained publishing where appropriate.

Document installation and PATH configuration.

## 15. Documentation

Create a README containing:

- Project overview
- Requirements
- Build instructions
- Installation instructions
- Endpoint configuration
- Authentication setup
- Environment selection
- Interactive mode
- Direct command-line mode
- Docker update workflows
- JSON output examples
- Security considerations
- Troubleshooting
- Complete command reference

Include realistic examples using fictional hostnames and tokens.

## 16. Development workflow

Follow this implementation order:

**Phase 1 — API inspection**
- Retrieve and inspect OpenAPI.
- Document the available operations.
- Identify the exact update workflow.
- Record any unsupported operations.

**Phase 2 — CLI foundation**
- Create the .NET 10 solution.
- Configure dependency injection.
- Implement command registration.
- Implement endpoint configuration storage.
- Implement endpoint alias parsing.

**Phase 3 — API integration**
- Implement typed HTTP client.
- Implement authentication.
- Implement environment discovery.
- Implement connectivity tests.

**Phase 4 — Docker operations**
- Implement container listing.
- Implement update checking.
- Implement pending updates.
- Implement update application if supported by the API.

**Phase 5 — Interactive shell**
- Implement `/command` help.
- Implement endpoint switching.
- Implement shared command dispatch.
- Implement command history and completion where practical.

**Phase 6 — Quality and delivery**
- Add automated tests.
- Build in Release configuration.
- Run all tests.
- Fix build warnings and test failures.
- Document publishing and usage.

## 17. Acceptance criteria

The implementation is complete when:

1. `dotnet build` succeeds.
2. `dotnet test` succeeds.
3. The executable supports interactive and direct command execution.
4. Multiple Dockerhand API endpoints can be registered and removed.
5. `dockcli -srv01` correctly resolves a saved endpoint.
6. Authentication is handled securely.
7. Docker environments can be selected.
8. Update checks call the documented Dockerhand API.
9. Update application uses only verified API operations.
10. `/command` and `--help` display useful documentation.
11. JSON output works for scripting.
12. Noninteractive commands return meaningful exit codes.
13. The README contains complete setup and usage instructions.
14. The CLI can be published as a standalone executable.

## 18. Important implementation instructions

Do not stop after generating project scaffolding.

Implement functional code, compile it, run tests, and resolve failures.

Prefer working functionality over unnecessary abstractions.

Do not invent Dockerhand API capabilities.

Do not report an operation as successful unless the API confirms it.

If the API documentation cannot be accessed, clearly report that blocker and do not fabricate API contracts.

Do not require live server credentials to complete unit tests or build the application.

If an operation cannot be implemented because the API does not expose it, clearly mark it as unsupported and explain what API capability is missing.

At completion, provide:

- A summary of implemented functionality
- The project structure
- Build and test results
- Commands to run the CLI
- Any API limitations discovered
- Remaining work, if any

**Begin by inspecting the actual Dockerhand OpenAPI specification, then implement the complete DockCLI application.**