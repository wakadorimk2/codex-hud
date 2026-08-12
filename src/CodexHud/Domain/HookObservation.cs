using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexHud.Domain;

public enum HookEventKind
{
    Unknown,
    SessionStart,
    UserPromptSubmit,
    PermissionRequest,
    Stop,
    SessionEnd
}

public sealed record HookObservation(
    HookEventKind Event,
    string SessionId,
    DateTimeOffset ObservedAtUtc);

public static class HookObservationParser
{
    public static bool TryParseHookPayload(string? json, out HookObservation? observation)
    {
        observation = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var eventName = GetFirstString(
                document.RootElement,
                "hook_event_name",
                "event",
                "type");
            var sessionId = GetFirstString(
                document.RootElement,
                "session_id",
                "conversation_id");

            if (eventName is null)
            {
                return false;
            }

            observation = new HookObservation(
                ParseEvent(eventName),
                HashSessionId(sessionId),
                DateTimeOffset.UtcNow);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryParseTransportMessage(string? json, out HookObservation? observation)
    {
        observation = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            var message = JsonSerializer.Deserialize<SanitizedHookMessage>(json, JsonOptions);
            if (message is null || string.IsNullOrWhiteSpace(message.SessionId))
            {
                return false;
            }

            observation = new HookObservation(
                ParseEvent(message.Event),
                message.SessionId,
                message.ObservedAtUtc == default ? DateTimeOffset.UtcNow : message.ObservedAtUtc);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string SerializeTransportMessage(HookObservation observation)
    {
        var message = new SanitizedHookMessage(
            observation.Event.ToString(),
            observation.SessionId,
            observation.ObservedAtUtc);
        return JsonSerializer.Serialize(message, JsonOptions);
    }

    private static HookEventKind ParseEvent(string eventName)
    {
        return Enum.TryParse<HookEventKind>(eventName, ignoreCase: true, out var result)
            ? result
            : HookEventKind.Unknown;
    }

    private static string HashSessionId(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return "session-unknown";
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sessionId));
        return $"session-{Convert.ToHexString(bytes.AsSpan(0, 12)).ToLowerInvariant()}";
    }

    private static string? GetFirstString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (element.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.String)
            {
                var value = property.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private sealed record SanitizedHookMessage(
        string Event,
        string SessionId,
        DateTimeOffset ObservedAtUtc);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
}
