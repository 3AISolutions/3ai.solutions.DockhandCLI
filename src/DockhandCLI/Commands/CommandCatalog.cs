namespace DockhandCLI.Commands;

internal sealed record CatalogEntry(string Path, string Arguments, string Description)
{
    public string[] Words { get; } = Path.Split(' ');
}

/// <summary>
/// Every command with a one-line description. Drives the shell's <c>/command</c> overview and tab completion;
/// <see cref="CommandRegistration"/> registers the same paths with Spectre.Console.Cli (a unit test keeps them in sync).
/// </summary>
internal static class CommandCatalog
{
    public static readonly IReadOnlyList<CatalogEntry> Entries =
    [
        new("endpoint add", "<alias> <url>", "Register a Dockhand installation"),
        new("endpoint list", "", "List registered endpoints"),
        new("endpoint show", "<alias>", "Show an endpoint and its (masked) token"),
        new("endpoint remove", "<alias>", "Remove an endpoint and its stored token"),
        new("endpoint default", "<alias>", "Set the default endpoint"),
        new("endpoint set-env", "<alias> <env>", "Save the default Docker environment of an endpoint"),
        new("endpoint test", "[alias]", "Check connectivity and authentication"),
        new("endpoint auth", "<alias>", "Store an API token (paste one, or sign in to create one)"),
        new("endpoint logout", "<alias>", "Remove the stored token"),
        new("env list", "", "List Docker environments of the endpoint"),
        new("env show", "<env>", "Show one Docker environment"),
        new("containers list", "", "List containers"),
        new("containers search", "<text>", "Find containers in every environment, with the command to update each (--all-endpoints)"),
        new("containers inspect", "<container>", "Show the Docker inspect data of a container"),
        new("containers logs", "<container>", "Show container logs"),
        new("containers start", "<container>", "Start a container"),
        new("containers stop", "<container>", "Stop a container"),
        new("containers restart", "<container>", "Restart a container"),
        new("containers pause", "<container>", "Pause a container"),
        new("containers unpause", "<container>", "Unpause a container"),
        new("check updates", "", "Check containers for image updates (--pending, --cached, --all-envs)"),
        new("check apply update docker", "<container>", "Update a container to its latest image (--dry-run, --yes)"),
        new("update apply", "<container>", "Same as 'check apply update docker'"),
        new("update container", "<container>", "Check one container for a new image and update it if there is one (--check-only, --force, --yes)"),
        new("images list", "", "List images"),
        new("stacks list", "", "List compose stacks"),
        new("stacks show", "<stack>", "Show a stack and its compose file"),
        new("stacks start", "<stack>", "Start a stack"),
        new("stacks stop", "<stack>", "Stop a stack"),
        new("stacks restart", "<stack>", "Restart a stack"),
        new("stacks deploy", "<stack>", "Deploy (compose up) a stack"),
        new("stacks down", "<stack>", "Take a stack down"),
        new("jobs show", "<job-id>", "Show a background job"),
        new("jobs cancel", "<job-id>", "Request cancellation of a background job"),
        new("self path", "", "Add this executable's folder to your PATH (--remove to take it off)"),
        new("self update", "", "Update dockhand to the latest release (--check, --force)"),
        new("health", "", "Check the health of the endpoint"),
        new("system", "", "Show Dockhand host, runtime and Docker information"),
    ];

    /// <summary>Built-in commands of the interactive shell.</summary>
    public static readonly IReadOnlyList<CatalogEntry> ShellCommands =
    [
        new("command", "[name]", "List commands, or show detailed help for one (alias: /help)"),
        new("use", "[alias]", "Switch the active endpoint for this session (the saved default is unchanged)"),
        new("history", "", "Show command history"),
        new("clear", "", "Clear the screen"),
        new("exit", "", "Leave the shell (alias: /quit)"),
    ];

    public static readonly IReadOnlyList<string> ShellWords = ["command", "help", "use", "history", "clear", "exit", "quit"];

    /// <summary>Words that can follow <paramref name="path"/> in the command tree.</summary>
    public static IEnumerable<string> NextWords(IReadOnlyList<string> path) =>
        Entries
            .Where(e => e.Words.Length > path.Count
                && e.Words.Take(path.Count).SequenceEqual(path, StringComparer.OrdinalIgnoreCase))
            .Select(e => e.Words[path.Count])
            .Distinct(StringComparer.OrdinalIgnoreCase);
}
