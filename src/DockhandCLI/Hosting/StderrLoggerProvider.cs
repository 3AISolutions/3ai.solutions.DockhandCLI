using DockhandCLI.Core.Services;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace DockhandCLI.Hosting;

/// <summary>
/// Writes log messages to stderr: warnings always, debug details with <c>--verbose</c>.
/// Messages pass through <see cref="SecretRedactor"/>; headers are never logged.
/// </summary>
internal sealed class StderrLoggerProvider(InvocationContext context) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new StderrLogger(context, categoryName);

    public void Dispose()
    {
    }

    private sealed class StderrLogger(InvocationContext context, string category) : ILogger
    {
        private readonly string _shortCategory = category[(category.LastIndexOf('.') + 1)..];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= (context.Options.Verbose ? LogLevel.Debug : LogLevel.Warning);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = Markup.Escape(SecretRedactor.Redact(formatter(state, exception)));
            var line = logLevel switch
            {
                >= LogLevel.Error => $"[red]Error:[/] {message}",
                LogLevel.Warning => $"[yellow]Warning:[/] {message}",
                _ => $"[grey]{_shortCategory}: {message}[/]",
            };
            context.Console.Error.MarkupLine(line);
        }
    }
}
