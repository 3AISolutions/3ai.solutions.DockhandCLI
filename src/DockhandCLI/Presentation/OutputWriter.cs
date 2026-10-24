using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DockhandCLI.Core.Services;
using DockhandCLI.Hosting;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace DockhandCLI.Presentation;

internal static class CliJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

/// <summary>Writes command results in the selected format.</summary>
internal sealed class OutputWriter(InvocationContext context)
{
    public bool IsJson => context.IsJson;

    private CliConsole Console => context.Console;

    /// <summary>Writes a JSON document to stdout, with nothing else around it.</summary>
    public void Json<T>(T value)
    {
        Console.RawOutput.WriteLine(JsonSerializer.Serialize(value, CliJson.Options));
        Console.RawOutput.Flush();
    }

    public void Write(IRenderable renderable) => Console.Output.Write(renderable);

    public void MarkupLine(string markup) => Console.Output.MarkupLine(markup);

    public void Text(string text) => Console.Output.WriteLine(text);

    /// <summary>Confirmation of a completed action. Goes to stderr in JSON mode so stdout stays parseable.</summary>
    public void Success(string message) =>
        Human.MarkupLine($"[green]✓[/] {Markup.Escape(SecretRedactor.Redact(message))}");

    public void Info(string message) =>
        Human.MarkupLine($"[grey]{Markup.Escape(SecretRedactor.Redact(message))}[/]");

    public void Warning(string message) =>
        Console.Error.MarkupLine($"[yellow]Warning:[/] {Markup.Escape(SecretRedactor.Redact(message))}");

    /// <summary>Runs <paramref name="action"/> behind a spinner when stdout is an interactive terminal.</summary>
    public async Task<T> StatusAsync<T>(string message, Func<Task<T>> action)
    {
        if (IsJson || !Console.InteractiveOutput)
        {
            return await action();
        }

        return await Console.Output.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync(Markup.Escape(message), _ => action());
    }

    private IAnsiConsole Human => IsJson ? Console.Error : Console.Output;
}
