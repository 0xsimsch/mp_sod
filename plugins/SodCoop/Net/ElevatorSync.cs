using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using SodCoop.Core;
using UnityEngine;

namespace SodCoop.Net;

/// <summary>
/// Elevators are shared physical objects: before this every machine ran its own car, so one player rode a car the other
/// saw parked on another floor (and the rider floated in the shaft). The host owns them:
/// <list type="bullet">
/// <item>Calls: a client's button press is sent to the host instead of moving the client's car; every call the host
///   accepts (players' and citizens') goes to clients near that elevator, which replay it, so the car moves smoothly on
///   every machine with the game's own movement, sounds and doors.</item>
/// <item>State: the host streams floor, destination, direction, transit and the car's height for elevators near a remote
///   player (every 0.5 s, plus on change); a client snaps its car only when it has drifted.</item>
/// </list>
/// Elevators are identified like the save does (<c>StateSaveData.ElevatorStateSave.tileID</c>): the bottom tile's id.
/// </summary>
public static class ElevatorSync
{
    public const byte MsgTypeLift = 48;
    const ushort Magic = 0x4D53;
    const float Interest = 40f;        // host: stream elevators within this of a remote body
    const float StreamInterval = 0.5f;
    const float SnapDrift = 0.75f;     // client: snap the car when its height is off by more than this
    const float ButtonReach = 6f;      // client: only forward calls made next to our own player

    enum Kind : byte { Call = 1, State = 2 }

    [ThreadStatic] static bool _applying;
    static float _nextStream;
    static readonly Dictionary<int, string> _lastSent = new();
    public static int Sent { get; private set; }
    public static int Applied { get; private set; }

    static NetSession Session => NetSession.Current;
    static bool IsHost => Session?.Role == NetSession.SessionRole.Host;
    static bool IsClient => Session?.Role == NetSession.SessionRole.Client;

    static int IdOf(Elevator e) => e?.bottom != null ? e.bottom.tileID : -1;

    static Elevator Find(int id)
    {
        var list = SessionData.Instance?.activeElevators;
        if (list == null) return null;
        for (int i = 0; i < list.Count; i++)
            if (list[i] != null && IdOf(list[i]) == id) return list[i];
        return null;
    }

    /// <summary>A point to measure distance to: the car, else the bottom floor's door/button.</summary>
    static Vector3? PositionOf(Elevator e)
    {
        if (e.spawnedObject != null) return e.spawnedObject.position;
        if (e.elevatorFloors != null)
            foreach (var kv in e.elevatorFloors)
            {
                var f = kv.Value;
                var it = f?.door ?? f?.upButton ?? f?.downButton;
                if (it != null) return it.wPos;
            }
        return null;
    }

    static bool NearRemoteBody(Elevator e)
    {
        var p = PositionOf(e);
        if (p == null) return false;
        foreach (var pl in PlayerRegistry.All)
        {
            var h = pl.IsLocal ? null : pl.RemoteBody?.Human;
            if (h == null || h.WasCollected) continue;
            var d = h.transform.position - p.Value;
            d.y *= 0.25f; // tall shafts: height matters less than horizontal distance
            if (d.sqrMagnitude < Interest * Interest) return true;
        }
        return false;
    }

    static bool NearLocalPlayer(Elevator e, int floor)
    {
        var me = Player.Instance;
        if (me == null) return false;
        if (e.elevatorFloors != null && e.elevatorFloors.TryGetValue(floor, out var f) && f != null)
        {
            var it = f.upButton ?? f.downButton ?? f.door;
            if (it != null && Vector3.Distance(it.wPos, me.transform.position) < ButtonReach) return true;
        }
        var car = e.spawnedObject;
        return car != null && Vector3.Distance(car.position, me.transform.position) < ButtonReach; // the car's own panel
    }

    // --- wire

    static byte[] Write(Kind kind, Action<BinaryWriter> body)
    {
        using var ms = new MemoryStream(48);
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(Magic); w.Write(MsgTypeLift); w.Write((byte)kind);
        body(w);
        w.Flush();
        return ms.ToArray();
    }

    static byte[] CallMsg(int id, int floor, bool up) => Write(Kind.Call, w => { w.Write(id); w.Write(floor); w.Write(up); });

    static byte[] StateMsg(Elevator e) => Write(Kind.State, w =>
    {
        w.Write(IdOf(e)); w.Write(e.currentFloor); w.Write(e.currentDestination); w.Write(e.ultimateDesitnation);
        w.Write(e.isGoingUp); w.Write(e.inTransit); w.Write(e.desiredY);
        w.Write(e.spawnedObject != null ? e.spawnedObject.position.y : float.NaN);
    });

    public static void OnPacket(byte[] data)
    {
        if (!World.Ready || data.Length < 4) return;
        using var ms = new MemoryStream(data, 3, data.Length - 3);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        var kind = (Kind)r.ReadByte();
        int id = r.ReadInt32();
        var e = Find(id);
        if (e == null) return; // not spawned here: nothing to move
        _applying = true;
        try
        {
            if (kind == Kind.Call)
            {
                int floor = r.ReadInt32(); bool up = r.ReadBoolean();
                e.CallElevator(floor, up); // host: its postfix sends it on to clients; client: replay
                Applied++;
            }
            else if (kind == Kind.State && IsClient)
            {
                int cur = r.ReadInt32(), dest = r.ReadInt32(), ult = r.ReadInt32();
                bool goingUp = r.ReadBoolean(), transit = r.ReadBoolean();
                float desiredY = r.ReadSingle(), carY = r.ReadSingle();
                e.currentFloor = cur; e.currentDestination = dest; e.ultimateDesitnation = ult;
                e.isGoingUp = goingUp; e.desiredY = desiredY;
                if (e.inTransit != transit) e.SetInTransit(transit);
                var car = e.spawnedObject;
                if (car != null && !float.IsNaN(carY) && Mathf.Abs(car.position.y - carY) > SnapDrift)
                {
                    var p = car.position;
                    car.position = new Vector3(p.x, carY, p.z);
                    Plugin.Event($"LIFT {id}: car snapped to the host's height ({p.y:F1} -> {carY:F1})");
                }
                Applied++;
            }
        }
        catch (Exception ex) { Plugin.Event($"LIFT {id} {kind} failed: {ex.Message}"); }
        finally { _applying = false; }
    }

    /// <summary>Host, per frame: stream elevators near remote players.</summary>
    public static void HostTick(float now)
    {
        if (!IsHost || now < _nextStream || !World.Ready) return;
        _nextStream = now + StreamInterval;
        var list = SessionData.Instance?.activeElevators;
        if (list == null) return;
        for (int i = 0; i < list.Count; i++)
        {
            var e = list[i];
            if (e == null || !NearRemoteBody(e)) continue;
            int id = IdOf(e);
            float y = e.spawnedObject != null ? e.spawnedObject.position.y : 0f;
            string key = $"{e.currentFloor}/{e.currentDestination}/{e.ultimateDesitnation}/{e.isGoingUp}/{e.inTransit}/{y:F1}";
            // Moving cars every tick; idle ones only on change.
            if (!e.inTransit && _lastSent.TryGetValue(id, out var last) && last == key) continue;
            _lastSent[id] = key;
            Session?.SendWorld(StateMsg(e));
            Sent++;
        }
    }

    [HarmonyPatch(typeof(Elevator), nameof(Elevator.CallElevator))]
    static class Calls
    {
        /// <summary>Client: a press next to our player goes to the host; everything else (local citizens) is dropped.</summary>
        static bool Prefix(Elevator __instance, int newFloor, bool upButton)
        {
            if (!IsClient || _applying || !World.Ready) return true;
            if (NearLocalPlayer(__instance, newFloor))
            {
                Session?.SendWorld(CallMsg(IdOf(__instance), newFloor, upButton));
                Sent++;
                Plugin.Event($"LIFT {IdOf(__instance)}: called to floor {newFloor} ({(upButton ? "up" : "down")}); asking the host");
            }
            return false;
        }

        /// <summary>Host: every accepted call (players' and citizens') goes to clients near this elevator.</summary>
        static void Postfix(Elevator __instance, int newFloor, bool upButton)
        {
            if (!IsHost || !World.Ready || !NearRemoteBody(__instance)) return;
            Session?.SendWorld(CallMsg(IdOf(__instance), newFloor, upButton));
            Sent++;
        }
    }

    public static void Reset() => _lastSent.Clear();
}
