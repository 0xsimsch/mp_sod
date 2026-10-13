using System;
using System.Collections.Generic;
using HarmonyLib;
using SodCoop.Core;
using UnityEngine;

namespace SodCoop.Net;

/// <summary>
/// First world-state sync: doors (open amount + lock).
/// Client: its own player's door changes go to the host. Host: applies them with the client's
/// body as the actor (so citizens attribute it to that player), and broadcasts every door change
/// within <see cref="InterestRadius"/> of a client. Network-applied changes aren't echoed back.
/// </summary>
public static class DoorSync
{
    const float InterestRadius = 60f;

    static Dictionary<IntPtr, int> _keyOf;
    [ThreadStatic] static bool _applying;

    public static int Sent { get; private set; }
    public static int Applied { get; private set; }

    static bool TryKey(NewDoor door, out int key)
    {
        key = 0;
        if (door == null) return false;
        if (_keyOf == null)
        {
            var dict = CityData.Instance?.doorDictionary;
            if (dict == null) return false;
            _keyOf = new Dictionary<IntPtr, int>();
            foreach (var kv in dict) if (kv.Value != null) _keyOf[kv.Value.Pointer] = kv.Key;
            Plugin.Event($"DOORS indexed {_keyOf.Count} doors");
        }
        return _keyOf.TryGetValue(door.Pointer, out key);
    }

    public static void Reset() => _keyOf = null;

    static DoorSync() => World.Reloaded += Reset;

    /// <summary>Called from the patches after a door changed locally.</summary>
    static void OnLocalChange(NewDoor door, Actor actor)
    {
        var s = NetSession.Current;
        if (s == null || _applying || !World.Ready || !TryKey(door, out var key)) return;

        var by = PlayerRegistry.OwnerOf(actor);
        if (s.Role == NetSession.SessionRole.Client && (by == null || !by.IsLocal)) return; // clients only report themselves

        var msg = new DoorStateMsg { DoorKey = key, Ajar = door.ajar, Locked = door.isLocked, ByPlayer = by != null ? by.Id.Value : (byte)255 };
        if (s.SendDoor(msg, door.transform.position, InterestRadius)) Sent++;
    }

    /// <summary>Apply a received door state (both roles).</summary>
    public static void Apply(DoorStateMsg m, Actor actor)
    {
        if (!World.Ready) return;
        var dict = CityData.Instance?.doorDictionary;
        if (dict == null || !dict.TryGetValue(m.DoorKey, out var door) || door == null) return;
        _applying = true;
        try
        {
            if (door.isLocked != m.Locked) door.SetLocked(m.Locked, actor, true);
            if (Mathf.Abs(door.ajar - m.Ajar) > 0.01f) door.SetOpen(m.Ajar, actor, false, 1f);
            Applied++;
        }
        finally { _applying = false; }
    }

    [HarmonyPatch(typeof(NewDoor), nameof(NewDoor.SetOpen))]
    static class OpenPatch
    {
        static void Postfix(NewDoor __instance, Actor actor)
        {
            try { OnLocalChange(__instance, actor); }
            catch (Exception e) { Plugin.Event($"door sync failed: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(NewDoor), nameof(NewDoor.SetLocked))]
    static class LockPatch
    {
        static void Postfix(NewDoor __instance, Actor actor)
        {
            try { OnLocalChange(__instance, actor); }
            catch (Exception e) { Plugin.Event($"door sync failed: {e.Message}"); }
        }
    }
}
