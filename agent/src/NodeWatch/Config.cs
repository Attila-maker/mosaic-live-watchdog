using System.Text.Json;
using System.Text.Json.Serialization;

namespace NodeWatch;

/// <summary>The whole configuration (/etc/nodewatch/appsettings.json). Secrets are never in it, only paths to them.</summary>
internal sealed class Config
{
    public NodeOptions Node { get; set; } = new();
    public CheckOptions Checks { get; set; } = new();
    public RestartOptions Restart { get; set; } = new();

    /// <summary>direct: the agent sends to <see cref="Sinks"/>; relay: only to the relay; both: to the relay, and critical
    /// alerts the relay did not take within <see cref="RelayOptions.FallbackAfterMinutes"/> also to <see cref="Sinks"/>.</summary>
    public string Mode { get; set; } = "direct";

    public RelayOptions Relay { get; set; } = new();
    public List<SinkOptions> Sinks { get; set; } = [];

    /// <summary>Where the outbox and the state live (systemd StateDirectory).</summary>
    public string StateDirectory { get; set; } = "/var/lib/nodewatch";

    public bool VerboseLog { get; set; }

    [JsonIgnore] public string BaseDirectory { get; private set; } = "/etc/nodewatch";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Config Load(string path)
    {
        if (!File.Exists(path)) throw new ConfigException($"A konfigurációs fájl nem található: {path}");
        Config? config;
        try
        {
            config = JsonSerializer.Deserialize<Config>(File.ReadAllText(path), Json);
        }
        catch (JsonException e)
        {
            throw new ConfigException($"Hibás JSON a konfigurációban ({path}): {e.Message}");
        }
        if (config is null) throw new ConfigException($"Üres konfiguráció: {path}");
        config.BaseDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "/etc/nodewatch";
        var errors = config.Validate();
        if (errors.Count > 0) throw new ConfigException("Hibás konfiguráció:\n  - " + string.Join("\n  - ", errors));
        return config;
    }

    public List<string> Validate()
    {
        var e = new List<string>();
        if (string.IsNullOrWhiteSpace(Node.Name)) e.Add("Node.Name kötelező.");
        if (!Uri.TryCreate(Node.RelayRpc, UriKind.Absolute, out _)) e.Add("Node.RelayRpc nem érvényes URL.");
        if (!Uri.TryCreate(Node.ParaRpc, UriKind.Absolute, out _)) e.Add("Node.ParaRpc nem érvényes URL.");
        if (!string.IsNullOrWhiteSpace(Node.ValidatorAddress))
        {
            try { Ss58.DecodePublicKey(Node.ValidatorAddress); }
            catch (FormatException ex) { e.Add($"Node.ValidatorAddress: {ex.Message}"); }
        }
        if (Checks.IntervalSeconds is < 5 or > 600) e.Add("Checks.IntervalSeconds 5 és 600 között legyen.");
        if (Mode is not ("direct" or "relay" or "both")) e.Add("Mode értéke direct, relay vagy both lehet.");
        if (Mode is "relay" or "both")
        {
            if (!Uri.TryCreate(Relay.Url, UriKind.Absolute, out _)) e.Add("Relay.Url kötelező relay és both módban.");
            if (string.IsNullOrWhiteSpace(Relay.NodeTokenFile)) e.Add("Relay.NodeTokenFile kötelező relay és both módban.");
        }
        if (Mode is "direct" or "both" && Sinks.Count == 0) e.Add("Legalább egy kimenet (Sinks) kell direct és both módban.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Sinks)
        {
            if (s.Type is not ("telegram" or "matrix" or "safechat")) e.Add($"Ismeretlen kimenet-típus: {s.Type}");
            if (!names.Add(s.DisplayName)) e.Add($"Két kimenetnek ugyanaz a neve: {s.DisplayName} (adj meg Name-et).");
            if (!Severity.TryParse(s.MinSeverity, out _)) e.Add($"{s.DisplayName}: MinSeverity értéke info, warning vagy critical.");
            switch (s.Type)
            {
                case "telegram":
                    if (string.IsNullOrWhiteSpace(s.BotTokenFile)) e.Add($"{s.DisplayName}: BotTokenFile kötelező.");
                    if (string.IsNullOrWhiteSpace(s.ChatId)) e.Add($"{s.DisplayName}: ChatId kötelező.");
                    break;
                case "matrix":
                    if (!Uri.TryCreate(s.Homeserver, UriKind.Absolute, out _)) e.Add($"{s.DisplayName}: Homeserver nem érvényes URL.");
                    if (string.IsNullOrWhiteSpace(s.RoomId)) e.Add($"{s.DisplayName}: RoomId kötelező.");
                    if (string.IsNullOrWhiteSpace(s.AccessTokenFile)) e.Add($"{s.DisplayName}: AccessTokenFile kötelező.");
                    break;
                case "safechat":
                    if (!Uri.TryCreate(s.Url, UriKind.Absolute, out _)) e.Add($"{s.DisplayName}: Url nem érvényes.");
                    if (string.IsNullOrWhiteSpace(s.TokenFile)) e.Add($"{s.DisplayName}: TokenFile kötelező.");
                    break;
            }
        }
        if (Restart.Enabled)
        {
            if (Restart.AfterMinutes < 1) e.Add("Restart.AfterMinutes legalább 1 legyen.");
            if (Restart.MinHoursBetween < 1) e.Add("Restart.MinHoursBetween legalább 1 legyen.");
            if (Restart.GraceMinutes < 0) e.Add("Restart.GraceMinutes nem lehet negatív.");
            if (string.IsNullOrWhiteSpace(Restart.Unit)) e.Add("Restart.Unit kötelező.");
            if (Restart.Command.Count == 0) e.Add("Restart.Command nem lehet üres.");
            else if (Path.GetFileName(Restart.Command[0]) == "systemctl" && !Restart.Command.Contains(Restart.Unit))
                e.Add($"Restart.Command nem a Restart.Unit unitot ({Restart.Unit}) indítja újra.");
            foreach (var t in Restart.Triggers)
                if (!CheckNames.All.Contains(t)) e.Add($"Restart.Triggers: ismeretlen ellenőrzés: {t}");
        }
        return e;
    }

    /// <summary>Reads a secret file (relative paths are relative to the config's folder). The content is trimmed.</summary>
    public string ReadSecret(string path)
    {
        var full = Path.IsPathRooted(path) ? path : Path.Combine(BaseDirectory, path);
        if (!File.Exists(full)) throw new ConfigException($"A titokfájl nem található: {full}");
        string value;
        try
        {
            value = File.ReadAllText(full).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ConfigException($"A titokfájl nem olvasható: {full} ({e.Message})");
        }
        if (value.Length == 0) throw new ConfigException($"A titokfájl üres: {full}");
        if (value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ConfigException($"A titokfájlban csak a token legyen, egy sorban, szóköz nélkül: {full}");
        return value;
    }
}

internal sealed class NodeOptions
{
    public string Name { get; set; } = "";
    public string Network { get; set; } = "mosaic-mainnet";

    /// <summary>SS58 address or 0x public key of the validator; empty: no validator-state check.</summary>
    public string ValidatorAddress { get; set; } = "";

    public string RelayRpc { get; set; } = "http://127.0.0.1:8844";
    public string ParaRpc { get; set; } = "http://127.0.0.1:9944";
    public string DataPath { get; set; } = "/var/lib/mosaic-mainnet";

    /// <summary>systemd units to watch: the first is the chain (critical when down), the rest only warn.</summary>
    public List<string> Services { get; set; } = ["mosaic-chain-mainnet.service", "mosaic-indexer-mainnet.service"];
}

internal sealed class CheckOptions
{
    public int IntervalSeconds { get; set; } = 30;
    public double RelayStallMinutes { get; set; } = 3;
    public double FinalityStallMinutes { get; set; } = 5;
    public double SyncWarnMinutes { get; set; } = 10;
    public double ReminderMinutes { get; set; } = 30;

    /// <summary>A change counts once it was seen this many polls in a row.</summary>
    public int ConfirmPolls { get; set; } = 2;

    public double DiskWarnPercent { get; set; } = 15;
    public double DiskCriticalPercent { get; set; } = 5;
    public int RpcTimeoutSeconds { get; set; } = 10;

    /// <summary>A longer pause between two polls (machine suspended, VM paused, clock jump) means the agent did not watch:
    /// "for how long" measurements start again.</summary>
    [JsonIgnore] public TimeSpan GapLimit => TimeSpan.FromSeconds(Math.Max(180, 3 * IntervalSeconds));

    /// <summary>Small backward clock steps (NTP slewing a drifting VM clock) are not a gap.</summary>
    public static readonly TimeSpan BackwardTolerance = TimeSpan.FromSeconds(30);
}

internal sealed class RestartOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Restart when a trigger has been critical for this long without a break.</summary>
    public double AfterMinutes { get; set; } = 10;

    /// <summary>At most one restart in this many hours (rolling window, survives agent restarts).</summary>
    public double MinHoursBetween { get; set; } = 24;

    /// <summary>After a restart the node needs time to catch up: no new restart decision and no new alerts from the
    /// node checks for this long.</summary>
    public double GraceMinutes { get; set; } = 15;

    public string Unit { get; set; } = "mosaic-chain-mainnet.service";
    /// <summary>--no-block: systemctl returns once the restart job is accepted (polkit is still checked), it does not wait
    /// for the slow stop and start of the chain.</summary>
    public List<string> Command { get; set; } =
        ["systemctl", "--no-ask-password", "--no-block", "restart", "mosaic-chain-mainnet.service"];
    public int CommandTimeoutSeconds { get; set; } = 180;

    public List<string> Triggers { get; set; } =
        [CheckNames.RelayRpc, CheckNames.RelayBlocks, CheckNames.RelayFullPeers, CheckNames.ParaRpc, CheckNames.ParaFinality];

    /// <summary>No restart while any of these units is active (for example the Mosaic auto-update).</summary>
    public List<string> SkipWhileUnitsActive { get; set; } = ["update-mosaic.service"];
}

internal sealed class RelayOptions
{
    public string Url { get; set; } = "";
    public string NodeTokenFile { get; set; } = "";
    public double FallbackAfterMinutes { get; set; } = 2;
    public int HeartbeatSeconds { get; set; } = 60;

    /// <summary>The channel names sent with every event ("channels" extension); empty: the relay uses all of them.</summary>
    public List<string> Channels { get; set; } = [];

    [JsonIgnore] public bool Configured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(NodeTokenFile);
}

internal sealed class SinkOptions
{
    public string Type { get; set; } = "";
    public string? Name { get; set; }
    public string MinSeverity { get; set; } = "warning";

    // telegram
    public string? BotTokenFile { get; set; }
    public string? ChatId { get; set; }

    // matrix
    public string? Homeserver { get; set; }
    public string? RoomId { get; set; }
    public string? AccessTokenFile { get; set; }

    // safechat
    public string? Url { get; set; }
    public string? TokenFile { get; set; }

    [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Type : Name!;
}

internal sealed class ConfigException(string message) : Exception(message);
