using System.Globalization;
using DockhandCLI.Core.Services;
using Spectre.Console;

namespace DockhandCLI.Presentation;

/// <summary>Formatting helpers shared by the command renderers.</summary>
internal static class Styles
{
    public static string Escape(string? text) => Markup.Escape(text ?? "");

    public static string Dim(string? text) => string.IsNullOrEmpty(text) ? "[grey]-[/]" : $"[grey]{Escape(text)}[/]";

    public static string ContainerState(string? state) => (state ?? "").ToLowerInvariant() switch
    {
        "running" => "[green]running[/]",
        "exited" or "dead" => $"[red]{Escape(state)}[/]",
        "paused" => "[yellow]paused[/]",
        "restarting" => "[blue]restarting[/]",
        "" => "[grey]-[/]",
        _ => $"[grey]{Escape(state)}[/]",
    };

    public static string StackStatus(string? status) => (status ?? "").ToLowerInvariant() switch
    {
        "running" => "[green]running[/]",
        "partial" => "[yellow]partial[/]",
        "stopped" => "[red]stopped[/]",
        "restarting" => "[blue]restarting[/]",
        _ => Dim(status),
    };

    public static string UpdateStatus(UpdateRow row) => row.Status switch
    {
        Core.Services.UpdateStatus.UpdateAvailable => "[yellow]● update available[/]",
        Core.Services.UpdateStatus.Pending => "[yellow]● update pending[/]",
        Core.Services.UpdateStatus.UpToDate => "[green]✓ up to date[/]",
        Core.Services.UpdateStatus.Error => "[red]✗ error[/]",
        Core.Services.UpdateStatus.Held => $"[blue]◷ held ({row.ReleaseAgeRemainingHours?.ToString(CultureInfo.InvariantCulture) ?? "?"} h left)[/]",
        Core.Services.UpdateStatus.NewerTag => "[cyan]↑ newer tag[/]",
        Core.Services.UpdateStatus.PinnedDigest => "[grey]pinned digest moved[/]",
        Core.Services.UpdateStatus.Disabled => "[grey]updates disabled[/]",
        Core.Services.UpdateStatus.SystemContainer => "[grey]system container[/]",
        Core.Services.UpdateStatus.LocalImage => "[grey]local image[/]",
        _ => Escape(row.Status),
    };

    public static string ShortDigest(string? digest)
    {
        if (string.IsNullOrEmpty(digest))
        {
            return "";
        }

        var value = digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest[7..] : digest;
        return value.Length > 12 ? value[..12] : value;
    }

    public static string Bytes(long? bytes)
    {
        if (bytes is not { } value)
        {
            return "-";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{size:0.#} {units[unit]}");
    }

    public static string UnixTime(long? seconds) =>
        seconds is { } value
            ? DateTimeOffset.FromUnixTimeSeconds(value).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "-";

    public static string Check(bool ok) => ok ? "[green]✓[/]" : "[red]✗[/]";

    public static Table NewTable(params string[] columns)
    {
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        foreach (var column in columns)
        {
            table.AddColumn(new TableColumn($"[bold]{Escape(column)}[/]"));
        }

        return table;
    }
}
