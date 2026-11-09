namespace NodeWatch;

internal enum Level { Ok = 0, Warning = 1, Critical = 2 }

internal static class Severity
{
    public static string Name(Level level) => level switch
    {
        Level.Critical => "critical",
        Level.Warning => "warning",
        _ => "info",
    };

    public static int Rank(string severity) => severity switch
    {
        "critical" => 2,
        "warning" => 1,
        _ => 0,
    };

    public static bool TryParse(string? text, out int rank)
    {
        rank = text switch { "info" => 0, "warning" => 1, "critical" => 2, _ => -1 };
        return rank >= 0;
    }
}

internal static class CheckNames
{
    public const string RelayRpc = "relay.rpc";
    public const string RelayBlocks = "relay.block_progress";
    public const string RelayFullPeers = "relay.full_peers";
    public const string ParaRpc = "para.rpc";
    public const string ParaFinality = "para.finality";
    public const string ParaSync = "para.sync";
    public const string Validator = "validator.state";
    public const string Disk = "host.disk";
    public const string ServicePrefix = "host.service.";

    public static readonly HashSet<string> All =
        [RelayRpc, RelayBlocks, RelayFullPeers, ParaRpc, ParaFinality, ParaSync, Validator, Disk];

    /// <summary>Checks that a node restart disturbs: frozen during the grace period after a restart.</summary>
    public static bool IsNodeCheck(string name) => name != Disk;
}

/// <summary>One check's verdict at one poll. For <see cref="Level.Ok"/>, the title and text describe the recovery.</summary>
internal sealed record CheckResult(string Name, Level Level, string Title, string Text, DateTimeOffset? Since = null,
    Dictionary<string, object?>? Facts = null);

/// <summary>Remembers when a number last grew.</summary>
internal sealed class ProgressTracker
{
    public long? Last { get; private set; }
    public DateTimeOffset LastChange { get; private set; }

    public void Update(long value, DateTimeOffset now)
    {
        if (Last is null || value > Last)
        {
            Last = value;
            LastChange = now;
        }
        else if (value < Last)
        {
            // Went back (node resynced or restarted from older state): a new starting point, not a stall.
            Last = value;
            LastChange = now;
        }
    }

    /// <summary>The agent did not watch for a while: measure from now, not from before the gap.</summary>
    public void Restart(DateTimeOffset now)
    {
        if (Last is not null) LastChange = now;
    }
}

/// <summary>Turns snapshots into check results. Keeps only what "for how long" needs; no alerting logic here.</summary>
internal sealed class CheckEvaluator(Config config)
{
    private readonly ProgressTracker _relay = new();
    private readonly ProgressTracker _finality = new();
    private DateTimeOffset? _relayRpcDownSince, _paraRpcDownSince, _noFullPeersSince, _syncingSince, _noParaPeersSince;
    private DateTimeOffset? _lastAt;

    public List<CheckResult> Evaluate(Snapshot s)
    {
        var now = s.At;
        var c = config.Checks;
        var results = new List<CheckResult>();

        // Suspended machine, paused VM or a clock jump: what happened in between was not seen, so every
        // "for how long" starts again instead of counting the gap as an outage.
        if (_lastAt is { } last && (now - last > c.GapLimit || last - now > CheckOptions.BackwardTolerance))
        {
            Log.Warn($"{Text.Length((now - last).Duration())} szünet volt két ellenőrzés között (felfüggesztés vagy óraugrás); " +
                     "az időtartamokat újrakezdem.");
            _relay.Restart(now);
            _finality.Restart(now);
            if (_relayRpcDownSince is not null) _relayRpcDownSince = now;
            if (_paraRpcDownSince is not null) _paraRpcDownSince = now;
            if (_noFullPeersSince is not null) _noFullPeersSince = now;
            if (_syncingSince is not null) _syncingSince = now;
            if (_noParaPeersSince is not null) _noParaPeersSince = now;
        }
        _lastAt = now;

        // ---- relay chain ----
        if (s.RelayError is not null)
        {
            _relayRpcDownSince ??= now;
            results.Add(new(CheckNames.RelayRpc, Level.Critical, "A relay chain RPC nem válaszol",
                $"{Text.Duration(now - _relayRpcDownSince.Value)} nem válaszol: {s.RelayError}.", _relayRpcDownSince));
        }
        else
        {
            _relayRpcDownSince = null;
            results.Add(new(CheckNames.RelayRpc, Level.Ok, "A relay chain RPC újra válaszol", "A node RPC-je elérhető."));
        }

        if (s.RelayBest is { } relayBest)
        {
            _relay.Update(relayBest, now);
            var peers = s.RelayPeers;
            var facts = new Dictionary<string, object?>
            {
                ["relayBest"] = relayBest, ["fullPeers"] = peers?.SyncSources, ["nonePeers"] = peers?.None,
                ["totalPeers"] = s.RelayTotalPeers,
            };
            var peerText = peers is not null
                ? $"Full peer: {peers.SyncSources}, összes peer: {peers.Total} (ebből szerep nélküli, NONE: {peers.None})."
                : s.RelayTotalPeers is { } count ? $"Peerek: {count} (a szerepük nem kérdezhető le)."
                : "A peerek nem kérdezhetők le.";
            var stall = now - _relay.LastChange;
            results.Add(stall >= TimeSpan.FromMinutes(c.RelayStallMinutes)
                ? new(CheckNames.RelayBlocks, Level.Critical, "A relay chain nem halad",
                    $"{Text.Duration(stall)} a #{_relay.Last} blokkon áll. {peerText}", _relay.LastChange, facts)
                : new(CheckNames.RelayBlocks, Level.Ok, "A relay chain újra halad", $"A #{relayBest} blokknál tart.", null, facts));

            if (peers is null && s.RelayTotalPeers is { } total)
            {
                // The relay chain RPC only allows safe methods: the roles (FULL / NONE) are unknown, only the count is.
                // No peer at all is still a stall reason; a stall with only NONE peers shows in relay.block_progress.
                if (total == 0)
                {
                    _noFullPeersSince ??= now;
                    results.Add(new(CheckNames.RelayFullPeers, Level.Critical, "A relay chain-nek nincs egy peere sem",
                        "Peer nélkül a node nem tud szinkronizálni. (A peerek szerepe nem kérdezhető le, mert a relay chain " +
                        "RPC csak biztonságos hívásokat enged.)", _noFullPeersSince, facts));
                }
                else
                {
                    _noFullPeersSince = null;
                    results.Add(new(CheckNames.RelayFullPeers, Level.Ok, "A relay chain-nek újra van peere",
                        $"Peerek: {total} (a szerepük nem kérdezhető le).", null, facts));
                }
            }
            else if (peers is null)
            {
                // Not a restart reason: the node may simply not allow system_peers ("unsafe" RPC method).
                _noFullPeersSince = null;
                results.Add(new(CheckNames.RelayFullPeers, Level.Warning, "A relay chain peerjei nem kérdezhetők le",
                    $"{s.RelayPeersError}. Sem a system_peers, sem a system_health nem adott választ.", now, facts));
            }
            else if (peers.SyncSources == 0)
            {
                _noFullPeersSince ??= now;
                results.Add(new(CheckNames.RelayFullPeers, Level.Critical, "A relay chain-nek nincs full peere",
                    $"Peerek: {peers.Total}, ebből szerep nélküli (NONE): {peers.None}. Full peer nélkül a node nem tud " +
                    "szinkronizálni.", _noFullPeersSince, facts));
            }
            else
            {
                _noFullPeersSince = null;
                results.Add(peers.SyncSources == 1
                    ? new(CheckNames.RelayFullPeers, Level.Warning, "Csak egy full peer van a relay chain-en",
                        $"Full peer: 1, összes peer: {peers.Total} (NONE: {peers.None}). Ha ez is kiesik, a node megállhat.",
                        now, facts)
                    : new(CheckNames.RelayFullPeers, Level.Ok, "A relay chain-nek újra van elég full peere",
                        $"Full peer: {peers.SyncSources}, összes peer: {peers.Total}.", null, facts));
            }
        }

        // ---- parachain ----
        if (s.ParaError is not null)
        {
            _paraRpcDownSince ??= now;
            results.Add(new(CheckNames.ParaRpc, Level.Critical, "A parachain RPC nem válaszol",
                $"{Text.Duration(now - _paraRpcDownSince.Value)} nem válaszol: {s.ParaError}.", _paraRpcDownSince));
        }
        else
        {
            _paraRpcDownSince = null;
            results.Add(new(CheckNames.ParaRpc, Level.Ok, "A parachain RPC újra válaszol", "A node RPC-je elérhető."));
        }

        if (s.ParaFinalized is { } finalized)
        {
            _finality.Update(finalized, now);
            var stall = now - _finality.LastChange;
            var facts = new Dictionary<string, object?> { ["paraBest"] = s.ParaBest, ["paraFinalized"] = finalized };
            results.Add(stall >= TimeSpan.FromMinutes(c.FinalityStallMinutes)
                ? new(CheckNames.ParaFinality, Level.Critical, "A parachain nem véglegesít",
                    $"{Text.Duration(stall)} a #{_finality.Last} blokknál áll a véglegesítés (legjobb blokk: #{s.ParaBest}).",
                    _finality.LastChange, facts)
                : new(CheckNames.ParaFinality, Level.Ok, "A parachain újra véglegesít", $"Véglegesítve: #{finalized}.", null,
                    facts));
        }

        if (s.ParaHealth is { } health)
        {
            if (health.Peers == 0)
            {
                _noParaPeersSince ??= now;
                results.Add(new(CheckNames.ParaSync, Level.Critical, "A parachain-nek nincs peere",
                    "A parachain node egyetlen másik node-dal sincs kapcsolatban.", _noParaPeersSince));
            }
            else
            {
                _noParaPeersSince = null;
                if (health.IsSyncing) _syncingSince ??= now;
                else _syncingSince = null;
                results.Add(_syncingSince is { } since && now - since >= TimeSpan.FromMinutes(c.SyncWarnMinutes)
                    ? new(CheckNames.ParaSync, Level.Warning, "A parachain régóta szinkronizál",
                        $"{Text.Duration(now - since)} szinkronizál, peerek: {health.Peers}.", since)
                    : new(CheckNames.ParaSync, Level.Ok, "A parachain szinkronban van", $"Peerek: {health.Peers}."));
            }
        }

        // ---- validator ----
        if (!string.IsNullOrWhiteSpace(config.Node.ValidatorAddress) && s.ParaError is null)
        {
            if (s.ValidatorError is not null)
            {
                results.Add(new(CheckNames.Validator, Level.Warning, "A validátor állapota nem kérdezhető le",
                    $"{s.ValidatorError}. Ha a runtime frissült, lehet, hogy a NodeWatch-ot is frissíteni kell."));
            }
            else if (s.ValidatorMissing)
            {
                results.Add(new(CheckNames.Validator, Level.Warning, "A validátor nem található",
                    "A megadott cím nincs a NftStaking.ValidatorStates tárolóban. Ellenőrizd a ValidatorAddress beállítást."));
            }
            else if (s.Validator is { } v)
            {
                results.Add(v.Kind switch
                {
                    ValidatorStateKind.Faulted => new CheckResult(CheckNames.Validator, Level.Warning,
                        "A validátor Faulted állapotban van",
                        "Slash történt vagy várható. A következő slash után automatikusan chillelődik."),
                    ValidatorStateKind.Chilled => new CheckResult(CheckNames.Validator, Level.Critical,
                        "A validátor chillelve van",
                        $"Chill a(z) {v.ChilledSession}. sessionben. Ha nem te chillelted, nézd meg a dashboardot."),
                    _ => new CheckResult(CheckNames.Validator, Level.Ok, "A validátor újra Normal állapotban van",
                        "Állapot: Normal."),
                });
            }
        }

        // ---- host ----
        var first = true;
        foreach (var unit in config.Node.Services)
        {
            var state = s.Services.GetValueOrDefault(unit, "unknown");
            var name = CheckNames.ServicePrefix + unit;
            results.Add(state == "active"
                ? new(name, Level.Ok, $"A(z) {unit} újra fut", "Állapot: active.")
                : new(name, first ? Level.Critical : Level.Warning, $"A(z) {unit} nem fut", $"Állapot: {state}."));
            first = false;
        }

        if (s.Disk is { } disk)
        {
            var text = $"{disk.Path}: {disk.FreePercent:0.#}% szabad ({disk.FreeGb:0.#} GB).";
            results.Add(disk.FreePercent < c.DiskCriticalPercent
                ? new(CheckNames.Disk, Level.Critical, "Nagyon kevés a szabad hely", text)
                : disk.FreePercent < c.DiskWarnPercent
                    ? new(CheckNames.Disk, Level.Warning, "Kevés a szabad hely", text)
                    : new(CheckNames.Disk, Level.Ok, "Van elég szabad hely", text));
        }
        else
        {
            results.Add(new(CheckNames.Disk, Level.Warning, "A szabad hely nem kérdezhető le",
                $"{s.DiskError}. Ellenőrizd a Node.DataPath beállítást."));
        }

        return results;
    }
}

internal static class Text
{
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        var minutes = (int)Math.Round(span.TotalMinutes);
        if (span.TotalMinutes < 1) return $"{Math.Max(1, (int)span.TotalSeconds)} másodperce";
        if (minutes < 60) return $"{minutes} perce";
        var hours = minutes / 60;
        var rest = minutes % 60;
        return rest == 0 ? $"{hours} órája" : $"{hours} óra {rest} perce";
    }

    /// <summary>"12 perc", "1 óra 5 perc" (for "… után").</summary>
    public static string Length(TimeSpan span)
    {
        var minutes = Math.Max(1, (int)Math.Round(span.TotalMinutes));
        if (minutes < 60) return $"{minutes} perc";
        var rest = minutes % 60;
        return rest == 0 ? $"{minutes / 60} óra" : $"{minutes / 60} óra {rest} perc";
    }
}
