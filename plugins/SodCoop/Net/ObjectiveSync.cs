using System;
using System.Collections.Generic;
using HarmonyLib;
using SodCoop.Core;
using UnityEngine;

namespace SodCoop.Net;

/// <summary>
/// Objectives and case resolution are decided by the host (design: one shared case).
/// <list type="bullet">
/// <item><b>Objectives</b>: each machine evaluates triggers against its *own* player (<c>Objective.CheckingLoop</c> →
///   <c>ObjectiveTrigger.Trigger</c> → progress → <c>Objective.Complete</c>). <c>Complete</c> also runs the objective's
///   effects (side job phase / hand-in via <c>SideJobController.SideJobObjectiveComplete</c>, ransom/cover-up steps on
///   <c>MurderController</c>, <c>ChapterController.LoadPart</c>). On a client, case objectives never progress or complete
///   by themselves: a trigger the client's check finds met becomes a <i>fact</i> sent to the host ("trigger T of
///   objective O is met for me"). The host checks it by trigger type (<see cref="Judge"/>): a location against where the
///   client's body is on the host, a personal action (opened a file, used a computer…) taken on trust, and shared state
///   (pins, evidence, case status…) never, because the host's own check sees it. Accepted facts run the host's own
///   <c>Trigger</c>; the host then sends progress and completion, which clients apply with the effects suppressed.
///   (Before 0.9.10 a client asked the host to complete the whole objective, and the host trusted it: a crime-scene
///   objective that fired on the client while it set the case up removed the crime scene for everyone.)</item>
/// <item><b>Hand-in</b>: <c>ActionController.HandInCase</c> triggers the case's submit objective (covered above). Before
///   that, a client sends its resolve answers (<c>Case.ResolveQuestion.input/inputtedEvidence</c>), so the host grades
///   what the client typed.</item>
/// <item><b>Resolution</b>: only the host runs <c>Case.Resolve</c> (reward, arrest via <c>Human.RemoveFromWorld</c>,
///   <c>Murder.SetMurderState</c>, status). It then tells clients (after the updated case record), which run
///   <c>Resolve</c> for the reveal screen and payout with world effects suppressed. <c>MurderController.OnCaseSolved</c>
///   never runs on a client.</item>
/// </list>
/// </summary>
public static class ObjectiveSync
{
    /// <summary>
    /// Reward rule (design decision pending): true = every player receives the full money reward of a resolved case or
    /// job (each machine runs the game's own <c>AddMoney</c> with its message). false = only the host is paid.
    /// </summary>
    public const bool PayEveryone = true;

    // kind 1 (client asks the host to complete an objective) was retired with protocol v38
    const byte KindCompleted = 2;      // host → clients: int caseId, str name, byte byPlayer
    const byte KindTriggerFact = 3;    // client → host: int caseId, str objective, str trigger name, int trigger type
    const byte KindProgress = 4;       // host → clients: int caseId, str objective, float progress
    const byte KindAnswers = 1;        // (CaseResolve) client → host: int caseId, int n, n × (str name, str input, str evidence)
    const byte KindResolved = 2;       // (CaseResolve) host → clients: int caseId, byte byPlayer

    /// <summary>Applying the host's decision: world effects inside Complete/Resolve are skipped.</summary>
    [ThreadStatic] static bool _applying;
    public static bool SuppressEffects => _applying;

    /// <summary>Client: facts already sent (CheckingLoop re-triggers every check until the host answers).</summary>
    static readonly Dictionary<(int, string, string), float> _requested = new();
    const float RequestRepeat = 3f;
    /// <summary>Client: direct completions dropped (not through a trigger), logged once per objective.</summary>
    static readonly HashSet<(int, string)> _droppedLogged = new();

    /// <summary>Host: who caused the completion being run now (for the notification).</summary>
    [ThreadStatic] static byte _completingFor;

    /// <summary>Host: resolutions waiting for their case record to reach clients (CaseRecordSync debounces 1 s).</summary>
    static readonly List<(int caseId, byte by, float at)> _pendingResolved = new();
    const float ResolvedDelay = 1.5f;

    public static int Requests { get; private set; }
    public static int FactsAccepted { get; private set; }
    public static int FactsRejected { get; private set; }
    public static int Completed { get; private set; }

    static NetSession S => NetSession.Current;
    static bool IsHost => S?.Role == NetSession.SessionRole.Host;
    static bool IsClient => S?.Role == NetSession.SessionRole.Client;

    static Case FindCase(int id)
    {
        var cases = CasePanelController.Instance?.activeCases;
        if (cases == null) return null;
        for (int i = 0; i < cases.Count; i++) if (cases[i] != null && cases[i].id == id) return cases[i];
        return null;
    }

    static Objective FindObjective(Case c, string name)
    {
        if (c == null) return null;
        foreach (var list in new[] { c.currentActiveObjectives, c.inactiveCurrentObjectives, c.waitForObjectives, c.endedObjectives })
        {
            if (list == null) continue;
            for (int i = 0; i < list.Count; i++) if (list[i] != null && list[i].name == name) return list[i];
        }
        return null;
    }

    /// <summary>Per frame (host): send resolutions whose case record has had time to go out.</summary>
    public static void Tick(float now)
    {
        if (!IsHost || _pendingResolved.Count == 0) return;
        for (int i = _pendingResolved.Count - 1; i >= 0; i--)
        {
            var (caseId, by, at) = _pendingResolved[i];
            if (now < at) continue;
            _pendingResolved.RemoveAt(i);
            S.SendExtra(ExtraWire.Write(ExtraWire.CaseResolve, KindResolved, w => { w.Write(caseId); w.Write(by); }));
            Plugin.Event($"OBJ case {caseId} resolved; told the clients");
        }
    }

    internal static void OnPacket(NetSession s, PeerId from, PlayerId? fromPlayer, byte type, byte[] data)
    {
        using var r = ExtraWire.Read(data, out var kind);
        if (type == ExtraWire.Objective && kind == KindTriggerFact && s.Role == NetSession.SessionRole.Host)
        {
            int caseId = r.ReadInt32();
            string objName = ExtraWire.Str(r), trigName = ExtraWire.Str(r);
            int trigType = r.ReadInt32();
            OnFact(fromPlayer, caseId, objName, trigName, trigType);
        }
        else if (type == ExtraWire.Objective && kind == KindProgress && s.Role == NetSession.SessionRole.Client)
        {
            int caseId = r.ReadInt32();
            string name = ExtraWire.Str(r);
            float progress = r.ReadSingle();
            var obj = FindObjective(FindCase(caseId), name);
            if (obj == null || obj.isComplete || obj.progress >= progress) return;
            _applying = true;
            try { obj.SetProgress(progress); }
            catch (Exception e) { Plugin.Event($"OBJ applying progress of '{name}' failed: {e.Message}"); }
            finally { _applying = false; }
        }
        else if (type == ExtraWire.Objective && kind == KindCompleted && s.Role == NetSession.SessionRole.Client)
        {
            int caseId = r.ReadInt32();
            string name = ExtraWire.Str(r);
            byte by = r.ReadByte();
            var obj = FindObjective(FindCase(caseId), name);
            if (obj == null || obj.isComplete) return;
            _applying = true;
            try { obj.Complete(); Completed++; }
            catch (Exception e) { Plugin.Event($"OBJ applying '{name}' failed: {e.Message}"); }
            finally { _applying = false; }
            if (PlayerRegistry.Local != null && by != PlayerRegistry.Local.Id.Value)
                Notify.Show($"{Notify.NameOf(by)} completed an objective", InterfaceControls.Icon.star);
        }
        else if (type == ExtraWire.CaseResolve && kind == KindAnswers && s.Role == NetSession.SessionRole.Host)
        {
            int caseId = r.ReadInt32();
            int n = r.ReadInt32();
            var c = FindCase(caseId);
            for (int i = 0; i < n; i++)
            {
                string qName = ExtraWire.Str(r), input = ExtraWire.Str(r), evidence = ExtraWire.Str(r);
                var q = FindQuestion(c, qName);
                if (q == null) continue;
                q.input = input;
                q.inputtedEvidence = evidence;
                try { q.UpdateValid(c); } catch { /* grading happens again on resolve */ }
            }
            Plugin.Event($"OBJ {fromPlayer} handed in case {caseId} with {n} answer(s)");
        }
        else if (type == ExtraWire.CaseResolve && kind == KindResolved && s.Role == NetSession.SessionRole.Client)
        {
            int caseId = r.ReadInt32();
            byte by = r.ReadByte();
            var c = FindCase(caseId);
            if (c == null) { Plugin.Event($"OBJ resolved case {caseId} not found here"); return; }
            _applying = true;
            try { c.Resolve(); } // reveal screen + (PayEveryone) payout; arrest/murder state/status are the host's
            catch (Exception e) { Plugin.Event($"OBJ applying resolution of case {caseId} failed: {e.Message}"); }
            finally { _applying = false; }
            Notify.Show($"Case \"{c.name}\" solved", InterfaceControls.Icon.star);
        }
    }

    // ------------------------------------------------------------------ facts (host)

    enum FactRule
    {
        /// <summary>Accepted if the client's body is there (or, for escape triggers, not there) on the host.</summary>
        Location,
        /// <summary>Something only the client's machine sees (UI, own inventory, a call…): taken on trust.</summary>
        Trust,
        /// <summary>Shared state the host's own check already sees (synced board, doors, switches, case status…).</summary>
        HostOnly,
    }

    static FactRule RuleOf(Objective.ObjectiveTriggerType t)
    {
        switch (t)
        {
            case Objective.ObjectiveTriggerType.goToNode:
            case Objective.ObjectiveTriggerType.goToRoom:
            case Objective.ObjectiveTriggerType.goToAddress:
            case Objective.ObjectiveTriggerType.goToPublicFacingAddress:
            case Objective.ObjectiveTriggerType.exploreCrimeScene:
            case Objective.ObjectiveTriggerType.escapeGameLocation:
            case Objective.ObjectiveTriggerType.escapeBuilding:
                return FactRule.Location;
            case Objective.ObjectiveTriggerType.evidencePinned:          // board edits merge into the host's case
            case Objective.ObjectiveTriggerType.switchStateTrue:         // switches flipped by players are synced
            case Objective.ObjectiveTriggerType.switchStateFalse:
            case Objective.ObjectiveTriggerType.switchStateTrueForType:
            case Objective.ObjectiveTriggerType.roomLightOn:
            case Objective.ObjectiveTriggerType.unlockDoor:              // door state is synced
            case Objective.ObjectiveTriggerType.interactableRemoved:     // removals are reported to the host
            case Objective.ObjectiveTriggerType.gamePaused:
            case Objective.ObjectiveTriggerType.gameUnpaused:
            case Objective.ObjectiveTriggerType.noMoreObjectives:
            case Objective.ObjectiveTriggerType.successsfulSolve:
            case Objective.ObjectiveTriggerType.submitCase:
            case Objective.ObjectiveTriggerType.waitForCaseProcessing:
            case Objective.ObjectiveTriggerType.onCompleteJob:
            case Objective.ObjectiveTriggerType.ifValidRansomBriefcase:
            case Objective.ObjectiveTriggerType.ifNoValidRansomBriefcase:
            case Objective.ObjectiveTriggerType.kidnapperHasValidBriefcase:
            case Objective.ObjectiveTriggerType.victimIsFreed:
            case Objective.ObjectiveTriggerType.nothing:
                return FactRule.HostOnly;
            default:
                return FactRule.Trust;
        }
    }

    const float NodeRange = 3f;

    /// <summary>Is the fact true for this player as far as the host can tell?</summary>
    static bool Judge(Objective.ObjectiveTrigger t, NetPlayer player, out string why)
    {
        switch (RuleOf(t.triggerType))
        {
            case FactRule.HostOnly: why = "the host's own check decides this one"; return false;
            case FactRule.Trust: why = "player action, taken on trust"; return true;
        }
        var body = player?.Body;
        if (body == null) { why = "no body for that player here"; return false; }
        bool inside;
        string where;
        if (t.triggerType == Objective.ObjectiveTriggerType.escapeBuilding)
        {
            var b = t.gameLocation?.building ?? t.room?.gameLocation?.building ?? t.node?.gameLocation?.building;
            if (b == null) { why = "trigger has no building"; return false; }
            inside = body.currentBuilding != null && body.currentBuilding.Pointer == b.Pointer;
            where = $"building {b.name}";
        }
        else if (t.node != null)
        {
            inside = (body.currentNode != null && body.currentNode.Pointer == t.node.Pointer)
                     || Vector3.Distance(body.transform.position, t.node.position) < NodeRange;
            where = $"node {t.node.position}";
        }
        else if (t.room != null)
        {
            inside = body.currentRoom != null && body.currentRoom.Pointer == t.room.Pointer;
            where = $"room {t.room.roomID}";
        }
        else if (t.gameLocation != null)
        {
            inside = body.currentGameLocation != null && body.currentGameLocation.Pointer == t.gameLocation.Pointer;
            where = $"location {t.gameLocation.name}";
        }
        else { why = "trigger has no location (room/node/address) on the host"; return false; }
        bool escape = t.triggerType is Objective.ObjectiveTriggerType.escapeGameLocation or Objective.ObjectiveTriggerType.escapeBuilding;
        bool ok = escape ? !inside : inside;
        string at = body.currentGameLocation != null ? body.currentGameLocation.name : "nowhere";
        why = ok ? $"body is {(escape ? "out of" : "at")} {where}" : $"body is {(escape ? "still at" : "not at")} {where} (it is at {at})";
        return ok;
    }

    static Objective.ObjectiveTrigger FindTrigger(Objective obj, string name, int type)
    {
        var list = obj?.queueElement?.triggers;
        if (list == null) return null;
        Objective.ObjectiveTrigger fallback = null;
        for (int i = 0; i < list.Count; i++)
        {
            var t = list[i];
            if (t == null || (int)t.triggerType != type) continue;
            if (t.name == name) return t;
            fallback ??= t;
        }
        return fallback;
    }

    /// <summary>Host: rejected facts already logged, so a client re-sending every 3 s doesn't flood the log.</summary>
    static readonly Dictionary<(int, string, string), float> _rejectLogged = new();

    static void OnFact(PlayerId? from, int caseId, string objName, string trigName, int trigType)
    {
        var obj = FindObjective(FindCase(caseId), objName);
        string what = $"'{objName}' / {(Objective.ObjectiveTriggerType)trigType} '{trigName}' of case {caseId}";
        if (obj == null) { LogReject(caseId, objName, trigName, $"OBJ fact from {from}: {what}, which the host doesn't have"); return; }
        if (obj.isComplete || obj.isCancelled) return;
        var trig = FindTrigger(obj, trigName, trigType);
        if (trig == null) { LogReject(caseId, objName, trigName, $"OBJ fact from {from}: {what}: no such trigger here"); return; }
        if (trig.triggered) return;
        NetPlayer player = null;
        if (from != null) PlayerRegistry.TryGet(from.Value, out player);
        if (!Judge(trig, player, out var why))
        {
            FactsRejected++;
            LogReject(caseId, objName, trigName, $"OBJ fact from {from}: {what} rejected: {why}");
            return;
        }
        FactsAccepted++;
        Plugin.Event($"OBJ fact from {from}: {what} accepted ({why})");
        _completingFor = from?.Value ?? 0;
        try { trig.Trigger(false); } // progress → Complete; the postfixes tell the clients
        catch (Exception e) { Plugin.Event($"OBJ triggering {what} failed: {e.Message}"); }
        finally { _completingFor = 0; }
    }

    static void LogReject(int caseId, string objName, string trigName, string message)
    {
        var key = (caseId, objName, trigName);
        float now = Time.unscaledTime;
        if (_rejectLogged.TryGetValue(key, out var at) && now - at < 30f) return;
        _rejectLogged[key] = now;
        Plugin.Event(message);
    }

    static Case.ResolveQuestion FindQuestion(Case c, string name)
    {
        var qs = c?.resolveQuestions;
        if (qs == null) return null;
        for (int i = 0; i < qs.Count; i++) if (qs[i] != null && qs[i].name == name) return qs[i];
        return null;
    }

    /// <summary>Set by CaseRecordSync while it removes the objectives of a case copy it replaces.</summary>
    internal static bool Retiring;

    static bool IsCurrentCase(Case c)
    {
        var cases = CasePanelController.Instance?.activeCases;
        if (cases == null) return false;
        for (int i = 0; i < cases.Count; i++)
            if (cases[i] != null && cases[i].Pointer == c.Pointer) return true;
        return false;
    }

    // ------------------------------------------------------------------ patches

    /// <summary>
    /// Client: a met trigger of a case objective becomes a fact for the host instead of progress here. Objectives
    /// outside a case (tutorial) stay local.
    /// </summary>
    [HarmonyPatch(typeof(Objective.ObjectiveTrigger), nameof(Objective.ObjectiveTrigger.Trigger))]
    static class TriggerPatch
    {
        static bool Prefix(Objective.ObjectiveTrigger __instance)
        {
            if (Retiring) return false;
            if (!IsClient || _applying || !World.Ready) return true;
            var objs = __instance.addedToObjectives;
            if (objs == null || objs.Count == 0) return true;
            bool inCase = false;
            for (int i = 0; i < objs.Count; i++)
            {
                var o = objs[i];
                var c = o?.thisCase;
                if (c == null) continue;
                inCase = true;
                // Only the case copy we show: every host re-send builds a new copy, and the old one is an orphan.
                if (o.isComplete || o.isCancelled || !IsCurrentCase(c)) continue;
                SendFact(c.id, o.name, __instance);
            }
            return !inCase;
        }
    }

    static void SendFact(int caseId, string objName, Objective.ObjectiveTrigger t)
    {
        string trigName = t.name ?? "";
        var key = (caseId, objName, trigName);
        float now = Time.unscaledTime;
        if (_requested.TryGetValue(key, out var at) && now - at < RequestRepeat) return;
        bool first = !_requested.ContainsKey(key);
        _requested[key] = now;
        int type = (int)t.triggerType;
        S.SendExtra(ExtraWire.Write(ExtraWire.Objective, KindTriggerFact, w =>
        {
            w.Write(caseId); ExtraWire.Str(w, objName); ExtraWire.Str(w, trigName); w.Write(type);
        }));
        Requests++;
        if (first) Plugin.Event($"OBJ '{objName}' of case {caseId}: {t.triggerType} '{trigName}' met here; told the host");
    }

    [HarmonyPatch(typeof(Objective), nameof(Objective.Complete))]
    static class CompletePatch
    {
        static bool Prefix(Objective __instance)
        {
            if (Retiring) return false; // an old copy being taken down: no completion, no effects, nothing sent
            if (!IsClient || _applying || !World.Ready) return true;
            var c = __instance.thisCase;
            if (c == null) return true; // not part of a case (tutorial etc.): local
            // Case objectives complete only on the host's word. Triggers go to the host as facts (TriggerPatch);
            // anything completing an objective directly is dropped, and logged once so we learn about the path.
            if (_droppedLogged.Add((c.id, __instance.name)))
                Plugin.Event($"OBJ '{__instance.name}' of case {c.id} tried to complete here directly; left to the host");
            return false;
        }

        static void Postfix(Objective __instance)
        {
            if (!IsHost || !__instance.isComplete || __instance.thisCase == null || !World.Ready) return;
            int caseId = __instance.thisCase.id;
            string name = __instance.name;
            byte by = _completingFor;
            S.SendExtra(ExtraWire.Write(ExtraWire.Objective, KindCompleted, w => { w.Write(caseId); ExtraWire.Str(w, name); w.Write(by); }));
            Completed++;
            if (by != 0) Notify.Show($"{Notify.NameOf(by)} completed an objective", InterfaceControls.Icon.star);
        }
    }

    /// <summary>Case objectives' progress is the host's: clients only apply it, the host sends partial steps.</summary>
    [HarmonyPatch(typeof(Objective), nameof(Objective.SetProgress))]
    static class ProgressPatch
    {
        static bool Prefix(Objective __instance)
        {
            if (Retiring) return false;
            return !IsClient || _applying || !World.Ready || __instance.thisCase == null;
        }

        static void Postfix(Objective __instance)
        {
            if (!IsHost || !World.Ready || __instance.thisCase == null || __instance.isComplete) return;
            int caseId = __instance.thisCase.id;
            string name = __instance.name;
            float progress = __instance.progress;
            if (progress <= 0f) return;
            S.SendExtra(ExtraWire.Write(ExtraWire.Objective, KindProgress, w => { w.Write(caseId); ExtraWire.Str(w, name); w.Write(progress); }));
        }
    }

    /// <summary>Client hand-in: the host grades the answers this player typed, so send them first.</summary>
    [HarmonyPatch(typeof(ActionController), nameof(ActionController.HandInCase))]
    static class HandIn
    {
        static void Prefix()
        {
            if (!IsClient || !World.Ready) return;
            var c = CasePanelController.Instance?.activeCase;
            var qs = c?.resolveQuestions;
            if (qs == null) return;
            S.SendExtra(ExtraWire.Write(ExtraWire.CaseResolve, KindAnswers, w =>
            {
                w.Write(c.id);
                w.Write(qs.Count);
                for (int i = 0; i < qs.Count; i++)
                {
                    var q = qs[i];
                    ExtraWire.Str(w, q?.name);
                    ExtraWire.Str(w, q?.input);
                    ExtraWire.Str(w, q?.inputtedEvidence);
                }
            }));
            Plugin.Event($"OBJ handing in case {c.id}: answers sent to the host");
        }
    }

    [HarmonyPatch(typeof(Case), nameof(Case.Resolve))]
    static class ResolvePatch
    {
        static bool Prefix() => !IsClient || _applying; // clients resolve only on the host's word

        static void Postfix(Case __instance)
        {
            if (!IsHost || !World.Ready) return;
            CaseRecordSync.MarkChanged(__instance); // solved state, rank, answers go out with the record
            _pendingResolved.Add((__instance.id, _completingFor, Time.unscaledTime + ResolvedDelay));
            Notify.Broadcast($"Case \"{__instance.name}\" solved", InterfaceControls.Icon.star);
        }
    }

    /// <summary>Host: status changes (submitted, processing, closable…) go out with the case record.</summary>
    [HarmonyPatch(typeof(Case), nameof(Case.SetStatus))]
    static class StatusChanged
    {
        static void Postfix(Case __instance) { if (IsHost && World.Ready) CaseRecordSync.MarkChanged(__instance); }
    }

    // --- effects that belong to the host's world: skipped while a client replays Complete/Resolve

    [HarmonyPatch(typeof(SideJobController), nameof(SideJobController.SideJobObjectiveComplete))]
    static class NoJobEffects { static bool Prefix() => !_applying; }

    [HarmonyPatch(typeof(ChapterController), nameof(ChapterController.LoadPart), typeof(int), typeof(bool), typeof(bool))]
    static class NoChapterEffects { static bool Prefix() => !_applying; }

    [HarmonyPatch(typeof(ChapterController), nameof(ChapterController.LoadPart), typeof(string))]
    static class NoChapterEffectsByName { static bool Prefix() => !_applying; }

    [HarmonyPatch(typeof(MurderController), nameof(MurderController.VictimFreed))]
    static class NoVictimFreed { static bool Prefix() => !_applying; }

    [HarmonyPatch(typeof(MurderController), nameof(MurderController.TriggerSuccessfulCoverUp))]
    static class NoCoverUp { static bool Prefix() => !_applying; }

    [HarmonyPatch(typeof(MurderController), nameof(MurderController.TriggerRansomDelivery))]
    static class NoRansomDelivery { static bool Prefix() => !_applying; }

    [HarmonyPatch(typeof(MurderController), nameof(MurderController.KidnapperCollectsRansom))]
    static class NoRansomCollect { static bool Prefix() => !_applying; }

    [HarmonyPatch(typeof(MurderController), nameof(MurderController.KidnapperCollectedRansom))]
    static class NoRansomCollected { static bool Prefix() => !_applying; }

    [HarmonyPatch(typeof(Human), nameof(Human.RemoveFromWorld))]
    static class NoArrest { static bool Prefix() => !_applying; }

    [HarmonyPatch(typeof(MurderController.Murder), nameof(MurderController.Murder.SetMurderState))]
    static class NoMurderState { static bool Prefix() => !_applying; }

    /// <summary>The murder director is the host's: a client never runs "case solved" itself.</summary>
    [HarmonyPatch(typeof(MurderController), nameof(MurderController.OnCaseSolved))]
    static class NoCaseSolvedOnClient { static bool Prefix() => !IsClient; }

    [HarmonyPatch(typeof(GameplayController), nameof(GameplayController.AddMoney))]
    static class Payout
    {
        // Replaying the host's resolution: the payout is ours only under PayEveryone.
        static bool Prefix() => !_applying || PayEveryone;
    }
}
