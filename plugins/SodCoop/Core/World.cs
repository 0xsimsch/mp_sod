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
        bool hadWorld = _city != IntPtr.Zero;
        _city = ptr;
        if (!hadWorld || ptr == IntPtr.Zero) return;
        Generation++;
        Plugin.Event($"WORLD reloaded (generation {Generation}); resetting caches");
        Reloaded?.Invoke();
    }
}
