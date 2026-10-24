using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using DockhandCLI.Core.Interfaces;
using DockhandCLI.Core.Models;
using DockhandCLI.Core.Services;
using DockhandCLI.Presentation;
using Spectre.Console;
using Spectre.Console.Cli;

namespace DockhandCLI.Commands;

internal class AliasSettings : GlobalSettings
{
    [CommandArgument(0, "<ALIAS>")]
    [Description("Endpoint alias")]
    public string Alias { get; init; } = "";
}

/// <summary>Endpoint as shown by list/show. Never contains a token.</summary>
internal sealed record EndpointView(
    string Alias,
    string BaseUrl,
    bool IsDefault,
    int? DefaultEnvironment,
    int? TimeoutSeconds,
    string? Description,
    string? Credential)
{
    public static EndpointView From(EndpointConfig endpoint, CliConfiguration config, ICredentialManager credentials) => new(
        endpoint.Alias,
        endpoint.BaseUrl,
        string.Equals(config.DefaultEndpoint, endpoint.Alias, StringComparison.OrdinalIgnoreCase),
        endpoint.DefaultEnvironment,
        endpoint.TimeoutSeconds,
        endpoint.Description,
        credentials.DescribeCredential(endpoint));
}

// ---- endpoint add ---------------------------------------------------------------------------

internal sealed class EndpointAddSettings : AliasSettings
{
    [CommandArgument(1, "<URL>")]
    [Description("Base URL of the Dockhand installation, e.g. https://docker01.example.com")]
    public string Url { get; init; } = "";

    [CommandOption("-d|--description <TEXT>")]
    [Description("Friendly description")]
    public string? Description { get; init; }

    [CommandOption("--default-env <ID>")]
    [Description("Default Docker environment id for this endpoint")]
    public int? DefaultEnvironment { get; init; }

    [CommandOption("--request-timeout <SECONDS>")]
    [Description("Request timeout for this endpoint (default 100)")]
    public int? RequestTimeout { get; init; }

    [CommandOption("--default")]
    [Description("Make this the default endpoint")]
    public bool MakeDefault { get; init; }

    [CommandOption("--allow-http")]
    [Description("Allow a plain http:// URL (the token is then sent unencrypted)")]
    public bool AllowHttp { get; init; }
}

internal sealed class EndpointAddCommand(CommandServices services) : DockhandCommand<EndpointAddSettings>(services)
{
    protected override async Task<int> RunAsync(EndpointAddSettings settings, CancellationToken cancellationToken)
    {
        var endpoint = await Services.Endpoints.AddAsync(
            new NewEndpoint(settings.Alias, settings.Url, settings.AllowHttp, settings.Description, settings.DefaultEnvironment, settings.RequestTimeout, settings.MakeDefault),
            cancellationToken);
        var config = await Services.Store.LoadAsync(cancellationToken);

        if (Output.IsJson)
        {
            Output.Json(EndpointView.From(endpoint, config, Services.Credentials));
            return ExitCodes.Success;
        }

        if (endpoint.BaseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            Output.Warning("This endpoint uses plain http://; the API token will be sent unencrypted.");
        }

        var isDefault = string.Equals(config.DefaultEndpoint, endpoint.Alias, StringComparison.OrdinalIgnoreCase);
        Output.Success($"Added endpoint '{endpoint.Alias}' ({endpoint.BaseUrl}){(isDefault ? " as the default" : "")}.");
        Output.Info($"Next: dockhand endpoint auth {endpoint.Alias}");
        return ExitCodes.Success;
    }
}

// ---- endpoint list / show ---------------------------------------------------------------------

internal sealed class EndpointListCommand(CommandServices services) : DockhandCommand<GlobalSettings>(services)
{
    protected override async Task<int> RunAsync(GlobalSettings settings, CancellationToken cancellationToken)
    {
        var config = await Services.Store.LoadAsync(cancellationToken);
        var views = config.Endpoints
            .OrderBy(e => e.Alias, StringComparer.OrdinalIgnoreCase)
            .Select(e => EndpointView.From(e, config, Services.Credentials))
            .ToList();

        if (Output.IsJson)
        {
            Output.Json(views);
            return ExitCodes.Success;
        }

        if (views.Count == 0)
        {
            Output.Info("No endpoints registered. Add one with: dockhand endpoint add <alias> <url>");
            return ExitCodes.Success;
        }

        var table = Styles.NewTable("", "Alias", "URL", "Env", "Credential", "Description");
        foreach (var view in views)
        {
            table.AddRow(
                view.IsDefault ? "[yellow]★[/]" : "",
                $"[bold]{Styles.Escape(view.Alias)}[/]",
                Styles.Escape(view.BaseUrl),
                view.DefaultEnvironment?.ToString(CultureInfo.InvariantCulture) ?? "[grey]-[/]",
                view.Credential is null ? "[red]none[/]" : Styles.Escape(view.Credential),
                Styles.Dim(view.Description));
        }

        Output.Write(table);
        Output.Info("★ = default endpoint. Select another with -<alias> or --server <alias>.");
        return ExitCodes.Success;
    }
}

internal sealed class EndpointShowCommand(CommandServices services) : DockhandCommand<AliasSettings>(services)
{
    protected override async Task<int> RunAsync(AliasSettings settings, CancellationToken cancellationToken)
    {
        var config = await Services.Store.LoadAsync(cancellationToken);
        var endpoint = EndpointService.Require(config, settings.Alias);
        var view = EndpointView.From(endpoint, config, Services.Credentials);
        var token = await Services.Credentials.GetTokenAsync(endpoint, cancellationToken);

        if (Output.IsJson)
        {
            Output.Json(new { endpoint = view, token = token is null ? null : SecretRedactor.Mask(token.Token) });
            return ExitCodes.Success;
        }

        var grid = new Grid().AddColumn(new GridColumn().NoWrap()).AddColumn();
        grid.AddRow("[grey]Alias[/]", $"[bold]{Styles.Escape(view.Alias)}[/]{(view.IsDefault ? " [yellow](default)[/]" : "")}");
        grid.AddRow("[grey]URL[/]", Styles.Escape(view.BaseUrl));
        grid.AddRow("[grey]Description[/]", Styles.Dim(view.Description));
        grid.AddRow("[grey]Default environment[/]", view.DefaultEnvironment?.ToString(CultureInfo.InvariantCulture) ?? "[grey]not set[/]");
        grid.AddRow("[grey]Request timeout[/]", view.TimeoutSeconds is { } t ? $"{t} s" : "[grey]default (100 s)[/]");
        grid.AddRow("[grey]Token[/]", token is null
            ? "[red]not configured[/] [grey](dockhand endpoint auth " + Styles.Escape(view.Alias) + ")[/]"
            : $"{Styles.Escape(SecretRedactor.Mask(token.Token))} [grey]from {Styles.Escape(token.Source)}[/]");
        Output.Write(new Panel(grid).Header($"Endpoint {Styles.Escape(view.Alias)}").BorderColor(Color.Grey));
        return ExitCodes.Success;
    }
}

// ---- endpoint remove / default --------------------------------------------------------------

internal sealed class EndpointRemoveSettings : AliasSettings
{
    [CommandOption("-y|--yes")]
    [Description("Do not ask for confirmation")]
    public bool Yes { get; init; }
}

internal sealed class EndpointRemoveCommand(CommandServices services) : DockhandCommand<EndpointRemoveSettings>(services)
{
    protected override async Task<int> RunAsync(EndpointRemoveSettings settings, CancellationToken cancellationToken)
    {
        var config = await Services.Store.LoadAsync(cancellationToken);
        var endpoint = EndpointService.Require(config, settings.Alias);

        await Confirmation.EnsureAsync(Services.Prompt, settings.Yes,
            $"Remove endpoint '{endpoint.Alias}' ({endpoint.BaseUrl}) and its stored token?", cancellationToken);

        var removed = await Services.Endpoints.RemoveAsync(endpoint.Alias, cancellationToken);
        if (Output.IsJson)
        {
            Output.Json(new { removed = removed.Alias });
        }
        else
        {
            Output.Success($"Removed endpoint '{removed.Alias}'. The token stays valid on the server until you revoke it there.");
        }

        return ExitCodes.Success;
    }
}

internal sealed class EndpointDefaultCommand(CommandServices services) : DockhandCommand<AliasSettings>(services)
{
    protected override async Task<int> RunAsync(AliasSettings settings, CancellationToken cancellationToken)
    {
        var endpoint = await Services.Endpoints.SetDefaultAsync(settings.Alias, cancellationToken);
        if (Output.IsJson)
        {
            Output.Json(new { defaultEndpoint = endpoint.Alias });
        }
        else
        {
            Output.Success($"'{endpoint.Alias}' is now the default endpoint.");
        }

        return ExitCodes.Success;
    }
}

// ---- endpoint set-env -------------------------------------------------------------------------

internal sealed class EndpointSetEnvSettings : AliasSettings
{
    [CommandArgument(1, "[ENVIRONMENT]")]
    [Description("Environment id or name (from 'dockhand env list')")]
    public string? Environment { get; init; }

    [CommandOption("--clear")]
    [Description("Remove the saved default environment")]
    public bool Clear { get; init; }

    [CommandOption("--no-verify")]
    [Description("Save a numeric id without checking it against the API")]
    public bool NoVerify { get; init; }
}

internal sealed class EndpointSetEnvCommand(CommandServices services) : DockhandCommand<EndpointSetEnvSettings>(services)
{
    protected override async Task<int> RunAsync(EndpointSetEnvSettings settings, CancellationToken cancellationToken)
    {
        var config = await Services.Store.LoadAsync(cancellationToken);
        var endpoint = EndpointService.Require(config, settings.Alias);

        if (settings.Clear == (settings.Environment is not null))
        {
            throw DockhandException.Usage("Specify an environment id or name, or --clear.");
        }

        int? id = null;
        string? name = null;
        if (settings.Environment is { } selector)
        {
            if (settings.NoVerify)
            {
                id = int.TryParse(selector, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : throw DockhandException.Usage("--no-verify requires a numeric environment id.");
            }
            else
            {
                var api = await Services.ApiFactory.CreateAsync(endpoint, cancellationToken);
                var environments = await Output.StatusAsync("Loading environments…", () => api.GetEnvironmentsAsync(cancellationToken));
                var environment = EnvironmentResolver.Match(environments, selector, endpoint.Alias);
                (id, name) = (environment.Id, environment.Name);
            }
        }

        await Services.Endpoints.SetDefaultEnvironmentAsync(endpoint.Alias, id, cancellationToken);

        if (Output.IsJson)
        {
            Output.Json(new { endpoint = endpoint.Alias, defaultEnvironment = id });
        }
        else if (id is null)
        {
            Output.Success($"Cleared the default environment of '{endpoint.Alias}'.");
        }
        else
        {
            Output.Success($"Default environment of '{endpoint.Alias}' is now {id}{(name is null ? "" : $" ({name})")}.");
        }

        return ExitCodes.Success;
    }
}

// ---- endpoint test ----------------------------------------------------------------------------

internal sealed class OptionalAliasSettings : GlobalSettings
{
    [CommandArgument(0, "[ALIAS]")]
    [Description("Endpoint alias (default: the selected endpoint)")]
    public string? Alias { get; init; }
}

internal sealed record EndpointCheck(string Name, bool Ok, string Detail);

internal sealed class EndpointTestCommand(CommandServices services) : DockhandCommand<OptionalAliasSettings>(services)
{
    protected override async Task<int> RunAsync(OptionalAliasSettings settings, CancellationToken cancellationToken)
    {
        var config = await Services.Store.LoadAsync(cancellationToken);
        var endpoint = settings.Alias is not null
            ? EndpointService.Require(config, settings.Alias)
            : EndpointResolver.Resolve(config, Options.Server, Context.SessionEndpoint);
        var api = await Services.ApiFactory.CreateAsync(endpoint, cancellationToken);

        var checks = new List<EndpointCheck>();
        var exitCode = await Output.StatusAsync($"Testing {endpoint.Alias}…", () => RunChecksAsync(api, checks, cancellationToken));

        if (Output.IsJson)
        {
            Output.Json(new { endpoint = endpoint.Alias, baseUrl = endpoint.BaseUrl, success = exitCode == 0, checks });
            return exitCode;
        }

        var table = Styles.NewTable("", "Check", "Result");
        foreach (var check in checks)
        {
            table.AddRow(Styles.Check(check.Ok), Styles.Escape(check.Name), Styles.Escape(check.Detail));
        }

        Output.MarkupLine($"[bold]{Styles.Escape(endpoint.Alias)}[/] [grey]{Styles.Escape(endpoint.BaseUrl)}[/]");
        Output.Write(table);
        return exitCode;
    }

    private static async Task<int> RunChecksAsync(IDockhandApi api, List<EndpointCheck> checks, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var health = await api.GetHealthAsync(cancellationToken);
            checks.Add(new("Reachable", true, $"GET /api/health: {health.Status} ({stopwatch.ElapsedMilliseconds} ms)"));
        }
        catch (DockhandException ex)
        {
            checks.Add(new("Reachable", false, ex.Message));
            return ex.ExitCode;
        }

        SessionInfo session;
        try
        {
            session = await api.GetSessionAsync(cancellationToken);
        }
        catch (DockhandException ex)
        {
            checks.Add(new("Authentication", false, ex.Message));
            return ex.ExitCode;
        }

        if (!session.AuthEnabled)
        {
            checks.Add(new("Authentication", true, "Authentication is disabled on this server"));
        }
        else if (!api.HasCredentials)
        {
            checks.Add(new("Authentication", false, $"No API token configured. Run 'dockhand endpoint auth {api.Endpoint.Alias}'."));
            return ExitCodes.Unauthorized;
        }

        try
        {
            var environments = await api.GetEnvironmentsAsync(cancellationToken);
            if (session.AuthEnabled)
            {
                checks.Add(new("Authentication", true, "API token accepted"));
            }

            checks.Add(new("Environments", true, $"{environments.Count} accessible: {string.Join(", ", environments.Select(e => $"{e.Id}={e.Name}"))}"));
            return ExitCodes.Success;
        }
        catch (DockhandException ex)
        {
            checks.Add(new(ex.ExitCode == ExitCodes.Unauthorized ? "Authentication" : "Environments", false, ex.Message));
            return ex.ExitCode;
        }
    }
}

// ---- endpoint auth / logout -------------------------------------------------------------------

internal sealed class EndpointAuthSettings : AliasSettings
{
    [CommandOption("--token-stdin")]
    [Description("Read an existing API token from stdin")]
    public bool TokenStdin { get; init; }

    [CommandOption("--login")]
    [Description("Sign in with username and password and create a new API token")]
    public bool Login { get; init; }

    [CommandOption("-u|--username <USERNAME>")]
    [Description("Username for --login")]
    public string? Username { get; init; }

    [CommandOption("--password-stdin")]
    [Description("Read the password for --login from stdin")]
    public bool PasswordStdin { get; init; }

    [CommandOption("--mfa-code <CODE>")]
    [Description("One-time MFA code, if the account requires one")]
    public string? MfaCode { get; init; }

    [CommandOption("--token-name <NAME>")]
    [Description("Name of the token created by --login (default: dockhand-cli@<machine>)")]
    public string? TokenName { get; init; }

    [CommandOption("--expires <DATE>")]
    [Description("Expiry of the token created by --login, e.g. 2027-12-31")]
    public string? Expires { get; init; }
}

internal sealed class EndpointAuthCommand(CommandServices services) : DockhandCommand<EndpointAuthSettings>(services)
{
    private const string PasteToken = "Paste an existing API token";
    private const string LoginChoice = "Sign in with username and password (creates a new token)";

    protected override async Task<int> RunAsync(EndpointAuthSettings settings, CancellationToken cancellationToken)
    {
        var config = await Services.Store.LoadAsync(cancellationToken);
        var endpoint = EndpointService.Require(config, settings.Alias);
        var prompt = Services.Prompt;

        if (settings.TokenStdin && (settings.Login || settings.PasswordStdin))
        {
            throw DockhandException.Usage("--token-stdin cannot be combined with --login or --password-stdin.");
        }

        var useLogin = settings.Login || settings.Username is not null || settings.PasswordStdin;
        if (!settings.TokenStdin && !useLogin)
        {
            if (!prompt.CanPrompt)
            {
                throw DockhandException.Usage("No terminal to ask for credentials. Use --token-stdin, or --login --username <name> --password-stdin.");
            }

            useLogin = await prompt.SelectAsync($"How do you want to authenticate to {endpoint.Alias}?", [PasteToken, LoginChoice], cancellationToken) == LoginChoice;
        }

        string token;
        string? username = null;
        if (useLogin)
        {
            (token, username) = await LoginAndCreateTokenAsync(endpoint, settings, cancellationToken);
        }
        else
        {
            token = settings.TokenStdin
                ? (await Context.Console.Input.ReadToEndAsync(cancellationToken)).Trim()
                : (await prompt.AskSecretAsync($"API token for {endpoint.Alias}:", cancellationToken)).Trim();

            if (!TokenFormat.IsValid(token))
            {
                throw DockhandException.Usage("That does not look like a Dockhand API token (expected 'dh_' followed by letters, digits, '-' or '_').");
            }
        }

        var authEnabled = await Output.StatusAsync("Verifying token…", () => VerifyTokenAsync(endpoint, token, cancellationToken));
        var reference = await Services.Endpoints.StoreTokenAsync(endpoint.Alias, token, cancellationToken);
        var stored = await Services.Store.LoadAsync(cancellationToken);
        var location = Services.Credentials.DescribeCredential(EndpointService.Require(stored, endpoint.Alias)) ?? reference;

        if (Output.IsJson)
        {
            Output.Json(new { endpoint = endpoint.Alias, token = SecretRedactor.Mask(token), storedIn = location, username, authEnabled });
            return ExitCodes.Success;
        }

        if (!authEnabled)
        {
            Output.Warning($"Authentication is disabled on '{endpoint.Alias}'; the token is stored for when it is enabled.");
        }

        Output.Success($"Authenticated to '{endpoint.Alias}'{(username is null ? "" : $" as {username}")}. Token {SecretRedactor.Mask(token)} stored in {location}.");
        return ExitCodes.Success;
    }

    private async Task<(string Token, string Username)> LoginAndCreateTokenAsync(
        EndpointConfig endpoint, EndpointAuthSettings settings, CancellationToken cancellationToken)
    {
        var prompt = Services.Prompt;
        var username = settings.Username;
        if (string.IsNullOrWhiteSpace(username))
        {
            username = prompt.CanPrompt
                ? await prompt.AskAsync("Username:", cancellationToken)
                : throw DockhandException.Usage("--username is required when no terminal is available.");
        }

        string password;
        if (settings.PasswordStdin)
        {
            password = (await Context.Console.Input.ReadLineAsync(cancellationToken))?.TrimEnd('\r', '\n') ?? "";
        }
        else if (prompt.CanPrompt)
        {
            password = await prompt.AskSecretAsync("Password:", cancellationToken);
        }
        else
        {
            throw DockhandException.Usage("Use --password-stdin when no terminal is available.");
        }

        DateTimeOffset? expires = null;
        if (settings.Expires is { } expiresText)
        {
            expires = DateTimeOffset.TryParse(expiresText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : throw DockhandException.Usage($"Invalid --expires date '{expiresText}'. Use e.g. 2027-12-31.");
        }

        var api = Services.ApiFactory.Create(endpoint, token: null);
        try
        {
            var login = await Output.StatusAsync("Signing in…", () => api.LoginAsync(username, password, settings.MfaCode, cancellationToken));
            if (login.RequiresMfa == true)
            {
                var code = settings.MfaCode;
                if (string.IsNullOrWhiteSpace(code))
                {
                    code = prompt.CanPrompt
                        ? await prompt.AskAsync("MFA code:", cancellationToken)
                        : throw DockhandException.Usage("The account requires MFA. Pass --mfa-code <code>.");
                }

                login = await api.LoginAsync(username, password, code, cancellationToken);
                if (login.RequiresMfa == true)
                {
                    throw new DockhandException(ExitCodes.Unauthorized, "The MFA code was not accepted.");
                }
            }
        }
        catch (ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new DockhandException(ExitCodes.Unauthorized, $"Sign-in to '{endpoint.Alias}' failed: {ex.ApiMessage ?? "invalid username or password"}.");
        }

        try
        {
            var name = settings.TokenName ?? $"dockhand-cli@{Environment.MachineName}".ToLowerInvariant();
            var created = await api.CreateTokenAsync(name, expires, password, cancellationToken);
            Output.Info($"Created API token '{name}' (id {created.Id}). Revoke it in the Dockhand web UI if this machine is lost.");
            return (created.Token, username);
        }
        finally
        {
            try
            {
                await api.LogoutAsync(CancellationToken.None);
            }
            catch (DockhandException)
            {
                // The session expires on its own; the token is what matters.
            }
        }
    }

    /// <summary>
    /// Checks a token before storing it. <c>/api/auth/session</c> only reads the session cookie, so it tells
    /// whether authentication is enabled; an authenticated call then proves the token works.
    /// </summary>
    private async Task<bool> VerifyTokenAsync(EndpointConfig endpoint, string token, CancellationToken cancellationToken)
    {
        var api = Services.ApiFactory.Create(endpoint, token);
        var session = await api.GetSessionAsync(cancellationToken);
        if (!session.AuthEnabled)
        {
            return false;
        }

        try
        {
            await api.GetEnvironmentsAsync(cancellationToken);
        }
        catch (ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new DockhandException(ExitCodes.Unauthorized, $"'{endpoint.Alias}' did not accept the token. Nothing was stored.");
        }

        return true;
    }
}

internal sealed class EndpointLogoutCommand(CommandServices services) : DockhandCommand<AliasSettings>(services)
{
    protected override async Task<int> RunAsync(AliasSettings settings, CancellationToken cancellationToken)
    {
        var config = await Services.Store.LoadAsync(cancellationToken);
        var endpoint = EndpointService.Require(config, settings.Alias);
        await Services.Endpoints.RemoveTokenAsync(endpoint.Alias, cancellationToken);

        if (Output.IsJson)
        {
            Output.Json(new { endpoint = endpoint.Alias, tokenRemoved = true });
        }
        else
        {
            Output.Success($"Removed the stored token for '{endpoint.Alias}'. Revoke it in the Dockhand web UI if it should stop working.");
            var variable = TokenEnvironment.VariableFor(endpoint.Alias);
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable)))
            {
                Output.Warning($"{variable} is still set and will keep being used.");
            }
        }

        return ExitCodes.Success;
    }
}
