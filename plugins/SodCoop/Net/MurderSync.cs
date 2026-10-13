using System;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace SodCoop.Net;

/// <summary>
/// Host-authoritative murders: the host's <c>MurderController.Murder</c> records (the murder director's
/// state: victim, murderer, MO, weapon, phase…) go to clients in the game's own serialized form
/// (<c>[Serializable]</c> + <c>JsonUtility</c>, as in save files). The client rebuilds them with
/// <c>LoadSerializedData</c>, which is what loading a save does, so a later <see cref="DeathSync"/> can run
/// the full <c>Human.Murder</c> (it needs the record).
/// Sent: all active murders when a client is welcomed, and each record again when its state changes.
/// </summary>
public static class MurderSync
{
    public static int Sent { get; private set; }
    public static int Applied { get; private set; }

    public static string Serialize(MurderController.Murder m) => JsonUtility.ToJson(m);

    /// <summary>Host: every active murder, for a newly welcomed client.</summary>
    public static void SendAll(Action<string> send)
    {
        var murders = MurderController.Instance?.activeMurders;
        if (murders == null) return;
        for (int i = 0; i < murders.Count; i++)
            if (murders[i] != null) { send(Serialize(murders[i])); Sent++; }
    }

    [HarmonyPatch(typeof(MurderController.Murder), nameof(MurderController.Murder.SetMurderState))]
    static class StateChange
    {
        static void Postfix(MurderController.Murder __instance)
        {
            var s = NetSession.Current;
            if (s == null || s.Role != NetSession.SessionRole.Host || !Core.World.Ready) return;
            try { s.SendMurderRecord(Serialize(__instance)); Sent++; }
            catch (Exception e) { Plugin.Event($"murder sync failed: {e.Message}"); }
        }
    }

    public static void Apply(string json)
    {
        var mc = MurderController.Instance;
        // Before the host's world is loaded (welcome arrives first) there is nothing to attach to; the snapshot carries them.
        if (mc?.activeMurders == null || !Core.World.Ready) return;
        try
        {
            // Unity's serializer builds the object itself (no gameplay constructor), like a save load.
            // (FromJsonOverwrite fails through the interop: missing Il2CppSystem.Object.GetType.)
            var obj = JsonUtility.FromJson(json, Il2CppType.Of<MurderController.Murder>());
            var m = obj?.TryCast<MurderController.Murder>();
            if (m == null) { Plugin.Event("MURDER record: JsonUtility returned nothing"); return; }

            for (int i = mc.activeMurders.Count - 1; i >= 0; i--)
                if (mc.activeMurders[i] != null && mc.activeMurders[i].murderID == m.murderID) mc.activeMurders.RemoveAt(i);
            m.LoadSerializedData();
            mc.activeMurders.Add(m);
            Applied++;
            Plugin.Event($"MURDER record {m.murderID} applied: state={m.state} victim={m.victimID} murderer={m.murdererID} preset={m.presetStr} ({json.Length} chars)");
        }
        catch (Exception e) { Plugin.Event($"MURDER record failed: {e.Message}"); }
    }
}
