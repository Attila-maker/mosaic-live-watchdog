using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using NodeWatch.Sinks;

namespace NodeWatch;

internal static class Program
{
    public static string Version =>
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "?";

    private const string Usage = """
        NodeWatch – Mosaic node figyelő (watchdog)

        Használat: nodewatch [parancs] [--config <fájl>] [--verbose]

        Parancsok:
          run              figyelés, riasztás és újraindítás (alapértelmezett; ezt futtatja a systemd)
          check            egyszeri lekérdezés: kiírja, mit lát a node-ból (riasztást nem küld)
          test-alert       próbariasztás és „helyreállt” üzenet minden beállított kimenetre
          telegram-chatid  kiírja a Telegram-botnak írt chatek azonosítóit (a ChatId beállításhoz)
          matrix-login     a Matrix bot bejelentkeztetése: belép a szobába és elmenti az access tokent (sudo-val)
          version          verziószám

        Alapértelmezett konfiguráció: /etc/nodewatch/appsettings.json
        """;

    public static async Task<int> Main(string[] args)
    {
        var command = args.Where((a, i) => !a.StartsWith('-') && (i == 0 || args[i - 1] != "--config")).FirstOrDefault() ?? "run";
        var configPath = Option(args, "--config") ?? "/etc/nodewatch/appsettings.json";
        if (args.Contains("--help") || args.Contains("-h") || command == "help")
        {
            Console.WriteLine(Usage);
            return 0;
        }
        if (command == "version")
        {
            Console.WriteLine(Version);
            return 0;
        }

        Config config;
        try
        {
            config = Config.Load(configPath);
        }
        catch (ConfigException e)
        {
            Log.Error(e.Message);
            return 2;
        }
        Log.Verbose = config.VerboseLog || args.Contains("--verbose");

        using var cts = new CancellationTokenSource();
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; cts.Cancel(); });
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; cts.Cancel(); });

        using var rpcHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(2, config.Checks.RpcTimeoutSeconds)) };
        using var outHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        outHttp.DefaultRequestHeaders.UserAgent.ParseAdd($"NodeWatch/{Version}");

        try
        {
            return command switch
            {
                "run" => await RunAsync(config, rpcHttp, outHttp, cts.Token),
                "check" => await CheckAsync(config, rpcHttp, cts.Token),
                "test-alert" => await TestAlertAsync(config, outHttp, cts.Token),
                "telegram-chatid" => await TelegramChatIdAsync(config, outHttp, cts.Token),
                "matrix-login" => await MatrixLoginAsync(config, outHttp, cts.Token),
                _ => Unknown(command),
            };
        }
        catch (ConfigException e)
        {
            Log.Error(e.Message);
            return 2;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return 0;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Ismeretlen parancs: {command}\n\n{Usage}");
        return 2;
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static (List<ISink> Sinks, ISink? Relay) BuildSinks(Config config, HttpClient http)
    {
        var sinks = config.Sinks.Select(s => SinkFactory.Create(s, config, http)).ToList();
        ISink? relay = config.Relay.Configured
            ? new RelaySink(http, config.Relay.Url, config.ReadSecret(config.Relay.NodeTokenFile))
            : null;
        return (sinks, relay);
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    private static async Task<int> RunAsync(Config config, HttpClient rpcHttp, HttpClient outHttp, CancellationToken ct)
    {
        // Files a root run leaves in the state folder could not be changed by the service later.
        if (OperatingSystem.IsLinux() && GetEffectiveUserId() == 0)
        {
            Log.Error("A run parancsot ne rootként futtasd: a systemd a nodewatch felhasználóval futtatja " +
                      "(kézzel: sudo -u nodewatch nodewatch run).");
            return 2;
        }
        using var instance = InstanceLock.TryAcquire(config.StateDirectory, out var problem, out var busy);
        if (instance is null)
        {
            Log.Error(problem!);
            return busy ? 3 : 2; // 3: systemd tries again later; 2: needs fixing first
        }

        var (sinks, relay) = BuildSinks(config, outHttp);
        var services = new SystemctlStatus();
        var agent = new Agent(config, new NodeProbe(config, rpcHttp, services), services, new CommandRestarter(config.Restart),
            sinks, relay, new StateStore(config.StateDirectory), DateTimeOffset.UtcNow, BootClock.Real);
        Log.Info($"NodeWatch {Version} indul – node: {config.Node.Name}, mód: {config.Mode}, kimenetek: " +
                 $"{string.Join(", ", sinks.Select(s => s.Name).DefaultIfEmpty("-"))}" +
                 (relay is null ? "" : $", relay: {config.Relay.Url}") +
                 (config.Restart.Enabled
                     ? $", újraindítás: {config.Restart.AfterMinutes:0.#} perc hiba után, legfeljebb {config.Restart.MinHoursBetween:0.#} óránként egyszer"
                     : ", újraindítás: kikapcsolva"));
        await agent.RunAsync(ct);
        Log.Info("NodeWatch leáll.");
        return 0;
    }

    private static async Task<int> CheckAsync(Config config, HttpClient rpcHttp, CancellationToken ct)
    {
        var probe = new NodeProbe(config, rpcHttp, new SystemctlStatus());
        var s = await probe.CollectAsync(DateTimeOffset.UtcNow, ct);
        Console.WriteLine($"Node: {config.Node.Name} ({config.Node.Network})");
        Console.WriteLine(s.RelayError is not null ? $"Relay chain:  HIBA – {s.RelayError}"
            : s.RelayPeers is { } p ? $"Relay chain:  #{s.RelayBest}, peerek: {p.Total} (FULL {p.Full}, AUTHORITY {p.Authority}, NONE {p.None}, LIGHT {p.Light})"
            : s.RelayTotalPeers is { } t ? $"Relay chain:  #{s.RelayBest}, peerek: {t} (a szerepük nem kérdezhető le: a relay chain RPC csak biztonságos hívásokat enged)"
            : $"Relay chain:  #{s.RelayBest}, peerek: nem kérdezhetők le – {s.RelayPeersError}");
        Console.WriteLine(s.ParaError is null
            ? $"Parachain:    #{s.ParaBest}, véglegesítve #{s.ParaFinalized}, peerek: {s.ParaHealth!.Peers}, szinkronizál: {(s.ParaHealth.IsSyncing ? "igen" : "nem")}"
            : $"Parachain:    HIBA – {s.ParaError}");
        if (!string.IsNullOrWhiteSpace(config.Node.ValidatorAddress))
        {
            var state = s.ParaError is not null ? "nem lekérdezhető (a parachain RPC nem válaszol)"
                : s.ValidatorError is not null ? $"HIBA – {s.ValidatorError}"
                : s.ValidatorMissing ? "nem található a NftStaking.ValidatorStates tárolóban"
                : s.Validator is { Kind: ValidatorStateKind.Chilled } v ? $"Chilled (session {v.ChilledSession})"
                : s.Validator?.Kind.ToString() ?? "ismeretlen";
            Console.WriteLine($"Validátor:    {state}");
        }
        foreach (var (unit, state) in s.Services) Console.WriteLine($"Szolgáltatás: {unit}: {state}");
        Console.WriteLine(s.Disk is { } d ? $"Lemez:        {d.Path}: {d.FreePercent:0.#}% szabad ({d.FreeGb:0.#} GB)" : $"Lemez:        HIBA – {s.DiskError}");
        var state2 = new StateStore(config.StateDirectory).Load(DateTimeOffset.UtcNow);
        if (state2.LastRestartAt is { } last)
            Console.WriteLine($"Utolsó újraindítás: {last.ToLocalTime():yyyy-MM-dd HH:mm}" +
                              (state2.LastRestartAssumed ? " (feltételezett: az állapotfájl nem volt olvasható)" : ""));
        return s.RelayError is null && s.ParaError is null ? 0 : 1;
    }

    private static async Task<int> TestAlertAsync(Config config, HttpClient outHttp, CancellationToken ct)
    {
        var (sinks, relay) = BuildSinks(config, outHttp);
        var targets = new List<ISink>();
        if (config.Mode is "relay" or "both") targets.Add(relay!);
        if (config.Mode is "direct" or "both") targets.AddRange(sinks);
        var events = new EventFactory(config);
        var now = DateTimeOffset.UtcNow;
        var firing = events.Firing("test", Level.Critical, "Próbariasztás",
            "Ez egy próbaüzenet a NodeWatch-tól. Ha megkaptad, a riasztás működik.", now, now);
        var resolved = events.Resolved("test", Level.Critical, "Próbariasztás vége", "A próba rendben lezajlott.", now, now);
        var failures = 0;
        foreach (var sink in targets)
        {
            foreach (var e in new[] { firing, resolved })
            {
                var result = await sink.SendAsync(e, ct);
                Console.WriteLine($"{sink.Name,-12} {(e.IsResolved ? "helyreállt" : "riasztás"),-11} {(result.Outcome == Outcome.Ok ? "OK" : "HIBA: " + result.Message)}");
                if (result.Outcome != Outcome.Ok) failures++;
            }
        }
        return failures == 0 ? 0 : 1;
    }

    private static async Task<int> TelegramChatIdAsync(Config config, HttpClient outHttp, CancellationToken ct)
    {
        var telegram = config.Sinks.FirstOrDefault(s => s.Type == "telegram")
                       ?? throw new ConfigException("Nincs telegram kimenet a konfigurációban.");
        var token = config.ReadSecret(telegram.BotTokenFile!);
        var doc = await outHttp.GetFromJsonAsync<JsonElement>($"https://api.telegram.org/bot{token}/getUpdates", ct);
        var seen = new HashSet<long>();
        // Private chat and group: message; channel: channel_post; bot added to a group or channel: my_chat_member.
        string[] kinds = ["message", "edited_message", "channel_post", "my_chat_member"];
        foreach (var update in doc.GetProperty("result").EnumerateArray())
        {
            var item = kinds.Select(k => update.TryGetProperty(k, out var v) ? v : (JsonElement?)null).FirstOrDefault(v => v is not null);
            if (item is not { } m || !m.TryGetProperty("chat", out var chat)) continue;
            var id = chat.GetProperty("id").GetInt64();
            if (!seen.Add(id)) continue;
            var type = chat.TryGetProperty("type", out var ty) ? ty.GetString() : "?";
            var name = chat.TryGetProperty("title", out var t) ? t.GetString()
                : chat.TryGetProperty("username", out var u) ? "@" + u.GetString()
                : chat.TryGetProperty("first_name", out var f) ? f.GetString() : "";
            Console.WriteLine($"ChatId: {id}   ({type}: {name})");
        }
        if (seen.Count == 0)
            Console.WriteLine("Nincs üzenet. Írj a botnak Telegramon (például /start), csoportban küldd el: /start@<botneve>, " +
                              "majd futtasd újra.");
        return 0;
    }

    private static async Task<int> MatrixLoginAsync(Config config, HttpClient outHttp, CancellationToken ct)
    {
        var matrix = config.Sinks.FirstOrDefault(s => s.Type == "matrix")
                     ?? throw new ConfigException("Nincs matrix kimenet a konfigurációban: előbb vedd fel a Sinks listába " +
                                                  "(Homeserver, RoomId, AccessTokenFile).");
        var path = config.ResolvePath(matrix.AccessTokenFile!);
        // Checked first, so a login (a new device on the bot account) is not wasted when the token cannot be saved.
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.Write)) { }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"A tokenfájl nem írható: {path} ({e.Message}). Hozd létre, majd futtasd sudo-val:");
            Console.WriteLine($"  sudo install -m 0640 -o root -g nodewatch /dev/null {path}");
            Console.WriteLine("  sudo nodewatch matrix-login");
            return 1;
        }

        Console.WriteLine($"Matrix szerver: {matrix.Homeserver}   szoba: {matrix.RoomId}");
        var user = Prompt("A bot felhasználóneve (pl. mynode-alerts vagy @mynode-alerts:matrix.org): ", hidden: false);
        var password = Prompt("A bot jelszava: ", hidden: true);
        if (user.Length == 0 || password.Length == 0)
        {
            Console.WriteLine("Felhasználónév és jelszó kell.");
            return 1;
        }

        string token;
        try
        {
            token = await MatrixAccount.LoginAsync(outHttp, matrix.Homeserver!, user, password,
                $"NodeWatch ({config.Node.Name})", ct);
        }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException or TaskCanceledException &&
                                  !ct.IsCancellationRequested)
        {
            Console.WriteLine($"HIBA: {e.Message}");
            return 1;
        }
        File.WriteAllText(path, token + "\n");
        Console.WriteLine($"Bejelentkezés: OK, az access token mentve: {path}");

        string? joinError;
        try
        {
            joinError = await MatrixAccount.JoinAsync(outHttp, matrix.Homeserver!, token, matrix.RoomId!, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            joinError = e.Message;
        }
        if (joinError is not null)
        {
            Console.WriteLine($"Belépés a szobába ({matrix.RoomId}): HIBA – {joinError}");
            Console.WriteLine("Hívd meg a botot a szobába a saját fiókodról, majd futtasd újra ezt a parancsot.");
            return 1;
        }
        Console.WriteLine($"Belépés a szobába ({matrix.RoomId}): OK. Próba: sudo -u nodewatch nodewatch test-alert");
        return 0;
    }

    private static string Prompt(string text, bool hidden)
    {
        Console.Write(text);
        if (!hidden || Console.IsInputRedirected) return (Console.ReadLine() ?? "").Trim();
        var value = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0) value.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                value.Append(key.KeyChar);
            }
        }
        Console.WriteLine();
        return value.ToString();
    }
}
