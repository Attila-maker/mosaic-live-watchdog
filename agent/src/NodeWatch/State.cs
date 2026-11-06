using System.Text.Json;

namespace NodeWatch;

/// <summary>What survives a restart of the agent: the restart limit and the open alerts.</summary>
internal sealed class AgentState
{
    public DateTimeOffset? LastRestartAt { get; set; }

    /// <summary>Boot id and time since boot (CLOCK_MONOTONIC) at the last restart: within the same boot the restart limit is
    /// measured on this clock, which wall-clock changes (NTP steps, a dead RTC fixed later) cannot move.</summary>
    public string? LastRestartBootId { get; set; }
    public TimeSpan? LastRestartUptime { get; set; }

    public DateTimeOffset? GraceUntil { get; set; }

    /// <summary>The "NodeWatch cannot check the node" alert is open since then (resolved after the next good poll).</summary>
    public DateTimeOffset? AgentAlertSince { get; set; }

    /// <summary>The state file could not be read, so <see cref="LastRestartAt"/> was set to that moment to be safe.</summary>
    public bool LastRestartAssumed { get; set; }

    /// <summary>A restart (or a refused restart) alert is open: resolve it once the node works again.</summary>
    public bool RestartAlertOpen { get; set; }

    public DateTimeOffset? RestartAlertSince { get; set; }

    /// <summary>The "not restarting again" alert went out for the current outage.</summary>
    public bool RestartRefusedNotified { get; set; }
    public Dictionary<string, AlertTrack> Alerts { get; set; } = [];
}

internal sealed class StateStore(string directory)
{
    private string FilePath => Path.Combine(directory, "state.json");

    /// <summary>
    /// A missing file means a fresh start. A file that exists but cannot be read may have held a recent restart: then the
    /// restart limit starts now, so a broken state file can never allow an extra restart.
    /// </summary>
    public AgentState Load(DateTimeOffset now)
    {
        if (!File.Exists(FilePath)) return new AgentState();
        try
        {
            var state = JsonSerializer.Deserialize<AgentState>(File.ReadAllText(FilePath));
            if (state is not null) return state;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Error($"Az állapotfájl nem olvasható ({FilePath}: {e.Message}).");
        }
        Log.Warn("Biztonsági okból úgy veszem, hogy most volt újraindítás: a következő automatikus újraindítás csak a " +
                 "korlát leteltével lehet.");
        return new AgentState { LastRestartAt = now, LastRestartAssumed = true };
    }

    /// <summary>Writes the state durably (fsync, then atomic rename). Throws on failure.</summary>
    public void Save(AgentState state)
    {
        Directory.CreateDirectory(directory);
        var tmp = FilePath + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, state);
            stream.Flush(flushToDisk: true);
        }
        File.Move(tmp, FilePath, overwrite: true);
    }

    public bool TrySave(AgentState state)
    {
        try
        {
            Save(state);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Error($"Az állapotfájl nem menthető ({FilePath}): {e.Message}");
            return false;
        }
    }
}

/// <summary>Only one "run" per state folder: two agents would overwrite each other's restart limit.</summary>
internal sealed class InstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private InstanceLock(FileStream stream) => _stream = stream;

    /// <summary>Null if the lock cannot be taken; <paramref name="problem"/> says why, <paramref name="busy"/> is true when
    /// another process holds it.</summary>
    public static InstanceLock? TryAcquire(string directory, out string? problem, out bool busy)
    {
        var path = Path.Combine(directory, "nodewatch.lock");
        busy = false;
        try
        {
            Directory.CreateDirectory(directory);
            // FileShare.None is an exclusive flock() on Linux; the kernel releases it when the process ends.
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            problem = null;
            return new InstanceLock(stream);
        }
        catch (UnauthorizedAccessException e)
        {
            problem = $"Az állapotmappa nem írható ({e.Message}). Javítás: sudo chown -R nodewatch:nodewatch {directory}";
            return null;
        }
        catch (IOException e) when (File.Exists(path))
        {
            busy = true;
            problem = $"Már fut egy NodeWatch ezzel az állapotmappával ({directory}); két példány felülírná egymás " +
                      $"újraindítási korlátját. Nézd meg: systemctl status nodewatch ({e.Message})";
            return null;
        }
        catch (IOException e)
        {
            problem = $"Az állapotmappa nem használható ({directory}): {e.Message}";
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>A clock wall-clock changes cannot move: the boot id plus the time since boot.</summary>
internal interface IBootClock
{
    /// <summary>Null if it cannot be read.</summary>
    (string BootId, TimeSpan Uptime)? Now();
}

internal sealed class BootClock : IBootClock
{
    public static readonly BootClock Real = new();

    public (string BootId, TimeSpan Uptime)? Now()
    {
        try
        {
            var bootId = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
            if (bootId.Length == 0) return null;
            // Stopwatch is CLOCK_MONOTONIC on Linux: time since boot, not counting suspend.
            return (bootId, TimeSpan.FromSeconds(global::System.Diagnostics.Stopwatch.GetTimestamp() / (double)global::System.Diagnostics.Stopwatch.Frequency));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
