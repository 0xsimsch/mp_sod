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
/// </summary>
public static class MapSync
{
    const byte KindBlueprint = 1; // int buildingId, int level

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

    public static void OnPacket(NetSession s, PeerId from, PlayerId? fromPlayer, byte[] data)
    {
        var r = ExtraWire.Read(data, out var kind);
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
