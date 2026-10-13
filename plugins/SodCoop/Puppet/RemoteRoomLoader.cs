using System.Collections.Generic;
using SodCoop.Core;
using UnityEngine;

namespace SodCoop.Puppet;

/// <summary>
/// A2 fix (experimental). The host only loads rooms around its own player; unloaded rooms have no
/// walls or colliders, so citizens near a remote player see straight through walls and physics there
/// breaks. Keep each remote body's room and its physical neighbours loaded on the host.
///
/// Uses <c>NewRoom.SetVisible</c>, the same call <c>GeometryCullingController</c> makes. Culling
/// works as a diff of its own sets (<c>currentRoomsCullingTree</c>), so rooms we turn on outside
/// those sets are left alone until we release them.
/// </summary>
public static class RemoteRoomLoader
{
    const float Interval = 1f;
    /// <summary>Keep a room this long after the body left it: bodies on stairs or doorways flipped rooms every second,
    /// loading and unloading up to 14 rooms each time (two-machine test, 0.7.1).</summary>
    const float Linger = 15f;
    static float _next;
    static readonly Dictionary<System.IntPtr, NewRoom> _forced = new();
    static readonly Dictionary<System.IntPtr, float> _lastWanted = new();

    public static int ForcedCount => _forced.Count;

    static RemoteRoomLoader() => Core.World.Reloaded += () => { _forced.Clear(); _lastWanted.Clear(); }; // old rooms are gone

    /// <summary>Give every forced room back to the host's own culling (F10 off, session end).</summary>
    public static void ReleaseAll()
    {
        var hostSet = GeometryCullingController.Instance?.currentRoomsCullingTree;
        foreach (var r in _forced.Values)
            if (r != null && (hostSet == null || !hostSet.Contains(r))) r.SetVisible(false, true, false, false);
        if (_forced.Count > 0) Plugin.Event($"ROOMS released all {_forced.Count} forced rooms");
        _forced.Clear();
    }

    public static void Update(float now)
    {
        if (now < _next) return;
        _next = now + Interval;

        var wanted = new Dictionary<System.IntPtr, NewRoom>();
        foreach (var p in PlayerRegistry.All)
        {
            var body = p.RemoteBody?.Human;
            if (body?.currentRoom == null) continue;
            var room = body.currentRoom;
            Add(wanted, room);
            AddAll(wanted, room.adjacentRooms);
            AddAll(wanted, room.aboveRooms);
            AddAll(wanted, room.belowRooms);
            // adjacentRooms is empty for some rooms (seen in a test home), so also take the whole address.
            var rooms = body.currentGameLocation?.rooms;
            if (rooms != null) for (int i = 0; i < rooms.Count; i++) Add(wanted, rooms[i]);
        }

        int loaded = 0;
        foreach (var kv in wanted)
        {
            var r = kv.Value;
            if (!r.isVisible || !r.geometryLoaded)
            {
                r.SetVisible(true, true, true, true);
                loaded++;
            }
            _forced[kv.Key] = r;
            _lastWanted[kv.Key] = now;
        }

        // Release rooms nobody needs any more, unless the host's own culling wants them.
        var hostSet = GeometryCullingController.Instance?.currentRoomsCullingTree;
        var release = new List<System.IntPtr>();
        foreach (var kv in _forced)
        {
            if (wanted.ContainsKey(kv.Key)) continue;
            if (_lastWanted.TryGetValue(kv.Key, out var last) && now - last < Linger) continue;
            release.Add(kv.Key);
            if (hostSet != null && hostSet.Contains(kv.Value)) continue;
            kv.Value.SetVisible(false, true, false, false);
        }
        foreach (var k in release) { _forced.Remove(k); _lastWanted.Remove(k); }

        if (loaded > 0 || release.Count > 0)
            Plugin.Event($"ROOMS forced {wanted.Count} around remote players (+{loaded} loaded now, {release.Count} released)");
    }

    static void Add(Dictionary<System.IntPtr, NewRoom> set, NewRoom r)
    {
        if (r != null) set[r.Pointer] = r;
    }

    static void AddAll(Dictionary<System.IntPtr, NewRoom> set, Il2CppSystem.Collections.Generic.HashSet<NewRoom> rooms)
    {
        if (rooms == null) return;
        foreach (var r in rooms) Add(set, r);
    }

    public static string Describe(NewRoom room)
    {
        if (room == null) return "room=null";
        int on = 0, total = 0;
        foreach (var col in room.GetComponentsInChildren<Collider>(true)) { total++; if (col.enabled && col.gameObject.activeInHierarchy) on++; }
        return $"room={room.roomID} visible={room.isVisible} geom={room.geometryLoaded} goActive={room.gameObject.activeInHierarchy} colliders={on}/{total} walls={room.combinedWalls != null}";
    }
}
