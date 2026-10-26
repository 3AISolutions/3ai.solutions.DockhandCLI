using System.Text.Json;
using DockhandCLI.Infrastructure.SelfManagement;

namespace DockhandCLI.UnitTests;

public class SelfManagementTests
{
    private static string Identity(string value) => value;

    [Fact]
    public void Path_add_appends_once_and_keeps_an_unchanged_value_as_is()
    {
        var (added, _) = PathEntries.Add(@"C:\bin;C:\tools;", @"C:\dockhand", _ => false, Identity);
        Assert.Equal(@"C:\bin;C:\tools;C:\dockhand", added);

        var (again, removed) = PathEntries.Add(@"C:\bin;C:\Dockhand\;", @"C:\dockhand", _ => false, Identity);
        Assert.Equal(@"C:\bin;C:\Dockhand\;", again);
        Assert.Empty(removed);
    }

    [Fact]
    public void Path_add_replaces_older_dockhand_folders_and_compares_expanded_entries()
    {
        var expand = (string e) => e.Replace("%LOCALAPPDATA%", @"C:\Users\me\AppData\Local", StringComparison.Ordinal);

        var (value, removed) = PathEntries.Add(
            @"%LOCALAPPDATA%\Programs\dockhand;C:\bin;C:\old-dockhand", @"C:\Users\me\AppData\Local\Programs\dockhand",
            dir => dir == @"C:\old-dockhand", expand);

        Assert.Equal(@"%LOCALAPPDATA%\Programs\dockhand;C:\bin", value);
        Assert.Equal([@"C:\old-dockhand"], removed);
    }

    [Fact]
    public void Path_remove_drops_every_matching_entry()
    {
        var (value, removed) = PathEntries.Remove(@"C:\dockhand;C:\bin;c:\DOCKHAND\", @"C:\dockhand", Identity);

        Assert.Equal(@"C:\bin", value);
        Assert.Equal(2, removed.Count);
    }

    [Fact]
    public void Profile_block_is_appended_replaced_and_removed()
    {
        var original = "export EDITOR=vim\n";

        var added = ShellProfile.Apply(original, ShellProfile.PosixBlock("/opt/dockhand"));
        Assert.StartsWith(original + "\n" + PathInstaller.BlockStart, added, StringComparison.Ordinal);
        Assert.Contains("export PATH='/opt/dockhand':\"$PATH\"", added, StringComparison.Ordinal);
        Assert.Equal(added, ShellProfile.Apply(added, ShellProfile.PosixBlock("/opt/dockhand")));

        var moved = ShellProfile.Apply(added, ShellProfile.PosixBlock("/home/me/bin"));
        Assert.DoesNotContain("/opt/dockhand", moved, StringComparison.Ordinal);
        Assert.Single(moved.Split(PathInstaller.BlockStart)[1..]);

        Assert.Equal(original, ShellProfile.Apply(moved, null));
    }

    [Fact]
    public void Profile_block_quotes_single_quotes()
    {
        Assert.Contains(@"'/home/o'\''brien/bin'", ShellProfile.PosixBlock("/home/o'brien/bin"), StringComparison.Ordinal);
    }

    [Fact]
    public void Profiles_are_written_for_the_users_shell_and_cleaned_up()
    {
        var home = Path.Combine(Path.GetTempPath(), "dockhand-ut", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            File.WriteAllText(Path.Combine(home, ".profile"), "umask 022\n");
            var installer = new PathInstaller(home, "/usr/bin/zsh");

            var add = installer.UpdateProfiles("/opt/dockhand", add: true);
            Assert.True(add.Changed);
            Assert.Equal([Path.Combine(home, ".profile"), Path.Combine(home, ".zshrc")], add.Updated);

            Assert.False(installer.UpdateProfiles("/opt/dockhand", add: true).Changed);

            var remove = installer.UpdateProfiles("/opt/dockhand", add: false);
            Assert.Equal(2, remove.Updated.Count);
            Assert.Equal("umask 022\n", File.ReadAllText(Path.Combine(home, ".profile")));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("1.2.3-beta.1+abc", "1.2.3")]
    public void Versions_are_parsed_from_tags(string text, string expected)
    {
        Assert.Equal(Version.Parse(expected), SelfUpdater.ParseVersion(text));
    }

    [Fact]
    public void Release_assets_are_matched_by_tag_and_platform()
    {
        using var json = JsonDocument.Parse("""
            {
              "tag_name": "v1.2.0",
              "html_url": "https://github.com/x/y/releases/tag/v1.2.0",
              "assets": [
                { "name": "dockhand-v1.2.0-win-x64.zip", "browser_download_url": "https://example.com/a.zip", "size": 1 },
                { "name": "dockhand-v1.2.0-linux-arm64.tar.gz", "browser_download_url": "https://example.com/b.tar.gz", "size": 2 },
                { "name": "SHA256SUMS.txt", "browser_download_url": "https://example.com/sums", "size": 3 }
              ]
            }
            """);

        var release = SelfUpdater.ParseRelease(json.RootElement);

        Assert.Equal(new Version(1, 2, 0), release.Version);
        Assert.Equal("dockhand-v1.2.0-win-x64.zip", SelfUpdater.FindAsset(release, "win-x64")?.Name);
        Assert.Equal("dockhand-v1.2.0-linux-arm64.tar.gz", SelfUpdater.FindAsset(release, "linux-arm64")?.Name);
        Assert.Null(SelfUpdater.FindAsset(release, "osx-x64"));
        Assert.True(new UpdateCheck(new Version(1, 1, 0), release, null).UpdateAvailable);
        Assert.False(new UpdateCheck(new Version(1, 2, 0), release, null).UpdateAvailable);
    }

    [Fact]
    public void Checksums_are_read_from_sha256sum_output()
    {
        var sums = SelfUpdater.ParseChecksums("abc123  dockhand-v1.2.0-win-x64.zip\r\ndef456 *dockhand-v1.2.0-linux-x64.tar.gz\n");

        Assert.Equal("abc123", sums["dockhand-v1.2.0-win-x64.zip"]);
        Assert.Equal("def456", sums["dockhand-v1.2.0-linux-x64.tar.gz"]);
    }
}
