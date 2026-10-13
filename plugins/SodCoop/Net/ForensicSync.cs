using System;
using HarmonyLib;
using Il2CppInterop.Runtime;
using SodCoop.Core;
using UnityEngine;

namespace SodCoop.Net;

/// <summary>
/// Fingerprints and footprints become interactables when a player reveals them
/// (<c>PrintController</c>/<c>BioScreenController</c>/<c>FootprintController.PrintConfirmed</c>), through their own
/// creators rather than <c>CreateWorldInteractable</c>. They're replayed the same way as spawns: same world id
/// (forced), print data in the game's serialized form (both classes are <c>[Serializable]</c>).
/// </summary>
public static class ForensicSync
{
    [ThreadStatic] static bool _applying;
    public static int Sent { get; private set; }
    public static int Applied { get; private set; }

    static void Report(byte kind, Interactable result, Human belongsTo, Vector3 pos, Vector3 euler, Il2CppSystem.Object print)
    {
        var s = NetSession.Current;
        if (s == null || _applying || result == null || print == null || PlayerRegistry.Local == null || !World.Ready) return;
        if (!ItemSync.ClientMayReport(pos)) return;
        try
        {
            s.SendForensic(new ForensicMsg
            {
                ByPlayer = PlayerRegistry.Local.Id.Value,
                Kind = kind,
                Id = result.id,
                BelongsTo = DeathSync.EncodeKiller(belongsTo),
                Position = new Vec3(pos.x, pos.y, pos.z),
                Euler = new Vec3(euler.x, euler.y, euler.z),
                Json = JsonUtility.ToJson(print),
            });
            Sent++;
        }
        catch (Exception e) { Plugin.Event($"forensic sync failed: {e.Message}"); }
    }

    [HarmonyPatch(typeof(InteractableCreator), nameof(InteractableCreator.CreateFingerprintInteractable))]
    static class Finger
    {
        static void Postfix(Interactable __result, Human belongsTo, Vector3 worldPos, Vector3 worldEuler, FingerprintScannerController.Print print)
            => Report(1, __result, belongsTo, worldPos, worldEuler, print);
    }

    [HarmonyPatch(typeof(InteractableCreator), nameof(InteractableCreator.CreateFootprintInteractable))]
    static class Foot
    {
        static void Postfix(Interactable __result, Human belongsTo, Vector3 worldPos, Vector3 worldEuler, GameplayController.Footprint print)
            => Report(2, __result, belongsTo, worldPos, worldEuler, print);
    }

    public static void Apply(ForensicMsg m)
    {
        if (!World.Ready) return;
        var dict = CityData.Instance?.savableInteractableDictionary;
        if (dict != null && dict.ContainsKey(m.Id)) return;
        var owner = DeathSync.DecodeKiller(m.BelongsTo);
        var pos = new Vector3(m.Position.X, m.Position.Y, m.Position.Z);
        var euler = new Vector3(m.Euler.X, m.Euler.Y, m.Euler.Z);
        _applying = true;
        try
        {
            using var quiet = ItemSync.Suppress();
            Interactable it;
            if (m.Kind == 1)
            {
                var print = JsonUtility.FromJson(m.Json, Il2CppType.Of<FingerprintScannerController.Print>())?.TryCast<FingerprintScannerController.Print>();
                it = SpawnSync.WithForcedId(m.Id, () => InteractableCreator.Instance.CreateFingerprintInteractable(owner, pos, euler, print));
            }
            else
            {
                var print = JsonUtility.FromJson(m.Json, Il2CppType.Of<GameplayController.Footprint>())?.TryCast<GameplayController.Footprint>();
                it = SpawnSync.WithForcedId(m.Id, () => InteractableCreator.Instance.CreateFootprintInteractable(owner, pos, euler, print));
            }
            if (it != null) Applied++;
            Plugin.Event($"FORENSIC {(m.Kind == 1 ? "fingerprint" : "footprint")} #{m.Id} from P{m.ByPlayer} -> {(it != null ? $"#{it.id}" : "null")}");
        }
        catch (Exception e) { Plugin.Event($"FORENSIC #{m.Id} failed: {e.Message}"); }
        finally { _applying = false; }
    }
}
