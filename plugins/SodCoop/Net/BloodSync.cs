using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SodCoop.Core;
using UnityEngine;

namespace SodCoop.Net;

/// <summary>
/// Blood: spatter decals from fights, KOs, bleeding and murders (<c>SpatterSimulation</c>). Blood pools are
/// interactables and already arrive through SpawnSync ("SPAWN applied BloodPool").
///
/// Choke point: <c>GameplayController.spatter</c>, the list every simulation registers in. It is polled once a second
/// instead of patching the constructors (IL2CPP constructor patches are a crash risk) or <c>Execute</c> (it also runs
/// again whenever a room becomes visible, <c>NewRoom.SetVisible/LoadRoomStuff</c>).
/// <list type="bullet">
/// <item>Host: every new simulation goes to the clients (murder scenes far away too: decals of unloaded rooms are only
///   executed when the room loads, on each machine).</item>
/// <item>Client: a new local simulation near its own player (its own blood, its own hits) goes to the host, which applies
///   and relays it; one elsewhere comes from the client's own diverged citizens or a local replay, and is removed.</item>
/// <item>Receiving: rebuilt with the world-position constructor (which registers and executes it); skipped if an equal
///   one (same preset, origin within 10 cm) is already there, e.g. from a local murder replay.</item>
/// </list>
/// The spatter that came with a save or the join snapshot is taken as known when the world (re)loads, never sent.
/// </summary>
public static class BloodSync
{
    public const byte MsgTypeSpatter = 50;
    const ushort Magic = 0x4D53; // same header as Protocol.Write
    const float PollInterval = 1f;
    const float OwnReach = 4f;

    [ThreadStatic] static bool _applying;
    static readonly HashSet<IntPtr> _known = new();
    static int _primedGeneration = -1;
    static float _nextPoll;

    public static int Sent { get; private set; }
    public static int Applied { get; private set; }
    public static int RemovedLocal { get; private set; }

    static NetSession Session => NetSession.Current;
    static bool IsHost => Session?.Role == NetSession.SessionRole.Host;
    static bool IsClient => Session?.Role == NetSession.SessionRole.Client;

    struct SpatterMsg
    {
        public string Preset;
        public Vector3 Origin, Target;
        public byte EraseMode, Force;
        public float Multiplier;
        public bool Stick;
    }

    static byte[] Write(SpatterMsg m)
    {
        using var ms = new MemoryStream(64);
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(Magic); w.Write(MsgTypeSpatter);
        w.Write(m.Preset ?? "");
        foreach (var v in new[] { m.Origin, m.Target }) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        w.Write(m.EraseMode); w.Write(m.Force); w.Write(m.Multiplier); w.Write(m.Stick);
        w.Flush();
        return ms.ToArray();
    }

    static SpatterMsg Read(byte[] d)
    {
        using var ms = new MemoryStream(d, 3, d.Length - 3);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        var m = new SpatterMsg { Preset = r.ReadString() };
        m.Origin = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        m.Target = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        m.EraseMode = r.ReadByte(); m.Force = r.ReadByte(); m.Multiplier = r.ReadSingle(); m.Stick = r.ReadBoolean();
        return m;
    }

    /// <summary>Per frame (host with clients, or welcomed client).</summary>
    public static void Tick(float now)
    {
        if (now < _nextPoll || Session == null || !World.Ready) return;
        _nextPoll = now + PollInterval;
        var list = GameplayController.Instance?.spatter;
        if (list == null) return;

        if (_primedGeneration != World.Generation)
        {
            // What the save/snapshot brought along is already on every machine.
            _known.Clear();
            for (int i = 0; i < list.Count; i++) if (list[i] != null) _known.Add(list[i].Pointer);
            _primedGeneration = World.Generation;
            return;
        }

        var me = Player.Instance;
        List<SpatterSimulation> remove = null;
        for (int i = 0; i < list.Count; i++)
        {
            var sim = list[i];
            if (sim == null || !_known.Add(sim.Pointer)) continue;
            try
            {
                if (IsHost)
                {
                    Session.SendWorld(Write(Capture(sim)));
                    Sent++;
                }
                else if (IsClient)
                {
                    if (me != null && (sim.worldOrigin - me.transform.position).sqrMagnitude <= OwnReach * OwnReach)
                    {
                        Session.SendWorld(Write(Capture(sim)));
                        Sent++;
                    }
                    else (remove ??= new List<SpatterSimulation>()).Add(sim);
                }
            }
            catch (Exception e) { Plugin.Event($"BLOOD capture failed: {e.Message}"); }
        }
        if (remove != null)
        {
            foreach (var sim in remove)
            {
                try { sim.Remove(); RemovedLocal++; }
                catch (Exception e) { Plugin.Event($"BLOOD removing a local spatter failed: {e.Message}"); }
            }
            Plugin.Event($"BLOOD removed {remove.Count} local spatter(s) away from our player (the host's blood comes over the network)");
        }
    }

    static SpatterMsg Capture(SpatterSimulation sim) => new()
    {
        Preset = sim.presetStr ?? (sim.preset != null ? sim.preset.name : ""),
        Origin = sim.worldOrigin, Target = sim.worldTarget,
        EraseMode = (byte)sim.eraseMode, Force = (byte)sim.force,
        Multiplier = sim.spatterCountMultiplier, Stick = sim.stickToActors,
    };

    /// <summary>Both roles: a spatter from the other side (the host relays a client's to the other clients itself).</summary>
    public static void OnPacket(byte[] data, bool fromClient)
    {
        if (!World.Ready) return;
        SpatterMsg m;
        try { m = Read(data); }
        catch (Exception e) { Plugin.Event($"BLOOD bad packet: {e.Message}"); return; }
        if (fromClient && IsHost) Session?.SendWorld(data); // relay: the sender skips it as a duplicate
        Apply(m);
    }

    static void Apply(SpatterMsg m)
    {
        var gc = GameplayController.Instance;
        if (gc?.spatter == null || Toolbox.Instance == null || string.IsNullOrEmpty(m.Preset)) return;
        for (int i = 0; i < gc.spatter.Count; i++)
        {
            var s = gc.spatter[i];
            if (s != null && s.presetStr == m.Preset && (s.worldOrigin - m.Origin).sqrMagnitude < 0.01f) return; // already here
        }
        if (!Toolbox.Instance.LoadDataFromResources<SpatterPatternPreset>(m.Preset, out var preset) || preset == null)
        {
            Plugin.Event($"BLOOD unknown spatter preset {m.Preset}");
            return;
        }
        _applying = true;
        try
        {
            var sim = new SpatterSimulation(m.Origin, m.Target, preset, (SpatterSimulation.EraseMode)m.EraseMode, m.Multiplier, m.Stick);
            sim.force = (SpatterSimulation.ForceType)m.Force;
            _known.Add(sim.Pointer); // never re-send what we were sent
            Applied++;
        }
        catch (Exception e) { Plugin.Event($"BLOOD replay of {m.Preset} failed: {e.Message}"); }
        finally { _applying = false; }
    }

    public static void Reset()
    {
        _known.Clear();
        _primedGeneration = -1;
    }
}
