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

    public static void Reset()
    {
        _keyOf = null;
        _inRange.Clear();
    }

    // Host: door states far from a client diverge (the host only sends changes within InterestRadius, and the
    // client's own citizens lock/unlock doors too), and locks last. Sending every change city-wide is a lot of
    // traffic, so instead each door's state goes to a client once when the client's body comes within range.
    const float ResyncInterval = 1f, LeaveRadius = InterestRadius + 10f;
    static float _nextResync;
    static readonly Dictionary<PeerId, HashSet<int>> _inRange = new();

    public static int Resynced { get; private set; }

    /// <summary>Host, per frame: doors that came within range of a client since the last check get their state sent.</summary>
    public static void HostApproachTick(float now, IEnumerable<(PeerId peer, Vector3 pos)> clients, Action<PeerId, DoorStateMsg> send)
    {
        if (now < _nextResync || !World.Ready) return;
        _nextResync = now + ResyncInterval;
        var dict = CityData.Instance?.doorDictionary;
        if (dict == null) return;
        var seen = new HashSet<PeerId>();
        foreach (var (peer, pos) in clients)
        {
            seen.Add(peer);
            if (!_inRange.TryGetValue(peer, out var set)) _inRange[peer] = set = new HashSet<int>();
            int sent = 0;
            foreach (var kv in dict)
            {
                var door = kv.Value;
                if (door == null) continue;
                float d = Vector3.Distance(door.transform.position, pos);
                if (d <= InterestRadius)
                {
                    if (!set.Add(kv.Key)) continue;
                    send(peer, new DoorStateMsg { DoorKey = kv.Key, Ajar = door.ajar, Locked = door.isLocked, ByPlayer = 255 });
                    sent++;
                }
                else if (d > LeaveRadius) set.Remove(kv.Key);
            }
            Resynced += sent;
        }
        foreach (var gone in new List<PeerId>(_inRange.Keys)) if (!seen.Contains(gone)) _inRange.Remove(gone);
    }

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
