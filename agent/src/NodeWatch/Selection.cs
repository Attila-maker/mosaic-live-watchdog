namespace NodeWatch;

/// <summary>What happened to the validator in one session.</summary>
internal sealed class SessionRecord
{
    public uint Index { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>Selected into the session's validator subset (it had Aura slots).</summary>
    public bool Selected { get; set; }

    /// <summary>The chain expected a block or a heartbeat from it (selected, or not chilled).</summary>
    public bool Expected { get; set; }

    /// <summary>Blocks authored in the session (ImOnline.AuthoredBlocks, last value seen).</summary>
    public uint Authored { get; set; }

    public bool Heartbeat { get; set; }

    /// <summary>Own slots judged: with a block / without one.</summary>
    public int Produced { get; set; }
    public int Missed { get; set; }

    /// <summary>Own slots up to this slot are judged (or skipped).</summary>
    public ulong JudgedUpToSlot { get; set; }

    /// <summary>Authored blocks already matched to judged or skipped slots.</summary>
    public uint Matched { get; set; }

    public int MissedInRow { get; set; }
    public DateTimeOffset? MissedInRowSince { get; set; }

    /// <summary>Since when the slots cannot be judged (restart, node down or syncing, data not readable).</summary>
    public DateTimeOffset? BlockedSince { get; set; }

    public string? BlockedReason { get; set; }

    /// <summary>The blocked stretch was a restart or long: own slots up to this slot are skipped, not judged.</summary>
    public ulong SkipUpToSlot { get; set; }

    public double LastProgress { get; set; }

    /// <summary>The end of the session was not seen (no read near its end): its result is not known.</summary>
    public bool Unknown { get; set; }

    /// <summary>The session ended with no block and no heartbeat: the chain reports such a validator offline.</summary>
    public bool Offline => EndedAt is not null && Expected && !Unknown && Authored == 0 && !Heartbeat;
}

internal sealed class SelectionState
{
    /// <summary>The session in progress (survives an agent restart).</summary>
    public SessionRecord? Current { get; set; }

    /// <summary>Finished sessions of the last two days.</summary>
    public List<SessionRecord> History { get; set; } = [];

    /// <summary>The selection notice already went out for this session.</summary>
    public uint? NotifiedSession { get; set; }

    public DateTimeOffset? LastSelectedAt { get; set; }

    /// <summary>Since when the validator could be selected (not chilled), for the "not selected for long" alert.</summary>
    public DateTimeOffset? SelectableSince { get; set; }

    public DateTimeOffset? LastSummaryAt { get; set; }
}

/// <summary>
/// Watches the validator's part in the session rotation. Every session the chain expects a block or an "I am online"
/// heartbeat from every validator that is not chilled (ImOnline over NftStaking's slashable validators), and the selected
/// subset also takes turns in the Aura slots. Gives check results (alerts, restart triggers) and notices (info messages).
/// Slots are only judged while the node is in sync and not restarting, so a node coming back is never blamed for the
/// slots it had no chance at.
/// </summary>
internal sealed class SelectionWatcher(Config config, EventFactory events, AgentState state)
{
    private SelectionOptions O => config.Selection;
    private SelectionState S => state.Selection;

    /// <summary>The best block's slot is older than this: the node (or the chain) is not producing, slots are not judged.</summary>
    private static readonly TimeSpan StaleSlot = TimeSpan.FromMinutes(2);

    /// <summary>After the chain unit starts, the node needs this long to catch up before its slots count.</summary>
    private static readonly TimeSpan CatchUp = TimeSpan.FromMinutes(3);

    /// <summary>When the chain unit last started, as seen from its active time.</summary>
    private DateTimeOffset? _unitStartedAt;

    /// <summary>(slot, block) samples of the last minutes, for the chain-wide empty slot rate.</summary>
    private readonly Queue<(DateTimeOffset At, ulong Slot, long Block)> _samples = new();

    public (List<CheckResult> Results, List<CloudEvent> Notices) Process(Snapshot snapshot, DateTimeOffset now)
    {
        var results = new List<CheckResult>();
        var notices = new List<CloudEvent>();
        if (!O.Enabled || string.IsNullOrWhiteSpace(config.Node.ValidatorAddress)) return (results, notices);

        TrackSelectable(snapshot, now);
        if (snapshot.Session is { } info)
        {
            var current = Advance(info, snapshot, now);
            WatchSession(info, current, snapshot, now, results);
            Notify(info, now, notices);
        }
        else
        {
            // Not readable (RPC down, node restarting): judged again when it is back; a long gap is skipped then.
            if (S.Current is { } current)
            {
                current.BlockedSince ??= now;
                current.BlockedReason ??= "a session-adatok nem olvashatók";
            }
            if (snapshot.SessionError is not null && snapshot.ParaError is null)
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

    private bool Selectable(Snapshot s) => s.Validator is { } v && v.Kind != ValidatorStateKind.Chilled;

    private void TrackSelectable(Snapshot s, DateTimeOffset now)
    {
        if (s.Validator is not null)
        {
            if (Selectable(s)) S.SelectableSince ??= now;
            else S.SelectableSince = null;
        }
        else if (s.ValidatorMissing)
        {
            S.SelectableSince = null;
        }
    }

    /// <summary>Closes the previous session's record when a new session starts, and returns the current one.</summary>
    private SessionRecord Advance(SessionInfo info, Snapshot snapshot, DateTimeOffset now)
    {
        if (S.Current is { } previous && previous.Index != info.Index)
        {
            previous.EndedAt = now;
            // Values are read every poll; without a read near the end, a block or heartbeat at the end may be missing.
            previous.Unknown = previous.LastProgress < 0.95;
            S.History.Add(previous);
            S.Current = null;
            Log.Info($"A #{previous.Index} session véget ért: kiválasztva: {(previous.Selected ? "igen" : "nem")}, " +
                     $"{previous.Authored} blokk, {previous.Missed} kihagyott slot, életjel: {(previous.Heartbeat ? "igen" : "nem")}" +
                     (previous.Unknown ? " (a vége nem látszott)." : "."));
        }

        var current = S.Current ??= new SessionRecord
        {
            Index = info.Index,
            StartedAt = now,
            // Started watching mid-session (agent start): earlier slots and blocks are not judged.
            JudgedUpToSlot = info.CurrentSlot,
            Matched = info.Authored,
        };
        current.Selected |= info.MyIndex is not null;
        // From the chain, not from the validator state: unchilled or bound during the session, it is expected only from
        // the next session on.
        current.Expected = info.Expected;
        // The value of the best block: a reorg may take a block back.
        current.Authored = info.Authored;
        if (current.Matched > current.Authored) current.Matched = current.Authored;
        current.Heartbeat |= info.Heartbeat;
        current.LastProgress = info.Progress;
        if (current.Selected) S.LastSelectedAt = now;
        return current;
    }

    private void WatchSession(SessionInfo info, SessionRecord current, Snapshot snapshot, DateTimeOffset now,
        List<CheckResult> results)
    {
        // Can the node's slots be judged right now? Not while restarting, out of sync, or when its chain view is old.
        // "unknown" (systemctl did not answer) is not a reason: the session data was read, so the node runs.
        var restarting = state.GraceUntil is { } grace && now < grace;
        var unitState = snapshot.Services.GetValueOrDefault(config.Restart.Unit, "unknown");
        var unitDown = unitState is not ("active" or "unknown");
        var syncing = snapshot.ParaHealth?.IsSyncing == true;
        var stale = now - SlotStart(info.CurrentSlot) > StaleSlot;
        var blockedReason = restarting ? "újraindítás után vár"
            : unitDown ? $"a(z) {config.Restart.Unit} állapota {unitState}"
            : syncing ? "a parachain szinkronizál"
            : stale ? $"a node legutóbbi blokkja {Text.Length(now - SlotStart(info.CurrentSlot))} régi"
            : null;
        var emptyRate = ChainEmptyRate(info, now);
        var n = (ulong)Math.Max(1, info.AuthorityCount);

        // The unit (re)started, by anyone, even between two polls: the slots before it and while it caught up are not
        // the running node's, and a miss streak from before the start does not continue.
        if (snapshot.ChainActiveFor is { } activeFor)
        {
            var started = now - activeFor;
            if (_unitStartedAt is not { } known || started - known > TimeSpan.FromSeconds(60))
            {
                _unitStartedAt = started;
                var skipUntil = started + CatchUp;
                var skipSlot = (ulong)(skipUntil.ToUnixTimeMilliseconds() / (O.SlotSeconds * 1000L));
                if (skipSlot > current.SkipUpToSlot) current.SkipUpToSlot = skipSlot;
                if (current.MissedInRowSince is { } streak && streak < skipUntil)
                {
                    current.MissedInRow = 0;
                    current.MissedInRowSince = null;
                }
            }
        }

        if (blockedReason is not null)
        {
            // Not judged now; whether the slots of this stretch count is decided when it ends.
            current.BlockedSince ??= now;
            current.BlockedReason = blockedReason;
            if (restarting)
            {
                current.MissedInRow = 0;
                current.MissedInRowSince = null;
            }
        }
        else if (current.BlockedSince is { } blockedSince)
        {
            // A short hiccup (one failed read, a moment of syncing) changes nothing: the slots are judged as usual. After
            // a restart or a long stretch the node had no fair chance at those slots: they are skipped.
            var stretch = now - blockedSince;
            var longStretch = stretch > TimeSpan.FromMinutes(3) || stretch > TimeSpan.FromSeconds((double)n * O.SlotSeconds) ||
                              state.GraceUntil is { } g && g > blockedSince;
            if (longStretch)
            {
                current.SkipUpToSlot = Math.Max(current.SkipUpToSlot, info.CurrentSlot);
                current.MissedInRow = 0;
                current.MissedInRowSince = null;
            }
            current.BlockedSince = null;
            current.BlockedReason = null;
        }

        if (blockedReason is null && info.MyIndex is { } me && info.AuthorityCount == info.ActiveCount && info.AuthorityCount > 0)
        {
            // Judged on the chain's own slot, not the wall clock: if the whole chain stands, the stall checks speak.
            var graceSlots = (ulong)Math.Ceiling(O.MissedSlotGraceSeconds / (double)O.SlotSeconds);
            var upTo = info.CurrentSlot > graceSlots ? info.CurrentSlot - graceSlots : 0;
            var from = current.JudgedUpToSlot + 1;
            for (var slot = from + ((ulong)me + n - from % n) % n; slot <= upTo; slot += n)
            {
                if (slot <= current.SkipUpToSlot)
                {
                    // Skipped: its block, if there is one, is used up so it cannot hide a later miss.
                    if (current.Authored > current.Matched) current.Matched++;
                    continue;
                }
                // Blocks arrive in slot order: an unmatched block belongs to the oldest unjudged own slot.
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
                    current.MissedInRowSince ??= SlotStart(slot);
                    Log.Warn($"Kihagyott blokkhely: #{slot} slot ({config.Local(SlotStart(slot)):HH:mm:ss}), " +
                             $"egymás után {current.MissedInRow}.");
                }
            }
            if (upTo > current.JudgedUpToSlot) current.JudgedUpToSlot = upTo;
        }

        var facts = new Dictionary<string, object?>
        {
            ["session"] = info.Index, ["selected"] = current.Selected, ["authored"] = current.Authored,
            ["missed"] = current.Missed, ["heartbeat"] = current.Heartbeat, ["progress"] = Math.Round(info.Progress, 2),
            ["chainEmptySlots"] = emptyRate is { } r ? Math.Round(r, 2) : null,
        };
        var summary = $"Ebben a sessionben eddig {current.Authored} blokk, {current.Missed} kihagyott blokkhely, " +
                      $"életjel: {(current.Heartbeat ? "elküldve" : "még nincs")}.";

        if (!current.Selected)
        {
            results.Add(new(CheckNames.MissedSlots, Level.Ok, "A kiválasztott session véget ért", "A blokkhelyek figyelése szünetel."));
        }
        else
        {
            // When a large part of the whole chain's slots stays empty, the cause is not this node: no escalation.
            var chainTrouble = emptyRate > O.ChainEmptySlotsLimit;
            results.Add(current.MissedInRow switch
            {
                0 => new(CheckNames.MissedSlots, Level.Ok, "A node újra gyárt blokkot", summary, null, facts),
                var k when k >= O.MissedSlotsBeforeRestart && !chainTrouble => new(CheckNames.MissedSlots, Level.Critical,
                    "A node nem gyártja a blokkjait",
                    $"Kiválasztott validátorként {k} saját blokkhelye maradt egymás után üresen. {summary}",
                    current.MissedInRowSince, facts),
                var k => new(CheckNames.MissedSlots, Level.Warning, "Kimaradt egy saját blokk",
                    $"Kiválasztott validátorként {k} saját blokkhelye maradt üresen" +
                    (chainTrouble
                        ? $" (a láncon most a slotok {emptyRate:P0}-a üres, ez nem a node hibája). "
                        : " (a láncon kb. minden tizedik slot üres marad, ez önmagában még nem hiba). ") + summary,
                    current.MissedInRowSince, facts),
            });
        }

        results.Add(current.BlockedSince is { } since && now - since >= TimeSpan.FromMinutes(15)
            ? new(CheckNames.SessionData, Level.Warning, "A blokkgyártás figyelése szünetel",
                $"{Text.Duration(now - since)} nem ítélhetők meg a node blokkhelyei: {current.BlockedReason}. Ha ez tartós, " +
                "nézd meg a Restart.Unit beállítást és a node naplóját.", since)
            : new(CheckNames.SessionData, Level.Ok, "A session-adatok újra olvashatók", ""));

        // Every validator in ImOnline's set must show it is online in every session, selected or not.
        var atRisk = current.Expected && blockedReason is null && info.Progress >= O.OfflineRiskProgress &&
                     current.Authored == 0 && !current.Heartbeat && !info.KeysMissing;
        results.Add(atRisk
            ? new(CheckNames.OfflineRisk, Level.Critical, "A node offline-nak fog számítani",
                $"A #{info.Index} session {info.Progress:P0}-ánál tart, de a node még egy blokkot sem gyártott, és életjelet " +
                "sem küldött. Ha a session végéig így marad, a lánc offline-nak jelöli (Faulted, büntetés lehet).", now, facts)
            : new(CheckNames.OfflineRisk, Level.Ok, "A node jelentkezett ebben a sessionben", summary, null, facts));

        results.Add(info.KeysMissing && current.Expected
            ? new(CheckNames.Keys, Level.Critical, "Hiányzik a validátor im_online kulcsa",
                "Az ImOnline.MissingKeys szerint a validátornak nincs im_online session kulcsa, így életjelet sem tud küldeni, " +
                "és minden session végén offline-nak jelöli a lánc. Újraindítás ezen nem segít: a session kulcsokat be kell " +
                "állítani (rotateKeys + setKeys).", now)
            : new(CheckNames.Keys, Level.Ok, "A validátor im_online kulcsa rendben", ""));
    }

    /// <summary>Share of the chain's slots without a block over the last minutes; null until there are enough samples.</summary>
    private double? ChainEmptyRate(SessionInfo info, DateTimeOffset now)
    {
        _samples.Enqueue((now, info.CurrentSlot, info.Block));
        while (_samples.Count > 0 && now - _samples.Peek().At > TimeSpan.FromMinutes(5)) _samples.Dequeue();
        var first = _samples.Peek();
        var slots = (long)info.CurrentSlot - (long)first.Slot;
        if (slots < 20) return null;
        return Math.Clamp(1.0 - (double)(info.Block - first.Block) / slots, 0, 1);
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
        if (S.History.LastOrDefault(r => r.Expected && !r.Unknown) is not { } last) yield break;
        yield return last.Offline
            ? new(CheckNames.SessionResult, Level.Warning, "Az előző sessionben nem jelentkezett a node",
                $"A #{last.Index} sessionben {(last.Selected ? "ki volt választva, de " : "")}nem gyártott blokkot, és életjelet " +
                "sem küldött. A lánc ilyenkor offline-nak jelöli (Faulted, büntetés lehet).", last.EndedAt)
            : new(CheckNames.SessionResult, Level.Ok, "A legutóbbi sessionben rendben jelentkezett a node",
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
        var selected = sessions.Where(r => r.Selected).ToList();
        var offline = sessions.Count(r => r.Offline);
        var restarts = state.RecentRestarts.Count(t => t >= from);
        var text = selected.Count == 0
            ? "Az elmúlt 24 órában nem választották ki" +
              (S.LastSelectedAt is { } last ? $" (utoljára: {config.Local(last):MM.dd. HH:mm})." : ".")
            : $"Az elmúlt 24 órában {selected.Count}× választották ki: {selected.Sum(r => r.Authored)} blokk, " +
              $"{selected.Sum(r => r.Missed)} kihagyott blokkhely.";
        text += offline == 0
            ? " Minden sessionben jelentkezett (blokk vagy életjel)."
            : $" {offline} sessionben nem jelentkezett (offline).";
        text += $" Validátor: {snapshot.Validator?.Kind.ToString() ?? "ismeretlen"}." +
                (restarts == 0 ? " Újraindítás nem volt." : $" Újraindítás: {restarts}×.");
        return events.Info("summary", config.Local(now).ToString("yyyy-MM-dd"), "Napi összesítés", text, now,
            new Dictionary<string, object?>
            {
                ["selected"] = selected.Count, ["authored"] = selected.Sum(r => r.Authored),
                ["missed"] = selected.Sum(r => r.Missed), ["offline"] = offline, ["restarts"] = restarts,
            });
    }

    private DateTimeOffset SlotStart(ulong slot) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)(slot * (ulong)O.SlotSeconds * 1000));
}
