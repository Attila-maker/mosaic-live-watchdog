using NodeWatch.Sinks;

namespace NodeWatch;

/// <summary>Ties it together: poll → checks → alerts and restart decision → outbox → sinks; plus the relay heartbeat.</summary>
internal sealed class Agent
{
    private readonly Config _config;
    private readonly IProbe _probe;
    private readonly IReadOnlyList<ISink> _sinks;
    private readonly ISink? _relay;
    private readonly StateStore _store;
    private readonly AgentState _state;
    private readonly CheckEvaluator _evaluator;
    private readonly EventFactory _events;
    private readonly AlertEngine _alerts;
    private readonly RestartGuard _guard;
    private Snapshot? _last;
    private DateTimeOffset? _heartbeatFailingSince;
    private bool _heartbeatWarned;
    private int _failedTicks;

    /// <summary>After this many failed polls in a row the agent reports that it cannot watch the node.</summary>
    private const int FailedTicksBeforeAlert = 5;
    private const string AgentKey = "agent";

    public Outbox Outbox { get; }

    public Agent(Config config, IProbe probe, IServiceStatus services, IRestarter restarter, IReadOnlyList<ISink> sinks,
        ISink? relay, StateStore store, DateTimeOffset now, IBootClock clock)
    {
        _config = config;
        _probe = probe;
        _sinks = sinks;
        _relay = relay;
        _store = store;
        _state = store.Load(now);
        _evaluator = new CheckEvaluator(config);
        _events = new EventFactory(config);
        _alerts = new AlertEngine(config, _events, _state.Alerts);
        _guard = new RestartGuard(config, _events, restarter, services, _state, store.TrySave, clock);
        var alertsToRelay = config.Mode is "relay" or "both";
        Outbox = new Outbox(config.StateDirectory, sinks, alertsToRelay ? relay : null,
            config.Mode == "both" ? sinks : [], TimeSpan.FromMinutes(config.Relay.FallbackAfterMinutes));
    }

    public async Task<List<CloudEvent>> TickAsync(DateTimeOffset now, CancellationToken ct)
    {
        var snapshot = await _probe.CollectAsync(now, ct);
        var results = _evaluator.Evaluate(snapshot);
        _alerts.FreezeNodeChecksUntil = _state.GraceUntil ?? DateTimeOffset.MinValue;
        var output = _alerts.Process(results, now);
        output.AddRange(await _guard.EvaluateAsync(results, snapshot, now, ct));
        // A restart decided just now starts the grace period for the alerts too.
        _alerts.FreezeNodeChecksUntil = _state.GraceUntil ?? DateTimeOffset.MinValue;
        Publish(output, now);
        _state.Alerts = _alerts.Tracks;
        _store.TrySave(_state);
        _last = snapshot;
        Log.Debug($"relay #{snapshot.RelayBest} (full peer {snapshot.RelayPeers?.SyncSources}), para #{snapshot.ParaBest} " +
                  $"fin #{snapshot.ParaFinalized}, validátor {snapshot.Validator?.Kind}");
        return output;
    }

    /// <summary>The monitor loop's body: a poll that throws is counted, and a lasting failure becomes an alert itself.</summary>
    public async Task<List<CloudEvent>> SafeTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            var output = await TickAsync(now, ct);
            _failedTicks = 0;
            if (_state.AgentAlertSince is { } since)
            {
                var resolved = _events.Resolved(AgentKey, Level.Critical, "A NodeWatch újra ellenőrzi a node-ot",
                    $"A hiba {Text.Length(now - since)} után szűnt meg.", since, now);
                Publish([resolved], now);
                output.Add(resolved);
                _state.AgentAlertSince = null;
                _store.TrySave(_state);
            }
            return output;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            _failedTicks++;
            Log.Error($"Hiba az ellenőrzés közben ({_failedTicks}. egymás után): {e}");
            if (_failedTicks < FailedTicksBeforeAlert || _state.AgentAlertSince is not null) return [];
            _state.AgentAlertSince = now;
            _store.TrySave(_state);
            var firing = _events.Firing(AgentKey, Level.Critical, "A NodeWatch nem tudja ellenőrizni a node-ot",
                $"{_failedTicks} ellenőrzés egymás után hibával állt le: {e.GetType().Name}: {e.Message}. " +
                "Ilyenkor újraindítás sincs. Nézd meg a naplót: journalctl -u nodewatch", now, now);
            Publish([firing], now);
            return [firing];
        }
    }

    private void Publish(IEnumerable<CloudEvent> events, DateTimeOffset now)
    {
        foreach (var e in events)
        {
            Log.Info($"Esemény: {EventText.Headline(e)}");
            try
            {
                Route(e, now);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // One event that cannot be queued (full disk?) must not take the others with it.
                Log.Error($"Az esemény nem tehető sorba ({ex.Message}): {EventText.Headline(e)}");
            }
        }
    }

    private void Route(CloudEvent e, DateTimeOffset now)
    {
        if (_config.Mode is "relay" or "both")
        {
            Outbox.Enqueue(_relay!, e, now);
            return;
        }
        var rank = Severity.Rank(e.Data.Severity ?? "info");
        foreach (var sink in _sinks.Where(s => rank >= s.MinRank))
        {
            try
            {
                Outbox.Enqueue(sink, e, now);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error($"{sink.Name}: az esemény nem tehető sorba ({ex.Message}): {EventText.Headline(e)}");
            }
        }
    }

    /// <summary>Tells the relay the agent is alive (sent directly, never queued: an old heartbeat is worthless).</summary>
    public async Task HeartbeatAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_relay is null) return;
        var s = _last;
        var facts = new Dictionary<string, object?>
        {
            ["relayBest"] = s?.RelayBest, ["fullPeers"] = s?.RelayPeers?.SyncSources, ["totalPeers"] = s?.RelayPeers?.Total,
            ["paraBest"] = s?.ParaBest, ["paraFinalized"] = s?.ParaFinalized, ["validator"] = s?.Validator?.Kind.ToString(),
            ["agentVersion"] = Program.Version,
        };
        var result = await _relay.SendAsync(_events.Heartbeat(now, facts), ct);
        if (result.Outcome == Outcome.Ok)
        {
            if (_heartbeatWarned) Log.Info("A relay újra fogadja az „élek” jelet.");
            _heartbeatFailingSince = null;
            _heartbeatWarned = false;
            return;
        }
        _heartbeatFailingSince ??= now;
        if (!_heartbeatWarned && now - _heartbeatFailingSince >= TimeSpan.FromMinutes(5))
        {
            Log.Warn($"A relay 5 perce nem fogadja az „élek” jelet: {result.Message}");
            _heartbeatWarned = true;
        }
        else
        {
            Log.Debug($"Élek jel sikertelen: {result.Message}");
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var monitor = Loop(TimeSpan.FromSeconds(_config.Checks.IntervalSeconds), now => SafeTickAsync(now, ct), "ellenőrzés", ct);
        var dispatch = Loop(TimeSpan.FromSeconds(2), now => Outbox.DispatchAsync(now, ct), "küldés", ct);
        var heartbeat = _relay is null
            ? Task.CompletedTask
            : Loop(TimeSpan.FromSeconds(Math.Max(15, _config.Relay.HeartbeatSeconds)), now => HeartbeatAsync(now, ct), "élek jel", ct);
        await Task.WhenAll(monitor, dispatch, heartbeat);
    }

    private static async Task Loop(TimeSpan every, Func<DateTimeOffset, Task> body, string what, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(every);
        do
        {
            try
            {
                await body(DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // One failed round must not stop the watchdog.
                Log.Error($"Hiba ({what}): {e}");
            }
        } while (await WaitAsync(timer, ct));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
