using System;
using System.Collections.Generic;
using HarmonyLib;
using SodCoop.Core;

namespace SodCoop.Net;

/// <summary>
/// Shared case knowledge (design: one shared mission). Evidence and facts are generated from the
/// same city on every machine, so a discovery is sent as its identifier and replayed on the others:
/// found evidence/facts, evidence discoveries (lives at, works at, …) and data-key merges
/// (e.g. a fingerprint tied to a person). The host relays; replaying is idempotent and not echoed.
/// Known gap: evidence created at runtime (B2 world ids) only matches once spawns are synced.
/// </summary>
public static class CaseSync
{
    [ThreadStatic] static bool _applying;
    internal static bool Applying => _applying;
    static Dictionary<string, Fact> _facts;
    static int _factsIndexedAt = -1;

    public static int Sent { get; private set; }
    public static int Applied { get; private set; }
    public static int Unknown { get; private set; }

    internal static void Report(CaseEventKind kind, string id, int a = 0, int b = 0)
    {
        var s = NetSession.Current;
        if (s == null || _applying || string.IsNullOrEmpty(id) || PlayerRegistry.Local == null || !World.Ready) return;
        s.SendCaseEvent(new CaseEventMsg { ByPlayer = PlayerRegistry.Local.Id.Value, Kind = kind, Id = id, A = a, B = b });
        Sent++;
    }

    /// <summary>
    /// Events whose evidence/fact doesn't exist here yet (e.g. facts a side job creates once its record arrives).
    /// Retried after each applied job record, dropped after <see cref="PendingMaxAge"/>.
    /// </summary>
    static readonly List<(CaseEventMsg msg, float at)> _pending = new();
    const float PendingMaxAge = 600f;
    const int PendingMax = 2000;
    static readonly HashSet<string> _unknownLogged = new();

    public static void Apply(CaseEventMsg m)
    {
        if (!World.Ready) return;
        if (TryApply(m))
        {
            Applied++;
            if (m.Kind == CaseEventKind.EvidenceFound) NoteEvidence(m.ByPlayer);
            return;
        }
        Unknown++;
        if (_pending.Count < PendingMax) _pending.Add((m, UnityEngine.Time.unscaledTime));
        // Diagnostics: which evidence/facts don't exist here (case files "not updated on pickup").
        if (_unknownLogged.Count < 30 && _unknownLogged.Add(m.Id))
            Plugin.Event($"CASE waiting: {m.Kind} for '{m.Id}' (from P{m.ByPlayer}) doesn't exist here yet");
    }

    // "X found new evidence" fired up to 16 times in a few minutes: group it, at most once per 15 s per player.
    const float EvidenceNoticeWindow = 15f;
    static readonly Dictionary<byte, (int count, float firstAt)> _evidenceNotices = new();

    static void NoteEvidence(byte byPlayer)
    {
        _evidenceNotices[byPlayer] = _evidenceNotices.TryGetValue(byPlayer, out var e)
            ? (e.count + 1, e.firstAt) : (1, UnityEngine.Time.unscaledTime);
    }

    /// <summary>Per frame: show grouped evidence notices.</summary>
    static float _nextRetry;

    public static void FlushNotices()
    {
        // Also retry waiting events now and then: an item's evidence can become creatable once its room loads here.
        if (_pending.Count > 0 && UnityEngine.Time.unscaledTime >= _nextRetry)
        {
            _nextRetry = UnityEngine.Time.unscaledTime + 5f;
            RetryPending();
        }
        if (_evidenceNotices.Count == 0) return;
        float now = UnityEngine.Time.unscaledTime;
        foreach (var by in new List<byte>(_evidenceNotices.Keys))
        {
            var (count, firstAt) = _evidenceNotices[by];
            if (now - firstAt < EvidenceNoticeWindow && count < 10) continue;
            _evidenceNotices.Remove(by);
            Notify.Show(count == 1 ? $"{Notify.NameOf(by)} found new evidence" : $"{Notify.NameOf(by)} found {count} new pieces of evidence",
                InterfaceControls.Icon.lookingGlass);
        }
    }

    public static void RetryPending()
    {
        if (_pending.Count == 0 || !World.Ready) return;
        float now = UnityEngine.Time.unscaledTime;
        int done = 0;
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var (m, at) = _pending[i];
            if (now - at > PendingMaxAge) { _pending.RemoveAt(i); continue; }
            if (!TryApply(m)) continue;
            _pending.RemoveAt(i);
            Applied++;
            done++;
        }
        if (done > 0) Plugin.Event($"CASE applied {done} earlier event(s) that were waiting for their evidence/fact ({_pending.Count} still waiting)");
    }

    /// <summary>False if the evidence/fact isn't known here (yet).</summary>
    static bool TryApply(CaseEventMsg m)
    {
        _applying = true;
        try
        {
            if (m.Kind == CaseEventKind.FactFound)
            {
                var f = FindFact(m.Id);
                if (f == null) return false;
                if (!f.isFound) f.SetFound(true);
                return true;
            }
            if (m.Kind >= CaseEventKind.NoteText) return NoteSync.Apply(m);
            var dict = GameplayController.Instance?.evidenceDictionary;
            if (dict == null) return false;
            if (!dict.TryGetValue(m.Id, out var ev) || ev == null) ev = CreateOnDemand(m.Id);
            if (ev == null) return false;
            switch (m.Kind)
            {
                case CaseEventKind.EvidenceFound: if (!ev.isFound) ev.SetFound(true); break;
                case CaseEventKind.Discovery: ev.AddDiscovery((Evidence.Discovery)m.A); break;
                case CaseEventKind.MergeKeys: ev.MergeDataKeys((Evidence.DataKey)m.A, (Evidence.DataKey)m.B); break;
            }
            return true;
        }
        catch (Exception e) { Plugin.Event($"case event {m.Kind} {m.Id} failed: {e.Message}"); return true; }
        finally { _applying = false; }
    }

    /// <summary>
    /// Some evidence is created on demand and its id encodes its parameters, so it can be rebuilt from
    /// the id: time evidence (<c>EvidenceCreator.GetTimeEvidence(string)</c>, ids like "time|43.893|43.893||-1|-1").
    /// </summary>
    internal static Evidence CreateOnDemand(string id)
    {
        if (id.StartsWith(NoteSync.StickyNotePrefix)) return NoteSync.CreateStickyNote(id);
        if (!id.StartsWith("time|"))
        {
            // Item evidence ("I<interactable id>", dates…) is created lazily, when someone first inspects the item:
            // the host's player read a case file, ours never did, so every update for it waited forever. The game's
            // own lookup creates it on demand from the id.
            try
            {
                if (Toolbox.Instance != null && Toolbox.Instance.TryGetEvidence(id, out var made) && made != null) { CreatedOnDemand++; return made; }
            }
            catch (Exception e) { Plugin.Event($"CASE could not create evidence '{id}': {e.Message}"); }
            return null;
        }
        var t = EvidenceCreator.Instance?.GetTimeEvidence(id);
        if (t != null) CreatedOnDemand++;
        return t;
    }

    public static int CreatedOnDemand { get; private set; }

    static CaseSync() => World.Reloaded += () => { _facts = null; _factsIndexedAt = -1; _pending.Clear(); };

    static Fact FindFact(string id)
    {
        var list = GameplayController.Instance?.factList;
        if (list == null) return null;
        if (_facts == null || _factsIndexedAt != list.Count)
        {
            _facts = new Dictionary<string, Fact>();
            for (int i = 0; i < list.Count; i++)
            {
                var f = list[i];
                if (f == null) continue;
                try { _facts[f.GetIdentifier()] = f; } catch { /* some facts can't name themselves yet */ }
            }
            _factsIndexedAt = list.Count;
        }
        return _facts.TryGetValue(id, out var fact) ? fact : null;
    }

    [HarmonyPatch(typeof(CaseComponent), nameof(CaseComponent.SetFound))]
    static class Found
    {
        static void Prefix(CaseComponent __instance, out bool __state) => __state = __instance.isFound;

        static void Postfix(CaseComponent __instance, bool newVal, bool __state)
        {
            if (!newVal || __state || NetSession.Current == null) return;
            try
            {
                var ev = __instance.TryCast<Evidence>();
                if (ev != null) { Report(CaseEventKind.EvidenceFound, ev.evID); return; }
                var fact = __instance.TryCast<Fact>();
                if (fact != null) Report(CaseEventKind.FactFound, fact.GetIdentifier());
            }
            catch (Exception e) { Plugin.Event($"case sync failed: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(Evidence), nameof(Evidence.AddDiscovery))]
    static class Discovery
    {
        static void Postfix(Evidence __instance, Evidence.Discovery disc)
        {
            if (NetSession.Current != null) Report(CaseEventKind.Discovery, __instance.evID, (int)disc);
        }
    }

    [ThreadStatic] static int _mergeDepth;

    static void MergePrefix() => _mergeDepth++;

    static void MergePostfix(Evidence ev, Evidence.DataKey keyOne, Evidence.DataKey keyTwo)
    {
        // The EvidenceLocation override may call base: report only the outermost call.
        if (--_mergeDepth == 0 && NetSession.Current != null) Report(CaseEventKind.MergeKeys, ev.evID, (int)keyOne, (int)keyTwo);
    }

    [HarmonyPatch(typeof(Evidence), nameof(Evidence.MergeDataKeys))]
    static class Merge
    {
        static void Prefix() => MergePrefix();
        static void Postfix(Evidence __instance, Evidence.DataKey keyOne, Evidence.DataKey keyTwo) => MergePostfix(__instance, keyOne, keyTwo);
    }

    [HarmonyPatch(typeof(EvidenceLocation), nameof(EvidenceLocation.MergeDataKeys))]
    static class MergeLocation
    {
        static void Prefix() => MergePrefix();
        static void Postfix(EvidenceLocation __instance, Evidence.DataKey keyOne, Evidence.DataKey keyTwo) => MergePostfix(__instance, keyOne, keyTwo);
    }

    /// <summary>Debug for F11: counts, plus one unfound evidence id to test with.</summary>
    public static string Describe()
    {
        var dict = GameplayController.Instance?.evidenceDictionary;
        if (dict == null) return "no evidence";
        int found = 0; string sample = null;
        foreach (var kv in dict)
        {
            if (kv.Value == null) continue;
            if (kv.Value.isFound) found++;
            else if (sample == null) sample = kv.Key;
        }
        return $"evidence {found}/{dict.Count} found, sample unfound '{sample}', case sent={Sent} applied={Applied} createdOnDemand={CreatedOnDemand} unknown={Unknown}";
    }
}
