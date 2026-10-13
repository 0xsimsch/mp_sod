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
///   <c>ObjectiveTrigger.Trigger</c> → <c>Objective.Complete</c>). <c>Complete</c> also runs the objective's effects
///   (side job phase / hand-in via <c>SideJobController.SideJobObjectiveComplete</c>, ransom/cover-up steps on
///   <c>MurderController</c>, <c>ChapterController.LoadPart</c>). A client therefore doesn't complete locally: it asks
///   the host, which completes (effects included) and tells everyone; clients then run <c>Complete</c> for the UI with
///   those effects suppressed.</item>
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

    const byte KindTriggerRequest = 1; // client → host: int caseId, str objective name
    const byte KindCompleted = 2;      // host → clients: int caseId, str name, byte byPlayer
    const byte KindAnswers = 1;        // (CaseResolve) client → host: int caseId, int n, n × (str name, str input, str evidence)
    const byte KindResolved = 2;       // (CaseResolve) host → clients: int caseId, byte byPlayer

    /// <summary>Applying the host's decision: world effects inside Complete/Resolve are skipped.</summary>
    [ThreadStatic] static bool _applying;
    public static bool SuppressEffects => _applying;

    /// <summary>Client: requests already sent (CheckingLoop keeps re-triggering until the host's answer arrives).</summary>
    static readonly Dictionary<(int, string), float> _requested = new();
    const float RequestRepeat = 3f;

    /// <summary>Host: who caused the completion being run now (for the notification).</summary>
    [ThreadStatic] static byte _completingFor;

    /// <summary>Host: resolutions waiting for their case record to reach clients (CaseRecordSync debounces 1 s).</summary>
    static readonly List<(int caseId, byte by, float at)> _pendingResolved = new();
    const float ResolvedDelay = 1.5f;

    public static int Requests { get; private set; }
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
        if (type == ExtraWire.Objective && kind == KindTriggerRequest && s.Role == NetSession.SessionRole.Host)
        {
            int caseId = r.ReadInt32();
            string name = ExtraWire.Str(r);
            var obj = FindObjective(FindCase(caseId), name);
            if (obj == null) { Plugin.Event($"OBJ {fromPlayer} completed '{name}' of case {caseId}, which the host doesn't have"); return; }
            if (obj.isComplete) return;
            Plugin.Event($"OBJ {fromPlayer} completed '{name}' of case {caseId}: completing it for everyone");
            _completingFor = fromPlayer?.Value ?? 0;
            try { obj.Complete(); } // host's postfix broadcasts
            finally { _completingFor = 0; }
        }
        else if (type == ExtraWire.Objective && kind == KindCompleted && s.Role == NetSession.SessionRole.Client)
        {
            int caseId = r.ReadInt32();
            string name = ExtraWire.Str(r);
            byte by = r.ReadByte();
            _requested.Remove((caseId, name));
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

    static Case.ResolveQuestion FindQuestion(Case c, string name)
    {
        var qs = c?.resolveQuestions;
        if (qs == null) return null;
        for (int i = 0; i < qs.Count; i++) if (qs[i] != null && qs[i].name == name) return qs[i];
        return null;
    }

    // ------------------------------------------------------------------ patches

    [HarmonyPatch(typeof(Objective), nameof(Objective.Complete))]
    static class CompletePatch
    {
        static bool Prefix(Objective __instance)
        {
            if (!IsClient || _applying || !World.Ready) return true;
            var c = __instance.thisCase;
            if (c == null) return true; // not part of a case (tutorial etc.): local
            var key = (c.id, __instance.name);
            float now = Time.unscaledTime;
            if (_requested.TryGetValue(key, out var at) && now - at < RequestRepeat) return false;
            _requested[key] = now;
            S.SendExtra(ExtraWire.Write(ExtraWire.Objective, KindTriggerRequest, w => { w.Write(c.id); ExtraWire.Str(w, __instance.name); }));
            Requests++;
            Plugin.Event($"OBJ '{__instance.name}' of case {c.id} reached here; asking the host");
            return false; // the host completes it (with its effects) and tells us
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
