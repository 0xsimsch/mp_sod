using System;
using System.Collections.Generic;
using HarmonyLib;
using SodCoop.Core;
using UnityEngine;

namespace SodCoop.Net;

/// <summary>
/// The hidden traces a scanner later finds (SYNC_GAPS G4), so both players find the same prints in the same places
/// with the same owner. <see cref="ForensicSync"/> only covers prints a player has already revealed.
///
/// <b>Fingerprints</b> live on the touched object (<c>Interactable.df</c>: owner id, time, life and a random seed
/// that places the print). <c>Interactable.AddNewDynamicFingerprint</c> made them on each machine by itself, with
/// its own seed and for whatever its own citizens touched. Now the host's are sent and clients add no citizen
/// prints of their own; a client's own touches go to the host (and on to other clients).
///
/// <b>Footprints</b> (<c>GameplayController.footprintsList</c>, made in <c>Human.OnFootstep</c> with random jitter)
/// are the host's citizens' only: the host sends each new one; a client drops whatever it generated locally.
/// Players leave none (user decision 2026-11-10). <c>OnFootstep</c> runs from animation events, which must not be
/// hooked (native crash, 0.7.0), and it also plays the step sound - so the list is swept instead of patching.
/// </summary>
public static class PrintSync
{
    const byte KindFinger = 1; // ushort n, n × (int interactable, int owner, float created, str seed, byte life)
    const byte KindFoot = 2;   // ushort n, n × (int human, int room, vec3 pos, vec3 euler, float dirt, float blood, float time)
    const float FlushInterval = 0.5f, SweepInterval = 0.25f;
    const int MaxWalk = 128;   // new footprints looked at per sweep
    const int MaxBatch = 200;

    public static int FingerSent { get; private set; }
    public static int FingerApplied { get; private set; }
    public static int FingerMissed { get; private set; }
    public static int FootSent { get; private set; }
    public static int FootApplied { get; private set; }
    public static int FootDropped { get; private set; }

    [ThreadStatic] static bool _applying;
    static NetSession S => NetSession.Current;
    static bool IsClient => S != null && S.Role == NetSession.SessionRole.Client;

    struct Finger { public int Interactable, Owner; public float Created; public string Seed; public byte Life; }
    struct Foot { public int Human, Room; public Vector3 Pos, Euler; public float Dirt, Blood, Time; }

    static readonly List<Finger> _fingerQueue = new();
    static float _nextFlush, _nextSweep;
    static bool _logged;

    // --- fingerprints

    [HarmonyPatch(typeof(Interactable), nameof(Interactable.AddNewDynamicFingerprint))]
    static class AddPrint
    {
        static bool Prefix(Interactable __instance, Human from, out int __state)
        {
            __state = -1;
            try
            {
                var s = S;
                if (s == null || _applying || __instance == null || !World.Ready || !s.ClientWorldJoined) return true;
                // Another player's body here: its owner reports its own touches.
                if (from != null && PlayerRegistry.IsRemoteBody(from)) return false;
                bool local = from != null && Player.Instance != null && from.Pointer == Player.Instance.Pointer;
                // Citizens' prints are the host's to make; a client keeps only its own player's.
                if (s.Role == NetSession.SessionRole.Client && !local) return false;
                __state = __instance.df != null ? __instance.df.Count : 0;
            }
            catch (Exception e) { LogOnce($"PRINT fingerprint prefix failed: {e.Message}"); }
            return true;
        }

        static void Postfix(Interactable __instance, int __state)
        {
            if (__state < 0) return;
            try
            {
                var df = __instance.df;
                if (df == null || df.Count <= __state) return; // the game decided against a print (gloves, duplicate)
                var p = df[df.Count - 1];
                if (p == null) return;
                if (_fingerQueue.Count < 2000)
                    _fingerQueue.Add(new Finger { Interactable = __instance.id, Owner = p.id, Created = p.created, Seed = p.seed ?? "", Life = (byte)p.life });
            }
            catch (Exception e) { LogOnce($"PRINT fingerprint report failed: {e.Message}"); }
        }
    }

    static void FlushFingers()
    {
        if (_fingerQueue.Count == 0) return;
        for (int at = 0; at < _fingerQueue.Count; at += MaxBatch)
        {
            int n = Math.Min(MaxBatch, _fingerQueue.Count - at), start = at;
            S.SendExtra(ExtraWire.Write(ExtraWire.Prints, KindFinger, w =>
            {
                w.Write((ushort)n);
                for (int i = start; i < start + n; i++)
                {
                    var f = _fingerQueue[i];
                    w.Write(f.Interactable); w.Write(f.Owner); w.Write(f.Created); ExtraWire.Str(w, f.Seed); w.Write(f.Life);
                }
            }));
            FingerSent += n;
        }
        if (!_fingerLogged) { _fingerLogged = true; Plugin.Event($"PRINT first fingerprints sent ({_fingerQueue.Count})"); }
        _fingerQueue.Clear();
    }

    static bool _fingerLogged, _fingerApplyLogged, _footLogged, _footApplyLogged;

    static void ApplyFinger(Finger f)
    {
        var it = WorldSync.FindInteractable(f.Interactable);
        if (it == null) { FingerMissed++; return; }
        if (it.df == null) it.df = new Il2CppSystem.Collections.Generic.List<Interactable.DynamicFingerprint>();
        for (int i = 0; i < it.df.Count; i++)
        {
            var have = it.df[i];
            if (have != null && have.id == f.Owner && have.seed == f.Seed) return; // already here
        }
        it.df.Add(new Interactable.DynamicFingerprint { id = f.Owner, created = f.Created, seed = f.Seed, life = (Interactable.PrintLife)f.Life });
        GameplayController.Instance?.objectsWithDynamicPrints?.Add(it);
        FingerApplied++;
        if (!_fingerApplyLogged) { _fingerApplyLogged = true; Plugin.Event($"PRINT first fingerprint from the other side: object #{f.Interactable}, owner {f.Owner}, seed {f.Seed}"); }
    }

    // --- footprints

    static IntPtr _lastFoot;      // newest entry of footprintsList at the last sweep
    static bool _footPrimed;
    static readonly HashSet<IntPtr> _fromHost = new(); // client: entries the host sent (everything else is local)

    /// <summary>
    /// Looks at the footprints added since the last sweep. Host: players' are removed, citizens' are sent.
    /// Client: all of them are removed (the host's arrive by message).
    /// </summary>
    static void SweepFootprints()
    {
        var gc = GameplayController.Instance;
        var list = gc != null ? gc.footprintsList : null;
        if (list == null) return;
        int n = list.Count;
        if (!_footPrimed)
        {
            // What the save brought along is the same on every machine: start after it.
            _footPrimed = true;
            _lastFoot = n > 0 && list[n - 1] != null ? list[n - 1].Pointer : IntPtr.Zero;
            return;
        }
        if (n == 0) { _lastFoot = IntPtr.Zero; return; }

        var fresh = new List<GameplayController.Footprint>();
        bool found = _lastFoot == IntPtr.Zero && n <= MaxWalk;
        for (int i = n - 1; i >= 0 && fresh.Count < MaxWalk; i--)
        {
            var f = list[i];
            if (f == null) continue;
            if (f.Pointer == _lastFoot) { found = true; break; }
            fresh.Add(f);
        }
        if (!found)
        {
            // The marker is gone (the game cleared or trimmed the list): start over rather than touch old entries.
            _lastFoot = list[n - 1] != null ? list[n - 1].Pointer : IntPtr.Zero;
            return;
        }
        if (fresh.Count == 0) return;

        bool client = IsClient;
        HashSet<int> playerIds = null;
        if (!client)
        {
            playerIds = new HashSet<int>();
            foreach (var p in PlayerRegistry.All) { var b = p.Body; if (b != null) playerIds.Add(b.humanID); }
        }
        List<Foot> send = null;
        for (int i = fresh.Count - 1; i >= 0; i--) // oldest first
        {
            var f = fresh[i];
            if (client)
            {
                if (_fromHost.Contains(f.Pointer)) continue;
                Remove(gc, f);
            }
            else if (playerIds.Contains(f.hID)) Remove(gc, f);
            else if (S.HasClients)
                (send ??= new List<Foot>()).Add(new Foot { Human = f.hID, Room = f.rID, Pos = f.wP, Euler = f.eU, Dirt = f.str, Blood = f.bl, Time = f.t });
        }
        n = list.Count;
        _lastFoot = n > 0 && list[n - 1] != null ? list[n - 1].Pointer : IntPtr.Zero;
        if (_fromHost.Count > 4096) _fromHost.Clear(); // only entries after the marker are ever looked up

        if (send == null) return;
        for (int at = 0; at < send.Count; at += MaxBatch)
        {
            int count = Math.Min(MaxBatch, send.Count - at), start = at;
            S.SendExtra(ExtraWire.Write(ExtraWire.Prints, KindFoot, w =>
            {
                w.Write((ushort)count);
                for (int i = start; i < start + count; i++)
                {
                    var f = send[i];
                    w.Write(f.Human); w.Write(f.Room);
                    w.Write(f.Pos.x); w.Write(f.Pos.y); w.Write(f.Pos.z);
                    w.Write(f.Euler.x); w.Write(f.Euler.y); w.Write(f.Euler.z);
                    w.Write(f.Dirt); w.Write(f.Blood); w.Write(f.Time);
                }
            }));
            FootSent += count;
        }
        if (!_footLogged) { _footLogged = true; Plugin.Event($"PRINT first footprints sent ({send.Count})"); }
    }

    static void Remove(GameplayController gc, GameplayController.Footprint f)
    {
        gc.footprintsList.Remove(f);
        NewRoom room = null;
        CityData.Instance?.roomDictionary?.TryGetValue(f.rID, out room);
        if (room != null)
        {
            if (gc.activeFootprints != null && gc.activeFootprints.TryGetValue(room, out var inRoom) && inRoom != null) inRoom.Remove(f);
            try { room.QueueFootprintUpdate(); } catch { /* a room that isn't drawn has nothing to refresh */ }
        }
        FootDropped++;
    }

    static void ApplyFoot(Foot m)
    {
        var gc = GameplayController.Instance;
        var city = CityData.Instance;
        if (gc == null || gc.footprintsList == null || city == null) return;
        if (!city.citizenDictionary.TryGetValue(m.Human, out var human) || human == null) return;
        NewRoom room = null;
        city.roomDictionary?.TryGetValue(m.Room, out room);
        if (room == null) return;

        SweepFootprints(); // local ones first, so the marker can move past this one
        int before = gc.footprintsList.Count;
        // The constructor files the footprint under its room; its random jitter is overwritten with the host's values.
        var f = new GameplayController.Footprint(human, m.Pos, m.Euler, m.Dirt, m.Blood, room);
        f.hID = m.Human; f.rID = m.Room; f.wP = m.Pos; f.eU = m.Euler; f.str = m.Dirt; f.bl = m.Blood; f.t = m.Time;
        if (gc.footprintsList.Count == before) gc.footprintsList.Add(f);
        _fromHost.Add(f.Pointer);
        _lastFoot = f.Pointer;
        _footPrimed = true;
        try { room.QueueFootprintUpdate(); } catch { /* not drawn */ }
        FootApplied++;
        if (!_footApplyLogged) { _footApplyLogged = true; Plugin.Event($"PRINT first footprint from the host: citizen {m.Human} in room {m.Room} at {m.Pos}"); }
    }

    // --- session plumbing

    /// <summary>Per frame, host and joined client.</summary>
    public static void Tick(float now)
    {
        if (S == null || !World.Ready || !S.ClientWorldJoined) return;
        try
        {
            if (now >= _nextSweep) { _nextSweep = now + SweepInterval; SweepFootprints(); }
            if (now >= _nextFlush) { _nextFlush = now + FlushInterval; FlushFingers(); }
        }
        catch (Exception e) { LogOnce($"PRINT tick failed: {e.Message}"); }
    }

    public static void OnPacket(NetSession s, PeerId from, PlayerId? fromPlayer, byte[] data)
    {
        var r = ExtraWire.Read(data, out var kind);
        int n = r.ReadUInt16();
        if (!World.Ready || !s.ClientWorldJoined) return; // still loading: the snapshot is the starting point
        bool host = s.Role == NetSession.SessionRole.Host;
        if (kind == KindFoot && host) return; // footprints are the host's
        if (kind == KindFinger && host) s.SendExtraExcept(data, from);
        _applying = true;
        try
        {
            for (int i = 0; i < n; i++)
            {
                if (kind == KindFinger)
                    ApplyFinger(new Finger { Interactable = r.ReadInt32(), Owner = r.ReadInt32(), Created = r.ReadSingle(), Seed = ExtraWire.Str(r), Life = r.ReadByte() });
                else if (kind == KindFoot)
                    ApplyFoot(new Foot
                    {
                        Human = r.ReadInt32(), Room = r.ReadInt32(),
                        Pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                        Euler = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                        Dirt = r.ReadSingle(), Blood = r.ReadSingle(), Time = r.ReadSingle(),
                    });
                else return;
            }
        }
        finally { _applying = false; }
    }

    public static string Status() =>
        $"prints[finger sent={FingerSent} applied={FingerApplied} missed={FingerMissed} foot sent={FootSent} applied={FootApplied} dropped={FootDropped}]";

    static void LogOnce(string msg)
    {
        if (_logged) return;
        _logged = true;
        Plugin.Event(msg);
    }

    public static void Reset()
    {
        _fingerQueue.Clear();
        _fromHost.Clear();
        _footPrimed = false;
        _lastFoot = IntPtr.Zero;
        _logged = _fingerLogged = _fingerApplyLogged = _footLogged = _footApplyLogged = false;
    }

    // A replaced world has a new footprint list.
    static PrintSync() => World.Reloaded += () => { _fromHost.Clear(); _footPrimed = false; _lastFoot = IntPtr.Zero; _fingerQueue.Clear(); };
}
