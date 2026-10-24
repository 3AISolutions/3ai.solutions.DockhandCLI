using DockhandCLI.Commands;
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
