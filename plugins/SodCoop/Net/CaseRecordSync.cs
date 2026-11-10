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
/// Sent: all cases on Welcome (not fresh), the case on <c>SetActiveCase</c>, and a case again (debounced) when it
/// gets an objective (a new murder case goes out fresh: clients who don't have it show the game's big
/// "New Murder Case" title, which their skipped <c>MurderController.OnVictimDiscovery</c> never shows).
/// Discoveries inside the case are <see cref="CaseSync"/>'s job.
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

    /// <summary>"No sender known" for <see cref="Apply(string, bool, bool, byte)"/>.</summary>
    public const byte NoSender = 255;

    /// <summary>
    /// Host: per case, the pins (element key) clients can know about and since when: the time we first sent the pin,
    /// or 0 when a client sent it to us. A pin missing from a client's board is taken as removed by that client only if
    /// the client can have seen it (<see cref="SeenGrace"/> after we sent it); a pin the host just made is kept.
    /// </summary>
    static readonly Dictionary<int, Dictionary<string, float>> _seenByClients = new();
    const float SeenGrace = 3f; // our 1 s debounce + the client's 1 s debounce + latency
    /// <summary>Client: per case, the pins of the last host record applied and their state there (a pin not in there
    /// is new from the host; a pin of ours whose position/colour still equals it was not changed by us).</summary>
    static readonly Dictionary<int, Dictionary<string, PinState>> _hostKeys = new();

    /// <summary>The per-pin fields both players can change. (The rest of a CaseElement - w, resPos, resPiv, sdk - is
    /// one machine's window state and never taken from the other side.)</summary>
    struct PinState
    {
        public float x, y;
        public int color;
        public bool co, m;
    }

    static PinState StateOf(Case.CaseElement e)
    {
        var v = e.v;
        return new PinState { x = v.x, y = v.y, color = (int)e.color, co = e.co, m = e.m };
    }

    static bool SamePos(PinState a, PinState b) => Math.Abs(a.x - b.x) < 0.01f && Math.Abs(a.y - b.y) < 0.01f;

    static Dictionary<string, PinState> StatesOf(Case c)
    {
        var map = new Dictionary<string, PinState>();
        var list = c?.caseElements;
        if (list != null)
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null) map[KeyOf(list[i])] = StateOf(list[i]);
        return map;
    }

    /// <summary>
    /// Host: per case and pin, the states we sent to clients: the current one (until = float.MaxValue) and those it
    /// replaced within the last <see cref="SeenGrace"/> seconds (a client's record may still be built on those). A
    /// field of a client's pin that equals one of them wasn't changed by that client, so ours stands.
    /// </summary>
    static readonly Dictionary<int, Dictionary<string, List<(PinState s, float until)>>> _sentPins = new();
    /// <summary>Host: hidden connections as last sent; client: as in the last host record applied. The base both
    /// sides' changes are measured against.</summary>
    static readonly Dictionary<int, HashSet<string>> _hiddenBase = new();
    /// <summary>Client: resolve answers (question name → input, evidence) of the last host record applied. An answer
    /// of ours that differs from it was typed here and survives the next record.</summary>
    static readonly Dictionary<int, Dictionary<string, (string input, string evidence)>> _hostAnswers = new();
    static float _nextAnswerSeed;
    /// <summary>Host: cases made active by an explicit SetActiveCase since their last send. Only those go out
    /// "active"; before, every send of the host's active case pulled the clients' boards onto it.</summary>
    static readonly HashSet<int> _activePending = new();

    static HashSet<string> HiddenOf(Case c)
    {
        var set = new HashSet<string>();
        var list = c?.hiddenConnections;
        if (list != null)
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null) set.Add(list[i]);
        return set;
    }

    static Dictionary<string, (string input, string evidence)> AnswersOf(Case c)
    {
        var map = new Dictionary<string, (string input, string evidence)>();
        var qs = c?.resolveQuestions;
        if (qs != null)
            for (int i = 0; i < qs.Count; i++)
            {
                var q = qs[i];
                if (q?.name != null) map[q.name] = (q.input ?? "", q.inputtedEvidence ?? "");
            }
        return map;
    }

    /// <summary>A pin's identity across machines: evidence id + its data keys.</summary>
    static string KeyOf(Case.CaseElement e)
    {
        if (e == null) return "";
        var sb = new System.Text.StringBuilder(e.id ?? "");
        var dk = e.dk;
        if (dk != null)
            for (int i = 0; i < dk.Count; i++) sb.Append('|').Append((int)dk[i]);
        return sb.ToString();
    }

    static HashSet<string> KeysOf(Case c)
    {
        var set = new HashSet<string>();
        var list = c?.caseElements;
        if (list != null)
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null) set.Add(KeyOf(list[i]));
        return set;
    }

    /// <summary>Host: the pins of a case just sent are known to clients from now on.</summary>
    static void NoteSent(Case c, float now)
    {
        if (!_seenByClients.TryGetValue(c.id, out var seen)) _seenByClients[c.id] = seen = new Dictionary<string, float>();
        foreach (var k in KeysOf(c))
            if (!seen.ContainsKey(k)) seen[k] = now;

        if (!_sentPins.TryGetValue(c.id, out var pins)) _sentPins[c.id] = pins = new Dictionary<string, List<(PinState s, float until)>>();
        var current = StatesOf(c);
        foreach (var kv in current)
        {
            if (!pins.TryGetValue(kv.Key, out var hist)) pins[kv.Key] = hist = new List<(PinState s, float until)>();
            var now2 = kv.Value;
            if (hist.Count > 0)
            {
                var last = hist[hist.Count - 1];
                bool same = SamePos(last.s, now2) && last.s.color == now2.color && last.s.co == now2.co && last.s.m == now2.m;
                if (same) continue;
                hist[hist.Count - 1] = (last.s, now); // replaced now
            }
            hist.Add((now2, float.MaxValue));
            hist.RemoveAll(h => now - h.until > SeenGrace);
        }
        var gone = new List<string>();
        foreach (var kv in pins) if (!current.ContainsKey(kv.Key)) gone.Add(kv.Key);
        foreach (var k in gone) pins.Remove(k);
        _hiddenBase[c.id] = HiddenOf(c);
    }

    public static int Sent { get; private set; }
    public static int Applied { get; private set; }

    static bool IsHost => NetSession.Current?.Role == NetSession.SessionRole.Host;

    public static void SendAll(Action<string, bool, bool> send)
    {
        var panel = CasePanelController.Instance;
        if (panel?.activeCases == null) return;
        for (int i = 0; i < panel.activeCases.Count; i++)
        {
            var c = panel.activeCases[i];
            if (c == null) continue;
            send(JsonUtility.ToJson(c), panel.activeCase != null && panel.activeCase.id == c.id, false); // pre-existing: no title on join
            Sent++;
            NoteSent(c, Time.unscaledTime); // the joiner gets these pins (welcome batch and join save)
        }
    }

    /// <summary>Host, per frame: send cases marked dirty once things settle.</summary>
    public static void Flush(float now, Action<string, bool, bool> send)
    {
        if (!IsHost)
        {
            ApplyDeferred(now);
            if (now >= _nextAnswerSeed) { _nextAnswerSeed = now + 5f; SeedAnswers(); }
        }
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
                bool isActive = panel.activeCase != null && panel.activeCase.id == c.id;
                // Host: "active" only for a case it explicitly made active since the last send (a client's flag is
                // ignored by the host anyway).
                if (IsHost) isActive = isActive && _activePending.Contains(c.id);
                send(json, isActive, c.caseType == Case.CaseType.murder);
                Sent++;
                if (IsHost) NoteSent(c, now);
                if (!IsHost) Plugin.Event($"CASE board edit for {c.id} \"{c.name}\" sent: {c.caseElements?.Count ?? 0} pins");
            }
        _dirty.Clear();
        _activePending.Clear();
    }

    /// <summary>
    /// Client, every few seconds: a case without a recorded host answer set (it came with the join save, no record
    /// applied yet) gets its current answers as the base, so what the player types from then on counts as typed here.
    /// </summary>
    static void SeedAnswers()
    {
        try
        {
            var cases = CasePanelController.Instance?.activeCases;
            if (cases == null || !Core.World.Ready) return;
            for (int i = 0; i < cases.Count; i++)
            {
                var c = cases[i];
                if (c != null && !_hostAnswers.ContainsKey(c.id)) _hostAnswers[c.id] = AnswersOf(c);
            }
        }
        catch (Exception e) { Plugin.Event($"CASE answer base failed: {e.Message}"); }
    }

    /// <summary>
    /// Client, when a host record replaces a case: answers the player typed here since the last host record (they
    /// differ from what that record had) go onto the new copy. They reach the host with the hand-in.
    /// </summary>
    static void KeepLocalAnswers(Case mine, Case incoming)
    {
        try
        {
            if (!_hostAnswers.TryGetValue(incoming.id, out var known) || known == null) return; // no base: can't tell ours from the host's
            var old = mine.resolveQuestions;
            var fresh = incoming.resolveQuestions;
            if (old == null || fresh == null) return;
            int kept = 0;
            for (int i = 0; i < old.Count; i++)
            {
                var q = old[i];
                if (q?.name == null || !known.TryGetValue(q.name, out var was)) continue;
                string input = q.input ?? "", evidence = q.inputtedEvidence ?? "";
                if (input == was.input && evidence == was.evidence) continue; // not changed here
                for (int j = 0; j < fresh.Count; j++)
                {
                    var n = fresh[j];
                    if (n == null || n.name != q.name) continue;
                    n.input = input;
                    n.inputtedEvidence = evidence;
                    try { n.UpdateValid(incoming); } catch { /* checked again when the answers are shown / handed in */ }
                    kept++;
                    break;
                }
            }
            if (kept > 0) Plugin.Event($"CASE record {incoming.id}: kept {kept} answer(s) typed here");
        }
        catch (Exception e) { Plugin.Event($"CASE record {incoming.id}: keeping our answers failed: {e.Message}"); }
    }

    /// <summary>Client, when a host record replaces a case and our board isn't kept: the pins' window state
    /// (w, resPos, resPiv, sdk: which windows this player has open and where) stays this machine's.</summary>
    static void KeepWindowState(Case mine, Case incoming)
    {
        try
        {
            var old = mine.caseElements;
            var fresh = incoming.caseElements;
            if (old == null || fresh == null || old.Count == 0) return;
            var byKey = new Dictionary<string, Case.CaseElement>();
            for (int i = 0; i < old.Count; i++)
                if (old[i] != null) byKey[KeyOf(old[i])] = old[i];
            for (int i = 0; i < fresh.Count; i++)
            {
                var e = fresh[i];
                if (e == null || !byKey.TryGetValue(KeyOf(e), out var o)) continue;
                e.w = o.w; e.resPos = o.resPos; e.resPiv = o.resPiv; e.sdk = o.sdk;
            }
        }
        catch (Exception e) { Plugin.Event($"CASE record {incoming.id}: keeping window state failed: {e.Message}"); }
    }

    /// <summary>New session or a reloaded world: the case objects are new, so nothing counts as sent or applied.</summary>
    public static void Reset()
    {
        _dirty.Clear();
        _lastSent.Clear();
        _lastApplied.Clear();
        _deferred.Clear();
        _seenByClients.Clear();
        _hostKeys.Clear();
        _sentPins.Clear();
        _hiddenBase.Clear();
        _hostAnswers.Clear();
        _activePending.Clear();
    }

    /// <summary>Host: is this case waiting to be sent (ObjectiveSync holds "resolved" back until it went out)?</summary>
    public static bool IsPending(int caseId) => _dirty.Contains(caseId);

    /// <summary>Host: send the waiting cases on the next frame instead of after the debounce.</summary>
    public static void FlushSoon()
    {
        float now = Time.unscaledTime;
        if (_flushAt > now) _flushAt = now;
    }

    /// <summary>
    /// Client: a message about this case arrived (objective progress/completion, resolved) while its record is still
    /// held back for a mouse drag: apply the record first, so the message finds the objective / the solved state.
    /// </summary>
    public static void ApplyDeferredNow(int caseId)
    {
        if (!_deferred.Remove(caseId, out var d)) return;
        Plugin.Event($"CASE record {caseId}: applied early (a message about the case arrived while it was held for a drag)");
        Apply(d.json, d.active, d.fresh, true, NoSender);
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
    static class Active
    {
        static void Postfix(Case newCase)
        {
            // The host chose this case (the player's click, a new murder/job case made active): its next send carries
            // "active" and the clients follow. Not while we close a case on another machine's word (they pick their own).
            if (newCase != null && IsHost && !_applying && !_closing && Core.World.Ready) _activePending.Add(newCase.id);
            MarkDirty(newCase, Time.unscaledTime - Debounce);
        }
    }

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

    /// <summary>
    /// A pin placed by dragging it off a window's needle: the placement coroutine moves it and the needle's release
    /// ends it (PinFolderButtonController.ForcePointerUp); the pin's own OnEndDrag isn't part of that. Without this
    /// the case was only marked at the press, so a placement longer than the debounce sent the mid-drag position and
    /// the echo put the pin back there. An unchanged case isn't sent (Flush compares with the last send).
    /// </summary>
    [HarmonyPatch(typeof(PinFolderButtonController), nameof(PinFolderButtonController.ForcePointerUp))]
    static class NeedleReleased { static void Postfix() => BoardEdited(); }

    /// <summary>String colour (stored in <c>Case.stringColours</c>).</summary>
    [HarmonyPatch(typeof(Case), nameof(Case.AddNewStringColour))]
    static class StringColour { static void Postfix(Case __instance) => BoardEdited(__instance); }

    [HarmonyPatch(typeof(CasePanelController), nameof(CasePanelController.PinToCasePanel), new[] { typeof(Case), typeof(Evidence), typeof(Il2CppSystem.Collections.Generic.List<Evidence.DataKey>), typeof(bool), typeof(Vector2), typeof(bool) })]
    static class Pin { static void Postfix() => BoardEdited(); }

    [HarmonyPatch(typeof(CasePanelController), nameof(CasePanelController.UnPinFromCasePanel))]
    static class Unpin { static void Postfix() => BoardEdited(); }

    /// <summary>
    /// A case the client's player makes on the board (the "new case" button; its ids come from the client's own range,
    /// see <see cref="CaseIds"/>). Sent like a board edit; the host doesn't have it, so it adds it whole. Other
    /// <c>CreateNewCase</c> callers (murder, side jobs, chapters) are the host's and aren't sent from here.
    /// </summary>
    [ThreadStatic] static bool _playerCase;

    [HarmonyPatch(typeof(CasePanelController), nameof(CasePanelController.NewCustomCaseButton))]
    static class NewCaseButton
    {
        static void Prefix() => _playerCase = true;
        static void Postfix() => _playerCase = false;
        static void Finalizer() => _playerCase = false; // also when the game method throws (no postfix then)
    }

    // ------------------------------------------------------------------ closing a case

    [ThreadStatic] static bool _closing;

    static void Forget(int caseId)
    {
        _dirty.Remove(caseId);
        _lastSent.Remove(caseId);
        _lastApplied.Remove(caseId);
        _deferred.Remove(caseId);
        _seenByClients.Remove(caseId);
        _hostKeys.Remove(caseId);
        _sentPins.Remove(caseId);
        _hiddenBase.Remove(caseId);
        _hostAnswers.Remove(caseId);
        _activePending.Remove(caseId);
    }

    /// <summary>
    /// A case closed on this machine (CasePanelController.CloseCase takes it off activeCases, which is all Flush looks
    /// at): tell the others, or they keep it forever and their next board edit brings it back here.
    /// </summary>
    [HarmonyPatch(typeof(CasePanelController), nameof(CasePanelController.CloseCase))]
    static class Closed
    {
        static void Postfix(Case closeThisCase)
        {
            if (closeThisCase == null || _closing || _applying || NetSession.Current == null || !Core.World.Ready) return;
            try
            {
                int id = closeThisCase.id;
                Forget(id);
                ObjectiveSync.SendClosed(id);
                Plugin.Event($"CASE {id} \"{closeThisCase.name}\" closed here; told the others");
            }
            catch (Exception e) { Plugin.Event($"CASE close report failed: {e.Message}"); }
        }
    }

    /// <summary>Another machine closed this case: close our copy the game's way, without reporting it back.</summary>
    public static void ApplyClosed(int caseId)
    {
        var panel = CasePanelController.Instance;
        var c = CaseById(caseId);
        Forget(caseId);
        if (panel == null || c == null) return;
        _closing = true;
        try
        {
            panel.CloseCase(c);
            panel.UpdateCaseControls();
            Plugin.Event($"CASE {caseId} \"{c.name}\" closed (closed on another machine)");
        }
        catch (Exception e) { Plugin.Event($"CASE closing {caseId} failed: {e.Message}"); }
        finally { _closing = false; }
    }

    [HarmonyPatch(typeof(CasePanelController), nameof(CasePanelController.CreateNewCase))]
    static class NewCase
    {
        static void Postfix(Case __result)
        {
            if (!_playerCase || __result == null || IsHost) return;
            Plugin.Event($"CASE created our own case {__result.id} \"{__result.name}\"");
            BoardEdited(__result);
        }
    }

    /// <summary>The name typed into the new-case popup.</summary>
    [HarmonyPatch(typeof(CasePanelController), nameof(CasePanelController.OnCreateNewCustomCase))]
    static class NewCaseNamed { static void Postfix() => BoardEdited(); }

    /// <summary>Hiding/showing a connection (stored in <c>Case.hiddenConnections</c>, merged by the host).</summary>
    [HarmonyPatch(typeof(Case), nameof(Case.SetHidden))]
    static class HideConnection { static void Postfix(Case __instance) => BoardEdited(__instance); }

    [HarmonyPatch(typeof(Case), nameof(Case.ToggleHidden))] // the fact window's button; doesn't go through SetHidden
    static class ToggleConnection { static void Postfix(Case __instance) => BoardEdited(__instance); }

    /// <summary>Client: records held back while the mouse button is down (a board rebuild would cut off a drag).</summary>
    static readonly Dictionary<int, (string json, bool active, bool fresh, float since)> _deferred = new();
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
            var (json, active, fresh, since) = _deferred[id];
            if (dragging && now - since < MaxDefer) continue;
            _deferred.Remove(id);
            Apply(json, active, fresh, true, NoSender);
        }
    }

    public static void Apply(string json, bool active, bool fresh) => Apply(json, active, fresh, false, NoSender);

    /// <param name="fromPlayer">Host: the client that sent the record (<see cref="NoSender"/> if unknown).</param>
    public static void Apply(string json, bool active, bool fresh, byte fromPlayer) => Apply(json, active, fresh, false, fromPlayer);

    static void Apply(string json, bool active, bool fresh, bool fromQueue, byte fromPlayer)
    {
        var panel = CasePanelController.Instance;
        if (panel?.activeCases == null) return;
        if (!IsHost && !fromQueue && Dragging())
        {
            int id = PeekId(json);
            if (id >= 0)
            {
                float since = _deferred.TryGetValue(id, out var d) ? d.since : Time.unscaledTime;
                _deferred[id] = (json, active || (_deferred.TryGetValue(id, out var d2) && d2.active),
                    fresh || (_deferred.TryGetValue(id, out var d3) && d3.fresh), since); // newest record wins
                return;
            }
        }
        _applying = true;
        try
        {
            var c = JsonUtility.FromJson(json, Il2CppType.Of<Case>())?.TryCast<Case>();
            if (c == null) { Plugin.Event("CASE record: JsonUtility returned nothing"); return; }
            if (IsHost && MergeBoardEdit(panel, c)) { Applied++; return; }
            // Host: a case we don't have is only taken from the client that made it (ids in its own range, see CaseIds).
            // Anything else is a case we closed (or never had): adding it back from the client's copy would set up its
            // objectives here again. Tell the clients it is closed instead.
            if (IsHost && fromPlayer != NoSender && c.id / CaseIds.Range != fromPlayer)
            {
                Plugin.Event($"CASE record {c.id} \"{c.name}\" from P{fromPlayer} refused: the host doesn't have that case (closed?); telling clients to close it");
                ObjectiveSync.SendClosed(c.id);
                return;
            }
            if (_lastApplied.TryGetValue(c.id, out var lastJson) && lastJson == json) return; // unchanged: keep the board as it is
            _lastApplied[c.id] = json;
            // Client: the case object is replaced, so "unchanged since our last send" no longer means the host has our
            // state (hide a connection, the host shows it again, hide it again: the same JSON as our last send).
            if (!IsHost) _lastSent.Remove(c.id);
            // The host's pins, hidden connections and answers as they came, before our own are put on the copy.
            var hostKeys = IsHost ? null : StatesOf(c);
            var hostHidden = IsHost ? null : HiddenOf(c);
            var hostAnswers = IsHost ? null : AnswersOf(c);
            SetupObjectives(c, c.currentActiveObjectives);
            SetupObjectives(c, c.inactiveCurrentObjectives);
            SetupObjectives(c, c.endedObjectives);

            bool wasActive = false, existed = false;
            for (int i = panel.activeCases.Count - 1; i >= 0; i--)
            {
                var old = panel.activeCases[i];
                if (old == null || old.id != c.id) continue;
                existed = true;
                wasActive |= panel.activeCase != null && panel.activeCase.Pointer == old.Pointer;
                if (_dirty.Contains(c.id)) KeepLocalBoard(old, c);
                else if (!IsHost) KeepWindowState(old, c);
                if (!IsHost) KeepLocalAnswers(old, c);
                RetireObjectives(old);
                panel.activeCases.RemoveAt(i);
            }
            if (hostKeys != null) _hostKeys[c.id] = hostKeys;
            if (hostHidden != null) _hiddenBase[c.id] = hostHidden;
            if (hostAnswers != null) _hostAnswers[c.id] = hostAnswers;
            panel.activeCases.Add(c);
            panel.UpdateCaseControls(); // case buttons on the board: new ones created, replaced ones refreshed
            if (active || wasActive)
            {
                panel.SetActiveCase(c);
                panel.UpdateStrings(); // string colours live in the case; redraw them from the new record
                RefreshWindows(c);
            }
            if (IsHost)
            {
                // A case a client made: it goes to everyone from here (NetSession no longer relays the client's raw
                // record). MarkDirty is off while applying, so set it directly.
                _dirty.Add(c.id);
                _flushAt = Time.unscaledTime;
            }
            Applied++;
            Plugin.Event($"CASE record {c.id} \"{c.name}\" applied: {c.caseElements?.Count ?? 0} pins, {c.currentActiveObjectives?.Count ?? 0} active objectives, active={active} ({json.Length} chars)");
            if (!IsHost && fresh && !existed && c.caseType == Case.CaseType.murder) ShowNewCaseTitle(c);
        }
        catch (Exception e) { Plugin.Event($"CASE record failed: {e}"); }
        finally { _applying = false; }
    }

    /// <summary>
    /// Client: the game's own big "New Murder Case" header text (InterfaceController.NewMurderCaseDisplay, what
    /// MurderController.OnVictimDiscovery shows there — a client never runs that, CaseIds sends the discovery to the
    /// host). Run when a fresh murder case lands that we didn't have: the synced murder record carries the victim and
    /// victim site the display reads.
    /// </summary>
    static void ShowNewCaseTitle(Case c)
    {
        try
        {
            InterfaceController.Instance?.NewMurderCaseDisplay();
            Plugin.Event($"CASE {c.id} \"{c.name}\": showed the new-murder-case title");
        }
        catch (Exception e) { Plugin.Event($"CASE could not show the new-murder-case title: {e.Message}"); }
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
        // Pins the host added since its last record (not on our board, not in that record) come along: without this
        // our board went out without them and the host took that as us removing them.
        int added = 0;
        try
        {
            var local = mine.caseElements;
            var remote = incoming.caseElements;
            if (local != null && remote != null && _hostKeys.TryGetValue(incoming.id, out var known) && known != null)
            {
                var have = new Dictionary<string, Case.CaseElement>();
                for (int i = 0; i < local.Count; i++)
                    if (local[i] != null) have[KeyOf(local[i])] = local[i];
                int taken = 0;
                for (int i = 0; i < remote.Count; i++)
                {
                    var e = remote[i];
                    if (e == null) continue;
                    string k = KeyOf(e);
                    if (have.TryGetValue(k, out var ours))
                    {
                        // On both boards: what we didn't change since the last host record (still as it was there)
                        // follows the host; what we changed stays ours and goes out with our edit.
                        if (!known.TryGetValue(k, out var was)) continue;
                        var now = StateOf(ours);
                        if (SamePos(now, was) && !SamePos(now, StateOf(e))) { ours.v = e.v; taken++; }
                        if (now.color == was.color && ours.color != e.color) { ours.color = e.color; taken++; }
                        if (now.co == was.co) ours.co = e.co;
                        if (now.m == was.m) ours.m = e.m;
                        continue;
                    }
                    if (known.ContainsKey(k)) continue; // one we removed ourselves
                    local.Add(e);
                    have[k] = e;
                    added++;
                }
                if (taken > 0) Plugin.Event($"CASE record {incoming.id}: took {taken} position/colour change(s) of the host on pins we hadn't touched");
            }
        }
        catch (Exception e) { Plugin.Event($"CASE record {incoming.id}: taking the host's new pins failed: {e.Message}"); }
        if (added > 0) Plugin.Event($"CASE record {incoming.id}: took {added} new pin(s) from the host onto our board");
        incoming.caseElements = mine.caseElements;
        incoming.stringColours = mine.stringColours;
        // Hidden connections: the host's list plus what we hid and minus what we showed again since its last record.
        if (!MergeHidden(incoming, HiddenOf(mine))) incoming.hiddenConnections = mine.hiddenConnections;
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
        // Per pin instead of the whole board: the client's board wins for the pins it has (position, colour) and a pin
        // it no longer has is removed, but a pin of ours the client can't have seen yet (made here in the last moments,
        // or not sent at all) stays. Before, the client's record replaced the list and such a pin vanished everywhere.
        int kept = 0;
        try
        {
            float now = Time.unscaledTime;
            if (!_seenByClients.TryGetValue(mine.id, out var seen))
            {
                // Nothing recorded for this case (it came with the join save): the client has had all of it.
                _seenByClients[mine.id] = seen = new Dictionary<string, float>();
                foreach (var k in KeysOf(mine)) seen[k] = 0f;
            }
            var theirs = KeysOf(incoming);
            foreach (var k in theirs) seen[k] = 0f; // the client has these
            var ours = mine.caseElements;
            // Pins on both boards, field by field: a position/colour of the client's pin that equals what we sent it
            // (now or until a moment ago) wasn't changed by the client, so ours stands - we may have moved the pin
            // since. Anything else is the client's change and is taken. Window state is always ours.
            int stood = 0;
            if (ours != null && incoming.caseElements != null)
            {
                _sentPins.TryGetValue(mine.id, out var sent);
                var byKey = new Dictionary<string, Case.CaseElement>();
                for (int i = 0; i < ours.Count; i++)
                    if (ours[i] != null) byKey[KeyOf(ours[i])] = ours[i];
                var theirList = incoming.caseElements;
                for (int i = 0; i < theirList.Count; i++)
                {
                    var e = theirList[i];
                    if (e == null || !byKey.TryGetValue(KeyOf(e), out var h)) continue;
                    e.w = h.w; e.resPos = h.resPos; e.resPiv = h.resPiv; e.sdk = h.sdk;
                    List<(PinState s, float until)> hist = null;
                    if (sent == null || !sent.TryGetValue(KeyOf(e), out hist) || hist == null) continue; // never sent: the client's
                    var cs = StateOf(e);
                    bool pos = false, col = false, co = false, min = false;
                    foreach (var entry in hist)
                    {
                        if (entry.until != float.MaxValue && now - entry.until > SeenGrace) continue; // too old to be what the client had
                        if (SamePos(entry.s, cs)) pos = true;
                        if (entry.s.color == cs.color) col = true;
                        if (entry.s.co == cs.co) co = true;
                        if (entry.s.m == cs.m) min = true;
                    }
                    if (pos && !SamePos(cs, StateOf(h))) { e.v = h.v; stood++; }
                    if (col && e.color != h.color) { e.color = h.color; stood++; }
                    if (co) e.co = h.co;
                    if (min) e.m = h.m;
                }
            }
            if (stood > 0) Plugin.Event($"CASE board edit for {mine.id}: kept {stood} position/colour change(s) of ours on pins the client hadn't touched");
            if (ours != null && incoming.caseElements != null)
                for (int i = 0; i < ours.Count; i++)
                {
                    var e = ours[i];
                    if (e == null) continue;
                    string k = KeyOf(e);
                    if (theirs.Contains(k)) continue;
                    if (seen.TryGetValue(k, out var since) && now - since > SeenGrace) continue; // the client had it and removed it
                    incoming.caseElements.Add(e);
                    theirs.Add(k);
                    kept++;
                }
            var gone = new List<string>();
            foreach (var kv in seen) if (!theirs.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (var k in gone) seen.Remove(k);
        }
        catch (Exception e) { Plugin.Event($"CASE board edit for {mine.id}: per-pin merge failed ({e.Message}); taking the client's board as it is"); }
        if (kept > 0) Plugin.Event($"CASE board edit for {mine.id}: kept {kept} pin(s) of ours the client hadn't seen yet");
        mine.caseElements = incoming.caseElements;
        mine.stringColours = incoming.stringColours;
        // Hidden connections: ours plus what the client hid and minus what it showed again since our last send.
        if (!MergeHidden(mine, HiddenOf(incoming))) mine.hiddenConnections = incoming.hiddenConnections;
        if (!string.IsNullOrEmpty(incoming.name)) mine.name = incoming.name;
        if (panel.activeCase != null && panel.activeCase.Pointer == mine.Pointer)
        {
            // SetActiveCase returns at once when the case is already active (seen: the host saw a client's pins only
            // after reopening the board). UpdatePinned is the game's own redraw: it drops pins whose element is gone,
            // spawns the new ones and redraws the strings.
            panel.UpdatePinned();
            panel.UpdateStrings();
            RefreshWindows(mine);
        }
        // Resync: the merged case goes straight back to everyone (the editor included), so every board shows the
        // host's state and the editor sees its edit confirmed. MarkDirty is off while applying, so set it directly.
        _dirty.Add(mine.id);
        _flushAt = Time.unscaledTime;
        Plugin.Event($"CASE board edit for {mine.id} \"{mine.name}\" merged: {mine.caseElements?.Count ?? 0} pins; resyncing everyone");
        return true;
    }

    /// <summary>
    /// Hidden connections, per entry instead of per list: <paramref name="target"/>'s list stands, and the other
    /// side's changes since the common base (<see cref="_hiddenBase"/>) are applied to it - what it hid is added, what
    /// it showed again is removed. False without a base (the caller then takes the other side's list whole, as before).
    /// </summary>
    static bool MergeHidden(Case target, HashSet<string> other)
    {
        try
        {
            if (!_hiddenBase.TryGetValue(target.id, out var was) || was == null) return false;
            var list = target.hiddenConnections;
            if (list == null) return false;
            foreach (var s in other)
                if (!was.Contains(s) && !list.Contains(s)) list.Add(s);
            foreach (var s in was)
                if (!other.Contains(s)) list.Remove(s);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Event($"CASE {target.id}: hidden-connection merge failed: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// After the active case's pins were replaced (new CaseElement objects, new pin controllers): bring the open
    /// evidence windows back in line. A window keeps the element it was pinned as (currentPinnedCaseElement, and
    /// forcedPinnedCaseElement when it was opened from a pin); the needle only unpins while that element's pin is
    /// alive (PinFolderButtonController.ForcePointerUp), so a window left on a replaced element showed "pinned" and
    /// could not be unpinned, or showed "unpinned" for evidence the other player had just pinned.
    /// InfoWindow.PinnedUpdateCheck is the game's own recheck (what SetActiveCase runs for every window); it runs
    /// here after the pins exist. A forced element is first moved to the new element with the same evidence and keys.
    /// </summary>
    static void RefreshWindows(Case active)
    {
        try
        {
            var windows = InterfaceController.Instance?.activeWindows;
            if (windows == null || active == null) return;
            var els = active.caseElements;
            for (int i = 0; i < windows.Count; i++)
            {
                var w = windows[i];
                if (w == null || w.WasCollected) continue;
                try
                {
                    var forced = w.forcedPinnedCaseElement;
                    if (forced != null && els != null && forced.caseID == active.id)
                    {
                        string k = KeyOf(forced);
                        bool still = false;
                        Case.CaseElement same = null;
                        for (int j = 0; j < els.Count; j++)
                        {
                            var e = els[j];
                            if (e == null) continue;
                            if (e.Pointer == forced.Pointer) { still = true; break; }
                            if (same == null && KeyOf(e) == k) same = e;
                        }
                        if (!still && same != null) w.forcedPinnedCaseElement = same;
                    }
                    w.PinnedUpdateCheck();
                }
                catch (Exception e) { Plugin.Event($"CASE window refresh failed: {e.Message}"); }
            }
        }
        catch (Exception e) { Plugin.Event($"CASE window refresh failed: {e.Message}"); }
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
