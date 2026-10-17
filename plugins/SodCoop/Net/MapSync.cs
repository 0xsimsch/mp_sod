using System;
using HarmonyLib;
using SodCoop.Core;

namespace SodCoop.Net;

/// <summary>
/// Map knowledge from blueprints. <c>ActionController.TakeBlueprints</c> reveals a building's floor plan for the player
/// who takes them: every room of the building gets <c>NewRoom.SetExplorationLevel</c> and every air duct section
/// <c>SetDiscovered(true)</c>; then the blueprint item is deleted (that part is ItemSync's). The reveal only happened on
/// that player's machine. While our player takes blueprints, the rooms the game reveals tell the building and the
/// level; the other machines reveal that building the same way (never lowering a room's level). The host relays.
/// Also shared: keys (keyring entries), see the keys section.
/// </summary>
public static class MapSync
{
    const byte KindBlueprint = 1; // int buildingId, int level
    const byte KindKeyAddress = 2; // int addressId: a key to a whole address (all its doors)
    const byte KindKeyDoor = 3;    // int doorKey (CityData.doorDictionary)
    const byte KindKeyObject = 4;  // int interactable id (a key to a safe, a box…)

    [ThreadStatic] static bool _inTake;
    [ThreadStatic] static bool _applying;
    static NewBuilding _building;
    static int _level = -1;

    public static int Sent { get; private set; }
    public static int Applied { get; private set; }

    static bool IsLocal(Actor a) => a != null && Player.Instance != null && a.Pointer == Player.Instance.Pointer;

    [HarmonyPatch(typeof(ActionController), nameof(ActionController.TakeBlueprints))]
    static class Take
    {
        static void Prefix(Actor who, out bool __state)
        {
            __state = !_applying && NetSession.Current != null && World.Ready && IsLocal(who);
            if (!__state) return;
            _inTake = true;
            _building = null;
            _level = -1;
        }

        static void Postfix(bool __state)
        {
            if (!__state) return;
            _inTake = false;
            if (_building == null || _level < 0) { Plugin.Event("MAP took blueprints, but no room was revealed"); return; }
            int id = _building.buildingID, level = _level;
            NetSession.Current?.SendExtra(ExtraWire.Write(ExtraWire.Map, KindBlueprint, w => { w.Write(id); w.Write(level); }));
            Sent++;
            Plugin.Event($"MAP took the blueprints of building {id} \"{_building.name}\": floor plan sent (level {level})");
        }
    }

    [HarmonyPatch(typeof(NewRoom), nameof(NewRoom.SetExplorationLevel))]
    static class Explore
    {
        static void Postfix(NewRoom __instance, int newLevel)
        {
            if (!_inTake || __instance == null) return;
            _building ??= __instance.building ?? __instance.floor?.building;
            if (newLevel > _level) _level = newLevel;
        }
    }

    // ------------------------------------------------------------------ keys: shared like the case knowledge

    // A key one player picks up (ActionController.TakeKey and others → Player.AddToKeyring) opens the door for the
    // other player too: the keyring entry is sent and added there. The address overload hands out each door of the
    // address again; those inner door calls aren't sent separately.
    [ThreadStatic] static int _keyDepth;

    static bool ReportKey(Player p) => !_applying && _keyDepth == 0 && NetSession.Current != null && World.Ready &&
                                       p != null && Player.Instance != null && p.Pointer == Player.Instance.Pointer;

    static void SendKey(byte kind, int id, string what)
    {
        NetSession.Current?.SendExtra(ExtraWire.Write(ExtraWire.Map, kind, w => w.Write(id)));
        Sent++;
        Plugin.Event($"MAP key to {what} shared");
    }

    [HarmonyPatch(typeof(Player), nameof(Player.AddToKeyring), typeof(NewAddress), typeof(bool))]
    static class KeyAddress
    {
        static void Prefix(Player __instance, out bool __state) { __state = ReportKey(__instance); _keyDepth++; }
        static void Postfix(NewAddress ad, bool __state)
        {
            _keyDepth--;
            if (__state && ad != null) SendKey(KindKeyAddress, ad.id, $"address {ad.id} \"{ad.name}\"");
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.AddToKeyring), typeof(NewDoor), typeof(bool))]
    static class KeyDoor
    {
        static void Postfix(Player __instance, NewDoor ac)
        {
            if (ReportKey(__instance) && WorldSync.TryDoorKey(ac, out var key)) SendKey(KindKeyDoor, key, $"door {key}");
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.AddToKeyring), typeof(Interactable), typeof(bool))]
    static class KeyObject
    {
        static void Postfix(Player __instance, Interactable inter)
        {
            if (ReportKey(__instance) && inter != null) SendKey(KindKeyObject, inter.id, $"object #{inter.id} {inter.name}");
        }
    }

    static void ApplyKey(byte kind, int id, PlayerId? fromPlayer)
    {
        var me = Player.Instance;
        if (me == null) return;
        string who = fromPlayer.HasValue ? fromPlayer.Value.ToString() : "the host";
        _applying = true;
        try
        {
            switch (kind)
            {
                case KindKeyAddress:
                    if (CityData.Instance?.addressDictionary != null && CityData.Instance.addressDictionary.TryGetValue(id, out var ad) && ad != null)
                    { me.AddToKeyring(ad, true); Plugin.Event($"MAP {who} shared a key to address {id} \"{ad.name}\""); }
                    break;
                case KindKeyDoor:
                    if (CityData.Instance?.doorDictionary != null && CityData.Instance.doorDictionary.TryGetValue(id, out var door) && door != null)
                    { me.AddToKeyring(door, true); Plugin.Event($"MAP {who} shared a key to door {id}"); }
                    break;
                case KindKeyObject:
                    var it = WorldSync.FindInteractable(id);
                    if (it != null) { me.AddToKeyring(it, true); Plugin.Event($"MAP {who} shared a key to #{id} {it.name}"); }
                    break;
            }
            Applied++;
        }
        catch (Exception e) { Plugin.Event($"MAP shared key failed: {e.Message}"); }
        finally { _applying = false; }
    }

    public static void OnPacket(NetSession s, PeerId from, PlayerId? fromPlayer, byte[] data)
    {
        var r = ExtraWire.Read(data, out var kind);
        if (kind is KindKeyAddress or KindKeyDoor or KindKeyObject)
        {
            int keyId = r.ReadInt32();
            if (s.Role == NetSession.SessionRole.Host) s.SendExtraExcept(data, from);
            if (World.Ready) ApplyKey(kind, keyId, fromPlayer);
            return;
        }
        if (kind != KindBlueprint) return;
        int id = r.ReadInt32(), level = r.ReadInt32();
        if (s.Role == NetSession.SessionRole.Host) s.SendExtraExcept(data, from);
        if (!World.Ready) return;
        var b = FindBuilding(id);
        if (b == null) { Plugin.Event($"MAP blueprints of building {id}: no such building here"); return; }
        _applying = true;
        int rooms = 0, ducts = 0;
        try
        {
            if (b.floors != null)
                foreach (var f in b.floors)
                {
                    var addresses = f.Value?.addresses;
                    if (addresses == null) continue;
                    for (int a = 0; a < addresses.Count; a++)
                    {
                        var list = addresses[a]?.rooms;
                        if (list == null) continue;
                        for (int i = 0; i < list.Count; i++)
                        {
                            var room = list[i];
                            if (room == null || room.explorationLevel >= level) continue;
                            room.SetExplorationLevel(level);
                            rooms++;
                        }
                    }
                }
            if (b.airDucts != null)
                for (int g = 0; g < b.airDucts.Count; g++)
                {
                    var sections = b.airDucts[g]?.airDucts;
                    if (sections == null) continue;
                    for (int i = 0; i < sections.Count; i++) { sections[i]?.SetDiscovered(true); ducts++; }
                }
            Applied++;
            string who = fromPlayer.HasValue ? fromPlayer.Value.ToString() : "the host";
            Plugin.Event($"MAP {who} took the blueprints of building {id} \"{b.name}\": revealed {rooms} room(s), {ducts} duct section(s)");
        }
        catch (Exception e) { Plugin.Event($"MAP blueprint reveal failed: {e.Message}"); }
        finally { _applying = false; }
    }

    static NewBuilding FindBuilding(int id)
    {
        var list = HighlanderSingleton<CityBuildings>.Instance?.buildingDirectory;
        if (list == null) return null;
        for (int i = 0; i < list.Count; i++) if (list[i] != null && list[i].buildingID == id) return list[i];
        return null;
    }
}
