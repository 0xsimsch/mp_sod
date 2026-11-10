using System;

namespace SodCoop.Core;

/// <summary>
/// Is the game world in a state where sync makes sense? Loading a save (e.g. the host's snapshot on join)
/// marks thousands of evidence items found, creates/destroys interactables and rebuilds rooms; none of that
/// may be reported as player actions, and nothing may touch objects of the world being torn down.
/// Tested the hard way: a join load sent 12,329 bogus case events and threw in RemoteRoomLoader.
/// </summary>
public static class World
{
    static IntPtr _city;

    /// <summary>Bumped whenever a new city object appears (a load); caches keyed by old objects must reset.</summary>
    public static int Generation { get; private set; }

    public static event Action Reloaded;

    public static bool Ready
    {
        get
        {
            var cc = CityConstructor.Instance;
            var sd = SessionData.Instance;
            return CityData.Instance != null && cc != null && cc.isLoaded && !cc.loadingOperationActive && !cc.preSimActive
                   && sd != null && sd.startedGame;
        }
    }

    /// <summary>Per frame: detect a world reload.</summary>
    public static void Tick()
    {
        var city = CityData.Instance;
        var ptr = city != null ? city.Pointer : IntPtr.Zero;
        if (ptr == _city) return;
        _city = ptr;
        if (ptr == IntPtr.Zero) return;
        // Also a reload that went through "no city" for a frame (old world → null → new world): that used to
        // pass for a first world and reset nothing.
        if (!_hadWorld) { _hadWorld = true; return; }
        Generation++;
        Plugin.Event($"WORLD reloaded (generation {Generation}); resetting caches");
        Reloaded?.Invoke();
    }

    static bool _hadWorld;

    /// <summary>
    /// A reload the city pointer can't show: the join load from inside a world keeps the CityData object
    /// (SnapshotSync). Same effect as a detected reload.
    /// </summary>
    public static void NotifyReloaded(string why)
    {
        var city = CityData.Instance;
        _city = city != null ? city.Pointer : IntPtr.Zero;
        _hadWorld = true;
        Generation++;
        Plugin.Event($"WORLD reloaded (generation {Generation}, {why}); resetting caches");
        try { Reloaded?.Invoke(); }
        catch (Exception e) { Plugin.Event($"WORLD reload handlers failed: {e.Message}"); }
    }
}
