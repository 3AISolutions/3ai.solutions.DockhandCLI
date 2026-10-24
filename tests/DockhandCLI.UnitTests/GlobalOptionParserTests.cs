using DockhandCLI.Core.Models;
using DockhandCLI.Core.Services;

namespace DockhandCLI.UnitTests;

public class GlobalOptionParserTests
{
    private static readonly HashSet<string> Aliases = new(StringComparer.OrdinalIgnoreCase) { "srv01", "srv02", "prod.eu" };

    private static ParsedArguments Parse(params string[] args) => GlobalOptionParser.Parse(args, Aliases.Contains);

    [Fact]
    public void Shorthand_alias_selects_server_and_is_removed()
    {
        var parsed = Parse("-srv01", "check", "updates");

        Assert.Equal("srv01", parsed.Options.Server);
        Assert.Equal(["check", "updates"], parsed.Remaining);
    }

    [Theory]
    [InlineData("--server", "srv01", "check", "updates")]
    [InlineData("check", "updates", "--server", "srv01")]
    [InlineData("check", "--server=srv01", "updates")]
    [InlineData("check", "updates", "-srv01")]
    public void Server_selection_is_equivalent_in_any_position(params string[] args)
    {
        var parsed = Parse(args);

        Assert.Equal("srv01", parsed.Options.Server);
        Assert.Equal(["check", "updates"], parsed.Remaining);
    }

    [Fact]
    public void Shorthand_is_case_insensitive_and_supports_dots()
    {
        Assert.Equal("prod.eu", Parse("-PROD.EU", "health").Options.Server, ignoreCase: true);
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("-v")]
    [InlineData("-y")]
    [InlineData("--help")]
    [InlineData("--version")]
    [InlineData("--yes")]
    public void Ordinary_options_are_not_treated_as_aliases(string option)
    {
        var parsed = Parse("containers", "stop", "nginx", option);

        Assert.Null(parsed.Options.Server);
        Assert.Contains(option, parsed.Remaining);
    }

    [Fact]
    public void Unknown_shorthand_alias_is_a_usage_error()
    {
        var ex = Assert.Throws<DockhandException>(() => Parse("-srv99", "check", "updates"));

        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
        Assert.Contains("srv99", ex.Message);
    }

    [Fact]
    public void Conflicting_endpoints_are_rejected()
    {
        var ex = Assert.Throws<DockhandException>(() => Parse("-srv01", "--server", "srv02", "health"));

        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
        Assert.Contains("Conflicting", ex.Message);
    }

    [Fact]
    public void Repeating_the_same_endpoint_is_allowed()
    {
        Assert.Equal("srv01", Parse("-srv01", "health", "--server", "SRV01").Options.Server, ignoreCase: true);
    }

    [Fact]
    public void Server_option_value_is_not_validated_by_the_parser()
    {
        // --server values are resolved later so the error can list registered endpoints.
        Assert.Equal("unknown", Parse("--server", "unknown", "health").Options.Server);
    }

    [Fact]
    public void Parses_all_global_options()
    {
        var parsed = Parse("-srv02", "--env", "3", "--output", "json", "--timeout", "45", "--verbose", "--no-color", "containers", "list");

        Assert.Equal("srv02", parsed.Options.Server);
        Assert.Equal("3", parsed.Options.Environment);
        Assert.Equal(OutputFormat.Json, parsed.Options.Output);
        Assert.Equal(TimeSpan.FromSeconds(45), parsed.Options.Timeout);
        Assert.True(parsed.Options.Verbose);
        Assert.True(parsed.Options.NoColor);
        Assert.Equal(["containers", "list"], parsed.Remaining);
    }

    [Fact]
    public void Short_output_option_is_supported()
    {
        Assert.Equal(OutputFormat.Json, Parse("env", "list", "-o", "json").Options.Output);
    }

    [Fact]
    public void All_envs_flag_is_global()
    {
        var parsed = Parse("-srv01", "--all-envs", "check", "updates");

        Assert.True(parsed.Options.AllEnvironments);
        Assert.Equal(["check", "updates"], parsed.Remaining);
    }

    [Fact]
    public void Env_and_all_envs_cannot_be_combined()
    {
        Assert.Equal(ExitCodes.Usage, Assert.Throws<DockhandException>(() => Parse("--env", "1", "--all-envs", "check", "updates")).ExitCode);
    }

    [Theory]
    [InlineData("--server")]
    [InlineData("--env")]
    [InlineData("--output")]
    public void Missing_option_value_is_a_usage_error(string option)
    {
        Assert.Equal(ExitCodes.Usage, Assert.Throws<DockhandException>(() => Parse("health", option)).ExitCode);
    }

    [Fact]
    public void Option_value_cannot_be_another_option()
    {
        Assert.Throws<DockhandException>(() => Parse("--server", "--verbose", "health"));
    }

    [Fact]
    public void Invalid_output_format_is_rejected()
    {
        Assert.Contains("yaml", Assert.Throws<DockhandException>(() => Parse("--output", "yaml")).Message);
    }

    [Theory]
    [InlineData("30", 30)]
    [InlineData("30s", 30)]
    [InlineData("2m", 120)]
    [InlineData("1.5", 1.5)]
    [InlineData("500ms", 0.5)]
    public void Timeout_formats(string value, double seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), GlobalOptionParser.ParseTimeout(value));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    public void Invalid_timeouts_are_rejected(string value)
    {
        Assert.Throws<DockhandException>(() => GlobalOptionParser.ParseTimeout(value));
    }

    [Fact]
    public void Double_dash_stops_option_parsing()
    {
        var parsed = Parse("containers", "logs", "--", "-srv01", "--server");

        Assert.Null(parsed.Options.Server);
        Assert.Equal(["containers", "logs", "--", "-srv01", "--server"], parsed.Remaining);
    }

    [Fact]
    public void Negative_numbers_are_passed_through()
    {
        Assert.Equal(["x", "-10"], Parse("x", "-10").Remaining);
    }

    [Fact]
    public void Flag_with_inline_value_is_rejected()
    {
        Assert.Throws<DockhandException>(() => Parse("--verbose=true", "health"));
    }
}
