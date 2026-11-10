using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using SodCoop.Core;
using DataKeyList = Il2CppSystem.Collections.Generic.List<Evidence.DataKey>;

namespace SodCoop.Net;

/// <summary>
/// The player-written parts of the case board, which only exist on the machine where they were made:
/// note text (<c>Evidence.SetNote</c>), sticky notes (evidence "StickyNote&lt;n&gt;" from preset
/// "PlayerStickyNote") and player-drawn strings (<c>FactCustom</c> from preset "CustomLink", optionally renamed).
/// Sent as <see cref="CaseSync"/> events and replayed with the same arguments; the host relays.
/// Sticky note ids come from <c>InterfaceController.assignStickyNoteID</c>, which the client inherits from the
/// host's save, so a client moves its counter into its own range (player id × 1,000,000) before making one.
/// </summary>
public static class NoteSync
{
    public const string StickyNotePrefix = "StickyNote";
    const string StickyNotePreset = "PlayerStickyNote";
    const string CustomLinkPreset = "CustomLink";
    const char Sep = '\u001f';
    const int StickyRange = 1_000_000;

    public static bool Apply(CaseEventMsg m)
    {
        var parts = m.Id.Split(Sep);
        switch (m.Kind)
        {
            case CaseEventKind.StickyNote:
                return FindEvidence(m.Id) != null;
            case CaseEventKind.NoteText when parts.Length == 3:
            {
                var ev = FindEvidence(parts[0]);
                if (ev == null) return false;
                ev.SetNote(Keys(parts[1]), parts[2]);
                return true;
            }
            case CaseEventKind.CustomLink when parts.Length == 4:
            {
                var from = FindEvidence(parts[0]);
                var to = FindEvidence(parts[1]);
                if (from == null || to == null || EvidenceCreator.Instance == null) return false;
                // The same two pins can carry more than one string (different data keys): only an exact copy is skipped.
                if (FindCustomLink(parts[0], parts[1], parts[2], parts[3]) == null)
                    EvidenceCreator.Instance.CreateFact(CustomLinkPreset, from, to, null, null, m.A != 0, null,
                        parts[2].Length > 0 ? Keys(parts[2]) : null, parts[3].Length > 0 ? Keys(parts[3]) : null, true);
                RefreshStrings();
                return true;
            }
            case CaseEventKind.CustomLinkName when parts.Length == 3:
            {
                var fact = FindCustomLink(parts[0], parts[1]);
                if (fact == null) return false;
                fact.SetCustomName(parts[2]);
                RefreshStrings();
                return true;
            }
            case CaseEventKind.CustomLinkRemove when parts.Length == 4:
            {
                // What StringController.RemoveCustomLink does: take the fact off both evidences and off the fact list.
                var fact = FindCustomLink(parts[0], parts[1], parts[2], parts[3]) ?? FindCustomLink(parts[0], parts[1]);
                if (fact == null) return true; // not here (any more): nothing to remove
                try
                {
                    var from = fact.fromEvidence != null && fact.fromEvidence.Count > 0 ? fact.fromEvidence[0] : null;
                    var to = fact.toEvidence != null && fact.toEvidence.Count > 0 ? fact.toEvidence[0] : null;
                    if (from != null) from.RemoveFactLink(fact);
                    if (to != null) to.RemoveFactLink(fact);
                    GameplayController.Instance?.factList?.Remove(fact);
                    Plugin.Event($"NOTES string {parts[0]} -> {parts[1]} removed (removed by P{m.ByPlayer})");
                }
                catch (Exception e) { Plugin.Event($"NOTES removing string {parts[0]} -> {parts[1]} failed: {e.Message}"); }
                RefreshStrings();
                return true;
            }
        }
        return false;
    }

    /// <summary>Make a sticky note that another player created (also used on demand when a note event arrives first).</summary>
    internal static Evidence CreateStickyNote(string id) =>
        EvidenceCreator.Instance?.CreateEvidence(StickyNotePreset, id, null, null, null, null, null, false, null);

    static Evidence FindEvidence(string id)
    {
        var dict = GameplayController.Instance?.evidenceDictionary;
        if (dict == null) return null;
        if (dict.TryGetValue(id, out var ev) && ev != null) return ev;
        return CaseSync.CreateOnDemand(id);
    }

    /// <param name="fromKeys">With both key texts given (see <see cref="KeyText"/>), only a string with those data keys.</param>
    static Fact FindCustomLink(string fromId, string toId, string fromKeys = null, string toKeys = null)
    {
        var list = GameplayController.Instance?.factList;
        if (list == null) return null;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var f = list[i];
            if (f == null || !f.isCustom) continue;
            if (FirstId(f.fromEvidence) != fromId || FirstId(f.toEvidence) != toId) continue;
            if (fromKeys != null && toKeys != null && (KeyText(f.fromDataKeys) != fromKeys || KeyText(f.toDataKeys) != toKeys)) continue;
            return f;
        }
        return null;
    }

    static string FirstId(Il2CppSystem.Collections.Generic.List<Evidence> list) =>
        list != null && list.Count > 0 && list[0] != null ? list[0].evID : null;

    static void RefreshStrings()
    {
        try { CasePanelController.Instance?.UpdateStrings(); }
        catch (Exception e) { Plugin.Event($"NOTES string refresh failed: {e.Message}"); }
    }

    static string KeyText(DataKeyList keys)
    {
        if (keys == null) return "";
        var parts = new string[keys.Count];
        for (int i = 0; i < keys.Count; i++) parts[i] = ((int)keys[i]).ToString();
        return string.Join(",", parts);
    }

    static DataKeyList Keys(string text)
    {
        var list = new DataKeyList();
        foreach (var s in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(s, out var k)) list.Add((Evidence.DataKey)k);
        return list;
    }

    static bool ShouldReport => NetSession.Current != null && !CaseSync.Applying;

    [HarmonyPatch(typeof(Evidence), nameof(Evidence.SetNote))]
    static class SetNote
    {
        static void Postfix(Evidence __instance, DataKeyList keys, string str)
        {
            if (!ShouldReport) return;
            try { CaseSync.Report(CaseEventKind.NoteText, $"{__instance.evID}{Sep}{KeyText(keys)}{Sep}{str ?? ""}"); }
            catch (Exception e) { Plugin.Event($"NOTES note sync failed: {e.Message}"); }
        }
    }

    /// <summary>Client: move the sticky note counter into this player's range so ids never collide with the host's.</summary>
    static void EnsureStickyRange()
    {
        var s = NetSession.Current;
        var local = PlayerRegistry.Local;
        if (s == null || s.Role != NetSession.SessionRole.Client || local == null || local.Id.Value == 0) return;
        int floor = local.Id.Value * StickyRange;
        if (InterfaceController.assignStickyNoteID >= floor) return;
        // First note since the world loaded (the counter is the host's again): don't start at the range's floor if
        // notes from this range exist already (ours from an earlier session, or a previous player with this id).
        int top = floor + StickyRange;
        try
        {
            var dict = GameplayController.Instance?.evidenceDictionary;
            if (dict != null)
                foreach (var kv in dict)
                {
                    string key = kv.Key;
                    if (key == null || !key.StartsWith(StickyNotePrefix)) continue;
                    if (int.TryParse(key.Substring(StickyNotePrefix.Length), out int n) && n >= floor && n < top) floor = n + 1;
                }
        }
        catch (Exception e) { Plugin.Event($"NOTES sticky note id scan failed: {e.Message}"); }
        InterfaceController.assignStickyNoteID = floor;
    }

    [HarmonyPatch(typeof(CasePanelController), nameof(CasePanelController.NewStickyNote))]
    static class StickyFromPanel { static void Prefix() => EnsureStickyRange(); }

    [HarmonyPatch(typeof(PinnedItemController), nameof(PinnedItemController.NewStickyNote))]
    static class StickyFromPin { static void Prefix() => EnsureStickyRange(); }

    [HarmonyPatch]
    static class StickyCreated
    {
        static MethodBase TargetMethod() => typeof(EvidenceCreator).GetMethods()
            .First(m => m.Name == nameof(EvidenceCreator.CreateEvidence) && m.GetParameters()[0].ParameterType == typeof(string));

        static void Postfix(string presetName, Evidence __result)
        {
            if (!ShouldReport || presetName != StickyNotePreset || __result == null) return;
            CaseSync.Report(CaseEventKind.StickyNote, __result.evID);
        }
    }

    [HarmonyPatch(typeof(EvidenceCreator), nameof(EvidenceCreator.CreateFact))]
    static class CustomLinkCreated
    {
        static void Postfix(string presetName, bool forceDiscoveryOnCreate, Fact __result)
        {
            if (!ShouldReport || presetName != CustomLinkPreset || __result == null) return;
            try
            {
                string from = FirstId(__result.fromEvidence), to = FirstId(__result.toEvidence);
                if (from == null || to == null) return;
                CaseSync.Report(CaseEventKind.CustomLink,
                    $"{from}{Sep}{to}{Sep}{KeyText(__result.fromDataKeys)}{Sep}{KeyText(__result.toDataKeys)}", forceDiscoveryOnCreate ? 1 : 0);
            }
            catch (Exception e) { Plugin.Event($"NOTES string sync failed: {e.Message}"); }
        }
    }

    /// <summary>
    /// A player removes a string they drew (the string's context menu → StringController.RemoveCustomLink, which takes
    /// every custom fact of that connection off its evidences and the fact list). Report the ones that are gone after.
    /// </summary>
    [HarmonyPatch(typeof(StringController), nameof(StringController.RemoveCustomLink))]
    static class CustomLinkRemoved
    {
        static void Prefix(StringController __instance, out System.Collections.Generic.List<(Fact fact, string id)> __state)
        {
            __state = null;
            if (!ShouldReport) return;
            try
            {
                var facts = __instance?.connection?.facts;
                if (facts == null) return;
                for (int i = 0; i < facts.Count; i++)
                {
                    var f = facts[i];
                    if (f == null || !f.isCustom) continue;
                    string from = FirstId(f.fromEvidence), to = FirstId(f.toEvidence);
                    if (from == null || to == null) continue;
                    (__state ??= new System.Collections.Generic.List<(Fact, string)>())
                        .Add((f, $"{from}{Sep}{to}{Sep}{KeyText(f.fromDataKeys)}{Sep}{KeyText(f.toDataKeys)}"));
                }
            }
            catch (Exception e) { Plugin.Event($"NOTES string removal check failed: {e.Message}"); }
        }

        static void Postfix(System.Collections.Generic.List<(Fact fact, string id)> __state)
        {
            if (__state == null || !ShouldReport) return;
            try
            {
                var list = GameplayController.Instance?.factList;
                foreach (var (fact, id) in __state)
                {
                    if (list != null && list.Contains(fact)) continue; // still there: not removed after all
                    CaseSync.Report(CaseEventKind.CustomLinkRemove, id);
                }
            }
            catch (Exception e) { Plugin.Event($"NOTES string removal sync failed: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(FactCustom), nameof(FactCustom.SetCustomName))]
    static class CustomLinkRenamed
    {
        static void Postfix(FactCustom __instance, string str)
        {
            if (!ShouldReport) return;
            string from = FirstId(__instance.fromEvidence), to = FirstId(__instance.toEvidence);
            if (from != null && to != null) CaseSync.Report(CaseEventKind.CustomLinkName, $"{from}{Sep}{to}{Sep}{str ?? ""}");
        }
    }
}
