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
    /// <summary>Last record sent / applied per case id: an unchanged case isn't sent or rebuilt again. Each rebuild
    /// replaces the board under the player's hands (seen: the same 16 KB case three times in 26 s while the client
    /// was pinning, and the client's unsent pins were lost).</summary>
    static readonly Dictionary<int, string> _lastSent = new(), _lastApplied = new();

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
        if (!IsHost) ApplyDeferred(now);
        if (_dirty.Count == 0 || now < _flushAt) return;
        var panel = CasePanelController.Instance;
        if (panel?.activeCases != null)
            for (int i = 0; i < panel.activeCases.Count; i++)
            {
                var c = panel.activeCases[i];
                if (c == null || !_dirty.Contains(c.id)) continue;
                var json = JsonUtility.ToJson(c);
                if (_lastSent.TryGetValue(c.id, out var last) && last == json)
                {
                    if (!IsHost) Plugin.Event($"CASE board edit for {c.id} not sent: unchanged since our last send");
                    continue;
                }
                _lastSent[c.id] = json;
                send(json, panel.activeCase != null && panel.activeCase.id == c.id);
                Sent++;
                if (!IsHost) Plugin.Event($"CASE board edit for {c.id} \"{c.name}\" sent: {c.caseElements?.Count ?? 0} pins");
            }
        _dirty.Clear();
    }

    /// <summary>New session or a reloaded world: the case objects are new, so nothing counts as sent or applied.</summary>
    public static void Reset()
    {
        _dirty.Clear();
        _lastSent.Clear();
        _lastApplied.Clear();
        _deferred.Clear();
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
        try
        {
            c ??= CasePanelController.Instance?.activeCase;
            if (!IsHost)
            {
                // Diagnostics (seen: the host never got a client's pins and the client logged nothing): say why an
                // edit isn't queued, or that it is.
                string skip = c == null ? "no active case" : _applying ? "while applying the host's record" :
                              NetSession.Current == null ? "no session" : !Core.World.Ready ? "world not ready" : null;
                Plugin.Event(skip != null ? $"CASE board edit not sent: {skip}" : $"CASE board edit on {c.id} \"{c.name}\" queued ({c.caseElements?.Count ?? 0} pins)");
            }
            MarkDirty(c, Time.unscaledTime);
        }
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

    /// <summary>Hiding/showing a connection (stored in <c>Case.hiddenConnections</c>, merged by the host).</summary>
    [HarmonyPatch(typeof(Case), nameof(Case.SetHidden))]
    static class HideConnection { static void Postfix(Case __instance) => BoardEdited(__instance); }

    [HarmonyPatch(typeof(Case), nameof(Case.ToggleHidden))] // the fact window's button; doesn't go through SetHidden
    static class ToggleConnection { static void Postfix(Case __instance) => BoardEdited(__instance); }

    /// <summary>Client: records held back while the mouse button is down (a board rebuild would cut off a drag).</summary>
    static readonly Dictionary<int, (string json, bool active, float since)> _deferred = new();
    const float MaxDefer = 10f;

    static bool Dragging()
    {
        try { return Input.GetMouseButton(0); } catch { return false; }
    }

    /// <summary>Client, per frame: apply held-back records once the mouse is released (or after <see cref="MaxDefer"/> s).</summary>
    static void ApplyDeferred(float now)
    {
        if (_deferred.Count == 0) return;
        bool dragging = Dragging();
        foreach (var id in new List<int>(_deferred.Keys))
        {
            var (json, active, since) = _deferred[id];
            if (dragging && now - since < MaxDefer) continue;
            _deferred.Remove(id);
            Apply(json, active, fromQueue: true);
        }
    }

    public static void Apply(string json, bool active) => Apply(json, active, false);

    static void Apply(string json, bool active, bool fromQueue)
    {
        var panel = CasePanelController.Instance;
        if (panel?.activeCases == null) return;
        if (!IsHost && !fromQueue && Dragging())
        {
            int id = PeekId(json);
            if (id >= 0)
            {
                float since = _deferred.TryGetValue(id, out var d) ? d.since : Time.unscaledTime;
                _deferred[id] = (json, active || (_deferred.TryGetValue(id, out var d2) && d2.active), since); // newest record wins
                return;
            }
        }
        _applying = true;
        try
        {
            var c = JsonUtility.FromJson(json, Il2CppType.Of<Case>())?.TryCast<Case>();
            if (c == null) { Plugin.Event("CASE record: JsonUtility returned nothing"); return; }
            if (IsHost && MergeBoardEdit(panel, c)) { Applied++; return; }
            if (_lastApplied.TryGetValue(c.id, out var lastJson) && lastJson == json) return; // unchanged: keep the board as it is
            _lastApplied[c.id] = json;
            SetupObjectives(c, c.currentActiveObjectives);
            SetupObjectives(c, c.inactiveCurrentObjectives);
            SetupObjectives(c, c.endedObjectives);

            bool wasActive = false;
            for (int i = panel.activeCases.Count - 1; i >= 0; i--)
            {
                var old = panel.activeCases[i];
                if (old == null || old.id != c.id) continue;
                wasActive |= panel.activeCase != null && panel.activeCase.Pointer == old.Pointer;
                if (_dirty.Contains(c.id)) KeepLocalBoard(old, c);
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

    /// <summary>The case id from a record without parsing the whole case ("id":N in the JSON); -1 if not found.</summary>
    static int PeekId(string json)
    {
        int i = json.IndexOf("\"id\":", StringComparison.Ordinal);
        if (i < 0) return -1;
        i += 5;
        int j = i;
        while (j < json.Length && (char.IsDigit(json[j]) || json[j] == '-')) j++;
        return int.TryParse(json.AsSpan(i, j - i), out var id) ? id : -1;
    }

    /// <summary>
    /// Client: a host record arrived while our own board edit is still waiting to be sent. Keep our board on the new
    /// copy (it stays dirty, so it goes to the host next and the host merges it).
    /// </summary>
    static void KeepLocalBoard(Case mine, Case incoming)
    {
        incoming.caseElements = mine.caseElements;
        incoming.stringColours = mine.stringColours;
        incoming.hiddenConnections = mine.hiddenConnections;
        Plugin.Event($"CASE record {incoming.id}: kept our {mine.caseElements?.Count ?? 0} pins (our edit isn't sent yet)");
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
            // SetActiveCase returns at once when the case is already active (seen: the host saw a client's pins only
            // after reopening the board). UpdatePinned is the game's own redraw: it drops pins whose element is gone,
            // spawns the new ones and redraws the strings.
            panel.UpdatePinned();
            panel.UpdateStrings();
        }
        // Resync: the merged case goes straight back to everyone (the editor included), so every board shows the
        // host's state and the editor sees its edit confirmed. MarkDirty is off while applying, so set it directly.
        _dirty.Add(mine.id);
        _flushAt = Time.unscaledTime;
        Plugin.Event($"CASE board edit for {mine.id} \"{mine.name}\" merged: {mine.caseElements?.Count ?? 0} pins; resyncing everyone");
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
