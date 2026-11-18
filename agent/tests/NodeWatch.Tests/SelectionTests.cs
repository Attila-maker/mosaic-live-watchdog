namespace NodeWatch.Tests;

internal static class SelectionTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset Start = new(2026, 11, 18, 6, 0, 0, TimeSpan.Zero);
    private const string Alice = "15oF4uVJwmo4TdGW7VfQxNLavjCXviqxT9S1MgbjMNHr6Sp5";

    private sealed class World
    {
        public required FakeProbe Probe;
        public required SessionSim Sim;
        public required FakeRestarter Restarter;
        public required Agent Agent;
        public DateTimeOffset Now;
        public required FakeBootClock Clock;
        public readonly List<CloudEvent> Events = [];

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

    private static World NewWorld(Action<Config>? configure = null, string? dir = null, DateTimeOffset? start = null)
    {
        var config = TestConfig.Create(dir ?? TestConfig.TempDir());
        config.Node.ValidatorAddress = Alice;
        config.TimeZone = "UTC";
        config.Restart = new RestartOptions();
        config.Validate();
        configure?.Invoke(config);
        var now = start ?? Start;
        var sim = new SessionSim();
        sim.Start(now, 100, myIndex: null);
        var probe = new FakeProbe { Validator = new ValidatorState(ValidatorStateKind.Normal, null), Session = sim };
        var restarter = new FakeRestarter();
        restarter.OnRestart = () => probe.ChainActiveSince = null;
        var clock = new FakeBootClock();
        var agent = new Agent(config, probe, probe, restarter, [new MemorySink("safechat")], null,
            new StateStore(config.StateDirectory), now, clock);
        return new World { Probe = probe, Sim = sim, Restarter = restarter, Agent = agent, Now = now, Clock = clock };
    }

    [Test]
    public static async Task Selection_notice_once_per_session()
    {
        var w = NewWorld();
        w.Sim.SelectedNext = true;
        await w.RunFor(TimeSpan.FromMinutes(10));
        var notices = w.Titled("Kiválasztottak a következő sessionre");
        Assert.Equal(1, notices.Count, "one notice");
        Assert.Equal("info", notices[0].Data.Severity, "info level");
        Assert.True(notices[0].Data.Text!.Contains("#101"), "names the session: " + notices[0].Data.Text);

        w.Sim.Start(w.Now, 101, myIndex: 3, selectedNext: true);
        await w.RunFor(TimeSpan.FromMinutes(10));
        Assert.Equal(2, w.Titled("Kiválasztottak a következő sessionre").Count, "again for #102");
    }

    [Test]
    public static async Task Own_slots_with_blocks_give_no_alert()
    {
        var w = NewWorld();
        await w.RunFor(TimeSpan.FromMinutes(12));
        w.Sim.Start(w.Now, 101, myIndex: 3);
        await w.RunFor(TimeSpan.FromMinutes(30));
        Assert.Equal(0, w.Events.Count(e => e.Data.Severity is "warning" or "critical"), "no alert");
        Assert.Equal(0, w.Restarter.Count, "no restart");
    }

    [Test]
    public static async Task One_missed_slot_warns_two_in_a_row_restart_at_once()
    {
        var w = NewWorld();
        await w.RunFor(TimeSpan.FromMinutes(12)); // the unit has run for 10 minutes
        w.Sim.Start(w.Now, 101, myIndex: 3);
        w.Sim.Produce = false;
        // Own slot every 60 s (10 validators × 6 s): judged 60 s after it.
        await w.RunFor(TimeSpan.FromSeconds(150));
        Assert.Equal(1, w.Titled("Kimaradt egy saját blokk").Count, "first miss: warning");
        Assert.Equal(0, w.Restarter.Count, "no restart for one miss");
        await w.RunFor(TimeSpan.FromSeconds(90));
        Assert.Equal(1, w.Restarter.Count, "second miss in a row: restart at once, not after 10 minutes");
        Assert.Equal(1, w.Titled("Újraindítom a Mosaic node-ot").Count, "reported");
    }

    [Test]
    public static async Task A_block_between_misses_resets_the_row()
    {
        var w = NewWorld();
        await w.RunFor(TimeSpan.FromMinutes(12));
        w.Sim.Start(w.Now, 101, myIndex: 3);
        for (var i = 0; i < 6; i++)
        {
            w.Sim.Produce = i % 2 == 0; // every other own slot is empty
            await w.RunFor(TimeSpan.FromSeconds(60));
        }
        Assert.Equal(0, w.Restarter.Count, "never two in a row");
    }

    [Test]
    public static async Task Offline_risk_late_in_the_session_restarts()
    {
        var w = NewWorld();
        await w.RunFor(TimeSpan.FromMinutes(12));
        // Many validators: no own slot in this hour, so only the offline risk can tell.
        w.Sim.Count = 1000;
        w.Sim.Start(w.Now, 101, myIndex: 999);
        w.Sim.Produce = false;
        await w.RunFor(TimeSpan.FromMinutes(50));
        Assert.Equal(0, w.Restarter.Count, "not before 85% of the session");
        await w.RunFor(TimeSpan.FromMinutes(3));
        Assert.Equal(1, w.Restarter.Count, "restart at 85% without block or heartbeat");
        // The restart goes out at the first poll; its message names the reason (the alert itself waits for a second poll).
        Assert.True(w.Titled("Újraindítom a Mosaic node-ot")[0].Data.Text!.Contains("A node offline-nak fog számítani"),
            "the restart message names the reason");
    }

    [Test]
    public static async Task Heartbeat_prevents_the_offline_risk()
    {
        var w = NewWorld();
        await w.RunFor(TimeSpan.FromMinutes(12));
        w.Sim.Count = 1000;
        w.Sim.Start(w.Now, 101, myIndex: 999);
        w.Sim.Heartbeat = true;
        await w.RunFor(TimeSpan.FromMinutes(58));
        Assert.Equal(0, w.Restarter.Count, "the heartbeat counts as online");
    }

    [Test]
    public static async Task Offline_session_result_is_reported_and_cleared_by_a_good_session()
    {
        var w = NewWorld(c => c.Restart.Enabled = false);
        w.Sim.Count = 1000;
        w.Sim.Start(w.Now, 101, myIndex: 999);
        w.Sim.Produce = false;
        await w.RunFor(TimeSpan.FromMinutes(10));
        w.Sim.Start(w.Now, 102, myIndex: null);
        await w.RunFor(TimeSpan.FromMinutes(2));
        Assert.Equal(1, w.Titled("Az előző sessionben nem jelentkezett a node").Count, "reported");

        w.Sim.Count = 10;
        w.Sim.Produce = true;
        w.Sim.Start(w.Now, 103, myIndex: 3);
        await w.RunFor(TimeSpan.FromMinutes(5));
        w.Sim.Start(w.Now, 104, myIndex: null);
        await w.RunFor(TimeSpan.FromMinutes(2));
        Assert.Equal(1, w.Titled("A legutóbbi kiválasztott sessionben rendben dolgozott").Count, "resolved");
    }

    [Test]
    public static async Task Long_without_selection_warns_and_clears()
    {
        var w = NewWorld(c => c.Selection.NotSelectedAlertHours = 6);
        await w.RunFor(TimeSpan.FromHours(5));
        Assert.Equal(0, w.Titled("Régóta nem választották ki a validátort").Count, "not yet");
        await w.RunFor(TimeSpan.FromHours(1.1));
        Assert.Equal(1, w.Titled("Régóta nem választották ki a validátort").Count, "after 6 hours");
        w.Sim.Start(w.Now, 101, myIndex: 3);
        await w.RunFor(TimeSpan.FromMinutes(2));
        Assert.Equal(1, w.Titled("Újra kiválasztották a validátort").Count, "resolved");
    }

    [Test]
    public static async Task Chilled_validator_is_not_expected_to_be_selected()
    {
        var w = NewWorld(c => c.Selection.NotSelectedAlertHours = 2);
        w.Probe.Validator = new ValidatorState(ValidatorStateKind.Chilled, 5);
        await w.RunFor(TimeSpan.FromHours(3));
        Assert.Equal(0, w.Titled("Régóta nem választották ki a validátort").Count, "no alert while chilled");
    }

    [Test]
    public static async Task Daily_summary_once_a_day_at_the_set_hour()
    {
        var w = NewWorld(c => c.Selection.DailySummaryHour = 8); // the world starts at 06:00 UTC
        w.Sim.Start(w.Now, 101, myIndex: 3);
        await w.RunFor(TimeSpan.FromMinutes(20));
        w.Sim.Start(w.Now, 102, myIndex: null);
        await w.RunFor(TimeSpan.FromHours(1.6));
        Assert.Equal(0, w.Titled("Napi összesítés").Count, "not before 08:00");
        await w.RunFor(TimeSpan.FromHours(1));
        var summary = w.Titled("Napi összesítés");
        Assert.Equal(1, summary.Count, "at 08:00");
        Assert.True(summary[0].Data.Text!.Contains("1× választották ki"), summary[0].Data.Text!);
        await w.RunFor(TimeSpan.FromHours(20));
        Assert.Equal(1, w.Titled("Napi összesítés").Count, "only once a day");
        await w.RunFor(TimeSpan.FromHours(4));
        Assert.Equal(2, w.Titled("Napi összesítés").Count, "next day");
    }

    [Test]
    public static async Task Restarts_are_limited_per_hour_and_per_day()
    {
        var w = NewWorld(c => c.Restart.MaxPerDay = 3);
        await w.RunFor(TimeSpan.FromMinutes(5));
        w.Probe.RelayAdvances = false; // stuck all day
        await w.RunFor(TimeSpan.FromHours(10));
        Assert.Equal(3, w.Restarter.Count, "three restarts, then the daily limit");
        var refused = w.Titled("Nem indítom újra a node-ot");
        Assert.True(refused.Count >= 1 && refused.Last().Data.Text!.Contains("legfeljebb 3-szor"),
            "says the daily limit: " + string.Join(" | ", refused.Select(e => e.Data.Text)));
    }

    [Test]
    public static async Task Missed_slots_while_restarting_do_not_count()
    {
        var w = NewWorld();
        await w.RunFor(TimeSpan.FromMinutes(12));
        w.Sim.Start(w.Now, 101, myIndex: 3);
        w.Sim.Produce = false;
        await w.RunFor(TimeSpan.FromMinutes(4));
        Assert.Equal(1, w.Restarter.Count, "restarted for the misses");
        // The node is restarting: its slots are empty, but that is the restart, not a new failure.
        await w.RunFor(TimeSpan.FromMinutes(14));
        w.Sim.Produce = true;
        await w.RunFor(TimeSpan.FromMinutes(5));
        Assert.Equal(1, w.Restarter.Count, "no second restart");
        Assert.True(w.Titled("A node újra gyárt blokkot").Count >= 1, "resolved once it works");
    }

    [Test]
    public static async Task Unreadable_session_data_warns_and_never_restarts()
    {
        var w = NewWorld();
        w.Sim.Error = "Session.CurrentIndex nem található";
        await w.RunFor(TimeSpan.FromMinutes(30));
        Assert.Equal(1, w.Titled("A session-adatok nem kérdezhetők le").Count, "warning");
        Assert.Equal(0, w.Restarter.Count, "no restart");
    }

    [Test]
    public static async Task Selection_survives_an_agent_restart()
    {
        var dir = TestConfig.TempDir();
        var w = NewWorld(dir: dir);
        await w.RunFor(TimeSpan.FromMinutes(12));
        w.Sim.Start(w.Now, 101, myIndex: 3);
        w.Sim.Produce = false;
        await w.RunFor(TimeSpan.FromSeconds(150));
        Assert.Equal(1, w.Titled("Kimaradt egy saját blokk").Count, "one miss before the agent restart");

        var again = NewWorld(dir: dir, start: w.Now);
        again.Sim.Start(w.Sim.StartedAt, 101, myIndex: 3);
        again.Sim.Produce = false;
        again.Probe.ChainActiveSince = w.Now - TimeSpan.FromMinutes(20);
        await again.RunFor(TimeSpan.FromSeconds(90));
        Assert.Equal(1, again.Restarter.Count, "the second miss in a row still counts after the agent restart");
    }
}
