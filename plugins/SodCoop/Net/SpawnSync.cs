using System;
using HarmonyLib;
using SodCoop.Core;
using UnityEngine;

namespace SodCoop.Net;

/// <summary>
/// B2 fix, first part: world interactables created at runtime (<c>InteractableCreator.CreateWorldInteractable</c>)
/// are replayed on the other machines with the **same id**.
/// Ids: the receiver forces the creator's id through a prefix on <c>Interactable.AssignIDWorld</c>.
/// The host uses the game's own counter; a client allocates its own creations from a disjoint range
/// (0x40000000 + playerId &lt;&lt; 24), so two machines can never hand out the same id.
/// Not yet: <c>passedObject</c> (an arbitrary object, logged when present), moving/picking up/deleting.
/// </summary>
public static class SpawnSync
{
    const int ClientRangeBase = 0x40000000;

    [ThreadStatic] static bool _applying;
    static int _forcedId = -1;
    static int _savedCounter;

    public static int Sent { get; private set; }
    public static int Applied { get; private set; }
    public static int Failed { get; private set; }

    [HarmonyPatch(typeof(Interactable), nameof(Interactable.AssignIDWorld))]
    static class Ids
    {
        static void Prefix()
        {
            _savedCounter = Interactable.worldAssignID;
            if (_forcedId >= 0) { Interactable.worldAssignID = _forcedId; return; }
            var s = NetSession.Current;
            if (s != null && s.Role == NetSession.SessionRole.Client && PlayerRegistry.Local != null)
            {
                int bottom = ClientRangeBase + (PlayerRegistry.Local.Id.Value << 24);
                if (Interactable.worldAssignID < bottom) Interactable.worldAssignID = bottom;
            }
        }

        static void Postfix(Interactable __instance)
        {
            if (_forcedId < 0) return;
            if (__instance.id != _forcedId) Plugin.Event($"SPAWN id {_forcedId} was taken here, got {__instance.id}");
            _forcedId = -1; // single use: nested creations inside the same spawn get normal ids
            Interactable.worldAssignID = _savedCounter; // don't move our own counter into the other machine's range
        }
    }

    [HarmonyPatch(typeof(InteractableCreator), nameof(InteractableCreator.CreateWorldInteractable))]
    static class Create
    {
        static void Postfix(Interactable __result, InteractablePreset preset, Human belongsTo, Human writer, Human recevier,
            Vector3 worldPos, Vector3 worldEuler, Il2CppSystem.Collections.Generic.List<Interactable.Passed> passedVars,
            Il2CppSystem.Object passedObject, string ddsOverride)
        {
            var s = NetSession.Current;
            if (s == null || _applying || __result == null || preset == null || PlayerRegistry.Local == null || !World.Ready) return;
            if (!ItemSync.ClientMayReport(worldPos, belongsTo)) return; // a client's own citizens don't spawn into the shared world
            try
            {
                var passed = new PassedVar[passedVars?.Count ?? 0];
                for (int i = 0; i < passed.Length; i++)
                    passed[i] = new PassedVar { Type = (byte)passedVars[i].varType, Value = passedVars[i].value, Str = passedVars[i].str ?? "" };
                if (passedObject != null) Plugin.Event($"SPAWN {preset.name} #{__result.id}: passedObject {passedObject.GetIl2CppType().Name} not synced");
                s.SendSpawn(new SpawnMsg
                {
                    ByPlayer = PlayerRegistry.Local.Id.Value,
                    Id = __result.id,
                    Preset = preset.name,
                    BelongsTo = belongsTo != null ? belongsTo.humanID : -1,
                    Writer = writer != null ? writer.humanID : -1,
                    Receiver = recevier != null ? recevier.humanID : -1,
                    Position = new Vec3(worldPos.x, worldPos.y, worldPos.z),
                    Euler = new Vec3(worldEuler.x, worldEuler.y, worldEuler.z),
                    DdsOverride = ddsOverride ?? "",
                    Passed = passed,
                });
                Sent++;
            }
            catch (Exception e) { Plugin.Event($"spawn sync failed: {e.Message}"); }
        }
    }

    /// <summary>Run a creator so the world interactable it makes gets exactly <paramref name="id"/>.</summary>
    public static Interactable WithForcedId(int id, Func<Interactable> create)
    {
        _forcedId = id;
        int counterBefore = Interactable.worldAssignID;
        try { return create(); }
        finally
        {
            if (_forcedId >= 0) Interactable.worldAssignID = counterBefore; // AssignIDWorld never ran or threw
            _forcedId = -1;
        }
    }

    public static void Apply(SpawnMsg m)
    {
        if (!World.Ready) return;
        var dictInt = CityData.Instance?.savableInteractableDictionary;
        if (dictInt != null && dictInt.ContainsKey(m.Id)) return; // already have it
        var preset = Toolbox.Instance?.GetInteractablePreset(m.Preset);
        if (preset == null) { Failed++; Plugin.Event($"SPAWN unknown preset '{m.Preset}'"); return; }

        var passed = new Il2CppSystem.Collections.Generic.List<Interactable.Passed>();
        foreach (var p in m.Passed ?? Array.Empty<PassedVar>())
            passed.Add(new Interactable.Passed((Interactable.PassedVarType)p.Type, p.Value, string.IsNullOrEmpty(p.Str) ? null : p.Str));

        _applying = true;
        _forcedId = m.Id;
        int counterBefore = Interactable.worldAssignID;
        using var quiet = ItemSync.Suppress(); // the spawn's own moves/inventory changes aren't new events
        try
        {
            var it = InteractableCreator.Instance.CreateWorldInteractable(preset, HumanById(m.BelongsTo), HumanById(m.Writer), HumanById(m.Receiver),
                new Vector3(m.Position.X, m.Position.Y, m.Position.Z), new Vector3(m.Euler.X, m.Euler.Y, m.Euler.Z),
                passed, null, string.IsNullOrEmpty(m.DdsOverride) ? "" : m.DdsOverride);
            if (it != null) Applied++; else Failed++;
            Plugin.Event($"SPAWN applied {m.Preset} #{m.Id} from P{m.ByPlayer} -> {(it != null ? $"#{it.id}" : "null")}");
        }
        catch (Exception e) { Failed++; Plugin.Event($"SPAWN {m.Preset} #{m.Id} failed: {e.Message}"); }
        finally
        {
            if (_forcedId >= 0) Interactable.worldAssignID = counterBefore; // AssignIDWorld never ran or threw
            _forcedId = -1;
            _applying = false;
        }
    }

    /// <summary>Debug: a few preset names to test with.</summary>
    public static string SamplePresets()
    {
        var dict = Toolbox.Instance?.objectPresetDictionary;
        if (dict == null) return "";
        var names = new System.Collections.Generic.List<string>();
        foreach (var kv in dict)
        {
            var k = kv.Key.ToLowerInvariant();
            if (k.Contains("coin") || k.Contains("briefcase") || k.Contains("bottle") || k.Contains("newspaper")) names.Add(kv.Key);
            if (names.Count >= 8) break;
        }
        return string.Join(",", names);
    }

    static Human HumanById(int id)
    {
        if (id < 0) return null;
        Human h = null;
        CityData.Instance?.citizenDictionary?.TryGetValue(id, out h);
        return h;
    }
}
