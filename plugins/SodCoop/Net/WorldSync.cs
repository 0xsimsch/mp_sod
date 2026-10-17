using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using SodCoop.Core;
using UnityEngine;

namespace SodCoop.Net;

/// <summary>Wire format for <see cref="WorldSync"/>: one message type (35) carrying a tagged union.</summary>
public enum WorldEventKind : byte
{
    /// <summary>Host → clients: SetWeather values. F0..F4 = rain, wind, snow, lightning, fog; F5 = transition speed.</summary>
    Weather = 1,
    /// <summary>A window broke. V0 = its key (BreakableWindowController.GetRoughPosition), V1 = contact, V2 = velocity, F0 = brokenAt.</summary>
    Window = 2,
    /// <summary>NewDoor.SetJammed. I0 = door key, B0 = jammed, I1 = wedge interactable id (-1 none).</summary>
    DoorJam = 3,
    /// <summary>NewDoor.SetPoliceTape. I0 = door key, B0 = tape up.</summary>
    DoorTape = 4,
    /// <summary>NewDoor.Barge. I0 = door key; ByPlayer/I1 = barger (player id, else humanID).</summary>
    DoorBarge = 5,
    /// <summary>Host → clients: a non-player (citizen, timer) flipped a switch. I0 = interactable id, I1 = InteractablePreset.Switch, B0 = value.</summary>
    Switch = 6,
    /// <summary>Host → clients: alarm state. B1 = is address (else building), I0 = id, B0 = active, I1 = AlarmTargetMode, F0 = timer, I2 = floor (-999 none).</summary>
    Alarm = 7,
    /// <summary>Interactable.SetExtraStateValue on a breaker. I0 = interactable id, F0 = value.</summary>
    Breaker = 8,
    /// <summary>Client → host: the client's own player set off an alarm. B1 = is address (else building), I0 = id, I2 = floor (-999 none).</summary>
    AlarmRequest = 9,
}

public struct WorldEventMsg
{
    public WorldEventKind Kind;
    public byte ByPlayer; // 255 = not a player
    public int I0, I1, I2;
    public bool B0, B1;
    public float F0, F1, F2, F3, F4, F5;
    public Vector3 V0, V1, V2;
}

public static class WorldProtocol
{
    public const byte MsgTypeWorldEvent = 35;
    const ushort Magic = 0x4D53; // same header as Protocol.Write: magic, type

    public static byte[] Write(WorldEventMsg m)
    {
        using var ms = new MemoryStream(96);
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(Magic); w.Write(MsgTypeWorldEvent);
        w.Write((byte)m.Kind); w.Write(m.ByPlayer);
        w.Write(m.I0); w.Write(m.I1); w.Write(m.I2); w.Write(m.B0); w.Write(m.B1);
        w.Write(m.F0); w.Write(m.F1); w.Write(m.F2); w.Write(m.F3); w.Write(m.F4); w.Write(m.F5);
        foreach (var v in new[] { m.V0, m.V1, m.V2 }) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        w.Flush();
        return ms.ToArray();
    }

    public static WorldEventMsg Read(byte[] d)
    {
        using var ms = new MemoryStream(d, 3, d.Length - 3);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        var m = new WorldEventMsg
        {
            Kind = (WorldEventKind)r.ReadByte(), ByPlayer = r.ReadByte(),
            I0 = r.ReadInt32(), I1 = r.ReadInt32(), I2 = r.ReadInt32(), B0 = r.ReadBoolean(), B1 = r.ReadBoolean(),
            F0 = r.ReadSingle(), F1 = r.ReadSingle(), F2 = r.ReadSingle(), F3 = r.ReadSingle(), F4 = r.ReadSingle(), F5 = r.ReadSingle(),
        };
        m.V0 = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        m.V1 = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        m.V2 = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        return m;
    }
}

/// <summary>
/// World state that both players see (SYNC_PRIORITIES items 3, 6, 10, 11, 17):
/// <list type="bullet">
/// <item>Weather: the host's roll (SessionData.Update → SetWeather) wins; clients only accept the host's values.</item>
/// <item>Switches flipped by citizens or timers near a client (lights, TVs, appliances): the host sends them; a client
///   doesn't let its own local citizens flip switches near its player. (Player-flipped switches are ItemSync's.)</item>
/// <item>Alarms (building/address on/off, target mode, timer) and security cameras/sentries: host-authoritative.
///   Clients never raise alarms or let cameras investigate; the host's cameras see the client's body.</item>
/// <item>Breakers: the breaker interactable's extra state value.</item>
/// <item>Broken windows (keyed like the save, by rough position), door wedges/jams, police tape, barging.</item>
/// </list>
/// Clients send only their own player's actions; the host relays them and sends its own within
/// <see cref="InterestRadius"/> of a client. Applying never re-sends.
/// </summary>
public static class WorldSync
{
    const float InterestRadius = 60f;
    const float WeatherResend = 10f;
    const byte NoPlayer = 255;

    [ThreadStatic] static bool _applying;
    public static int Sent { get; private set; }
    public static int Applied { get; private set; }

    static NetSession Session => NetSession.Current;
    static bool InSession => Session != null && World.Ready;
    static bool IsHost => Session?.Role == NetSession.SessionRole.Host;
    static bool IsClient => Session?.Role == NetSession.SessionRole.Client;
    static bool IsLocal(Actor a) => a != null && Player.Instance != null && a.Pointer == Player.Instance.Pointer;

    // --- indexes (reset when the world reloads)

    static Dictionary<IntPtr, int> _doorKey;
    static Dictionary<int, NewBuilding> _buildings;
    static Dictionary<int, Interactable> _interactables;

    static WorldSync() => World.Reloaded += () => { _doorKey = null; _buildings = null; _interactables = null; _lastWeather = default; _isBreaker.Clear(); };

    static bool TryDoorKey(NewDoor door, out int key)
    {
        key = 0;
        if (door == null) return false;
        if (_doorKey == null)
        {
            var dict = CityData.Instance?.doorDictionary;
            if (dict == null) return false;
            _doorKey = new Dictionary<IntPtr, int>();
            foreach (var kv in dict) if (kv.Value != null) _doorKey[kv.Value.Pointer] = kv.Key; // same keys as DoorSync
        }
        return _doorKey.TryGetValue(door.Pointer, out key);
    }

    static NewDoor Door(int key) =>
        CityData.Instance?.doorDictionary != null && CityData.Instance.doorDictionary.TryGetValue(key, out var d) ? d : null;

    static NewBuilding Building(int id)
    {
        if (_buildings == null)
        {
            var addresses = CityData.Instance?.addressDictionary;
            if (addresses == null) return null;
            _buildings = new Dictionary<int, NewBuilding>();
            foreach (var kv in addresses)
                if (kv.Value?.building != null) _buildings[kv.Value.building.buildingID] = kv.Value.building;
        }
        return _buildings.TryGetValue(id, out var b) ? b : null;
    }

    internal static Interactable FindInteractable(int id)
    {
        var city = CityData.Instance;
        if (city == null) return null;
        if (city.savableInteractableDictionary != null && city.savableInteractableDictionary.TryGetValue(id, out var it) && it != null) return it;
        if (_interactables == null && city.interactableDirectory != null)
        {
            _interactables = new Dictionary<int, Interactable>();
            for (int i = 0; i < city.interactableDirectory.Count; i++)
            {
                var x = city.interactableDirectory[i];
                if (x != null) _interactables[x.id] = x;
            }
        }
        return _interactables != null && _interactables.TryGetValue(id, out var y) ? y : null;
    }

    /// <summary>Is <paramref name="pos"/> within the interest radius of any remote player's body (host side)?</summary>
    static bool NearRemotePlayer(Vector3 pos)
    {
        foreach (var p in PlayerRegistry.All)
        {
            var h = p.IsLocal ? null : p.RemoteBody?.Human;
            if (h != null && !h.WasCollected && (h.transform.position - pos).sqrMagnitude < InterestRadius * InterestRadius) return true;
        }
        return false;
    }

    static bool NearLocalPlayer(Vector3 pos) =>
        Player.Instance != null && (Player.Instance.transform.position - pos).sqrMagnitude < InterestRadius * InterestRadius;

    static byte PlayerIdOf(Actor a)
    {
        var owner = PlayerRegistry.OwnerOf(a);
        return owner != null ? owner.Id.Value : NoPlayer;
    }

    static Actor ActorOf(byte playerId, int humanId)
    {
        if (playerId != NoPlayer) return PlayerRegistry.TryGet(new PlayerId(playerId), out var p) ? p.Body : null;
        return humanId >= 0 && CityData.Instance?.citizenDictionary != null && CityData.Instance.citizenDictionary.TryGetValue(humanId, out var h) ? h : null;
    }

    /// <summary>
    /// Send: a client only reports its own player's actions (to the host); the host sends its changes
    /// (and relays clients') to everyone.
    /// </summary>
    static void Send(WorldEventMsg m)
    {
        if (Session == null) return;
        Session.SendWorld(WorldProtocol.Write(m));
        Sent++;
    }

    // ------------------------------------------------------------------ receive

    /// <summary>Both roles. The host relays a client's event before calling this (NetSession).</summary>
    public static void Apply(WorldEventMsg m)
    {
        if (!World.Ready) return;
        _applying = true;
        try
        {
            switch (m.Kind)
            {
                case WorldEventKind.Weather: ApplyWeather(m); break;
                case WorldEventKind.Window: ApplyWindow(m); break;
                case WorldEventKind.DoorJam:
                {
                    var door = Door(m.I0);
                    if (door == null || door.isJammed == m.B0) break;
                    // createUsedWedge: false — the wedge item (if any) is synced by SpawnSync/ItemSync from the sender.
                    door.SetJammed(m.B0, m.I1 >= 0 ? FindInteractable(m.I1) : null, false);
                    break;
                }
                case WorldEventKind.DoorTape:
                {
                    var door = Door(m.I0);
                    if (door != null && door.policeTapeSpawned != m.B0) door.SetPoliceTape(m.B0);
                    break;
                }
                case WorldEventKind.DoorBarge:
                    Door(m.I0)?.Barge(ActorOf(m.ByPlayer, m.I1)); // same door damage, sound and NPC reaction here
                    break;
                case WorldEventKind.Switch:
                {
                    var it = FindInteractable(m.I0);
                    if (it == null) break;
                    it.SetSwtichByType((InteractablePreset.Switch)m.I1, m.B0, null, true, false);
                    break;
                }
                case WorldEventKind.Alarm: ApplyAlarm(m); break;
                case WorldEventKind.AlarmRequest when IsHost:
                    _applying = false; // the host raises it for real: its postfix broadcasts the state to everyone
                    RaiseForClient(m);
                    break;
                case WorldEventKind.Breaker:
                {
                    var it = FindInteractable(m.I0);
                    if (it != null && Mathf.Abs(it.GetExtraStateValue() - m.F0) > 0.001f) it.SetExtraStateValue(m.F0, true);
                    break;
                }
            }
            Applied++;
        }
        catch (Exception e) { Plugin.Event($"WORLD {m.Kind} failed: {e.Message}"); }
        finally { _applying = false; }
    }

    // ------------------------------------------------------------------ weather (item 10)

    static (float rain, float wind, float snow, float lightning, float fog) _lastWeather;
    static float _nextWeather;

    /// <summary>Host, per frame: resend the current weather target now and then (late joiners, lost packets).</summary>
    public static void HostTick(float now)
    {
        if (IsHost && World.Ready && now >= _nextWindows) { _nextWindows = now + WindowScan; ScanBrokenWindows(); }
        if (!IsHost || !World.Ready || now < _nextWeather) return;
        _nextWeather = now + WeatherResend;
        var sd = SessionData.Instance;
        if (sd != null) SendWeather(sd.desiredRain, sd.desiredWind, sd.desiredSnow, sd.desiredLightning, sd.desiredFog, 0.1f);
    }

    static void SendWeather(float rain, float wind, float snow, float lightning, float fog, float speed)
    {
        _lastWeather = (rain, wind, snow, lightning, fog);
        Send(new WorldEventMsg { Kind = WorldEventKind.Weather, ByPlayer = NoPlayer, F0 = rain, F1 = wind, F2 = snow, F3 = lightning, F4 = fog, F5 = speed });
    }

    static void ApplyWeather(WorldEventMsg m)
    {
        var sd = SessionData.Instance;
        if (sd == null || !IsClient) return;
        if (Mathf.Abs(sd.desiredRain - m.F0) < 0.001f && Mathf.Abs(sd.desiredWind - m.F1) < 0.001f && Mathf.Abs(sd.desiredSnow - m.F2) < 0.001f &&
            Mathf.Abs(sd.desiredLightning - m.F3) < 0.001f && Mathf.Abs(sd.desiredFog - m.F4) < 0.001f) return;
        sd.SetWeather(m.F0, m.F1, m.F2, m.F3, m.F4, m.F5, false);
        Plugin.Event($"WORLD weather from host: rain {m.F0:F2} wind {m.F1:F2} snow {m.F2:F2} lightning {m.F3:F2} fog {m.F4:F2}");
    }

    [HarmonyPatch(typeof(SessionData), nameof(SessionData.SetWeather))]
    static class Weather
    {
        // Clients: only the host's weather (their own SessionData.Update keeps rolling, but its result is dropped).
        static bool Prefix() => _applying || !IsClient || !World.Ready;

        static void Postfix(float newRain, float newWind, float newSnow, float newLightning, float newFog, float newTransitionSpeed)
        {
            if (!IsHost || !World.Ready || _lastWeather == (newRain, newWind, newSnow, newLightning, newFog)) return;
            SendWeather(newRain, newWind, newSnow, newLightning, newFog, newTransitionSpeed);
        }
    }

    // ------------------------------------------------------------------ switches by citizens (item 3)

    static bool IsDoor(Interactable it) => it.objectRef != null && it.objectRef.TryCast<NewDoor>() != null;

    /// <summary>A switch change by a citizen or a timer (not a player: those are ItemSync's).</summary>
    static bool NonPlayer(Actor interactor) => interactor == null || !interactor.isPlayer;

    static bool SwitchPrefix(Interactable it, Actor interactor)
    {
        // Client: our local citizens don't flip switches near our player; the host's state arrives instead.
        if (_applying || !IsClient || !World.Ready || it == null || interactor == null || interactor.isPlayer || IsDoor(it)) return true;
        return !NearLocalPlayer(it.wPos);
    }

    static void SwitchPostfix(Interactable it, InteractablePreset.Switch which, bool val, Actor interactor)
    {
        if (_applying || !IsHost || !World.Ready || it == null || !NonPlayer(interactor) || IsDoor(it) || !NearRemotePlayer(it.wPos)) return;
        Send(new WorldEventMsg { Kind = WorldEventKind.Switch, ByPlayer = NoPlayer, I0 = it.id, I1 = (int)which, B0 = val });
    }

    [HarmonyPatch(typeof(Interactable), nameof(Interactable.SetSwitchState))]
    static class SwMain
    {
        static bool Prefix(Interactable __instance, Actor interactor) => SwitchPrefix(__instance, interactor);
        static void Postfix(Interactable __instance, bool val, Actor interactor) => SwitchPostfix(__instance, InteractablePreset.Switch.switchState, val, interactor);
    }

    [HarmonyPatch(typeof(Interactable), nameof(Interactable.SetCustomState1))]
    static class SwC1
    {
        static bool Prefix(Interactable __instance, Actor interactor) => SwitchPrefix(__instance, interactor);
        static void Postfix(Interactable __instance, bool val, Actor interactor) => SwitchPostfix(__instance, InteractablePreset.Switch.custom1, val, interactor);
    }

    [HarmonyPatch(typeof(Interactable), nameof(Interactable.SetCustomState2))]
    static class SwC2
    {
        static bool Prefix(Interactable __instance, Actor interactor) => SwitchPrefix(__instance, interactor);
        static void Postfix(Interactable __instance, bool val, Actor interactor) => SwitchPostfix(__instance, InteractablePreset.Switch.custom2, val, interactor);
    }

    [HarmonyPatch(typeof(Interactable), nameof(Interactable.SetCustomState3))]
    static class SwC3
    {
        static bool Prefix(Interactable __instance, Actor interactor) => SwitchPrefix(__instance, interactor);
        static void Postfix(Interactable __instance, bool val, Actor interactor) => SwitchPostfix(__instance, InteractablePreset.Switch.custom3, val, interactor);
    }

    [HarmonyPatch(typeof(Interactable), nameof(Interactable.SetLockedState))]
    static class SwLock
    {
        static bool Prefix(Interactable __instance, Actor interactor) => SwitchPrefix(__instance, interactor);
        static void Postfix(Interactable __instance, bool val, Actor interactor) => SwitchPostfix(__instance, InteractablePreset.Switch.lockState, val, interactor);
    }

    // ------------------------------------------------------------------ breakers (item 6)

    /// <summary>
    /// A breaker's state is its interactable's extra state value (BreakerController.SetOpen reads GetExtraStateValue).
    /// Reported by whoever changes it on a loaded breaker; no actor is passed, so a client reports changes near its
    /// player and the host changes near a client.
    /// </summary>
    /// <summary>interactable id → is a breaker (SetExtraStateValue may be frequent; look the component up once).</summary>
    static readonly Dictionary<int, bool> _isBreaker = new();

    [HarmonyPatch(typeof(Interactable), nameof(Interactable.SetExtraStateValue))]
    static class Breaker
    {
        static void Postfix(Interactable __instance, float newValue)
        {
            if (_applying || !InSession || __instance?.controller == null) return;
            if (!_isBreaker.TryGetValue(__instance.id, out var isBreaker))
                _isBreaker[__instance.id] = isBreaker = __instance.controller.GetComponentInChildren<BreakerController>() != null;
            if (!isBreaker) return;
            var pos = __instance.wPos;
            if (IsClient ? !NearLocalPlayer(pos) : !(NearRemotePlayer(pos) || NearLocalPlayer(pos))) return;
            Send(new WorldEventMsg { Kind = WorldEventKind.Breaker, ByPlayer = NoPlayer, I0 = __instance.id, F0 = newValue });
        }
    }

    // ------------------------------------------------------------------ alarms, cameras, sentries (item 6)

    static void SendAlarm(bool isAddress, int id, bool active, NewBuilding.AlarmTargetMode mode, float timer, int floor)
    {
        if (_applying || !IsHost || !World.Ready) return;
        Send(new WorldEventMsg { Kind = WorldEventKind.Alarm, ByPlayer = NoPlayer, B1 = isAddress, I0 = id, B0 = active, I1 = (int)mode, F0 = timer, I2 = floor });
    }

    static void ApplyAlarm(WorldEventMsg m)
    {
        var mode = (NewBuilding.AlarmTargetMode)m.I1;
        if (m.B1)
        {
            if (CityData.Instance?.addressDictionary == null || !CityData.Instance.addressDictionary.TryGetValue(m.I0, out var a) || a == null) return;
            if (a.targetMode != mode) a.SetTargetMode(mode, true);
            if (a.alarmActive != m.B0) a.SetAlarm(m.B0, null);
            a.alarmTimer = m.F0;
        }
        else
        {
            var b = Building(m.I0);
            if (b == null) return;
            if (b.targetMode != mode) b.SetTargetMode(mode, true);
            NewFloor floor = null;
            if (m.I2 != -999 && b.floors != null) b.floors.TryGetValue(m.I2, out floor);
            if (b.alarmActive != m.B0) b.SetAlarm(m.B0, null, floor);
            b.alarmTimer = m.F0;
        }
        Plugin.Event($"WORLD alarm {(m.B1 ? "address" : "building")} {m.I0}: {(m.B0 ? "ON" : "off")} mode={mode}");
    }

    /// <summary>Clients don't raise alarms or change security targets themselves: the host's systems decide.</summary>
    static bool HostDecides() => _applying || !IsClient || !World.Ready;

    /// <summary>
    /// Client: an alarm the game raises because of OUR player (a camera saw us, we broke in) is asked of the host,
    /// which raises it with our body as the target. Before, clients' alarms were simply dropped and the host's own
    /// systems never noticed the client ("only the host can trigger alarms").
    /// </summary>
    static bool ForwardOwnAlarm(bool newVal, Human target, bool isAddress, int id, int floor, Func<bool> playerIsThere)
    {
        if (!newVal || _applying || !IsClient || !World.Ready) return false;
        var me = Player.Instance;
        if (me == null) return false;
        bool ours = target != null ? target.Pointer == me.Pointer : playerIsThere();
        if (!ours) return false;
        Session?.SendWorld(WorldProtocol.Write(new WorldEventMsg
        {
            Kind = WorldEventKind.AlarmRequest, ByPlayer = PlayerRegistry.Local?.Id.Value ?? NoPlayer, B1 = isAddress, I0 = id, I2 = floor,
        }));
        Sent++;
        Plugin.Event($"WORLD our player set off the {(isAddress ? "address" : "building")} {id} alarm: asking the host to raise it");
        return true;
    }

    /// <summary>Host: raise a client's alarm with that client's body as the target.</summary>
    static void RaiseForClient(WorldEventMsg m)
    {
        var body = PlayerRegistry.TryGet(new PlayerId(m.ByPlayer), out var p) ? p.Body : null;
        if (m.B1)
        {
            if (CityData.Instance?.addressDictionary != null && CityData.Instance.addressDictionary.TryGetValue(m.I0, out var a) && a != null && !a.alarmActive)
                a.SetAlarm(true, body);
        }
        else
        {
            var b = Building(m.I0);
            if (b == null || b.alarmActive) return;
            NewFloor floor = null;
            if (m.I2 != -999 && b.floors != null) b.floors.TryGetValue(m.I2, out floor);
            b.SetAlarm(true, body, floor);
        }
        Plugin.Event($"WORLD P{m.ByPlayer} set off the {(m.B1 ? "address" : "building")} {m.I0} alarm: raised here");
    }

    [HarmonyPatch(typeof(NewBuilding), nameof(NewBuilding.SetAlarm))]
    static class BuildingAlarm
    {
        static bool Prefix(NewBuilding __instance, bool newVal, Human target, NewFloor forFloor)
        {
            var me = Player.Instance;
            if (ForwardOwnAlarm(newVal, target, false, __instance.buildingID, forFloor != null ? forFloor.floor : -999,
                    () => me != null && me.currentBuilding != null && me.currentBuilding.Pointer == __instance.Pointer)) return false;
            return HostDecides();
        }
        static void Postfix(NewBuilding __instance, NewFloor forFloor) =>
            SendAlarm(false, __instance.buildingID, __instance.alarmActive, __instance.targetMode, __instance.alarmTimer, forFloor != null ? forFloor.floor : -999);
    }

    [HarmonyPatch(typeof(NewBuilding), nameof(NewBuilding.SetTargetMode))]
    static class BuildingMode
    {
        static bool Prefix() => HostDecides();
        static void Postfix(NewBuilding __instance) =>
            SendAlarm(false, __instance.buildingID, __instance.alarmActive, __instance.targetMode, __instance.alarmTimer, -999);
    }

    [HarmonyPatch(typeof(NewAddress), nameof(NewAddress.SetAlarm))]
    static class AddressAlarm
    {
        static bool Prefix(NewAddress __instance, bool newVal, Human target)
        {
            var me = Player.Instance;
            if (ForwardOwnAlarm(newVal, target, true, __instance.id, -999,
                    () => me != null && me.currentGameLocation != null && me.currentGameLocation.Pointer == __instance.Pointer)) return false;
            return HostDecides();
        }
        static void Postfix(NewAddress __instance) =>
            SendAlarm(true, __instance.id, __instance.alarmActive, __instance.targetMode, __instance.alarmTimer, -999);
    }

    [HarmonyPatch(typeof(NewAddress), nameof(NewAddress.SetTargetMode))]
    static class AddressMode
    {
        static bool Prefix() => HostDecides();
        static void Postfix(NewAddress __instance) =>
            SendAlarm(true, __instance.id, __instance.alarmActive, __instance.targetMode, __instance.alarmTimer, -999);
    }

    /// <summary>
    /// Client: cameras and sentries don't investigate (acquire targets) locally. The host's do, and they see the
    /// client's body; the resulting alarm comes over as <see cref="WorldEventKind.Alarm"/>, sentry hits as forwarded damage.
    /// </summary>
    [HarmonyPatch(typeof(SecuritySystem), nameof(SecuritySystem.OnInvestigate))]
    static class Cameras
    {
        // A client's cameras may react to that client's own player (the host's never see it reliably); the alarm they
        // raise goes to the host through ForwardOwnAlarm. Everything else stays the host's.
        static bool Prefix(Actor newTarget) =>
            HostDecides() || (newTarget != null && Player.Instance != null && newTarget.Pointer == Player.Instance.Pointer);
    }

    // ------------------------------------------------------------------ windows (item 11)

    // Host: every broken window goes to clients, wherever it is. Seen: a window shot out in a murder was broken on the
    // host only: BreakWindow was only sent near a client, and a break where nothing is loaded may never call
    // BreakWindow at all (the game just records it in GameplayController.brokenWindows, the list the save keeps).
    // So the host also watches that list and sends new entries. Clients that join later get the list in the save.
    const float WindowScan = 2f;
    static float _nextWindows;
    static readonly HashSet<Vector3> _knownBroken = new();
    static int _knownGeneration = -1;

    static void ScanBrokenWindows()
    {
        var broken = GameplayController.Instance?.brokenWindows;
        if (broken == null) return;
        bool seed = _knownGeneration != World.Generation;
        if (seed) { _knownBroken.Clear(); _knownGeneration = World.Generation; }
        int sent = 0;
        foreach (var kv in broken)
        {
            if (!_knownBroken.Add(kv.Key) || seed) continue; // first scan of a world: those are in every client's save
            Send(new WorldEventMsg { Kind = WorldEventKind.Window, ByPlayer = NoPlayer, V0 = kv.Key, V1 = kv.Key, V2 = Vector3.zero, F0 = kv.Value });
            sent++;
        }
        if (sent > 0) Plugin.Event($"WORLD {sent} newly broken window(s) sent to clients");
    }

    [HarmonyPatch(typeof(BreakableWindowController), nameof(BreakableWindowController.BreakWindow))]
    static class WindowBroken
    {
        static void Postfix(BreakableWindowController __instance, Vector3 contactPosition, Vector3 relativeVelocity, Actor brokenBy)
        {
            if (_applying || !InSession) return;
            var key = __instance.GetRoughPosition();
            bool mine = IsLocal(brokenBy);
            // Host: every break (citizens' too, e.g. a murderer's shot), not just near a client. Client: its own breaks.
            if (IsClient && !mine) return;
            if (IsHost) _knownBroken.Add(key);
            Send(new WorldEventMsg
            {
                Kind = WorldEventKind.Window, ByPlayer = PlayerIdOf(brokenBy),
                V0 = key, V1 = contactPosition, V2 = relativeVelocity, F0 = __instance.brokenAt,
            });
        }
    }

    /// <summary>
    /// Not BreakWindow on the receiver: it records vandalism and fines against the breaker, which belong to the
    /// sender's machine. SetBroken + Shatter give the state and the effect; an unloaded window gets its entry in
    /// GameplayController.brokenWindows (as the save does) and loads broken.
    /// </summary>
    static void ApplyWindow(WorldEventMsg m)
    {
        BreakableWindowController window = null;
        foreach (var w in UnityEngine.Object.FindObjectsOfType<BreakableWindowController>())
            if (w != null && (w.GetRoughPosition() - m.V0).sqrMagnitude < 0.01f) { window = w; break; }
        if (IsHost) _knownBroken.Add(m.V0);
        if (window != null && !window.isBroken)
        {
            window.SetBroken(true); // the game records it in brokenWindows itself
            window.Shatter(m.V1, m.V2);
            Plugin.Event($"WORLD window at {m.V0} broken (from {(m.ByPlayer == NoPlayer ? "the host's world" : $"P{m.ByPlayer}")})");
        }
        var broken = GameplayController.Instance?.brokenWindows;
        if (broken != null && !broken.ContainsKey(m.V0))
        {
            broken.Add(m.V0, m.F0 > 0f ? m.F0 : SessionData.Instance != null ? SessionData.Instance.gameTime : 0f);
            if (window == null) Plugin.Event($"WORLD window at {m.V0} recorded broken (not loaded here; it loads broken)");
        }
    }

    // ------------------------------------------------------------------ doors: wedges, tape, barging (item 17)

    static void DoorEvent(NewDoor door, WorldEventKind kind, bool val, int extra, Actor actor)
    {
        if (_applying || !InSession || !TryDoorKey(door, out var key)) return;
        bool mine = actor == null || IsLocal(actor); // jam/tape have no actor: whoever's machine did it reports it
        if (IsClient ? !(mine && NearLocalPlayer(door.transform.position)) : !(mine || NearRemotePlayer(door.transform.position))) return;
        var by = actor != null ? PlayerIdOf(actor) : NoPlayer;
        var h = actor?.TryCast<Human>();
        Send(new WorldEventMsg { Kind = kind, ByPlayer = by, I0 = key, B0 = val, I1 = kind == WorldEventKind.DoorBarge ? (h != null ? h.humanID : -1) : extra });
    }

    [HarmonyPatch(typeof(NewDoor), nameof(NewDoor.SetJammed))]
    static class Jam
    {
        static void Postfix(NewDoor __instance, bool val, Interactable doorWedgeUsed) =>
            DoorEvent(__instance, WorldEventKind.DoorJam, val, doorWedgeUsed != null ? doorWedgeUsed.id : -1, null);
    }

    [HarmonyPatch(typeof(NewDoor), nameof(NewDoor.SetPoliceTape))]
    static class Tape
    {
        static void Postfix(NewDoor __instance, bool policeTapActive) => DoorEvent(__instance, WorldEventKind.DoorTape, policeTapActive, 0, null);
    }

    [HarmonyPatch(typeof(NewDoor), nameof(NewDoor.Barge))]
    static class Barge
    {
        static void Postfix(NewDoor __instance, Actor barger)
        {
            // Citizens barging on a client are its own local AI: only players there; the host sends citizens too.
            if (IsClient && !IsLocal(barger)) return;
            DoorEvent(__instance, WorldEventKind.DoorBarge, true, 0, barger);
        }
    }
}
