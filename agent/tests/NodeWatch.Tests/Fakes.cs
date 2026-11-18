using System.Net;
using System.Net.Sockets;
using System.Text;
using NodeWatch.Sinks;

namespace NodeWatch.Tests;

internal sealed class FakeProbe : IProbe, IServiceStatus
{
    public long RelayBest { get; set; } = 1000;
    public bool RelayAdvances { get; set; } = true;
    public bool RelayDown { get; set; }
    public string? PeersError { get; set; }

    /// <summary>With <see cref="PeersError"/>: the peer count from system_health (null: that fails too).</summary>
    public int? HealthPeers { get; set; }
    public PeerCounts Peers { get; set; } = new(11, 1, 3, 7, 0);
    public long ParaFinalized { get; set; } = 500;
    public bool ParaAdvances { get; set; } = true;
    public string ChainState { get; set; } = "active";
    public string UpdateState { get; set; } = "inactive";
    public string? DiskError { get; set; }
    public ValidatorState? Validator { get; set; }
    public SessionSim? Session { get; set; }
    public bool ParaSyncing { get; set; }
    public Dictionary<string, string> ExtraUnits { get; } = [];

    /// <summary>When the chain unit became active, as systemd would report it (set by the polls and by restarts).</summary>
    public DateTimeOffset? ChainActiveSince { get; set; }
    private DateTimeOffset _now;

    public Task<Snapshot> CollectAsync(DateTimeOffset now, CancellationToken ct)
    {
        _now = now;
        if (ChainState == "active") ChainActiveSince ??= now;
        else ChainActiveSince = null;
        if (RelayAdvances) RelayBest += 5;
        if (ParaAdvances) ParaFinalized += 2;
        var s = new Snapshot { At = now };
        if (RelayDown) s.RelayError = "connection refused";
        else
        {
            s.RelayBest = RelayBest;
            if (PeersError is null)
            {
                s.RelayPeers = Peers;
                s.RelayTotalPeers = Peers.Total;
            }
            else
            {
                s.RelayPeersError = PeersError;
                s.RelayTotalPeers = HealthPeers;
            }
        }
        s.ParaBest = ParaFinalized + 2;
        s.ParaFinalized = ParaFinalized;
        s.ParaHealth = new ParaHealth(8, ParaSyncing);
        s.Services["mosaic-chain-mainnet.service"] = ChainState;
        s.Services["mosaic-indexer-mainnet.service"] = "active";
        s.Services["update-mosaic.service"] = UpdateState;
        foreach (var (unit, unitState) in ExtraUnits) s.Services[unit] = unitState;
        if (DiskError is null) s.Disk = new DiskStatus("/var/lib/mosaic-mainnet", 40, 200);
        else s.DiskError = DiskError;
        s.Validator = Validator;
        if (ChainState == "active" && ChainActiveSince is { } since) s.ChainActiveFor = now - since;
        if (Session is { } sim)
        {
            if (sim.Error is null) s.Session = sim.Read(now);
            else s.SessionError = sim.Error;
        }
        return Task.FromResult(s);
    }

    public Task<string> StateAsync(string unit, CancellationToken ct) => Task.FromResult(unit switch
    {
        "mosaic-chain-mainnet.service" => ChainState,
        "update-mosaic.service" => UpdateState,
        _ => "active",
    });

    public Task<TimeSpan?> ActiveForAsync(string unit, CancellationToken ct) =>
        Task.FromResult(ChainActiveSince is { } since ? _now - since : (TimeSpan?)null);
}

internal sealed class FakeRestarter : IRestarter
{
    public int Count { get; private set; }
    public string? Fail { get; set; }

    /// <summary>Runs when the restart command would run (e.g. to look at the state file at that moment).</summary>
    public Action? OnRestart { get; set; }

    public Task<string?> RestartAsync(CancellationToken ct)
    {
        Count++;
        OnRestart?.Invoke();
        return Task.FromResult(Fail);
    }
}

internal sealed class MemorySink(string name, int minRank = 0) : ISink
{
    public string Name => name;
    public int MinRank => minRank;
    public List<CloudEvent> Received { get; } = [];
    public Outcome Next { get; set; } = Outcome.Ok;

    public bool Throws { get; set; }

    public Task<SendResult> SendAsync(CloudEvent e, CancellationToken ct)
    {
        if (Throws) throw new FormatException("bad header value");
        if (Next != Outcome.Ok) return Task.FromResult(new SendResult(Next, "fake failure"));
        Received.Add(e);
        return Task.FromResult(SendResult.Success);
    }
}

/// <summary>
/// A session of the chain as the probe would read it: Aura slots follow the clock (slot = unix time / 6 s), the validator
/// authors its own slots (index % count) when <see cref="Produce"/> is on.
/// </summary>
internal sealed class SessionSim
{
    public uint Index { get; set; } = 100;
    public int Count { get; set; } = 10;
    public int? MyIndex { get; set; } = 3;
    public bool SelectedNext { get; set; }
    public int NextCount { get; set; } = 10;
    public bool Produce { get; set; } = true;
    public bool Heartbeat { get; set; }

    /// <summary>A healthy node sends its heartbeat after half of the session; null: never.</summary>
    public double? HeartbeatAtProgress { get; set; } = 0.6;
    public bool KeysMissing { get; set; }

    /// <summary>In ImOnline's set for this session (false: unchilled or bound during the session).</summary>
    public bool InImOnline { get; set; } = true;
    public uint Authored { get; set; }
    public uint SessionLength { get; set; } = 600;
    public DateTimeOffset StartedAt { get; set; }
    public string? Error { get; set; }

    /// <summary>Share of the chain's slots without a block (blocks per slot = 1 - this).</summary>
    public double ChainEmptyShare { get; set; }
    private ulong? _lastSlot;
    private double _blocks;
    private long _sessionStart = FirstBlock;

    public const uint FirstBlock = 1_000_000;

    public void Start(DateTimeOffset now, uint index, int? myIndex, bool selectedNext = false)
    {
        Index = index;
        MyIndex = myIndex;
        SelectedNext = selectedNext;
        StartedAt = now;
        Authored = 0;
        Heartbeat = false;
        _sessionStart = FirstBlock + (long)_blocks;
    }

    public SessionInfo Read(DateTimeOffset now)
    {
        var slot = (ulong)now.ToUnixTimeSeconds() / 6;
        if (_lastSlot is { } last)
        {
            for (var s = last + 1; s <= slot; s++)
                if (MyIndex is { } me && Produce && s % (ulong)Count == (ulong)me) Authored++;
            _blocks += (slot - last) * (1 - ChainEmptyShare);
        }
        _lastSlot = slot;
        var sessionStart = _sessionStart;
        var block = FirstBlock + (long)_blocks;
        var progress = (double)(block - sessionStart + 1) / SessionLength;
        if (HeartbeatAtProgress is { } at && progress >= at) Heartbeat = true;
        return new SessionInfo
        {
            Index = Index, ActiveCount = Count, MyIndex = MyIndex, SelectedNext = SelectedNext, NextCount = NextCount,
            AuthorityCount = Count, Authored = MyIndex is null ? 0 : Authored, Heartbeat = Heartbeat, KeysMissing = KeysMissing,
            Expected = MyIndex is not null || InImOnline || KeysMissing,
            CurrentSlot = slot,
            Block = block, SessionEnd = (uint)(sessionStart + SessionLength - 1), SessionLength = SessionLength,
            NextSessionEnd = (uint)(sessionStart + 2 * SessionLength - 1),
        };
    }
}

internal static class TestConfig
{
    public static Config Create(string stateDir, string mode = "direct") => new()
    {
        Node = new NodeOptions { Name = "testnode" },
        // The older tests were written for one restart per 24 hours and no daily limit.
        Restart = new RestartOptions { MinHoursBetween = 24, MaxPerDay = 0 },
        Mode = mode,
        StateDirectory = stateDir,
        Relay = mode == "direct" ? new RelayOptions() : new RelayOptions { Url = "http://relay", NodeTokenFile = "x", FallbackAfterMinutes = 2 },
        Sinks = [new SinkOptions { Type = "safechat", Url = "http://x", TokenFile = "x", MinSeverity = "info" }],
    };

    public static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nodewatch-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}

/// <summary>A tiny HTTP server on 127.0.0.1 for the sink and RPC tests.</summary>
internal sealed class FakeServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Func<HttpListenerRequest, string, (int Status, string Body)> _handler;
    private readonly Task _loop;
    public List<(string Method, string Path, Dictionary<string, string> Headers, string Body)> Requests { get; } = [];
    public string Url { get; }

    public FakeServer(Func<HttpListenerRequest, string, (int Status, string Body)> handler)
    {
        _handler = handler;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Url = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(Url);
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) { return; }
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var headers = ctx.Request.Headers.AllKeys.Where(k => k is not null)
                .ToDictionary(k => k!, k => ctx.Request.Headers[k] ?? "", StringComparer.OrdinalIgnoreCase);
            lock (Requests) Requests.Add((ctx.Request.HttpMethod, ctx.Request.RawUrl ?? "", headers, body));
            var (status, response) = _handler(ctx.Request, body);
            ctx.Response.StatusCode = status;
            var bytes = Encoding.UTF8.GetBytes(response);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        _listener.Close();
        try { await _loop; } catch (Exception) { }
    }
}

/// <summary>Time since boot for the tests: moves only when the test says so, wall-clock steps do not touch it.</summary>
internal sealed class FakeBootClock : IBootClock
{
    public string BootId { get; set; } = "boot-1";
    public TimeSpan Uptime { get; set; } = TimeSpan.FromDays(3);

    public (string BootId, TimeSpan Uptime)? Now() => (BootId, Uptime);
}
