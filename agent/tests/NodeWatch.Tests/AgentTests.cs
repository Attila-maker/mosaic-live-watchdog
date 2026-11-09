using NodeWatch.Sinks;

namespace NodeWatch.Tests;

internal static class AgentTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(30);

    private sealed class World
    {
        public required Config Config;
        public required FakeProbe Probe;
        public required FakeRestarter Restarter;
        public required MemorySink Sink;
        public required Agent Agent;
        public DateTimeOffset Now;
        public required FakeBootClock Clock;
        public readonly List<CloudEvent> Events = [];

        /// <summary>Polls every 30 s for the given time, delivering the outbox after each poll.</summary>
        public async Task RunFor(TimeSpan span)
        {
            var end = Now + span;
            while (Now < end)
            {
                Events.AddRange(await Agent.TickAsync(Now, CancellationToken.None));
                await Agent.Outbox.DispatchAsync(Now, CancellationToken.None);
                Now += Poll;
                Clock.Uptime += Poll;
            }
        }

        public List<CloudEvent> Titled(string title) => Events.Where(e => e.Data.Title == title).ToList();
    }

    private static readonly DateTimeOffset Start = new(2026, 11, 6, 8, 0, 0, TimeSpan.Zero);

    private static World NewWorld(string? stateDir = null, DateTimeOffset? start = null, Config? config = null)
    {
        config ??= TestConfig.Create(stateDir ?? TestConfig.TempDir());
        var probe = new FakeProbe();
        var restarter = new FakeRestarter();
        // A real restart makes the unit "active" from that moment on.
        restarter.OnRestart = () => probe.ChainActiveSince = null;
        var sink = new MemorySink("safechat");
        var now = start ?? Start;
        var clock = new FakeBootClock();
        var agent = new Agent(config, probe, probe, restarter, [sink], null, new StateStore(config.StateDirectory), now, clock);
        return new World
        {
            Config = config, Probe = probe, Restarter = restarter, Sink = sink, Agent = agent, Now = now, Clock = clock,
        };
    }

    [Test]
    public static async Task Healthy_node_sends_nothing()
    {
        var w = NewWorld();
        await w.RunFor(TimeSpan.FromHours(1));
        Assert.Equal(0, w.Events.Count, "events");
        Assert.Equal(0, w.Restarter.Count, "restarts");
    }

    [Test]
    public static async Task Relay_stall_alerts_restarts_after_10_minutes_and_resolves()
    {
        var w = NewWorld();
        await w.RunFor(TimeSpan.FromMinutes(5));
        w.Probe.RelayAdvances = false;

        await w.RunFor(TimeSpan.FromMinutes(4));
        var firing = w.Titled("A relay chain nem halad");
        Assert.Equal(1, firing.Count, "stall alert after 3 min (+1 confirming poll)");
        Assert.Equal("critical", firing[0].Data.Severity, "severity");
        Assert.Equal(0, w.Restarter.Count, "no restart yet");

        await w.RunFor(TimeSpan.FromMinutes(6.5));
        Assert.Equal(1, w.Restarter.Count, "restart at 10 min of stall");
        Assert.Equal(1, w.Titled("Újraindítom a Mosaic node-ot").Count, "restart notice");

        // The node catches up during the grace period.
        await w.RunFor(TimeSpan.FromMinutes(2));
        w.Probe.RelayAdvances = true;
        await w.RunFor(TimeSpan.FromMinutes(14));
        Assert.Equal(1, w.Restarter.Count, "no second restart");
        Assert.Equal(1, w.Titled("A relay chain újra halad").Count, "stall resolved after the grace period");
        Assert.Equal(1, w.Titled("A node újra működik").Count, "restart alert resolved");
        Assert.True(w.Titled("A relay chain újra halad")[0].IsResolved, "resolved type");

        // Everything reached the sink, in order.
        Assert.Equal(w.Events.Count, w.Sink.Received.Count, "delivered");
    }

    [Test]
    public static async Task Second_failure_within_24h_is_not_restarted_but_reported_once()
    {
        var w = NewWorld();
        await w.RunFor(TimeSpan.FromMinutes(5));
        w.Probe.RelayAdvances = false;
        await w.RunFor(TimeSpan.FromMinutes(12));
        Assert.Equal(1, w.Restarter.Count, "first restart");

        // Still stuck after the grace period.
        await w.RunFor(TimeSpan.FromMinutes(40));
        Assert.Equal(1, w.Restarter.Count, "no second restart within 24 h");
        Assert.Equal(1, w.Titled("Nem indítom újra a node-ot").Count, "refusal reported once");

        // 24 hours later, still stuck: the limit allows one more.
        await w.RunFor(TimeSpan.FromHours(24));
        Assert.Equal(2, w.Restarter.Count, "restart allowed again after 24 h");
    }

    [Test]
    public static async Task No_restart_while_the_service_is_stopped_or_updating()
    {
        var w = NewWorld();
        await w.RunFor(TimeSpan.FromMinutes(5));
        w.Probe.RelayAdvances = false;
        w.Probe.UpdateState = "active";
        await w.RunFor(TimeSpan.FromMinutes(20));
        Assert.Equal(0, w.Restarter.Count, "update-mosaic running");

        w.Probe.UpdateState = "inactive";
        w.Probe.ChainState = "inactive";
        await w.RunFor(TimeSpan.FromMinutes(5));
        Assert.Equal(0, w.Restarter.Count, "chain stopped on purpose");
        Assert.True(w.Titled("A(z) mosaic-chain-mainnet.service nem fut").Count == 1, "stopped service alert");

        w.Probe.ChainState = "active";
        await w.RunFor(TimeSpan.FromMinutes(9));
        Assert.Equal(0, w.Restarter.Count, "a freshly started service gets the full 10 minutes");
        await w.RunFor(TimeSpan.FromMinutes(2));
        Assert.Equal(1, w.Restarter.Count, "restarts once it ran 10 minutes and is still stuck");
    }

    [Test]
    public static async Task No_restart_right_after_an_update_finished()
    {
        var w = NewWorld();
        await w.RunFor(TimeSpan.FromMinutes(5));
        w.Probe.RelayAdvances = false;
        w.Probe.UpdateState = "active";
        await w.RunFor(TimeSpan.FromMinutes(12));
        // The update restarted the chain and finished; the node is still catching up.
        w.Probe.UpdateState = "inactive";
        w.Probe.ChainActiveSince = w.Now;
        await w.RunFor(TimeSpan.FromMinutes(9));
        Assert.Equal(0, w.Restarter.Count, "no restart in the first 10 minutes after the update");
        await w.RunFor(TimeSpan.FromMinutes(2));
        Assert.Equal(1, w.Restarter.Count, "restart once it had 10 minutes");
    }

    [Test]
    public static async Task No_restart_when_the_unit_was_restarted_between_two_polls()
    {
        var w = NewWorld();
        await w.RunFor(TimeSpan.FromMinutes(5));
        w.Probe.RelayAdvances = false;
        await w.RunFor(TimeSpan.FromMinutes(9));
        // Restarted by hand between two polls: the agent never saw it stopped, only systemd's start time tells.
        w.Probe.ChainActiveSince = w.Now - TimeSpan.FromSeconds(10);
        await w.RunFor(TimeSpan.FromMinutes(5));
        Assert.Equal(0, w.Restarter.Count, "the unit runs only for a few minutes");
        await w.RunFor(TimeSpan.FromMinutes(6));
        Assert.Equal(1, w.Restarter.Count, "restart once the unit ran 10 minutes and is still stuck");
    }

    [Test]
    public static async Task A_long_pause_between_polls_is_not_counted_as_an_outage()
    {
        var w = NewWorld();
        await w.RunFor(TimeSpan.FromMinutes(5));
        // The machine (or VM) was suspended for 12 minutes: the node did not move, but nobody watched either.
        w.Probe.RelayAdvances = false;
        w.Probe.ParaAdvances = false;
        w.Now += TimeSpan.FromMinutes(12);
        w.Probe.RelayAdvances = true;
        w.Probe.ParaAdvances = true;
        await w.RunFor(TimeSpan.FromMinutes(5));
        Assert.Equal(0, w.Restarter.Count, "no restart");
        Assert.Equal(0, w.Events.Count, "no false stall alert");
    }

    [Test]
    public static async Task Restart_time_is_on_disk_before_the_command_runs()
    {
        var dir = TestConfig.TempDir();
        var w = NewWorld(dir);
        string? onDisk = null;
        w.Restarter.OnRestart = () => onDisk = File.ReadAllText(Path.Combine(dir, "state.json"));
        await w.RunFor(TimeSpan.FromMinutes(5));
        w.Probe.RelayAdvances = false;
        await w.RunFor(TimeSpan.FromMinutes(11));
        Assert.Equal(1, w.Restarter.Count, "restart");
        var state = System.Text.Json.JsonSerializer.Deserialize<AgentState>(onDisk!)!;
        Assert.True(state.LastRestartAt is not null, "LastRestartAt saved before the command ran");
    }

    [Test]
    public static async Task No_restart_when_the_state_cannot_be_saved()
    {
        var dir = TestConfig.TempDir();
        var w = NewWorld(dir);
        Directory.CreateDirectory(Path.Combine(dir, "state.json.tmp")); // every save fails from now on
        await w.RunFor(TimeSpan.FromMinutes(5));
        w.Probe.RelayAdvances = false;
        await w.RunFor(TimeSpan.FromMinutes(20));
        Assert.Equal(0, w.Restarter.Count, "no restart without a saved limit");
        Assert.Equal(1, w.Titled("Nem indítom újra a node-ot").Count, "reported once");
    }

    [Test]
    public static async Task Unreadable_state_counts_as_a_restart_just_now()
    {
        var dir = TestConfig.TempDir();
        File.WriteAllText(Path.Combine(dir, "state.json"), "{ ez nem json");
        var w = NewWorld(dir);
        await w.RunFor(TimeSpan.FromMinutes(5));
        w.Probe.RelayAdvances = false;
        await w.RunFor(TimeSpan.FromMinutes(30));
        Assert.Equal(0, w.Restarter.Count, "no restart within 24 h of the unreadable state");
        Assert.True(w.Titled("Nem indítom újra a node-ot")[0].Data.Text!.Contains("nem volt olvasható"), "says why");
        await w.RunFor(TimeSpan.FromHours(24));
        Assert.Equal(1, w.Restarter.Count, "allowed again after 24 h");
    }

    [Test]
    public static async Task Clock_set_back_does_not_block_for_hours()
    {
        var dir = TestConfig.TempDir();
        var w = NewWorld(dir);
        await w.RunFor(TimeSpan.FromMinutes(5));
        w.Probe.RelayAdvances = false;
        await w.RunFor(TimeSpan.FromMinutes(11));
        Assert.Equal(1, w.Restarter.Count, "first restart");
        // The clock is set back by 5 hours: the stored grace period must not silence the node checks for 5 hours.
        w.Now -= TimeSpan.FromHours(5);
        w.Probe.RelayAdvances = true;
        await w.RunFor(TimeSpan.FromMinutes(20));
        w.Probe.RelayDown = true;
        await w.RunFor(TimeSpan.FromMinutes(3));
        Assert.Equal(1, w.Titled("A relay chain RPC nem válaszol").Count, "alerts work again after at most one grace period");
    }

    [Test]
    public static async Task Peer_list_error_only_warns()
    {
        var w = NewWorld();
        await w.RunFor(TimeSpan.FromMinutes(2));
        w.Probe.PeersError = "system_peers: Method not found";
        await w.RunFor(TimeSpan.FromMinutes(30));
        Assert.Equal(1, w.Titled("A relay chain peerjei nem kérdezhetők le").Count, "warning");
        Assert.Equal(0, w.Restarter.Count, "no restart for an unknown peer list");
    }

    [Test]
    public static async Task Unknown_peer_roles_fall_back_to_the_peer_count()
    {
        var w = NewWorld();
        w.Probe.PeersError = "system_peers: RPC call is unsafe to be called externally";
        w.Probe.HealthPeers = 8;
        await w.RunFor(TimeSpan.FromMinutes(30));
        Assert.Equal(0, w.Events.Count, "no alert while there are peers");

        w.Probe.HealthPeers = 0;
        await w.RunFor(TimeSpan.FromMinutes(5));
        Assert.Equal(1, w.Titled("A relay chain-nek nincs egy peere sem").Count, "no peer at all is critical");
        await w.RunFor(TimeSpan.FromMinutes(6));
        Assert.Equal(1, w.Restarter.Count, "and a restart reason");
    }

    [Test]
    public static async Task Disk_error_becomes_a_warning()
    {
        var w = NewWorld();
        w.Probe.DiskError = "a(z) /x mappa nem létezik";
        await w.RunFor(TimeSpan.FromMinutes(2));
        Assert.Equal(1, w.Titled("A szabad hely nem kérdezhető le").Count, "warning");
    }

    [Test]
    public static async Task Critical_only_sink_gets_the_end_of_an_alert_that_eased_through_warning()
    {
        var config = TestConfig.Create(TestConfig.TempDir());
        config.Restart.Enabled = false;
        var probe = new FakeProbe();
        var criticalOnly = new MemorySink("critical", 2);
        var now = Start;
        var agent = new Agent(config, probe, probe, new FakeRestarter(), [criticalOnly], null,
            new StateStore(config.StateDirectory), now, new FakeBootClock());
        async Task Run(int polls)
        {
            for (var i = 0; i < polls; i++)
            {
                await agent.TickAsync(now, CancellationToken.None);
                await agent.Outbox.DispatchAsync(now, CancellationToken.None);
                now += Poll;
            }
        }
        await Run(2);
        probe.Peers = new PeerCounts(11, 0, 0, 11, 0);
        await Run(3);
        probe.Peers = new PeerCounts(11, 1, 0, 10, 0);
        await Run(3);
        probe.Peers = new PeerCounts(11, 3, 0, 8, 0);
        await Run(3);
        var titles = criticalOnly.Received.Select(e => e.Data.Title).ToList();
        Assert.Equal("A relay chain-nek nincs full peere|A relay chain-nek újra van elég full peere", string.Join("|", titles),
            "critical firing, then its resolved");
    }

    [Test]
    public static async Task A_failing_poll_is_reported_after_five_in_a_row()
    {
        var config = TestConfig.Create(TestConfig.TempDir());
        var sink = new MemorySink("all");
        var probe = new ThrowingProbe();
        var agent = new Agent(config, probe, new FakeProbe(), new FakeRestarter(), [sink], null,
            new StateStore(config.StateDirectory), Start, new FakeBootClock());
        var now = Start;
        for (var i = 0; i < 8; i++)
        {
            await agent.SafeTickAsync(now, CancellationToken.None);
            await agent.Outbox.DispatchAsync(now, CancellationToken.None);
            now += Poll;
        }
        Assert.Equal(1, sink.Received.Count(e => e.Data.Title == "A NodeWatch nem tudja ellenőrizni a node-ot"), "one alert");
        probe.Fail = false;
        await agent.SafeTickAsync(now, CancellationToken.None);
        await agent.Outbox.DispatchAsync(now, CancellationToken.None);
        Assert.Equal(1, sink.Received.Count(e => e.Data.Title == "A NodeWatch újra ellenőrzi a node-ot"), "resolved");
    }

    [Test]
    public static async Task Wall_clock_step_forward_does_not_allow_a_second_restart()
    {
        var w = NewWorld();
        await w.RunFor(TimeSpan.FromMinutes(5));
        w.Probe.RelayAdvances = false;
        await w.RunFor(TimeSpan.FromMinutes(11));
        Assert.Equal(1, w.Restarter.Count, "first restart");
        await w.RunFor(TimeSpan.FromMinutes(20));
        // A dead RTC is corrected by NTP: the wall clock jumps a day ahead, the time since boot does not.
        w.Now += TimeSpan.FromHours(24);
        await w.RunFor(TimeSpan.FromMinutes(40));
        Assert.Equal(1, w.Restarter.Count, "still only one restart in real time");
    }

    [Test]
    public static async Task After_a_reboot_the_wall_clock_counts()
    {
        var dir = TestConfig.TempDir();
        var w = NewWorld(dir);
        await w.RunFor(TimeSpan.FromMinutes(5));
        w.Probe.RelayAdvances = false;
        await w.RunFor(TimeSpan.FromMinutes(11));
        Assert.Equal(1, w.Restarter.Count, "first restart");

        var rebooted = NewWorld(dir, w.Now + TimeSpan.FromHours(25));
        rebooted.Clock.BootId = "boot-2";
        rebooted.Clock.Uptime = TimeSpan.FromMinutes(1);
        await rebooted.RunFor(TimeSpan.FromMinutes(5));
        rebooted.Probe.RelayAdvances = false;
        await rebooted.RunFor(TimeSpan.FromMinutes(11));
        Assert.Equal(1, rebooted.Restarter.Count, "25 h later, after a reboot, one restart is allowed again");
    }

    [Test]
    public static async Task Small_backward_clock_steps_do_not_reset_the_outage()
    {
        var w = NewWorld();
        await w.RunFor(TimeSpan.FromMinutes(5));
        w.Probe.RelayAdvances = false;
        for (var i = 0; i < 4; i++)
        {
            await w.RunFor(TimeSpan.FromMinutes(5));
            w.Now -= TimeSpan.FromSeconds(1); // NTP correcting a drifting VM clock
        }
        Assert.Equal(1, w.Restarter.Count, "restart despite the small steps");
        Assert.Equal(1, w.Titled("A relay chain nem halad").Count, "one stall alert, not one per step");
    }

    [Test]
    public static async Task Agent_alert_is_resolved_by_the_next_agent_process()
    {
        var dir = TestConfig.TempDir();
        var config = TestConfig.Create(dir);
        var probe = new ThrowingProbe();
        var sink = new MemorySink("all");
        var agent = new Agent(config, probe, new FakeProbe(), new FakeRestarter(), [sink], null, new StateStore(dir), Start,
            new FakeBootClock());
        var now = Start;
        for (var i = 0; i < 6; i++)
        {
            await agent.SafeTickAsync(now, CancellationToken.None);
            now += Poll;
        }
        var restarted = new Agent(config, new FakeProbe(), new FakeProbe(), new FakeRestarter(), [sink], null,
            new StateStore(dir), now, new FakeBootClock());
        await restarted.SafeTickAsync(now, CancellationToken.None);
        await restarted.Outbox.DispatchAsync(now, CancellationToken.None);
        Assert.Equal(1, sink.Received.Count(e => e.Data.Title == "A NodeWatch nem tudja ellenőrizni a node-ot"), "alert");
        Assert.Equal(1, sink.Received.Count(e => e.Data.Title == "A NodeWatch újra ellenőrzi a node-ot"), "resolved");
    }

    [Test]
    public static void Only_one_instance_can_hold_the_lock()
    {
        var dir = TestConfig.TempDir();
        using var first = InstanceLock.TryAcquire(dir, out var problem, out _);
        Assert.True(first is not null, $"first lock ({problem})");
        using var second = InstanceLock.TryAcquire(dir, out _, out var busy);
        Assert.True(second is null && busy, "second refused as busy");
    }

    private sealed class ThrowingProbe : IProbe
    {
        private readonly FakeProbe _inner = new();
        public bool Fail { get; set; } = true;

        public Task<Snapshot> CollectAsync(DateTimeOffset now, CancellationToken ct) =>
            Fail ? throw new InvalidOperationException("váratlan hiba") : _inner.CollectAsync(now, ct);
    }

    [Test]
    public static async Task Relay_rpc_down_triggers_restart()
    {
        var w = NewWorld();
        await w.RunFor(TimeSpan.FromMinutes(2));
        w.Probe.RelayDown = true;
        await w.RunFor(TimeSpan.FromMinutes(11));
        Assert.Equal(1, w.Titled("A relay chain RPC nem válaszol").Count, "rpc alert");
        Assert.Equal(1, w.Restarter.Count, "restart");
    }

    [Test]
    public static async Task No_full_peers_alerts_and_restarts()
    {
        var w = NewWorld();
        await w.RunFor(TimeSpan.FromMinutes(2));
        w.Probe.Peers = new PeerCounts(11, 0, 0, 11, 0);
        await w.RunFor(TimeSpan.FromMinutes(11));
        Assert.Equal(1, w.Titled("A relay chain-nek nincs full peere").Count, "peer alert");
        Assert.Equal(1, w.Restarter.Count, "restart");
    }

    [Test]
    public static async Task One_full_peer_only_warns()
    {
        var w = NewWorld();
        await w.RunFor(TimeSpan.FromMinutes(2));
        w.Probe.Peers = new PeerCounts(11, 1, 0, 10, 0);
        await w.RunFor(TimeSpan.FromMinutes(30));
        var warn = w.Titled("Csak egy full peer van a relay chain-en");
        Assert.Equal(1, warn.Count, "warning");
        Assert.Equal("warning", warn[0].Data.Severity, "severity");
        Assert.Equal(0, w.Restarter.Count, "no restart for a warning");
    }

    [Test]
    public static async Task Restart_limit_survives_an_agent_restart()
    {
        var dir = TestConfig.TempDir();
        var w = NewWorld(dir);
        await w.RunFor(TimeSpan.FromMinutes(5));
        w.Probe.RelayAdvances = false;
        await w.RunFor(TimeSpan.FromMinutes(12));
        Assert.Equal(1, w.Restarter.Count, "first restart");

        var again = NewWorld(dir, w.Now + TimeSpan.FromMinutes(30));
        again.Clock.Uptime = w.Clock.Uptime + TimeSpan.FromMinutes(30);
        again.Probe.RelayAdvances = false;
        await again.RunFor(TimeSpan.FromMinutes(15));
        Assert.Equal(0, again.Restarter.Count, "the new agent process remembers the last restart");
    }

    [Test]
    public static async Task Lasting_critical_alert_is_repeated_every_30_minutes()
    {
        var w = NewWorld();
        w.Config.Restart.Enabled = false;
        await w.RunFor(TimeSpan.FromMinutes(2));
        w.Probe.ParaAdvances = false;
        await w.RunFor(TimeSpan.FromMinutes(70));
        Assert.Equal(1, w.Titled("A parachain nem véglegesít").Count, "first alert");
        Assert.Equal(2, w.Titled("Emlékeztető: A parachain nem véglegesít").Count, "two reminders in ~64 minutes");
    }

    [Test]
    public static async Task Single_bad_poll_does_not_alert()
    {
        var w = NewWorld();
        await w.RunFor(TimeSpan.FromMinutes(2));
        w.Probe.RelayDown = true;
        await w.RunFor(Poll);
        w.Probe.RelayDown = false;
        await w.RunFor(TimeSpan.FromMinutes(5));
        Assert.Equal(0, w.Events.Count, "a one-poll blip is ignored");
    }

    [Test]
    public static async Task Severity_filter_keeps_warnings_off_a_critical_only_sink()
    {
        var config = TestConfig.Create(TestConfig.TempDir());
        var probe = new FakeProbe();
        var all = new MemorySink("all");
        var criticalOnly = new MemorySink("critical", 2);
        var now = Start;
        var agent = new Agent(config, probe, probe, new FakeRestarter(), [all, criticalOnly], null,
            new StateStore(config.StateDirectory), now, new FakeBootClock());
        probe.Peers = new PeerCounts(11, 1, 0, 10, 0);
        for (var i = 0; i < 4; i++)
        {
            await agent.TickAsync(now, CancellationToken.None);
            await agent.Outbox.DispatchAsync(now, CancellationToken.None);
            now += Poll;
        }
        Assert.Equal(1, all.Received.Count, "warning to the info sink");
        Assert.Equal(0, criticalOnly.Received.Count, "not to the critical-only sink");
    }
}
