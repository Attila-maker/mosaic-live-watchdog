using System.Text.Json;
using NodeWatch.Sinks;

namespace NodeWatch.Tests;

internal static class HttpTests
{
    private static string RpcResult(string body, Func<string, JsonElement, object?> answer)
    {
        using var doc = JsonDocument.Parse(body);
        var method = doc.RootElement.GetProperty("method").GetString()!;
        var p = doc.RootElement.GetProperty("params");
        var result = answer(method, p);
        return JsonSerializer.Serialize(new { jsonrpc = "2.0", id = doc.RootElement.GetProperty("id").GetInt32(), result });
    }

    [Test]
    public static async Task NodeProbe_reads_a_substrate_node()
    {
        const string alice = "15oF4uVJwmo4TdGW7VfQxNLavjCXviqxT9S1MgbjMNHr6Sp5";
        var expectedKey = StorageKeys.ValidatorState(Ss58.DecodePublicKey(alice));
        await using var relay = new FakeServer((_, body) => (200, RpcResult(body, (m, _) => m switch
        {
            "chain_getHeader" => new { number = "0x2025f43" },
            "system_peers" => new object[] { new { roles = "NONE" }, new { roles = "AUTHORITY" }, new { roles = "FULL" } },
            _ => null,
        })));
        await using var para = new FakeServer((_, body) => (200, RpcResult(body, (m, p) => m switch
        {
            "chain_getHeader" when p.GetArrayLength() == 0 => new { number = "0x10" },
            "chain_getHeader" => new { number = "0x0e" },
            "chain_getFinalizedHead" => "0xabc",
            "system_health" => new { peers = 9, isSyncing = false, shouldHavePeers = true },
            "state_getStorage" when p[0].GetString() == expectedKey => "0x02e8030000",
            _ => null,
        })));
        var config = TestConfig.Create(TestConfig.TempDir());
        config.Node.RelayRpc = relay.Url;
        config.Node.ParaRpc = para.Url;
        config.Node.ValidatorAddress = alice;
        config.Node.DataPath = Path.GetTempPath();
        using var http = new HttpClient();
        var probe = new NodeProbe(config, http, new FixedServices());
        var s = await probe.CollectAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(null, s.RelayError, "relay error");
        Assert.Equal(0x2025f43L, s.RelayBest, "relay best");
        Assert.Equal(2, s.RelayPeers!.SyncSources, "sync sources");
        Assert.Equal(16L, s.ParaBest, "para best");
        Assert.Equal(14L, s.ParaFinalized, "para finalized");
        Assert.Equal(9, s.ParaHealth!.Peers, "para peers");
        Assert.Equal(ValidatorStateKind.Chilled, s.Validator!.Kind, "validator");
        Assert.Equal(1000u, s.Validator.ChilledSession, "chill session");
        Assert.True(s.Disk is { FreePercent: > 0 }, "disk");
        Assert.Equal("active", s.Services["mosaic-chain-mainnet.service"], "service");
    }

    [Test]
    public static async Task NodeProbe_reports_an_unreachable_node()
    {
        var config = TestConfig.Create(TestConfig.TempDir());
        config.Node.RelayRpc = "http://127.0.0.1:9/";
        config.Node.ParaRpc = "http://127.0.0.1:9/";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var s = await new NodeProbe(config, http, new FixedServices()).CollectAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.True(s.RelayError is not null && s.ParaError is not null, "errors reported");
        Assert.Equal(null, s.RelayBest, "no number");
    }

    [Test]
    public static async Task SafeChat_sink_sends_the_cloud_event_with_the_right_headers()
    {
        var status = 202;
        await using var server = new FakeServer((_, _) => (status, "{\"id\":\"x\"}"));
        using var http = new HttpClient();
        var sink = new SafeChatSink(http, "safechat", 0, server.Url + "api/ops/alerts", "scint_secret");
        var e = new EventFactory(TestConfig.Create("/tmp")).Firing("relay.block_progress", Level.Critical, "A relay chain nem halad",
            "3 perce a #1 blokkon áll.", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.Equal(Outcome.Ok, (await sink.SendAsync(e, CancellationToken.None)).Outcome, "202 is ok");
        var r = server.Requests.Single();
        Assert.Equal("POST", r.Method, "method");
        Assert.Equal("/api/ops/alerts", r.Path, "path");
        Assert.Equal("Bearer scint_secret", r.Headers["Authorization"], "auth");
        Assert.Equal("1", r.Headers["X-SafeChat"], "csrf header");
        Assert.True(r.Headers["Content-Type"].StartsWith("application/cloudevents+json"), "content type");
        using var doc = JsonDocument.Parse(r.Body);
        Assert.Equal("1.0", doc.RootElement.GetProperty("specversion").GetString(), "specversion");
        Assert.Equal("eu.aisdev.nodewatch.alert.firing", doc.RootElement.GetProperty("type").GetString(), "type");
        Assert.Equal("testnode/relay.block_progress", doc.RootElement.GetProperty("data").GetProperty("alertKey").GetString(), "alertKey");
        Assert.Equal("A relay chain nem halad", doc.RootElement.GetProperty("data").GetProperty("title").GetString(), "title (UTF-8)");
        Assert.True(!doc.RootElement.TryGetProperty("channels", out _), "no channels extension without relay channels");

        status = 503;
        Assert.Equal(Outcome.Retry, (await sink.SendAsync(e, CancellationToken.None)).Outcome, "503 retries");
        status = 401;
        Assert.Equal(Outcome.Drop, (await sink.SendAsync(e, CancellationToken.None)).Outcome, "401 drops");
        status = 429;
        Assert.Equal(Outcome.Retry, (await sink.SendAsync(e, CancellationToken.None)).Outcome, "429 retries");
    }

    [Test]
    public static async Task Matrix_sink_puts_an_m_notice_with_the_event_id_as_txn()
    {
        await using var server = new FakeServer((_, _) => (200, "{\"event_id\":\"$1\"}"));
        using var http = new HttpClient();
        var sink = new MatrixSink(http, "matrix", 0, server.Url.TrimEnd('/'), "!room:example.org", "syt_token");
        var e = new EventFactory(TestConfig.Create("/tmp")).Resolved("para.finality", Level.Critical, "A parachain újra véglegesít",
            "Véglegesítve: #5. <b>", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.Equal(Outcome.Ok, (await sink.SendAsync(e, CancellationToken.None)).Outcome, "ok");
        var r = server.Requests.Single();
        Assert.Equal("PUT", r.Method, "method");
        Assert.Equal($"/_matrix/client/v3/rooms/%21room%3Aexample.org/send/m.room.message/{e.Id}", r.Path, "path");
        Assert.Equal("Bearer syt_token", r.Headers["Authorization"], "auth");
        using var doc = JsonDocument.Parse(r.Body);
        Assert.Equal("m.notice", doc.RootElement.GetProperty("msgtype").GetString(), "msgtype");
        Assert.True(doc.RootElement.GetProperty("body").GetString()!.StartsWith("[HELYREÁLLT] testnode – A parachain"), "body");
        Assert.True(doc.RootElement.GetProperty("formatted_body").GetString()!.Contains("&lt;b&gt;"), "html escaped");
    }

    [Test]
    public static async Task Matrix_login_gets_a_non_expiring_token_and_joins_the_room()
    {
        await using var server = new FakeServer((req, _) => req.Url!.AbsolutePath switch
        {
            "/_matrix/client/v3/login" => (200, """{"access_token":"syt_new","device_id":"NW1"}"""),
            _ => (200, """{"room_id":"!room:example.org"}"""),
        });
        using var http = new HttpClient();
        var token = await MatrixAccount.LoginAsync(http, server.Url, "mynode-alerts", "titok \"x\"", "NodeWatch (n)",
            CancellationToken.None);
        Assert.Equal("syt_new", token, "token");
        using (var login = JsonDocument.Parse(server.Requests[0].Body))
        {
            Assert.Equal("m.login.password", login.RootElement.GetProperty("type").GetString(), "login type");
            Assert.Equal("titok \"x\"", login.RootElement.GetProperty("password").GetString(), "password escaped");
            Assert.True(!login.RootElement.TryGetProperty("refresh_token", out _), "no refresh token asked: the token does not expire");
        }
        Assert.Equal(null, await MatrixAccount.JoinAsync(http, server.Url, token, "!room:example.org", CancellationToken.None), "joined");
        Assert.Equal("/_matrix/client/v3/join/%21room%3Aexample.org", server.Requests[1].Path, "join path");
        Assert.Equal("Bearer syt_new", server.Requests[1].Headers["Authorization"], "join auth");
    }

    [Test]
    public static async Task Matrix_login_reports_the_server_error()
    {
        await using var server = new FakeServer((_, _) => (403, """{"errcode":"M_FORBIDDEN","error":"Invalid password"}"""));
        using var http = new HttpClient();
        var message = "";
        try
        {
            await MatrixAccount.LoginAsync(http, server.Url, "bot", "rossz", "NodeWatch", CancellationToken.None);
        }
        catch (InvalidOperationException e)
        {
            message = e.Message;
        }
        Assert.True(message.Contains("M_FORBIDDEN") && message.Contains("Invalid password"), "error shown: " + message);
    }

    [Test]
    public static void Matrix_room_alias_is_refused_in_the_config()
    {
        var c = TestConfig.Create(TestConfig.TempDir());
        c.Sinks.Add(new SinkOptions { Type = "matrix", Homeserver = "https://matrix-client.matrix.org", RoomId = "#alerts:matrix.org",
            AccessTokenFile = "matrix.token" });
        Assert.True(c.Validate().Any(e => e.Contains("belső azonosítója")), "alias refused");
    }

    [Test]
    public static async Task Both_mode_falls_back_when_the_relay_does_not_take_a_critical_alert()
    {
        var dir = TestConfig.TempDir();
        var relay = new MemorySink("relay") { Next = Outcome.Retry };
        var telegram = new MemorySink("telegram", 2);
        var outbox = new Outbox(dir, [telegram], relay, [telegram], TimeSpan.FromMinutes(2));
        var events = new EventFactory(TestConfig.Create(dir, "both"));
        var now = new DateTimeOffset(2026, 11, 6, 8, 0, 0, TimeSpan.Zero);
        outbox.Enqueue(relay, events.Firing("relay.rpc", Level.Critical, "A relay chain RPC nem válaszol", "", now, now), now);
        outbox.Enqueue(relay, events.Firing("host.disk", Level.Warning, "Kevés a szabad hely", "", now, now), now);

        for (var t = 0; t < 4; t++) await outbox.DispatchAsync(now + TimeSpan.FromSeconds(30 * t), CancellationToken.None);
        Assert.Equal(0, telegram.Received.Count, "nothing before the fallback limit");

        await outbox.DispatchAsync(now + TimeSpan.FromMinutes(2.5), CancellationToken.None);
        await outbox.DispatchAsync(now + TimeSpan.FromMinutes(2.6), CancellationToken.None);
        Assert.Equal(1, telegram.Received.Count, "the critical one fell back");
        Assert.Equal("A relay chain RPC nem válaszol", telegram.Received[0].Data.Title, "which one");

        relay.Next = Outcome.Ok;
        await outbox.DispatchAsync(now + TimeSpan.FromMinutes(30), CancellationToken.None);
        Assert.Equal(2, relay.Received.Count, "the relay still gets both once it is back");
        Assert.Equal(1, telegram.Received.Count, "no duplicate fallback");
        Assert.Equal(0, outbox.Pending, "queues empty");
    }

    [Test]
    public static async Task Outbox_keeps_order_and_survives_a_new_process()
    {
        var dir = TestConfig.TempDir();
        var sink = new MemorySink("safechat") { Next = Outcome.Retry };
        var events = new EventFactory(TestConfig.Create(dir));
        var now = DateTimeOffset.UtcNow;
        var outbox = new Outbox(dir, [sink], null, [], TimeSpan.FromMinutes(2));
        // Same millisecond on purpose: the order must still hold.
        string[] titles = ["első", "második", "harmadik", "negyedik", "ötödik"];
        foreach (var title in titles) outbox.Enqueue(sink, events.Firing(title, Level.Critical, title, "", now, now), now);
        await outbox.DispatchAsync(now, CancellationToken.None);

        sink.Next = Outcome.Ok;
        var restarted = new Outbox(dir, [sink], null, [], TimeSpan.FromMinutes(2));
        await restarted.DispatchAsync(now + TimeSpan.FromMinutes(1), CancellationToken.None);
        var delivered = sink.Received.Where(e => e.Data.Title is not null).Select(e => e.Data.Title).Distinct().ToList();
        Assert.Equal(string.Join(",", titles), string.Join(",", delivered), "all delivered after the restart, in order");
    }

    [Test]
    public static async Task Command_restarter_runs_the_command_without_a_shell_injection()
    {
        var marker = Path.Combine(TestConfig.TempDir(), "restarted");
        var ok = new CommandRestarter(new RestartOptions { Command = ["/bin/sh", "-c", $"echo ok > {marker}"] });
        Assert.Equal(null, await ok.RestartAsync(CancellationToken.None), "success");
        Assert.True(File.Exists(marker), "command ran");
        var failing = new CommandRestarter(new RestartOptions { Command = ["/bin/false"] });
        Assert.True(await failing.RestartAsync(CancellationToken.None) is not null, "failure reported");
        var hanging = new CommandRestarter(new RestartOptions { Command = ["/bin/sleep", "30"], CommandTimeoutSeconds = 1 });
        Assert.True((await hanging.RestartAsync(CancellationToken.None))!.Contains("nem fejeződött be"), "timeout reported");
    }

    [Test]
    public static void Config_validation_catches_mistakes()
    {
        var c = TestConfig.Create("/tmp");
        c.Mode = "relay";
        c.Relay = new RelayOptions();
        c.Node.ValidatorAddress = "15oF4uVJwmo4TdGW7VfQxNLavjCXviqxT9S1MgbjMNHr6Sp6";
        c.Restart.Triggers.Add("nincs.ilyen");
        var errors = c.Validate();
        Assert.True(errors.Any(e => e.Contains("Relay.Url")), "relay url");
        Assert.True(errors.Any(e => e.Contains("ValidatorAddress")), "address checksum");
        Assert.True(errors.Any(e => e.Contains("nincs.ilyen")), "unknown trigger");
    }

    [Test]
    public static async Task NodeProbe_survives_strange_answers()
    {
        // Relay: header is null (broken database); para: an error without a message, then a non-object body.
        await using var relay = new FakeServer((_, body) => (200, RpcResult(body, (_, _) => null)));
        var paraCalls = 0;
        await using var para = new FakeServer((_, _) => (200, Interlocked.Increment(ref paraCalls) % 2 == 1
            ? """{"jsonrpc":"2.0","id":1,"error":{"code":-1}}"""
            : "[1,2,3]"));
        var config = TestConfig.Create(TestConfig.TempDir());
        config.Node.RelayRpc = relay.Url;
        config.Node.ParaRpc = para.Url;
        config.Node.DataPath = "/nincs/ilyen/mappa";
        using var http = new HttpClient();
        var probe = new NodeProbe(config, http, new FixedServices());
        for (var i = 0; i < 2; i++)
        {
            var s = await probe.CollectAsync(DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.True(s.RelayError is not null, "relay error recorded");
            Assert.True(s.ParaError is not null, "para error recorded");
            Assert.True(s.DiskError!.Contains("nem létezik"), "disk error recorded");
            Assert.Equal("active", s.Services["mosaic-chain-mainnet.service"], "the rest still ran");
        }
    }

    [Test]
    public static async Task NodeProbe_keeps_the_block_number_when_system_peers_is_not_allowed()
    {
        await using var relay = new FakeServer((_, body) => body.Contains("system_peers")
            ? (200, """{"jsonrpc":"2.0","id":1,"error":{"code":-32601,"message":"Method not found"}}""")
            : (200, RpcResult(body, (_, _) => new { number = "0x10" })));
        var config = TestConfig.Create(TestConfig.TempDir());
        config.Node.RelayRpc = relay.Url;
        using var http = new HttpClient();
        var s = await new NodeProbe(config, http, new FixedServices()).CollectAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(null, s.RelayError, "relay works");
        Assert.Equal(16L, s.RelayBest, "block number");
        Assert.True(s.RelayPeersError!.Contains("Method not found"), "peer error kept apart");
    }

    [Test]
    public static async Task A_throwing_sink_does_not_hold_up_the_others()
    {
        var dir = TestConfig.TempDir();
        var broken = new MemorySink("matrix") { Throws = true };
        var good = new MemorySink("telegram");
        var events = new EventFactory(TestConfig.Create(dir));
        var now = DateTimeOffset.UtcNow;
        var outbox = new Outbox(dir, [broken, good], null, [], TimeSpan.FromMinutes(2));
        var e = events.Firing("x", Level.Critical, "próba", "", now, now);
        outbox.Enqueue(broken, e, now);
        outbox.Enqueue(good, e, now);
        await outbox.DispatchAsync(now, CancellationToken.None);
        Assert.Equal(1, good.Received.Count, "the good sink got it");
        Assert.Equal(2, outbox.Pending + good.Received.Count, "the broken one keeps it for a retry");
    }

    [Test]
    public static void Config_rejects_a_restart_command_for_another_unit_and_multiline_secrets()
    {
        var dir = TestConfig.TempDir();
        var c = TestConfig.Create(dir);
        c.Restart.Unit = "mosaic-chain-testnet.service";
        Assert.True(c.Validate().Any(e => e.Contains("Restart.Command")), "command/unit mismatch");

        var secret = Path.Combine(dir, "matrix.token");
        File.WriteAllText(secret, "syt_abc\nsyt_def\n");
        var threw = false;
        try { c.ReadSecret(secret); }
        catch (ConfigException) { threw = true; }
        Assert.True(threw, "a token with two lines is refused");
    }

    [Test]
    public static void Example_config_is_valid()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.example.json");
        var config = Config.Load(path);
        Assert.Equal("direct", config.Mode, "mode");
        Assert.Equal(10.0, config.Restart.AfterMinutes, "restart after");
        Assert.Equal(24.0, config.Restart.MinHoursBetween, "once a day");
    }

    private sealed class FixedServices : IServiceStatus
    {
        public Task<string> StateAsync(string unit, CancellationToken ct) =>
            Task.FromResult(unit.StartsWith("mosaic-") ? "active" : "inactive");

        public Task<TimeSpan?> ActiveForAsync(string unit, CancellationToken ct) => Task.FromResult<TimeSpan?>(TimeSpan.FromDays(1));
    }
}
