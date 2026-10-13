using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace SodCoop.Net;

/// <summary>
/// Design: one shared case, chosen by the host. The host's <c>Case</c> objects (pins, objectives, resolve
/// questions; <c>[Serializable]</c>) go to clients in the game's save form. The client rebuilds them the way
/// <c>SaveStateController.LoadSaveState</c> does (<c>Objective.Setup(case)</c>, then
/// <c>CasePanelController.SetActiveCase</c>) and follows the host's active case.
/// Sent: all cases on Welcome, the case on <c>SetActiveCase</c>, and a case again (debounced) when it gets an
/// objective. Discoveries inside the case are <see cref="CaseSync"/>'s job.
/// </summary>
public static class CaseRecordSync
{
    const float Debounce = 1f;
    [ThreadStatic] static bool _applying;
    static readonly HashSet<int> _dirty = new();
    static float _flushAt;

    public static int Sent { get; private set; }
    public static int Applied { get; private set; }

    static bool IsHost => NetSession.Current?.Role == NetSession.SessionRole.Host;

    public static void SendAll(Action<string, bool> send)
    {
        var panel = CasePanelController.Instance;
        if (panel?.activeCases == null) return;
        for (int i = 0; i < panel.activeCases.Count; i++)
        {
            var c = panel.activeCases[i];
            if (c == null) continue;
            send(JsonUtility.ToJson(c), panel.activeCase != null && panel.activeCase.id == c.id);
            Sent++;
        }
    }

    /// <summary>Host, per frame: send cases marked dirty once things settle.</summary>
    public static void Flush(float now, Action<string, bool> send)
    {
        if (_dirty.Count == 0 || now < _flushAt) return;
        var panel = CasePanelController.Instance;
        if (panel?.activeCases != null)
            for (int i = 0; i < panel.activeCases.Count; i++)
            {
                var c = panel.activeCases[i];
                if (c == null || !_dirty.Contains(c.id)) continue;
                send(JsonUtility.ToJson(c), panel.activeCase != null && panel.activeCase.id == c.id);
                Sent++;
            }
        _dirty.Clear();
    }

    /// <summary>Host: re-send this case (status/resolution changed).</summary>
    public static void MarkChanged(Case c) => MarkDirty(c, Time.unscaledTime);

    static void MarkDirty(Case c, float now)
    {
        if (c == null || _applying || NetSession.Current == null || !Core.World.Ready) return;
        // Clients only report board edits (pins); the case's lifecycle is the host's.
        if (!IsHost && !_boardEdit) return;
        _dirty.Add(c.id);
        _flushAt = now + Debounce;
    }

    [HarmonyPatch(typeof(CasePanelController), nameof(CasePanelController.SetActiveCase))]
    static class Active { static void Postfix(Case newCase) => MarkDirty(newCase, Time.unscaledTime - Debounce); }

    [HarmonyPatch(typeof(Case), nameof(Case.AddObjective))]
    static class Objectives { static void Postfix(Case __instance) => MarkDirty(__instance, Time.unscaledTime); }

    [ThreadStatic] static bool _boardEdit;

    /// <summary>Pinning/unpinning or recolouring on the board (either machine): re-send the case. Last writer wins.</summary>
    static void BoardEdited(Case c = null)
    {
        _boardEdit = true;
        try { MarkDirty(c ?? CasePanelController.Instance?.activeCase, Time.unscaledTime); }
        finally { _boardEdit = false; }
    }

    static Case CaseById(int id)
    {
        var cases = CasePanelController.Instance?.activeCases;
        if (cases == null) return null;
        for (int i = 0; i < cases.Count; i++)
            if (cases[i] != null && cases[i].id == id) return cases[i];
        return null;
    }

    /// <summary>Pin colour (stored in the case element). Only real changes: the board rebuild re-applies colours too.</summary>
    [HarmonyPatch(typeof(Case.CaseElement), nameof(Case.CaseElement.SetColour))]
    static class PinColour
    {
        static void Prefix(Case.CaseElement __instance, out InterfaceControls.EvidenceColours __state) => __state = __instance.color;

        static void Postfix(Case.CaseElement __instance, InterfaceControls.EvidenceColours newColour, InterfaceControls.EvidenceColours __state)
        {
            if (newColour != __state) BoardEdited(CaseById(__instance.caseID));
        }
    }

    /// <summary>
    /// A pin (evidence, note, sticky note) dragged to a new place on the board: its position lives in the case element
    /// (<c>CaseElement.v</c>). Only the end of a player's drag, not every move: the board rebuild after applying a
    /// record moves pins too, and that must not be sent back.
    /// </summary>
    [HarmonyPatch(typeof(PinnedItemController), nameof(PinnedItemController.OnEndDrag))]
    static class PinMoved { static void Postfix() => BoardEdited(); }

    /// <summary>String colour (stored in <c>Case.stringColours</c>).</summary>
    [HarmonyPatch(typeof(Case), nameof(Case.AddNewStringColour))]
    static class StringColour { static void Postfix(Case __instance) => BoardEdited(__instance); }

    [HarmonyPatch(typeof(CasePanelController), nameof(CasePanelController.PinToCasePanel), new[] { typeof(Case), typeof(Evidence), typeof(Il2CppSystem.Collections.Generic.List<Evidence.DataKey>), typeof(bool), typeof(Vector2), typeof(bool) })]
    static class Pin { static void Postfix() => BoardEdited(); }

    [HarmonyPatch(typeof(CasePanelController), nameof(CasePanelController.UnPinFromCasePanel))]
    static class Unpin { static void Postfix() => BoardEdited(); }

    public static void Apply(string json, bool active)
    {
        var panel = CasePanelController.Instance;
        if (panel?.activeCases == null) return;
        _applying = true;
        try
        {
            var c = JsonUtility.FromJson(json, Il2CppType.Of<Case>())?.TryCast<Case>();
            if (c == null) { Plugin.Event("CASE record: JsonUtility returned nothing"); return; }
            if (IsHost && MergeBoardEdit(panel, c)) { Applied++; return; }
            SetupObjectives(c, c.currentActiveObjectives);
            SetupObjectives(c, c.inactiveCurrentObjectives);
            SetupObjectives(c, c.endedObjectives);

            bool wasActive = false;
            for (int i = panel.activeCases.Count - 1; i >= 0; i--)
            {
                var old = panel.activeCases[i];
                if (old == null || old.id != c.id) continue;
                wasActive |= panel.activeCase != null && panel.activeCase.Pointer == old.Pointer;
                RetireObjectives(old);
                panel.activeCases.RemoveAt(i);
            }
            panel.activeCases.Add(c);
            panel.UpdateCaseControls(); // case buttons on the board: new ones created, replaced ones refreshed
            if (active || wasActive)
            {
                panel.SetActiveCase(c);
                panel.UpdateStrings(); // string colours live in the case; redraw them from the new record
            }
            Applied++;
            Plugin.Event($"CASE record {c.id} \"{c.name}\" applied: {c.caseElements?.Count ?? 0} pins, {c.currentActiveObjectives?.Count ?? 0} active objectives, active={active} ({json.Length} chars)");
        }
        catch (Exception e) { Plugin.Event($"CASE record failed: {e}"); }
        finally { _applying = false; }
    }

    /// <summary>
    /// Host, a client's board edit: take only the board (pins and their positions/colours, string colours, hidden
    /// connections, the name) into the host's own case. Replacing the whole case rebuilt the host's live objectives
    /// from the client's copy on every pin.
    /// </summary>
    static bool MergeBoardEdit(CasePanelController panel, Case incoming)
    {
        Case mine = null;
        for (int i = 0; i < panel.activeCases.Count; i++)
            if (panel.activeCases[i] != null && panel.activeCases[i].id == incoming.id) { mine = panel.activeCases[i]; break; }
        if (mine == null) return false; // a case the client made itself: add it whole
        mine.caseElements = incoming.caseElements;
        mine.stringColours = incoming.stringColours;
        mine.hiddenConnections = incoming.hiddenConnections;
        if (!string.IsNullOrEmpty(incoming.name)) mine.name = incoming.name;
        if (panel.activeCase != null && panel.activeCase.Pointer == mine.Pointer)
        {
            panel.SetActiveCase(mine); // rebuild the board from the merged elements
            panel.UpdateStrings();
        }
        Plugin.Event($"CASE board edit for {mine.id} \"{mine.name}\" merged: {mine.caseElements?.Count ?? 0} pins");
        return true;
    }

    /// <summary>
    /// Take the replaced copy's objectives down quietly (UI entry, pointer, checking loop) before the new copy's are set
    /// up. Left behind, they were completed/cancelled as orphans and those completions reached the host.
    /// </summary>
    static void RetireObjectives(Case old)
    {
        ObjectiveSync.Retiring = true;
        try
        {
            foreach (var list in new[] { old.currentActiveObjectives, old.inactiveCurrentObjectives })
            {
                if (list == null) continue;
                foreach (var o in list.ToArray())
                {
                    if (o == null || o.isComplete || o.isCancelled) continue;
                    try { o.Remove(); } catch (Exception e) { Plugin.Event($"CASE could not retire objective '{o.name}': {e.Message}"); }
                }
            }
        }
        finally { ObjectiveSync.Retiring = false; }
    }

    static void SetupObjectives(Case c, Il2CppSystem.Collections.Generic.List<Objective> list)
    {
        if (list == null) return;
        for (int i = 0; i < list.Count; i++) list[i]?.Setup(c);
    }
}
