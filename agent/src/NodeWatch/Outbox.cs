using System.Text.Json;
using NodeWatch.Sinks;

namespace NodeWatch;

internal sealed class OutboxItem
{
    public DateTimeOffset CreatedAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>"both" mode: the copy for the fallback sinks was already made.</summary>
    public bool FallbackDone { get; set; }

    public CloudEvent Event { get; set; } = new();
}

/// <summary>
/// One queue folder per sink on disk (/var/lib/nodewatch/outbox/&lt;sink&gt;), so nothing is lost when the agent or the
/// machine restarts. Each sink is worked in order; a failing sink does not hold up the others.
/// </summary>
internal sealed class Outbox
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(10);
    private readonly string _root;
    private readonly Dictionary<string, ISink> _sinks;
    private readonly ISink? _relay;
    private readonly IReadOnlyList<ISink> _fallback;
    private readonly TimeSpan _fallbackAfter;
    private long _sequence;

    /// <summary>Queue files that could not be deleted (wrong owner?): never sent twice, never retried.</summary>
    private readonly HashSet<string> _done = new(StringComparer.Ordinal);

    /// <summary>In-memory copies of what could not be written back to a queue file (retry time, fallback done).</summary>
    private readonly Dictionary<string, DateTimeOffset> _retryAt = new(StringComparer.Ordinal);
    private readonly HashSet<string> _fallbackDone = new(StringComparer.Ordinal);

    /// <param name="relay">In relay or both mode, the relay sink.</param>
    /// <param name="fallback">In both mode, where critical events go that the relay did not take in time.</param>
    public Outbox(string root, IEnumerable<ISink> sinks, ISink? relay, IReadOnlyList<ISink> fallback, TimeSpan fallbackAfter)
    {
        _root = Path.Combine(root, "outbox");
        _sinks = sinks.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        _relay = relay;
        if (relay is not null) _sinks[relay.Name] = relay;
        _fallback = fallback;
        _fallbackAfter = fallbackAfter;
    }

    public void Enqueue(ISink sink, CloudEvent e, DateTimeOffset now)
    {
        var dir = Path.Combine(_root, Safe(sink.Name));
        Directory.CreateDirectory(dir);
        var item = new OutboxItem { CreatedAt = now, NextAttemptAt = now, Event = e };
        // The counter keeps the order of events queued in the same millisecond.
        var seq = Interlocked.Increment(ref _sequence) % 1_000_000;
        var path = Path.Combine(dir, $"{now.ToUnixTimeMilliseconds():D13}-{seq:D6}-{Safe(e.Id)}.json");
        Write(path, item);
    }

    public int Pending => _sinks.Keys.Sum(name =>
        Directory.Exists(Path.Combine(_root, Safe(name))) ? Directory.GetFiles(Path.Combine(_root, Safe(name)), "*.json").Length : 0);

    /// <summary>One pass over every queue. Returns how many events went out.</summary>
    public async Task<int> DispatchAsync(DateTimeOffset now, CancellationToken ct)
    {
        var sent = 0;
        foreach (var sink in _sinks.Values)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                sent += await DispatchSinkAsync(sink, now, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                // A broken queue folder of one output must not hold up the others.
                Log.Error($"{sink.Name}: a sor feldolgozása nem sikerült: {e.Message}");
            }
        }
        return sent;
    }

    private async Task<int> DispatchSinkAsync(ISink sink, DateTimeOffset now, CancellationToken ct)
    {
        var sent = 0;
        var dir = Path.Combine(_root, Safe(sink.Name));
        if (!Directory.Exists(dir)) return 0;
        var files = Directory.GetFiles(dir, "*.json").Order(StringComparer.Ordinal).ToList();
        if (ReferenceEquals(sink, _relay))
            foreach (var path in files)
                if (Read(path) is { } waiting) FallbackIfLate(waiting, path, now);
        foreach (var path in files)
        {
            if (ct.IsCancellationRequested) return sent;
            if (_done.Contains(path)) continue;
            var item = Read(path);
            if (item is null)
            {
                Remove(path);
                continue;
            }
            if (now - item.CreatedAt > MaxAge)
            {
                Log.Error($"{sink.Name}: 24 óra alatt sem sikerült elküldeni, eldobom: {item.Event.Data.Title}");
                Remove(path);
                continue;
            }
            // After the clock was set back a retry time may lie far ahead: never wait longer than the longest backoff.
        if (_retryAt.TryGetValue(path, out var retryAt) && retryAt > item.NextAttemptAt) item.NextAttemptAt = retryAt;
        if (item.NextAttemptAt > now + MaxBackoff) item.NextAttemptAt = now;
        if (item.NextAttemptAt > now) break; // keep the order: later items wait for this one

            SendResult result;
            try
            {
                result = await sink.SendAsync(item.Event, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                // Unexpected (a bug or a strange token): retried with backoff like a network error, and logged.
                result = new SendResult(Outcome.Retry, $"{e.GetType().Name}: {e.Message}");
            }
            if (result.Outcome == Outcome.Ok)
            {
                Remove(path);
                sent++;
                Log.Info($"{sink.Name}: elküldve – {EventText.Headline(item.Event)}");
                continue;
            }
            if (result.Outcome == Outcome.Drop)
            {
                Log.Error($"{sink.Name}: a kimenet elutasította, eldobom ({result.Message}): {item.Event.Data.Title}");
                Remove(path);
                continue;
            }
            item.Attempts++;
            var delay = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, 5 * Math.Pow(2, Math.Min(item.Attempts - 1, 10))));
            item.NextAttemptAt = now + delay;
            _retryAt[path] = item.NextAttemptAt;
            TryWrite(path, item);
            Log.Warn($"{sink.Name}: sikertelen küldés ({result.Message}), újra {delay.TotalSeconds:0} mp múlva.");
            break;
        }
        return sent;
    }

    /// <summary>"both" mode: a critical event the relay has not taken within the limit also goes to the fallback sinks.</summary>
    private void FallbackIfLate(OutboxItem item, string path, DateTimeOffset now)
    {
        if (item.FallbackDone || _fallbackDone.Contains(path) || _fallback.Count == 0 || item.Event.IsHeartbeat) return;
        if (item.Event.Data.Severity != "critical" || now - item.CreatedAt < _fallbackAfter) return;
        _fallbackDone.Add(path);
        foreach (var sink in _fallback)
        {
            try { Enqueue(sink, item.Event, now); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Error($"{sink.Name}: a tartalék nem tehető sorba ({e.Message}): {item.Event.Data.Title}");
            }
        }
        item.FallbackDone = true;
        TryWrite(path, item);
        Log.Warn($"A relay {_fallbackAfter.TotalMinutes:0.#} perce nem veszi át, tartalék kimenetre is küldöm: {item.Event.Data.Title}");
    }

    private static void TryWrite(string path, OutboxItem item)
    {
        try
        {
            Write(path, item);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error($"A sorfájl nem írható ({e.Message}): {path}");
        }
    }

    /// <summary>Deletes a finished queue file. If that fails, the file is remembered as finished, so it is not sent again.</summary>
    private void Remove(string path)
    {
        try
        {
            File.Delete(path);
            _done.Remove(path);
            _retryAt.Remove(path);
            _fallbackDone.Remove(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (_done.Add(path))
                Log.Error($"A sorfájl nem törölhető ({e.Message}): {path}. Javítás: sudo chown -R nodewatch:nodewatch " +
                          $"{Path.GetDirectoryName(Path.GetDirectoryName(path))}");
        }
    }

    private static OutboxItem? Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<OutboxItem>(File.ReadAllText(path), CloudEvent.Json);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Warn($"Sérült vagy nem olvasható sorfájl, törlöm: {path} ({e.Message})");
            return null;
        }
    }

    private static void Write(string path, OutboxItem item)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(item, CloudEvent.Json));
        File.Move(tmp, path, overwrite: true);
    }

    private static string Safe(string name) =>
        string.Concat(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}
