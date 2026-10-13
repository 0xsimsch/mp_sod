using HarmonyLib;

namespace SodCoop.Net;

/// <summary>
/// Design decision: only the host controls time. The host sends its clock every 2 s; a client
/// snaps to it when it drifts more than <see cref="MaxDrift"/> game hours, and copies the time speed.
/// On a client, local attempts to change time (speed buttons, sleeping/waiting, fast-forward) are blocked.
/// </summary>
public static class TimeSync
{
    public const float Interval = 2f;
    /// <summary>0.02 game hours = 72 game seconds.</summary>
    const float MaxDrift = 0.02f;

    static bool _applying;
    internal static bool Applying => _applying;
    public static int Corrections { get; private set; }

    /// <summary>Host: send a TimeSync right away (pause state changed).</summary>
    public static bool Dirty;

    static bool BlockLocal => !_applying && NetSession.Current?.Role == NetSession.SessionRole.Client;

    public static TimeSyncMsg Capture()
    {
        var sd = SessionData.Instance;
        return new TimeSyncMsg { GameTime = sd.gameTime, TimeSpeed = (byte)sd.currentTimeSpeed, LeapYearCycle = sd.leapYearCycle, Paused = Patches.PauseSync.WorldPaused };
    }

    /// <summary>Session over: hand pausing back to the game.</summary>
    public static void Reset() => Patches.PauseSync.Reset();

    public static void Apply(TimeSyncMsg m)
    {
        var sd = SessionData.Instance;
        if (sd == null) return;
        _applying = true;
        try
        {
            float drift = sd.gameTime - m.GameTime;
            if (System.Math.Abs(drift) > MaxDrift)
            {
                sd.SetGameTime(m.GameTime, m.LeapYearCycle);
                Corrections++;
                Plugin.Event($"TIME corrected by {-drift:F3} h to host time {m.GameTime:F3}");
            }
            var speed = (SessionData.TimeSpeed)m.TimeSpeed;
            if (sd.currentTimeSpeed != speed) sd.SetTimeSpeed(speed);

            // The world pauses only while every player is in a menu; the host decides (Patches/PauseSync.cs).
            Patches.PauseSync.SetWorldPaused(m.Paused);
        }
        finally { _applying = false; }
    }

    [HarmonyPatch(typeof(SessionData), nameof(SessionData.SetTimeSpeed))]
    static class BlockSpeed
    {
        static bool Prefix() => !BlockLocal;
    }

    [HarmonyPatch(typeof(Player), nameof(Player.SetSpendingTimeMode))]
    static class BlockSpendTime
    {
        // Clients may enter sleep/wait/KO mode now: RestSync tells the host, which only speeds time up when every
        // player rests; the client's own speed-up stays blocked (BlockSpeed).
        static bool Prefix() => true;
    }

    [HarmonyPatch(typeof(Game), nameof(Game.SetFastForward))]
    static class BlockFastForward
    {
        static bool Prefix(bool val) => !val || !BlockLocal;
    }
}
