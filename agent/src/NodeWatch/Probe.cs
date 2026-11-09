using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace NodeWatch;

internal sealed class RpcException(string message) : Exception(message);

/// <summary>JSON-RPC over HTTP to a Substrate node (the same port also serves WebSocket).</summary>
internal sealed class JsonRpc(HttpClient http, string url)
{
    private int _id;

    public async Task<JsonElement> CallAsync(string method, CancellationToken ct, params object?[] parameters)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(url, NodeWatch.Sinks.Http.Json(new
            {
                jsonrpc = "2.0",
                id = Interlocked.Increment(ref _id),
                method,
                @params = parameters,
            }), ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new RpcException($"{url} nem érhető el ({e.Message})");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode) throw new RpcException($"{url} HTTP {(int)response.StatusCode} ({method})");
            JsonElement doc;
            try
            {
                doc = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            }
            catch (JsonException)
            {
                throw new RpcException($"{url} hibás választ adott ({method})");
            }
            if (doc.ValueKind != JsonValueKind.Object) throw new RpcException($"{url} hibás választ adott ({method})");
            if (doc.TryGetProperty("error", out var error))
                throw new RpcException(error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m)
                    ? $"{method}: {m}"
                    : $"{method}: {error}");
            if (!doc.TryGetProperty("result", out var result)) throw new RpcException($"{method}: nincs result a válaszban");
            return result.Clone();
        }
    }

    public static long ParseNumber(JsonElement header)
    {
        if (header.ValueKind != JsonValueKind.Object || !header.TryGetProperty("number", out var number) ||
            number.ValueKind != JsonValueKind.String)
            throw new RpcException("a blokkfejléc üres vagy hibás");
        var text = number.GetString()!;
        return long.TryParse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text,
            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new RpcException($"hibás blokkszám: {text}");
    }
}

internal sealed record PeerCounts(int Total, int Full, int Authority, int None, int Light)
{
    /// <summary>Peers the sync engine can download blocks from (role FULL or AUTHORITY).</summary>
    public int SyncSources => Full + Authority;
}

internal sealed record ParaHealth(int Peers, bool IsSyncing);

internal sealed record DiskStatus(string Path, double FreePercent, double FreeGb);

/// <summary>Everything one poll saw. A null value with an error text means that part could not be read.</summary>
internal sealed class Snapshot
{
    public DateTimeOffset At { get; init; }
    public long? RelayBest { get; set; }
    public PeerCounts? RelayPeers { get; set; }
    public string? RelayError { get; set; }

    /// <summary>The block number was read but the peer list was not (system_peers is an "unsafe" RPC method).</summary>
    public string? RelayPeersError { get; set; }

    /// <summary>Number of relay chain peers: from system_peers, or from the safe system_health when the roles cannot be
    /// read (Mosaic nodes run the relay chain RPC with methods=safe).</summary>
    public int? RelayTotalPeers { get; set; }
    public long? ParaBest { get; set; }
    public long? ParaFinalized { get; set; }
    public ParaHealth? ParaHealth { get; set; }
    public string? ParaError { get; set; }
    public ValidatorState? Validator { get; set; }
    public bool ValidatorMissing { get; set; }
    public string? ValidatorError { get; set; }
    public Dictionary<string, string> Services { get; } = new(StringComparer.Ordinal);
    public DiskStatus? Disk { get; set; }
    public string? DiskError { get; set; }
}

internal interface IProbe
{
    Task<Snapshot> CollectAsync(DateTimeOffset now, CancellationToken ct);
}

/// <summary>Reads the node through its local RPC ports and the host through systemctl and statvfs. Read-only.</summary>
internal sealed class NodeProbe : IProbe
{
    private readonly Config _config;
    private readonly JsonRpc _relay;
    private readonly JsonRpc _para;
    private readonly string? _validatorKey;
    private readonly IServiceStatus _services;

    public NodeProbe(Config config, HttpClient http, IServiceStatus services)
    {
        _config = config;
        _relay = new JsonRpc(http, config.Node.RelayRpc);
        _para = new JsonRpc(http, config.Node.ParaRpc);
        _services = services;
        if (!string.IsNullOrWhiteSpace(config.Node.ValidatorAddress))
            _validatorKey = StorageKeys.ValidatorState(Ss58.DecodePublicKey(config.Node.ValidatorAddress));
    }

    public async Task<Snapshot> CollectAsync(DateTimeOffset now, CancellationToken ct)
    {
        var s = new Snapshot { At = now };

        // Every part catches every error (except shutdown): a strange answer must not stop the other checks.
        try
        {
            s.RelayBest = JsonRpc.ParseNumber(await _relay.CallAsync("chain_getHeader", ct));
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            s.RelayError = Message(e);
        }

        if (s.RelayError is null)
        {
            try
            {
                s.RelayPeers = CountPeers(await _relay.CallAsync("system_peers", ct));
                s.RelayTotalPeers = s.RelayPeers.Total;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                s.RelayPeersError = Message(e);
                try
                {
                    var health = await _relay.CallAsync("system_health", ct);
                    s.RelayTotalPeers = health.GetProperty("peers").GetInt32();
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    // Neither works: the check reports that the peers cannot be read.
                }
            }
        }

        try
        {
            s.ParaBest = JsonRpc.ParseNumber(await _para.CallAsync("chain_getHeader", ct));
            var finalizedHash = await _para.CallAsync("chain_getFinalizedHead", ct);
            if (finalizedHash.ValueKind != JsonValueKind.String) throw new RpcException("chain_getFinalizedHead: hibás válasz");
            s.ParaFinalized = JsonRpc.ParseNumber(await _para.CallAsync("chain_getHeader", ct, finalizedHash.GetString()));
            var health = await _para.CallAsync("system_health", ct);
            s.ParaHealth = new ParaHealth(health.GetProperty("peers").GetInt32(), health.GetProperty("isSyncing").GetBoolean());
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            s.ParaError = Message(e);
        }

        if (_validatorKey is not null && s.ParaError is null)
        {
            try
            {
                var value = await _para.CallAsync("state_getStorage", ct, _validatorKey);
                if (value.ValueKind == JsonValueKind.Null) s.ValidatorMissing = true;
                else if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 2 } hex)
                    s.Validator = ValidatorState.Decode(Convert.FromHexString(hex[2..]));
                else throw new RpcException($"state_getStorage: hibás válasz ({value})");
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                s.ValidatorError = Message(e);
            }
        }

        var units = _config.Node.Services.Concat(_config.Restart.SkipWhileUnitsActive).Append(_config.Restart.Unit).Distinct();
        foreach (var unit in units) s.Services[unit] = await _services.StateAsync(unit, ct);

        try
        {
            if (!Directory.Exists(_config.Node.DataPath))
                throw new IOException($"a(z) {_config.Node.DataPath} mappa nem létezik vagy nem látható (Node.DataPath)");
            var drive = new DriveInfo(_config.Node.DataPath);
            if (drive.TotalSize <= 0) throw new IOException($"a(z) {_config.Node.DataPath} lemezmérete nem olvasható");
            s.Disk = new DiskStatus(_config.Node.DataPath, 100.0 * drive.AvailableFreeSpace / drive.TotalSize,
                drive.AvailableFreeSpace / 1024.0 / 1024 / 1024);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            s.DiskError = Message(e);
        }

        return s;
    }

    private static string Message(Exception e) => e is RpcException ? e.Message : $"{e.GetType().Name}: {e.Message}";

    internal static PeerCounts CountPeers(JsonElement peers)
    {
        int total = 0, full = 0, authority = 0, none = 0, light = 0;
        foreach (var peer in peers.EnumerateArray())
        {
            total++;
            // Debug-printed bitflags: "FULL", "AUTHORITY", "LIGHT", "NONE" (or combinations like "FULL | AUTHORITY").
            var roles = peer.TryGetProperty("roles", out var r) ? r.GetString() ?? "" : "";
            if (roles.Contains("AUTHORITY", StringComparison.OrdinalIgnoreCase)) authority++;
            else if (roles.Contains("FULL", StringComparison.OrdinalIgnoreCase)) full++;
            else if (roles.Contains("LIGHT", StringComparison.OrdinalIgnoreCase)) light++;
            else none++;
        }
        return new PeerCounts(total, full, authority, none, light);
    }
}

internal interface IServiceStatus
{
    /// <summary>"active", "inactive", "failed", "activating", … or "unknown".</summary>
    Task<string> StateAsync(string unit, CancellationToken ct);

    /// <summary>How long the unit has been active without a break; null if it is not known.</summary>
    Task<TimeSpan?> ActiveForAsync(string unit, CancellationToken ct);
}

internal sealed class SystemctlStatus : IServiceStatus
{
    public async Task<TimeSpan?> ActiveForAsync(string unit, CancellationToken ct)
    {
        try
        {
            // CLOCK_MONOTONIC in µs, the same clock Stopwatch uses on Linux.
            var result = await ProcessRunner.RunAsync(
                ["systemctl", "show", "--property=ActiveEnterTimestampMonotonic", "--value", unit], TimeSpan.FromSeconds(10), ct);
            if (result.ExitCode != 0 || !long.TryParse(result.Stdout.Trim(), out var enteredUs) || enteredUs <= 0) return null;
            var nowUs = (long)(Stopwatch.GetTimestamp() * (1_000_000.0 / Stopwatch.Frequency));
            return nowUs >= enteredUs ? TimeSpan.FromMicroseconds(nowUs - enteredUs) : null;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            return null;
        }
    }

    public async Task<string> StateAsync(string unit, CancellationToken ct)
    {
        try
        {
            // Only stdout: on a broken D-Bus systemctl prints the error to stderr and nothing to stdout.
            var result = await ProcessRunner.RunAsync(["systemctl", "is-active", unit], TimeSpan.FromSeconds(10), ct);
            var state = result.Stdout.Trim().Split('\n').LastOrDefault()?.Trim();
            return string.IsNullOrEmpty(state) || state.Contains(' ') ? "unknown" : state;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            return "unknown";
        }
    }
}

internal sealed record ProcessResult(int ExitCode, string Stdout, string Stderr)
{
    public string Output => (Stdout + Stderr).Trim();
}

internal static class ProcessRunner
{
    /// <summary>Runs a command without a shell. Returns the exit code, stdout and stderr.</summary>
    public static async Task<ProcessResult> RunAsync(IReadOnlyList<string> command, TimeSpan timeout,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(command[0])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in command.Skip(1)) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"nem indult el: {command[0]}");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"{string.Join(' ', command)}: nem fejeződött be {timeout.TotalSeconds:0} másodperc alatt");
        }
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }
}
