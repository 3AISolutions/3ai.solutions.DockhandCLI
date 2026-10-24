using DockhandCLI.Commands;
using DockhandCLI.Hosting;
using DockhandCLI.Core.Services;
using DockhandCLI.Shell;

namespace DockhandCLI.UnitTests;

public class CommandLineTokenizerTests
{
    [Theory]
    [InlineData("check updates", new[] { "check", "updates" })]
    [InlineData("  /check   updates  ", new[] { "/check", "updates" })]
    [InlineData("endpoint add srv01 \"https://x.example.com\" -d \"My server\"", new[] { "endpoint", "add", "srv01", "https://x.example.com", "-d", "My server" })]
    [InlineData("say 'single \"quoted\" text'", new[] { "say", "single \"quoted\" text" })]
    [InlineData("say \"a \\\"b\\\" c\"", new[] { "say", "a \"b\" c" })]
    [InlineData("say a\\ b", new[] { "say", "a b" })]
    [InlineData("say \"\"", new[] { "say", "" })]
    [InlineData("path \"C:\\temp\\x\"", new[] { "path", "C:\\temp\\x" })]
    [InlineData("", new string[0])]
    public void Splits_arguments(string line, string[] expected) =>
        Assert.Equal(expected, CommandLineTokenizer.Tokenize(line));

    [Theory]
    [InlineData("say \"unterminated")]
    [InlineData("say 'unterminated")]
    public void Unterminated_quotes_are_errors(string line) =>
        Assert.Throws<FormatException>(() => CommandLineTokenizer.Tokenize(line));
}

public class ShellCompleterTests
{
    private readonly ShellCompleter _completer = new(() => ["srv01", "srv02", "prod"]);

    [Fact]
    public void Slash_alone_lists_every_command()
    {
        var matches = _completer.MatchCommands("/");
        Assert.Equal(CommandCatalog.Entries.Count + CommandCatalog.ShellCommands.Count, matches.Count);
    }

    [Fact]
    public void Slash_filters_by_path_prefix_then_by_later_words()
    {
        Assert.Equal(["check updates", "check apply update docker"], _completer.MatchCommands("/check").Select(e => e.Path));
        Assert.Equal(["containers logs"], _completer.MatchCommands("/logs").Select(e => e.Path));

        var update = _completer.MatchCommands("/upd").Select(e => e.Path).ToList();
        Assert.Equal("update apply", update[0]);
        Assert.Contains("check updates", update);
    }

    [Theory]
    [InlineData("check")]
    [InlineData("/check updates ")]
    [InlineData("/containers logs nginx")]
    [InlineData("/nothing")]
    public void Slash_menu_closes_without_a_matching_command(string line) =>
        Assert.Empty(_completer.MatchCommands(line));

    [Fact]
    public void Menu_rows_scroll_with_the_selection_and_fit_the_width()
    {
        var entries = _completer.MatchCommands("/");
        var first = 0;

        var lines = LineEditor.MenuLines(entries, selected: 10, ref first, width: 80);

        Assert.Equal(3, first);
        Assert.Equal(9, lines.Count); // 8 rows and the position footer
        Assert.Contains("❯ /", lines[7].Markup);
        Assert.All(lines, l => Assert.True(l.Length < 80));
        Assert.Contains("11/", lines[^1].Markup);
    }

    [Fact]
    public void Completes_top_level_commands_and_shell_words()
    {
        var completion = _completer.Complete("ch");
        Assert.Equal(["check"], completion.Candidates);

        Assert.Contains("use", _completer.Complete("u").Candidates);
        Assert.Contains("update", _completer.Complete("u").Candidates);
    }

    [Fact]
    public void Keeps_the_leading_slash()
    {
        var completion = _completer.Complete("/con");

        Assert.Equal(1, completion.ReplaceFrom);
        Assert.Equal(["containers"], completion.Candidates);
    }

    [Fact]
    public void Completes_subcommands_after_options()
    {
        Assert.Equal(["updates"], _completer.Complete("-srv01 check up").Candidates);
        Assert.Equal(["docker"], _completer.Complete("check apply update --env 2 d").Candidates);
    }

    [Fact]
    public void Completes_aliases()
    {
        Assert.Equal(["-srv01", "-srv02"], _completer.Complete("-sr").Candidates);
        Assert.Equal(["srv01", "srv02"], _completer.Complete("use s").Candidates);
        Assert.Equal(["prod"], _completer.Complete("health --server p").Candidates);
    }
}

public class CommandCatalogTests
{
    [Fact]
    public void Catalog_paths_are_unique() =>
        Assert.Equal(CommandCatalog.Entries.Count, CommandCatalog.Entries.Select(e => e.Path).Distinct().Count());

    [Fact]
    public void Next_words_follow_the_tree()
    {
        Assert.Equal(["apply"], CommandCatalog.NextWords(["update"]));
        Assert.Contains("set-env", CommandCatalog.NextWords(["endpoint"]));
    }
}

public class SecretRedactorTests
{
    [Fact]
    public void Masks_tokens()
    {
        Assert.Equal("dh_…WXYZ", SecretRedactor.Mask("dh_abcdefghijklmnopqrstuvWXYZ"));
        Assert.Equal("dh_…", SecretRedactor.Mask("dh_abc"));
        Assert.Equal("(none)", SecretRedactor.Mask(null));
    }

    [Fact]
    public void Redacts_tokens_and_bearer_values_in_text()
    {
        var text = SecretRedactor.Redact("token dh_abcdefghijklmnopqrstuvWXYZ and Authorization: Bearer abcdefghijkl123");

        Assert.DoesNotContain("abcdefghijklmnop", text);
        Assert.Contains("dh_…WXYZ", text);
        Assert.Contains("Bearer ***", text);
    }

    [Theory]
    [InlineData("dh_abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQ", true)]
    [InlineData("dh_8chars-_", true)]
    [InlineData("dh_short", false)]
    [InlineData("dh_has space inside", false)]
    [InlineData("abc_defghijklmnop", false)]
    [InlineData("dh_\"quoted\"", false)]
    public void Validates_token_format(string token, bool valid) => Assert.Equal(valid, TokenFormat.IsValid(token));

    [Fact]
    public void Token_environment_variable_name() =>
        Assert.Equal("DOCKHAND_TOKEN_PROD_EU_1", TokenEnvironment.VariableFor("prod.eu-1"));
}

public class ErrorRendererTests
{
    [Theory]
    [InlineData("Unknown endpoint alias 'nope' (from '-nope'). Run 'dockhand endpoint list' to see registered aliases.",
        new[] { "Unknown endpoint alias 'nope' (from '-nope').", "Run 'dockhand endpoint list' to see registered aliases." })]
    [InlineData("Could not connect to 'tmp' (http://127.0.0.1:1). Refused it. (127.0.0.1:1)",
        new[] { "Could not connect to 'tmp' (http://127.0.0.1:1).", "Refused it. (127.0.0.1:1)" })]
    [InlineData("Not found (HTTP 404: Gone. Try later).", new[] { "Not found (HTTP 404: Gone. Try later)." })]
    [InlineData("Invalid date 'x. Y'. Use e.g. 2027-12-31.", new[] { "Invalid date 'x. Y'.", "Use e.g. 2027-12-31." })]
    [InlineData("Don't do it. Really.", new[] { "Don't do it.", "Really." })]
    public void Splits_messages_into_headline_and_hints(string message, string[] expected) =>
        Assert.Equal(expected, ErrorRenderer.SplitSentences(message));
}
