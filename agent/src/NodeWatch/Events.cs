using System.Text.Json;
using System.Text.Json.Serialization;

namespace NodeWatch;

/// <summary>A CloudEvents 1.0 event in structured JSON mode (what every sink receives or is built from).</summary>
internal sealed class CloudEvent
{
    public const string TypePrefix = "eu.aisdev.nodewatch.";
    public const string Firing = TypePrefix + "alert.firing";
    public const string Resolved = TypePrefix + "alert.resolved";
    public const string Heartbeat = TypePrefix + "heartbeat";

    [JsonPropertyName("specversion")] public string SpecVersion { get; set; } = "1.0";
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString();
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("subject")] public string? Subject { get; set; }
    [JsonPropertyName("time")] public DateTimeOffset Time { get; set; }
    [JsonPropertyName("datacontenttype")] public string DataContentType { get; set; } = "application/json";

    /// <summary>Extension attribute: which relay channels should deliver it (comma separated); null: all.</summary>
    [JsonPropertyName("channels")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Channels { get; set; }

    [JsonPropertyName("data")] public EventData Data { get; set; } = new();

    [JsonIgnore] public bool IsHeartbeat => Type == Heartbeat;
    [JsonIgnore] public bool IsResolved => Type == Resolved;

    public static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

internal sealed class EventData
{
    [JsonPropertyName("alertKey")] public string? AlertKey { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("severity")] public string? Severity { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("node")] public string Node { get; set; } = "";
    [JsonPropertyName("network")] public string Network { get; set; } = "";
    [JsonPropertyName("startsAt")] public DateTimeOffset? StartsAt { get; set; }
    [JsonPropertyName("endsAt")] public DateTimeOffset? EndsAt { get; set; }
    [JsonPropertyName("facts")] public Dictionary<string, object?>? Facts { get; set; }
}

/// <summary>Builds the events of one node.</summary>
internal sealed class EventFactory(Config config)
{
    private string Node => config.Node.Name;

    public CloudEvent Firing(string check, Level level, string title, string text, DateTimeOffset startsAt, DateTimeOffset now,
        Dictionary<string, object?>? facts = null) => Make(CloudEvent.Firing, check, now, new EventData
        {
            AlertKey = $"{Node}/{check}",
            Status = "firing",
            Severity = Severity.Name(level),
            Title = title,
            Text = text,
            StartsAt = startsAt,
            Facts = facts,
        });

    public CloudEvent Resolved(string check, Level firedLevel, string title, string text, DateTimeOffset startsAt,
        DateTimeOffset now, Dictionary<string, object?>? facts = null) => Make(CloudEvent.Resolved, check, now, new EventData
        {
            AlertKey = $"{Node}/{check}",
            Status = "resolved",
            Severity = Severity.Name(firedLevel),
            Title = title,
            Text = text,
            StartsAt = startsAt,
            EndsAt = now,
            Facts = facts,
        });

    public CloudEvent Heartbeat(DateTimeOffset now, Dictionary<string, object?> facts) =>
        Make(CloudEvent.Heartbeat, null, now, new EventData { Facts = facts });

    private CloudEvent Make(string type, string? subject, DateTimeOffset now, EventData data)
    {
        data.Node = Node;
        data.Network = config.Node.Network;
        return new CloudEvent
        {
            Source = $"nodewatch://{Node}",
            Type = type,
            Subject = subject,
            Time = now,
            Data = data,
            Channels = config.Relay.Channels.Count > 0 ? string.Join(',', config.Relay.Channels) : null,
        };
    }
}

/// <summary>Human-readable lines for chat sinks.</summary>
internal static class EventText
{
    public static string Label(CloudEvent e) => e.IsResolved
        ? "[HELYREÁLLT]"
        : e.Data.Severity switch
        {
            "critical" => "[KRITIKUS]",
            "warning" => "[FIGYELMEZTETÉS]",
            _ => "[INFO]",
        };

    public static string Icon(CloudEvent e) => e.IsResolved
        ? "✅"
        : e.Data.Severity switch
        {
            "critical" => "🔴",
            "warning" => "🟠",
            _ => "ℹ️",
        };

    public static string Headline(CloudEvent e) => $"{Label(e)} {e.Data.Node} – {e.Data.Title}";
}
