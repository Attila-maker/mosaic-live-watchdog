namespace NodeWatch;

/// <summary>Per-check alert state (persisted, so a restart of the agent does not forget an open alert).</summary>
internal sealed class AlertTrack
{
    public Level Level { get; set; } = Level.Ok;
    public bool Fired { get; set; }
    public Level FiredLevel { get; set; }
    public DateTimeOffset FiredAt { get; set; }
    public DateTimeOffset LastSent { get; set; }
    public Level PendingLevel { get; set; }
    public int PendingCount { get; set; }
}

/// <summary>
/// Turns check results into firing / resolved events. A change counts only after <see cref="CheckOptions.ConfirmPolls"/>
/// polls in a row; a lasting critical alert is repeated every <see cref="CheckOptions.ReminderMinutes"/>.
/// </summary>
internal sealed class AlertEngine(Config config, EventFactory events, Dictionary<string, AlertTrack> tracks)
{
    public Dictionary<string, AlertTrack> Tracks { get; } = tracks;

    /// <summary>Until then the node checks keep their state (after a restart the node needs time to catch up).</summary>
    public DateTimeOffset FreezeNodeChecksUntil { get; set; }

    public List<CloudEvent> Process(IEnumerable<CheckResult> results, DateTimeOffset now)
    {
        var output = new List<CloudEvent>();
        var confirm = Math.Max(1, config.Checks.ConfirmPolls);
        foreach (var r in results)
        {
            if (!Tracks.TryGetValue(r.Name, out var t)) Tracks[r.Name] = t = new AlertTrack();

            if (now < FreezeNodeChecksUntil && CheckNames.IsNodeCheck(r.Name))
            {
                t.PendingCount = 0;
                continue;
            }

            if (r.Level == t.Level)
            {
                t.PendingCount = 0;
                if (t.Fired && t.Level == Level.Critical && config.Checks.ReminderMinutes > 0 &&
                    now - t.LastSent >= TimeSpan.FromMinutes(config.Checks.ReminderMinutes))
                {
                    output.Add(events.Firing(r.Name, r.Level, "Emlékeztető: " + r.Title, r.Text, t.FiredAt, now, r.Facts));
                    t.LastSent = now;
                }
                continue;
            }

            if (t.PendingCount > 0 && t.PendingLevel == r.Level) t.PendingCount++;
            else
            {
                t.PendingLevel = r.Level;
                t.PendingCount = 1;
            }
            if (t.PendingCount < confirm) continue;

            var previous = t.Level;
            t.Level = r.Level;
            t.PendingCount = 0;
            if (r.Level != Level.Ok)
            {
                var newAlert = previous == Level.Ok || !t.Fired;
                if (newAlert) t.FiredAt = r.Since ?? now;
                output.Add(events.Firing(r.Name, r.Level, r.Title, r.Text, t.FiredAt, now, r.Facts));
                // The highest level since the alert opened: the "resolved" message carries it, so every output that got
                // the alert (a critical-only output too) also gets its end, even when it eased through warning.
                t.FiredLevel = newAlert ? r.Level : (Level)Math.Max((int)t.FiredLevel, (int)r.Level);
                t.Fired = true;
                t.LastSent = now;
            }
            else if (t.Fired)
            {
                output.Add(events.Resolved(r.Name, t.FiredLevel, r.Title,
                    $"{r.Text} A hiba {Text.Length(now - t.FiredAt)} után szűnt meg.", t.FiredAt, now, r.Facts));
                t.Fired = false;
            }
        }
        return output;
    }
}
