namespace NodeWatch;

internal interface IRestarter
{
    /// <summary>Restarts the chain service. Returns null on success, or the error.</summary>
    Task<string?> RestartAsync(CancellationToken ct);
}

internal sealed class CommandRestarter(RestartOptions options) : IRestarter
{
    public async Task<string?> RestartAsync(CancellationToken ct)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(options.Command,
                TimeSpan.FromSeconds(Math.Max(10, options.CommandTimeoutSeconds)), ct);
            return result.ExitCode == 0 ? null : $"kilépési kód {result.ExitCode}: {result.Output}";
        }
        catch (Exception e) when (e is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return e.Message;
        }
    }
}

/// <summary>
/// The watchdog part: when a trigger check has been critical for <see cref="RestartOptions.AfterMinutes"/> without a break,
/// restarts the chain service, at most once per <see cref="RestartOptions.MinHoursBetween"/> hours.
/// <para>The outage only counts from the moment the agent could fairly judge the node: since it runs, since the chain unit
/// is active, since no update runs and since the last grace period ended. So a node that was just started (by hand, by the
/// auto-update or by the machine) always gets the full time to come up. The restart time is saved to disk before the
/// command runs; if it cannot be saved, there is no restart.</para>
/// </summary>
internal sealed class RestartGuard(Config config, EventFactory events, IRestarter restarter, IServiceStatus services,
    AgentState state, Func<AgentState, bool> persist, IBootClock clock)
{
    private const string Key = "restart";
    private DateTimeOffset? _judgeableSince;
    private DateTimeOffset? _lastPoll;
    private string? _waitReason;
    private bool _saveFailedNotified;

    public async Task<List<CloudEvent>> EvaluateAsync(IReadOnlyList<CheckResult> results, Snapshot snapshot,
        DateTimeOffset now, CancellationToken ct)
    {
        var output = new List<CloudEvent>();
        var o = config.Restart;
        if (!o.Enabled) return output;
        ClampFutureTimes(now);

        var gap = _lastPoll is { } last && (now - last > config.Checks.GapLimit || last - now > CheckOptions.BackwardTolerance);
        _lastPoll = now;

        if (state.GraceUntil is { } grace && now < grace)
        {
            _judgeableSince = null;
            return output;
        }

        var failing = results.Where(r => r.Level == Level.Critical && o.Triggers.Contains(r.Name)).ToList();
        if (failing.Count == 0)
        {
            Wait(null);
            if (state.RestartAlertOpen)
            {
                output.Add(events.Resolved(Key, Level.Critical, "A node újra működik",
                    "A figyelt ellenőrzések rendben vannak.", state.RestartAlertSince ?? now, now));
                state.RestartAlertOpen = false;
                state.RestartAlertSince = null;
            }
            state.RestartRefusedNotified = false;
            _saveFailedNotified = false;
        }

        // When may the agent judge the node? Not while the unit is down or starting, not during an update, not across a
        // gap in the polls; and only from the first poll after these.
        var unitState = snapshot.Services.GetValueOrDefault(o.Unit, "unknown");
        var busy = o.SkipWhileUnitsActive.FirstOrDefault(u => snapshot.Services.GetValueOrDefault(u) is "active" or "activating");
        if (unitState != "active" || busy is not null || gap)
        {
            _judgeableSince = null;
            if (failing.Count > 0)
                Wait(busy is not null ? $"a(z) {busy} éppen fut"
                    : unitState != "active" ? $"a(z) {o.Unit} állapota {unitState} (nem active)"
                    : "szünet volt az ellenőrzésekben");
            return output;
        }
        _judgeableSince ??= now;
        if (failing.Count == 0) return output;

        var failingSince = failing.Min(r => r.Since ?? now);
        var since = failingSince > _judgeableSince.Value ? failingSince : _judgeableSince.Value;
        var down = now - since;
        // A selected node that does not work loses its slots in the session: no waiting for those triggers.
        var immediate = failing.Any(r => o.ImmediateTriggers.Contains(r.Name));
        if (!immediate && down < TimeSpan.FromMinutes(o.AfterMinutes)) return output;

        var reasons = string.Join("; ", failing.Select(r => r.Title));
        var outage = now - failingSince;

        var recent = state.RecentRestarts.Count(t => now - t < TimeSpan.FromHours(24));
        var tooSoon = state.LastRestartAt is { } lastRestart &&
                      SinceLastRestart(now, lastRestart) < TimeSpan.FromHours(o.MinHoursBetween);
        var dailyLimit = o.MaxPerDay > 0 && recent >= o.MaxPerDay;
        if (tooSoon || dailyLimit)
        {
            var nextAllowed = NextAllowed(now);
            if (nextAllowed - now <= TimeSpan.FromHours(2) && !dailyLimit)
            {
                // Soon allowed again (hourly limit): the open alert stays, no "manual action needed" message.
                Wait($"legközelebb {config.Local(nextAllowed):HH:mm}-kor indíthatok újra");
                return output;
            }
            if (!state.RestartRefusedNotified)
            {
                var previousAt = state.LastRestartAt ?? now;
                var why = dailyLimit
                    ? $"24 órán belül már {recent} újraindítás volt, és naponta legfeljebb {Text.Times(o.MaxPerDay)} indítok újra " +
                      $"(legközelebb {config.Local(nextAllowed):MM.dd. HH:mm}-kor)."
                    : state.LastRestartAssumed
                        ? $"az állapotfájl {config.Local(previousAt):MM.dd. HH:mm}-kor nem volt olvasható, ezért biztonsági okból " +
                          $"{o.MinHoursBetween:0.#} órán belül nem indítok újra."
                        : $"{config.Local(previousAt):MM.dd. HH:mm}-kor már volt újraindítás, és {o.MinHoursBetween:0.#} órán " +
                          "belül csak egyszer indítok újra.";
                output.Add(events.Firing(Key, Level.Critical, "Nem indítom újra a node-ot",
                    $"{reasons}. {Text.Duration(outage)} nem működik, de {why} Kézi beavatkozás kell.", failingSince, now));
                state.RestartAlertOpen = true;
                state.RestartAlertSince ??= failingSince;
                state.RestartRefusedNotified = true;
            }
            return output;
        }

        // Last look right before acting: the unit may have been restarted between two polls (by hand or by an update).
        var current = await services.StateAsync(o.Unit, ct);
        var nowBusy = false;
        foreach (var unit in o.SkipWhileUnitsActive)
            if (await services.StateAsync(unit, ct) is "active" or "activating") nowBusy = true;
        var activeFor = await services.ActiveForAsync(o.Unit, ct);
        if (current != "active" || nowBusy || activeFor < TimeSpan.FromMinutes(o.AfterMinutes))
        {
            Wait(nowBusy ? "frissítés indult"
                : current != "active" ? $"a(z) {o.Unit} állapota {current}"
                : $"a(z) {o.Unit} csak {Text.Length(activeFor!.Value)} óta fut");
            // Count from the unit's start, so it gets the full time to come up.
            _judgeableSince = current == "active" && !nowBusy && activeFor is { } a ? now - a : null;
            return output;
        }

        // Saved (fsync) before the command runs: a hanging or failing restart, a crash or a stop of the agent right now
        // still counts, so it can never cause a second restart within the limit.
        var previous = (state.LastRestartAt, state.LastRestartAssumed, state.LastRestartBootId, state.LastRestartUptime,
            state.GraceUntil);
        var previousRecent = state.RecentRestarts.ToList();
        state.RecentRestarts.RemoveAll(t => now - t >= TimeSpan.FromHours(24));
        state.RecentRestarts.Add(now);
        var boot = clock.Now();
        state.LastRestartAt = now;
        state.LastRestartAssumed = false;
        state.LastRestartBootId = boot?.BootId;
        state.LastRestartUptime = boot?.Uptime;
        state.GraceUntil = now + TimeSpan.FromMinutes(o.GraceMinutes);
        if (!persist(state))
        {
            (state.LastRestartAt, state.LastRestartAssumed, state.LastRestartBootId, state.LastRestartUptime,
                state.GraceUntil) = previous;
            state.RecentRestarts = previousRecent;
            if (!_saveFailedNotified)
            {
                output.Add(events.Firing(Key, Level.Critical, "Nem indítom újra a node-ot",
                    $"{reasons}. {Text.Duration(outage)} nem működik, de az állapotfájl nem menthető, így az újraindítás " +
                    "napi korlátja nem lenne biztos. Kézi beavatkozás kell (és nézd meg a lemezt).", failingSince, now));
                state.RestartAlertOpen = true;
                state.RestartAlertSince ??= failingSince;
                _saveFailedNotified = true;
            }
            return output;
        }

        state.RestartAlertOpen = true;
        state.RestartAlertSince ??= failingSince;
        state.RestartRefusedNotified = false;
        _judgeableSince = null;
        Log.Warn($"Újraindítás: {string.Join(' ', o.Command)} ({reasons}, {Text.Length(outage)})");
        var left = o.MaxPerDay > 0 ? $" 24 órán belül még {Math.Max(0, o.MaxPerDay - state.RecentRestarts.Count)} lehet." : "";
        output.Add(events.Firing(Key, Level.Critical, "Újraindítom a Mosaic node-ot",
            $"{reasons}. {Text.Duration(outage)} nem működik. A következő automatikus újraindítás legkorábban " +
            $"{config.Local(now + TimeSpan.FromHours(o.MinHoursBetween)):MM.dd. HH:mm}-kor lehet.{left}", failingSince, now));

        var error = await restarter.RestartAsync(ct);
        if (error is null)
        {
            Log.Info($"A(z) {o.Unit} újraindítása elindult; {o.GraceMinutes:0.#} percig várok, hogy utolérje a láncot.");
        }
        else
        {
            Log.Error($"Az újraindítás nem sikerült: {error}");
            output.Add(events.Firing(Key, Level.Critical, "Az újraindítás nem sikerült", error, failingSince, now));
        }
        return output;
    }

    /// <summary>
    /// Time since the last restart. Within the same boot both the wall clock and the time since boot must agree that enough
    /// time passed, so neither a forward wall-clock step (a dead RTC fixed by NTP) nor a backward one can shorten the limit.
    /// After a reboot only the wall clock is left.
    /// </summary>
    private TimeSpan SinceLastRestart(DateTimeOffset now, DateTimeOffset lastRestart)
    {
        var wall = now - lastRestart;
        if (state.LastRestartBootId is { } bootId && state.LastRestartUptime is { } uptime && clock.Now() is { } c &&
            c.BootId == bootId)
        {
            var monotonic = c.Uptime - uptime;
            return monotonic < wall ? monotonic : wall;
        }
        return wall;
    }

    /// <summary>The earliest time both limits allow a restart again.</summary>
    private DateTimeOffset NextAllowed(DateTimeOffset now)
    {
        var o = config.Restart;
        var next = now;
        if (state.LastRestartAt is { } last && last + TimeSpan.FromHours(o.MinHoursBetween) > next)
            next = last + TimeSpan.FromHours(o.MinHoursBetween);
        var recent = state.RecentRestarts.Where(t => now - t < TimeSpan.FromHours(24)).Order().ToList();
        if (o.MaxPerDay > 0 && recent.Count >= o.MaxPerDay)
        {
            var freed = recent[recent.Count - o.MaxPerDay] + TimeSpan.FromHours(24);
            if (freed > next) next = freed;
        }
        return next;
    }

    /// <summary>After the clock was set back, stored times may lie in the future; they must not block for longer than meant.</summary>
    private void ClampFutureTimes(DateTimeOffset now)
    {
        if (state.LastRestartAt > now) state.LastRestartAt = now;
        for (var i = 0; i < state.RecentRestarts.Count; i++)
            if (state.RecentRestarts[i] > now) state.RecentRestarts[i] = now;
        var maxGrace = now + TimeSpan.FromMinutes(config.Restart.GraceMinutes);
        if (state.GraceUntil > maxGrace) state.GraceUntil = maxGrace;
    }

    private void Wait(string? reason)
    {
        if (reason is not null && reason != _waitReason) Log.Warn($"Nem indítom újra: {reason}.");
        _waitReason = reason;
    }
}
