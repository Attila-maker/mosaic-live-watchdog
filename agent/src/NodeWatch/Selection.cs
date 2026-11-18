namespace NodeWatch;

/// <summary>What happened to the validator in one selected session.</summary>
internal sealed class SessionRecord
{
    public uint Index { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>Blocks authored in the session (ImOnline.AuthoredBlocks, last value seen).</summary>
    public uint Authored { get; set; }

    public bool Heartbeat { get; set; }

    /// <summary>Own slots judged: with a block / without one.</summary>
    public int Produced { get; set; }
    public int Missed { get; set; }

    /// <summary>Own slots up to this slot are judged.</summary>
    public ulong JudgedUpToSlot { get; set; }

    /// <summary>Authored blocks already matched to judged slots.</summary>
    public uint Matched { get; set; }

    public int MissedInRow { get; set; }
    public DateTimeOffset? MissedInRowSince { get; set; }

    /// <summary>The session ended with no block and no heartbeat: the chain marks such a validator offline.</summary>
    public bool Offline => EndedAt is not null && Authored == 0 && !Heartbeat;
}

internal sealed class SelectionState
{
    /// <summary>The selected session in progress (survives an agent restart).</summary>
    public SessionRecord? Current { get; set; }

    /// <summary>Finished selected sessions of the last two days.</summary>
    public List<SessionRecord> History { get; set; } = [];

    /// <summary>The selection notice already went out for this session.</summary>
    public uint? NotifiedSession { get; set; }

    public DateTimeOffset? LastSelectedAt { get; set; }

    /// <summary>Since when the validator could be selected (not chilled), for the "not selected for long" alert.</summary>
    public DateTimeOffset? SelectableSince { get; set; }

    public DateTimeOffset? LastSummaryAt { get; set; }
}

/// <summary>
/// Watches the validator's part in the session rotation: selection notices, its own Aura slots while selected, the
/// offline risk before the end of the session, the result of the session, a long time without selection, and the daily
/// summary. Gives check results (alerts, restart triggers) and notices (info messages).
/// </summary>
internal sealed class SelectionWatcher(Config config, EventFactory events, AgentState state)
{
    private SelectionOptions O => config.Selection;
    private SelectionState S => state.Selection;

    public (List<CheckResult> Results, List<CloudEvent> Notices) Process(Snapshot snapshot, DateTimeOffset now)
    {
        var results = new List<CheckResult>();
        var notices = new List<CloudEvent>();
        if (!O.Enabled || string.IsNullOrWhiteSpace(config.Node.ValidatorAddress)) return (results, notices);

        TrackSelectable(snapshot, now);
        if (snapshot.Session is { } info)
        {
            results.Add(new(CheckNames.SessionData, Level.Ok, "A session-adatok újra olvashatók", ""));
            FinishSession(info, now);
            WatchSlots(info, snapshot, now, results);
            Notify(info, now, notices);
        }
        else if (snapshot.SessionError is not null)
        {
            results.Add(new(CheckNames.SessionData, Level.Warning, "A session-adatok nem kérdezhetők le",
                $"{snapshot.SessionError}. A kiválasztás és a blokkgyártás figyelése addig szünetel. Ha a Mosaic runtime " +
                "frissült, lehet, hogy a NodeWatch-ot is frissíteni kell."));
        }

        results.AddRange(SessionResult());
        results.AddRange(NotSelected(snapshot, now));
        if (Summary(snapshot, now) is { } summary) notices.Add(summary);
        S.History.RemoveAll(r => r.EndedAt is { } end && now - end > TimeSpan.FromHours(48));
        return (results, notices);
    }

    private void TrackSelectable(Snapshot s, DateTimeOffset now)
    {
        if (s.Validator is { } v)
        {
            if (v.Kind == ValidatorStateKind.Chilled) S.SelectableSince = null;
            else S.SelectableSince ??= now;
        }
        else if (s.ValidatorMissing)
        {
            S.SelectableSince = null;
        }
    }

    private void FinishSession(SessionInfo info, DateTimeOffset now)
    {
        if (S.Current is not { } current || (info.MyIndex is not null && current.Index == info.Index)) return;
        current.EndedAt = now;
        S.History.Add(current);
        S.Current = null;
        Log.Info($"A #{current.Index} session véget ért: {current.Authored} blokk, {current.Missed} kihagyott slot, " +
                 $"életjel: {(current.Heartbeat ? "igen" : "nem")}.");
    }

    private void WatchSlots(SessionInfo info, Snapshot snapshot, DateTimeOffset now, List<CheckResult> results)
    {
        if (info.MyIndex is not { } me)
        {
            results.Add(new(CheckNames.MissedSlots, Level.Ok, "A kiválasztott session véget ért", "A blokkhelyek figyelése szünetel."));
            results.Add(new(CheckNames.OfflineRisk, Level.Ok, "A kiválasztott session véget ért", ""));
            return;
        }

        S.LastSelectedAt = now;
        var current = S.Current ??= new SessionRecord
        {
            Index = info.Index,
            StartedAt = now,
            // Started watching mid-session (agent start): earlier slots and blocks are not judged.
            JudgedUpToSlot = info.CurrentSlot,
            Matched = info.Authored,
            Authored = info.Authored,
        };
        current.Authored = Math.Max(current.Authored, info.Authored);
        current.Heartbeat |= info.Heartbeat;

        // Restarting: the slots of the grace period are not the node's fault, and a miss streak starts again after it.
        var restarting = state.GraceUntil is { } grace && now < grace;
        if (restarting)
        {
            current.MissedInRow = 0;
            current.MissedInRowSince = null;
        }

        // Judged on the chain's own slot, not the wall clock: if the whole chain stands, the stall checks speak instead.
        if (info.AuthorityCount == info.ActiveCount && info.AuthorityCount > 0)
        {
            var n = (ulong)info.AuthorityCount;
            var graceSlots = (ulong)Math.Ceiling(O.MissedSlotGraceSeconds / (double)O.SlotSeconds);
            var upTo = info.CurrentSlot > graceSlots ? info.CurrentSlot - graceSlots : 0;
            var from = current.JudgedUpToSlot + 1;
            for (var slot = from + ((ulong)me + n - from % n) % n; slot <= upTo; slot += n)
            {
                var start = SlotStart(slot);
                if (restarting || state.GraceUntil is { } g && start <= g) continue;
                if (current.Authored > current.Matched)
                {
                    current.Matched++;
                    current.Produced++;
                    current.MissedInRow = 0;
                    current.MissedInRowSince = null;
                }
                else
                {
                    current.Missed++;
                    current.MissedInRow++;
                    current.MissedInRowSince ??= start;
                    Log.Warn($"Kihagyott blokkhely: #{slot} slot ({config.Local(start):HH:mm:ss}), egymás után {current.MissedInRow}.");
                }
            }
            if (upTo > current.JudgedUpToSlot) current.JudgedUpToSlot = upTo;
            // A block that was not matched to a judged slot still shows the node works.
            if (current.Authored > current.Matched && current.MissedInRow > 0)
            {
                current.Matched = current.Authored;
                current.MissedInRow = 0;
                current.MissedInRowSince = null;
            }
        }

        var facts = new Dictionary<string, object?>
        {
            ["session"] = info.Index, ["authored"] = current.Authored, ["missed"] = current.Missed,
            ["heartbeat"] = current.Heartbeat, ["progress"] = Math.Round(info.Progress, 2),
        };
        var summary = $"Ebben a sessionben eddig {current.Authored} blokk, {current.Missed} kihagyott blokkhely, " +
                      $"életjel: {(current.Heartbeat ? "elküldve" : "még nincs")}.";
        results.Add(current.MissedInRow switch
        {
            0 => new(CheckNames.MissedSlots, Level.Ok, "A node újra gyárt blokkot", summary, null, facts),
            var k when k >= O.MissedSlotsBeforeRestart => new(CheckNames.MissedSlots, Level.Critical,
                "A node nem gyártja a blokkjait",
                $"Kiválasztott validátorként {k} saját blokkhelye maradt egymás után üresen. {summary}",
                current.MissedInRowSince, facts),
            var k => new(CheckNames.MissedSlots, Level.Warning, "Kimaradt egy saját blokk",
                $"Kiválasztott validátorként {k} saját blokkhelye maradt üresen (a láncon minden tizedik körüli slot üres " +
                $"marad, ez önmagában még nem hiba). {summary}", current.MissedInRowSince, facts),
        });

        var atRisk = info.Progress >= O.OfflineRiskProgress && current.Authored == 0 && !current.Heartbeat;
        results.Add(atRisk
            ? new(CheckNames.OfflineRisk, Level.Critical, "A node offline-nak fog számítani",
                $"A session {info.Progress:P0}-ánál tart, de a node még egy blokkot sem gyártott, és életjelet sem küldött. " +
                "Ha a session végéig így marad, a lánc offline-nak jelöli (Faulted, büntetés lehet).", now, facts)
            : new(CheckNames.OfflineRisk, Level.Ok, "A node jelentkezett ebben a sessionben", summary, null, facts));
    }

    private void Notify(SessionInfo info, DateTimeOffset now, List<CloudEvent> notices)
    {
        var next = info.Index + 1;
        if (!info.SelectedNext || S.NotifiedSession == next) return;
        S.NotifiedSession = next;
        S.LastSelectedAt = now;
        if (!O.NotifySelected) return;

        // Block times are estimates: about every tenth slot stays empty.
        var blockTime = TimeSpan.FromSeconds(O.SlotSeconds * 1.1);
        var start = now + blockTime * Math.Max(0, (long)info.SessionEnd - info.Block);
        var length = info.NextSessionEnd > info.SessionEnd ? info.NextSessionEnd - info.SessionEnd : 0;
        var end = start + blockTime * length;
        var slots = info.NextCount > 0 && length > 0 ? Math.Max(1, (int)Math.Round((double)length / info.NextCount)) : 0;
        var text = $"A #{next} sessionre kiválasztottak ({info.NextCount} validátor közül). " +
                   (length > 0 ? $"Várhatóan {config.Local(start):HH:mm} és {config.Local(end):HH:mm} között, kb. {slots} blokkhely jut rád." : "");
        notices.Add(events.Info("selection", next.ToString(), "Kiválasztottak a következő sessionre", text.Trim(), now,
            new Dictionary<string, object?> { ["session"] = next, ["validators"] = info.NextCount }));
    }

    private IEnumerable<CheckResult> SessionResult()
    {
        if (S.History.LastOrDefault() is not { } last) yield break;
        yield return last.Offline
            ? new(CheckNames.SessionResult, Level.Warning, "Az előző sessionben nem jelentkezett a node",
                $"A #{last.Index} sessionben ki volt választva, de nem gyártott blokkot és életjelet sem küldött. A lánc " +
                "ilyenkor offline-nak jelöli (Faulted, büntetés lehet).", last.EndedAt)
            : new(CheckNames.SessionResult, Level.Ok, "A legutóbbi kiválasztott sessionben rendben dolgozott",
                $"A #{last.Index} sessionben {last.Authored} blokk, életjel: {(last.Heartbeat ? "igen" : "nem")}.");
    }

    private IEnumerable<CheckResult> NotSelected(Snapshot snapshot, DateTimeOffset now)
    {
        if (O.NotSelectedAlertHours <= 0) yield break;
        if (S.SelectableSince is not { } selectable || snapshot.Session is null)
        {
            yield return new(CheckNames.NotSelected, Level.Ok, "A validátor nem választható (chill)", "");
            yield break;
        }
        var since = S.LastSelectedAt is { } last && last > selectable ? last : selectable;
        var limit = TimeSpan.FromHours(O.NotSelectedAlertHours);
        yield return now - since >= limit
            ? new(CheckNames.NotSelected, Level.Warning, "Régóta nem választották ki a validátort",
                $"{Text.Duration(now - since)} nem került be a kiválasztott validátorok közé, pedig választható " +
                $"(állapota: {snapshot.Validator?.Kind.ToString() ?? "ismeretlen"}). Nézd meg a Mosaic explorerben.", since)
            : new(CheckNames.NotSelected, Level.Ok, "Újra kiválasztották a validátort", "");
    }

    /// <summary>Once a day at <see cref="SelectionOptions.DailySummaryHour"/>, about the last 24 hours.</summary>
    private CloudEvent? Summary(Snapshot snapshot, DateTimeOffset now)
    {
        if (O.DailySummaryHour < 0) return null;
        var local = config.Local(now);
        var due = new DateTimeOffset(local.Year, local.Month, local.Day, O.DailySummaryHour, 0, 0, local.Offset);
        if (local < due) due = due.AddDays(-1);
        if (S.LastSummaryAt is null)
        {
            // The first run only sets the starting point: no summary of a day the agent did not watch.
            S.LastSummaryAt = now;
            return null;
        }
        if (S.LastSummaryAt >= due) return null;
        S.LastSummaryAt = now;

        var from = now - TimeSpan.FromHours(24);
        var sessions = S.History.Where(r => r.EndedAt >= from).ToList();
        if (S.Current is { } running) sessions.Add(running);
        var restarts = state.RecentRestarts.Count(t => t >= from);
        var offline = sessions.Count(r => r.Offline);
        var text = sessions.Count == 0
            ? "Az elmúlt 24 órában nem választották ki" +
              (S.LastSelectedAt is { } last ? $" (utoljára: {config.Local(last):MM.dd. HH:mm})." : ".")
            : $"Az elmúlt 24 órában {sessions.Count}× választották ki: {sessions.Sum(r => r.Authored)} blokk, " +
              $"{sessions.Sum(r => r.Missed)} kihagyott blokkhely, " +
              (offline == 0 ? "minden sessionben jelentkezett." : $"{offline} sessionben nem jelentkezett (offline).");
        text += $" Validátor: {snapshot.Validator?.Kind.ToString() ?? "ismeretlen"}." +
                (restarts == 0 ? " Újraindítás nem volt." : $" Újraindítás: {restarts}×.");
        return events.Info("summary", config.Local(now).ToString("yyyy-MM-dd"), "Napi összesítés", text, now,
            new Dictionary<string, object?>
            {
                ["selected"] = sessions.Count, ["authored"] = sessions.Sum(r => r.Authored),
                ["missed"] = sessions.Sum(r => r.Missed), ["offline"] = offline, ["restarts"] = restarts,
            });
    }

    private DateTimeOffset SlotStart(ulong slot) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)(slot * (ulong)O.SlotSeconds * 1000));
}
