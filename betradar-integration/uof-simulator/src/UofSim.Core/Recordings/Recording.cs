using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UofSim.Core.Recordings;

/// <summary>One line of a JSONL recording: when (relative to start), where and what to publish.</summary>
public sealed record RecordedMessage(
    [property: JsonPropertyName("offset_ms")] long OffsetMs,
    [property: JsonPropertyName("routing_key")] string RoutingKey,
    [property: JsonPropertyName("body")] string Body);

public static class Recording
{
    // Keep XML bodies readable in the JSONL file (no \u003C escapes); recordings are never embedded in HTML.
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static IReadOnlyList<RecordedMessage> Read(string path) =>
        File.ReadLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select((line, i) => JsonSerializer.Deserialize<RecordedMessage>(line)
                ?? throw new InvalidDataException($"{path}:{i + 1}: empty record"))
            .OrderBy(m => m.OffsetMs)
            .ToList();

    public static void Write(string path, IEnumerable<RecordedMessage> messages)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllLines(path, messages.Select(m => JsonSerializer.Serialize(m, WriteOptions)), new UTF8Encoding(false));
    }
}
