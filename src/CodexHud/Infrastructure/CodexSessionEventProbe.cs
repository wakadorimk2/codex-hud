using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using CodexHud.Domain;

namespace CodexHud.Infrastructure;

public sealed record JsonlReadResult(
    IReadOnlyList<JsonlActivityObservation> Observations,
    bool ReadError,
    bool HasBacklog,
    int BytesConsumed = 0);

public sealed class CodexSessionEventProbe
{
    public const int DefaultPerSessionByteBudget = 256 * 1024;
    public const int DefaultTotalByteBudget = 4 * 1024 * 1024;

    private const int MaximumJsonLineBytes = 64 * 1024;
    private const int FirstRecordProbeBytes = 4 * 1024;

    private readonly Dictionary<string, FileCursor> _cursors =
        new(StringComparer.OrdinalIgnoreCase);

    public bool HasBacklog { get; private set; }

    public IReadOnlyList<JsonlActivityObservation> Read(
        SessionDiscoveryResult discovery,
        DateTimeOffset observedAtUtc,
        int perSessionByteBudget = DefaultPerSessionByteBudget,
        int totalByteBudget = DefaultTotalByteBudget)
    {
        return ReadCandidates(
                discovery.Candidates,
                observedAtUtc,
                perSessionByteBudget,
                totalByteBudget)
            .Observations;
    }

    public JsonlReadResult ReadCandidates(
        IEnumerable<SessionFileCandidate> candidates,
        DateTimeOffset observedAtUtc,
        int perSessionByteBudget = DefaultPerSessionByteBudget,
        int totalByteBudget = DefaultTotalByteBudget)
    {
        if (perSessionByteBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(perSessionByteBudget));
        }

        if (totalByteBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalByteBudget));
        }

        observedAtUtc = observedAtUtc.ToUniversalTime();
        var observations = new List<JsonlActivityObservation>();
        var readError = false;
        var remainingTotalBudget = totalByteBudget;
        var currentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HasBacklog = false;

        foreach (var candidate in candidates)
        {
            currentPaths.Add(candidate.FullPath);
            if (remainingTotalBudget <= 0)
            {
                HasBacklog = true;
                break;
            }

            var sessionBudget = Math.Min(perSessionByteBudget, remainingTotalBudget);
            var result = ReadCandidate(candidate, observedAtUtc, sessionBudget);
            observations.AddRange(result.Observations);
            readError |= result.ReadError;
            HasBacklog |= result.HasBacklog;
            remainingTotalBudget = Math.Max(
                0,
                remainingTotalBudget - result.BytesConsumed);
        }

        foreach (var path in _cursors.Keys.ToArray())
        {
            if (!currentPaths.Contains(path))
            {
                _cursors.Remove(path);
            }
        }

        return new JsonlReadResult(observations, readError, HasBacklog);
    }

    public JsonlReadResult ReadCandidate(
        SessionFileCandidate candidate,
        DateTimeOffset observedAtUtc,
        int sessionByteBudget = DefaultPerSessionByteBudget)
    {
        if (sessionByteBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionByteBudget));
        }

        if (candidate.ReadBlocked)
        {
            return new JsonlReadResult(
                Array.Empty<JsonlActivityObservation>(),
                ReadError: true,
                HasBacklog: false);
        }

        var cursor = GetCursor(candidate.FullPath);
        var observations = new List<JsonlActivityObservation>();
        var result = ReadCandidateCore(
            candidate,
            cursor,
            observedAtUtc.ToUniversalTime(),
            sessionByteBudget,
            observations);
        return new JsonlReadResult(
            observations,
            result.ReadError,
            result.HasBacklog,
            result.BytesConsumed);
    }

    private FileCursor GetCursor(string path)
    {
        if (_cursors.TryGetValue(path, out var cursor))
        {
            return cursor;
        }

        cursor = new FileCursor();
        _cursors.Add(path, cursor);
        return cursor;
    }

    private static ReadCoreResult ReadCandidateCore(
        SessionFileCandidate candidate,
        FileCursor cursor,
        DateTimeOffset observedAtUtc,
        int sessionBudget,
        ICollection<JsonlActivityObservation> observations)
    {
        var bytesConsumed = 0;
        var firstRecordSignature = TryReadFirstRecordSignature(
            candidate.FullPath,
            Math.Min(FirstRecordProbeBytes, sessionBudget),
            out var signatureBytes,
            out var signatureReadError);
        bytesConsumed += signatureBytes;
        if (signatureReadError)
        {
            return new ReadCoreResult(bytesConsumed, ReadError: true, HasBacklog: false);
        }

        var remainingBudget = sessionBudget - signatureBytes;
        DateTimeOffset? creationTimeUtc;
        try
        {
            creationTimeUtc = new DateTimeOffset(
                DateTime.SpecifyKind(
                    File.GetCreationTimeUtc(candidate.FullPath),
                    DateTimeKind.Utc));
        }
        catch (IOException)
        {
            return new ReadCoreResult(bytesConsumed, ReadError: true, HasBacklog: false);
        }
        catch (UnauthorizedAccessException)
        {
            return new ReadCoreResult(bytesConsumed, ReadError: true, HasBacklog: false);
        }

        var replaced = cursor.Initialized
            && ((creationTimeUtc.HasValue
                    && cursor.CreationTimeUtc.HasValue
                    && creationTimeUtc.Value != cursor.CreationTimeUtc.Value)
                || (firstRecordSignature is not null
                    && cursor.FirstRecordSignature is not null
                    && !string.Equals(
                        firstRecordSignature,
                        cursor.FirstRecordSignature,
                        StringComparison.Ordinal)));
        var truncated = cursor.Initialized && candidate.Length < cursor.Offset;
        var reset = !cursor.Initialized || replaced || truncated;

        if (reset)
        {
            cursor.Reset();
            cursor.Initialized = true;
            cursor.CreationTimeUtc = creationTimeUtc;
            cursor.FirstRecordSignature = firstRecordSignature;
            if (remainingBudget <= 0)
            {
                return new ReadCoreResult(
                    bytesConsumed,
                    ReadError: false,
                    HasBacklog: candidate.Length > 0);
            }

            var startOffset = Math.Max(0, candidate.Length - remainingBudget);
            cursor.Offset = startOffset;
            cursor.DiscardUntilNewline = startOffset > 0
                && !IsLineBoundary(candidate.FullPath, startOffset);
        }
        else
        {
            cursor.CreationTimeUtc = creationTimeUtc;
            if (firstRecordSignature is not null)
            {
                cursor.FirstRecordSignature = firstRecordSignature;
            }
        }

        if (candidate.Length <= cursor.Offset || remainingBudget <= 0)
        {
            return new ReadCoreResult(
                bytesConsumed,
                ReadError: false,
                HasBacklog: candidate.Length > cursor.Offset);
        }

        var bytesToRead = (int)Math.Min(
            remainingBudget,
            candidate.Length - cursor.Offset);
        var start = cursor.Offset;
        try
        {
            using var stream = new FileStream(
                candidate.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 8192,
                options: FileOptions.SequentialScan);
            if (stream.Length < start)
            {
                cursor.Reset();
                cursor.Initialized = true;
                return new ReadCoreResult(bytesConsumed, ReadError: false, HasBacklog: true);
            }

            stream.Position = start;
            var buffer = new byte[Math.Min(8192, bytesToRead)];
            var read = 0;
            while (read < bytesToRead)
            {
                var requested = Math.Min(buffer.Length, bytesToRead - read);
                var count = stream.Read(buffer, 0, requested);
                if (count == 0)
                {
                    break;
                }

                ProcessBytes(
                    cursor,
                    buffer.AsSpan(0, count),
                    candidate.SessionId,
                    observedAtUtc,
                    observations);
                read += count;
            }

            cursor.Offset = start + read;
            bytesConsumed += read;
            return new ReadCoreResult(
                bytesConsumed,
                ReadError: false,
                HasBacklog: cursor.Offset < stream.Length);
        }
        catch (IOException)
        {
            return new ReadCoreResult(bytesConsumed, ReadError: true, HasBacklog: false);
        }
        catch (UnauthorizedAccessException)
        {
            return new ReadCoreResult(bytesConsumed, ReadError: true, HasBacklog: false);
        }
    }

    private static string? TryReadFirstRecordSignature(
        string path,
        int budget,
        out int bytesConsumed,
        out bool readError)
    {
        bytesConsumed = 0;
        readError = false;
        if (budget <= 0)
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1024,
                options: FileOptions.SequentialScan);
            var buffer = new byte[Math.Min(FirstRecordProbeBytes, budget)];
            var read = 0;
            while (read < buffer.Length)
            {
                var count = stream.Read(buffer, read, buffer.Length - read);
                if (count == 0)
                {
                    break;
                }

                read += count;
                var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
                if (newline >= 0)
                {
                    bytesConsumed = read;
                    return Convert.ToHexString(
                        SHA256.HashData(buffer.AsSpan(0, newline + 1)));
                }
            }

            bytesConsumed = read;
            return Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read)));
        }
        catch (IOException)
        {
            readError = true;
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            readError = true;
            return null;
        }
    }

    private static bool IsLineBoundary(string path, long offset)
    {
        if (offset <= 0)
        {
            return true;
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                options: FileOptions.SequentialScan);
            if (stream.Length < offset)
            {
                return false;
            }

            stream.Position = offset - 1;
            return stream.ReadByte() == '\n';
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void ProcessBytes(
        FileCursor cursor,
        ReadOnlySpan<byte> bytes,
        string sessionId,
        DateTimeOffset observedAtUtc,
        ICollection<JsonlActivityObservation> observations)
    {
        foreach (var value in bytes)
        {
            if (cursor.DiscardUntilNewline)
            {
                if (value == (byte)'\n')
                {
                    cursor.DiscardUntilNewline = false;
                    cursor.Line.Clear();
                    cursor.OversizedLine = false;
                }

                continue;
            }

            if (value == (byte)'\n')
            {
                if (!cursor.OversizedLine && cursor.Line.Count > 0)
                {
                    TryParseLine(
                        CollectionsMarshal.AsSpan(cursor.Line),
                        sessionId,
                        observedAtUtc,
                        observations);
                }

                cursor.Line.Clear();
                cursor.OversizedLine = false;
                continue;
            }

            if (cursor.Line.Count >= MaximumJsonLineBytes)
            {
                cursor.Line.Clear();
                cursor.OversizedLine = true;
                continue;
            }

            cursor.Line.Add(value);
        }
    }

    private static void TryParseLine(
        ReadOnlySpan<byte> line,
        string candidateSessionId,
        DateTimeOffset observedAtUtc,
        ICollection<JsonlActivityObservation> observations)
    {
        try
        {
            using var document = JsonDocument.Parse(line.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetString(root, "type", out var rootType)
                || !string.Equals(rootType, "event_msg", StringComparison.Ordinal)
                || !root.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object
                || !TryGetString(payload, "type", out var payloadType))
            {
                return;
            }

            if (TryGetString(payload, "session_id", out var payloadSessionId)
                && !string.Equals(
                    SessionIdHasher.Hash(payloadSessionId),
                    candidateSessionId,
                    StringComparison.Ordinal))
            {
                return;
            }

            switch (payloadType)
            {
                case "task_started":
                    observations.Add(new JsonlActivityObservation(
                        candidateSessionId,
                        JsonlActivityKind.TurnStarted,
                        observedAtUtc));
                    return;
                case "turn_aborted":
                    observations.Add(new JsonlActivityObservation(
                        candidateSessionId,
                        JsonlActivityKind.TurnAborted,
                        observedAtUtc));
                    return;
                case "task_complete":
                    if (!HasAssistantMessage(payload))
                    {
                        return;
                    }

                    observations.Add(new JsonlActivityObservation(
                        candidateSessionId,
                        JsonlActivityKind.TurnCompleted,
                        observedAtUtc));
                    return;
                default:
                    return;
            }
        }
        catch (JsonException)
        {
            // A malformed JSONL record is not state evidence.
        }
    }

    private static bool HasAssistantMessage(JsonElement payload)
    {
        if (!payload.TryGetProperty("last_agent_message", out var message)
            || message.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        if (message.ValueKind == JsonValueKind.String)
        {
            return !string.IsNullOrWhiteSpace(message.GetString());
        }

        if (message.ValueKind == JsonValueKind.Object)
        {
            return message.EnumerateObject().Any();
        }

        return true;
    }

    private static bool TryGetString(
        JsonElement element,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = property.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        value = text;
        return true;
    }

    private sealed class FileCursor
    {
        public long Offset { get; set; }

        public bool Initialized { get; set; }

        public DateTimeOffset? CreationTimeUtc { get; set; }

        public string? FirstRecordSignature { get; set; }

        public bool DiscardUntilNewline { get; set; }

        public bool OversizedLine { get; set; }

        public List<byte> Line { get; } = new();

        public void Reset()
        {
            Offset = 0;
            CreationTimeUtc = null;
            FirstRecordSignature = null;
            DiscardUntilNewline = false;
            OversizedLine = false;
            Line.Clear();
        }
    }

    private sealed record ReadCoreResult(
        int BytesConsumed,
        bool ReadError,
        bool HasBacklog);
}
