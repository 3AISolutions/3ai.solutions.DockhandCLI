using System.Runtime.CompilerServices;
using System.Text;

namespace DockhandCLI.Infrastructure.Api;

internal sealed record SseEvent(string Event, string Data);

/// <summary>Minimal Server-Sent Events parser (event/data fields, comments ignored).</summary>
internal static class SseReader
{
    public static async IAsyncEnumerable<SseEvent> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var eventName = "message";
        var data = new StringBuilder();

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return new SseEvent(eventName, data.ToString());
                }

                eventName = "message";
                data.Clear();
                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            switch (field)
            {
                case "event":
                    eventName = value;
                    break;
                case "data":
                    if (data.Length > 0)
                    {
                        data.Append('\n');
                    }

                    data.Append(value);
                    break;
            }
        }

        if (data.Length > 0)
        {
            yield return new SseEvent(eventName, data.ToString());
        }
    }
}
