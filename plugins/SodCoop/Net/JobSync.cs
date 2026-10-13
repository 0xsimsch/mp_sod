using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime;
using SodCoop.Core;
using UnityEngine;

namespace SodCoop.Net;

/// <summary>
/// Side jobs (job postings, notes, "Scope-Out Assassin"…) are one shared set, owned by the host:
/// <list type="bullet">
/// <item>Only the host generates jobs (<c>SideJobController.JobCreationCheck</c> is off on clients, like the murder director).</item>
/// <item>A job goes to the others in the game's save form (<c>[Serializable]</c> + <c>JsonUtility</c>, with its concrete
///   type) whenever it changes: posted, accepted, info acquired, hand-in set, completed, ended, failed. Accepted jobs are
///   re-sent every 10 s as a backstop. The receiver loads a new job like a save does (<c>SaveStateController.LoadJob</c>:
///   setup, state, acquisition facts) and overwrites a known one in place.</item>
/// <item>A client taking the job paper doesn't accept locally (its case id would clash with the host's): it asks the host,
///   which accepts; the case, its pins, the leads and the job then reach everyone through the usual syncs.</item>
/// <item>Job info a client acquires itself (calling the poster, …) is sent like any other change; last writer wins.</item>
/// </list>
/// Facts created by a job don't exist elsewhere until its record arrives; <see cref="CaseSync"/> retries the case events
/// it couldn't place after each applied job.
/// </summary>
public static class JobSync
{
    const float Debounce = 2f; // after CaseRecordSync's 1 s, so the job's case is there when the job links to it
    const float Resend = 10f;

    [ThreadStatic] static bool _applying;
    static readonly Dictionary<int, string> _lastLogged = new();
    static bool _acceptingForClient;
    static readonly HashSet<int> _dirty = new();
    static float _flushAt, _nextResend;

    public static int Sent { get; private set; }
    public static int Applied { get; private set; }
    public static int AcceptRequests { get; private set; }

    static NetSession Session => NetSession.Current;
    static bool IsHost => Session?.Role == NetSession.SessionRole.Host;
    static bool IsClient => Session?.Role == NetSession.SessionRole.Client;

    static void MarkDirty(SideJob job)
    {
        if (job == null || _applying || Session == null || !World.Ready) return;
        _dirty.Add(job.jobID);
        _flushAt = Time.unscaledTime + Debounce;
    }

    /// <summary>Per frame (host, or a welcomed client): send changed jobs, and the host's accepted jobs every 10 s.</summary>
    public static void Flush(float now, Action<string, string> send)
    {
        var jobs = SideJobController.Instance?.allJobsDictionary;
        if (jobs == null) return;
        if (IsHost && now >= _nextResend)
        {
            _nextResend = now + Resend;
            foreach (var kv in jobs)
                if (kv.Value != null && kv.Value.accepted && kv.Value.state != SideJob.JobState.ended) _dirty.Add(kv.Key);
            if (_flushAt < now) _flushAt = now;
        }
        if (_dirty.Count == 0 || now < _flushAt) return;
        foreach (var id in _dirty)
        {
            if (!jobs.TryGetValue(id, out var job) || job == null) continue;
            try
            {
                send(TypeName(job), JsonUtility.ToJson(job));
                Sent++;
            }
            catch (Exception e) { Plugin.Event($"JOB {id} could not be sent: {e.Message}"); }
        }
        _dirty.Clear();
    }

    /// <summary>The concrete job class (SideJob, SideJobAffair, …): JsonUtility needs it to rebuild the right type.</summary>
    static string TypeName(SideJob job) => job.GetIl2CppType().Name;

    static readonly Dictionary<string, Il2CppSystem.Type> _types = new();

    static Il2CppSystem.Type TypeOf(string name)
    {
        if (_types.TryGetValue(name, out var t)) return t;
        var managed = typeof(SideJob).Assembly.GetTypes().FirstOrDefault(x => x.Name == name && typeof(SideJob).IsAssignableFrom(x));
        t = managed != null ? Il2CppType.From(managed) : null;
        _types[name] = t;
        return t;
    }

    public static void Apply(string typeName, string json)
    {
        var ctrl = SideJobController.Instance;
        var save = SaveStateController.Instance;
        if (ctrl?.allJobsDictionary == null || !World.Ready) return;
        var type = TypeOf(typeName);
        if (type == null) { Plugin.Event($"JOB unknown job type {typeName}"); return; }
        _applying = true;
        try
        {
            var incoming = JsonUtility.FromJson(json, type)?.TryCast<SideJob>();
            if (incoming == null) { Plugin.Event("JOB record: JsonUtility returned nothing"); return; }
            int id = incoming.jobID;
            if (ctrl.allJobsDictionary.TryGetValue(id, out var job) && job != null)
            {
                bool wasAccepted = job.accepted;
                CopySavedState(incoming, job, typeName); // keep the instance: the controller's lists point at it
                job.SetupNonSerializedData();
                if (job.accepted && !wasAccepted) job.CreateAcqusitionFacts();
            }
            else
            {
                job = incoming;
                save?.LoadJob(job); // what loading a save does: setup, state, acquisition facts
                if (!ctrl.allJobsDictionary.ContainsKey(id)) ctrl.allJobsDictionary.Add(id, job);
            }
            if (SideJob.assignJobID <= id) SideJob.assignJobID = id + 1;
            DialogSync.DedupeJobDialog(job); // re-sent records re-add the job's dialog options
            Applied++;
            string summary = $"state={job.state} accepted={job.accepted} phase={job.phase} case={job.caseID}";
            if (!_lastLogged.TryGetValue(id, out var prev) || prev != summary) // the 10 s resends would flood the log
            {
                _lastLogged[id] = summary;
                Plugin.Event($"JOB {id} {typeName} applied: {summary}");
            }
        }
        catch (Exception e) { Plugin.Event($"JOB record failed: {e}"); }
        finally { _applying = false; }
        CaseSync.RetryPending(); // facts this job created may be what earlier case events were waiting for
    }

    /// <summary>Runtime references rebuilt by <c>SetupNonSerializedData</c>; never copied from a freshly parsed job.</summary>
    static readonly HashSet<string> RuntimeOnly = new()
    {
        "preset", "motive", "poster", "purp", "post", "activeJobItems", "thisCase", "objectiveReference",
        "hiddenItemPhoto", "chosenGooseChasePhone", "chosenMeetingPoint", "gooseChaseCall",
    };

    /// <summary>
    /// <c>JsonUtility.FromJsonOverwrite</c> fails through the interop (MissingMethodException: Il2CppSystem.Object.GetType,
    /// seen on every job update in the two-machine test), so copy the saved fields of the parsed job onto ours.
    /// Interop fields are properties; game-object references and dictionaries are runtime state and are skipped.
    /// </summary>
    static void CopySavedState(SideJob fromJob, SideJob toJob, string typeName)
    {
        // Both wrappers are typed SideJob; view them as the concrete class so its own fields are copied too.
        var type = typeof(SideJob).Assembly.GetTypes().FirstOrDefault(x => x.Name == typeName && typeof(SideJob).IsAssignableFrom(x)) ?? typeof(SideJob);
        var cast = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).GetMethod("TryCast").MakeGenericMethod(type);
        object from = cast.Invoke(fromJob, null) ?? fromJob, to = cast.Invoke(toJob, null) ?? toJob;
        for (var t = type; t != null && typeof(SideJob).IsAssignableFrom(t); t = t.BaseType)
            foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!prop.CanRead || !prop.CanWrite || prop.GetIndexParameters().Length > 0 || RuntimeOnly.Contains(prop.Name)) continue;
                var pt = prop.PropertyType;
                if (typeof(UnityEngine.Object).IsAssignableFrom(pt) || pt == typeof(Human) || pt == typeof(Interactable) || pt == typeof(Case)
                    || (pt.IsGenericType && pt.Name.StartsWith("Dictionary"))) continue;
                try { prop.SetValue(to, prop.GetValue(from)); }
                catch (Exception e) { Plugin.Event($"JOB field {prop.Name} not copied: {e.InnerException?.Message ?? e.Message}"); }
            }
    }

    /// <summary>Host: a client took a job paper.</summary>
    public static void OnAcceptRequest(int jobId, string who)
    {
        AcceptRequests++;
        var jobs = SideJobController.Instance?.allJobsDictionary;
        if (jobs == null || !jobs.TryGetValue(jobId, out var job) || job == null) { Plugin.Event($"JOB {who} took job {jobId}, which the host doesn't have"); return; }
        if (job.accepted) { MarkDirty(job); return; }
        Plugin.Event($"JOB {who} took job {jobId}: accepting it for everyone");
        Notify.Broadcast($"A job was taken: {job.preset?.name ?? "side job"}", InterfaceControls.Icon.document);
        _acceptingForClient = true;
        try { job.AcceptJob(); } // the postfix marks it dirty; the case and its pins go out with CaseRecordSync
        finally { _acceptingForClient = false; }
    }

    // --- patches ---

    [HarmonyPatch(typeof(SideJobController), nameof(SideJobController.JobCreationCheck))]
    static class NoJobsOnClients
    {
        static bool Prefix() => !IsClient;
    }

    /// <summary>Client: taking the paper asks the host instead of accepting here.</summary>
    [HarmonyPatch]
    static class Accept
    {
        static IEnumerable<MethodBase> TargetMethods() => JobMethods(nameof(SideJob.AcceptJob));

        static bool Prefix(SideJob __instance)
        {
            if (!IsClient || _applying) return true;
            try { Session?.SendJobAccept(__instance.jobID); }
            catch (Exception e) { Plugin.Event($"JOB accept request failed: {e.Message}"); }
            Plugin.Event($"JOB took job {__instance.jobID}; asking the host to accept it");
            return false;
        }

        static void Postfix(SideJob __instance)
        {
            if (!IsHost) return;
            MarkDirty(__instance);
            if (!_applying && !_acceptingForClient && PlayerRegistry.Local != null)
                Notify.Broadcast($"{Notify.NameOf(PlayerRegistry.Local.Id.Value)} took a job", InterfaceControls.Icon.document);
        }
    }

    /// <summary>Everything else that changes a job: send it (host: always; client: info it acquired itself).</summary>
    [HarmonyPatch]
    static class Changed
    {
        static IEnumerable<MethodBase> TargetMethods() =>
            JobMethods(nameof(SideJob.OnAcquireJobInfo), nameof(SideJob.SetJobState), nameof(SideJob.PostJob),
                       nameof(SideJob.SetHandIn), nameof(SideJob.Complete), nameof(SideJob.End), nameof(SideJob.TriggerFail));

        static void Postfix(SideJob __instance, MethodBase __originalMethod)
        {
            if (IsHost || __originalMethod.Name == nameof(SideJob.OnAcquireJobInfo)) MarkDirty(__instance);
        }
    }

    /// <summary>
    /// Clients don't run job logic: the per-frame loops (phase changes, timers), failing/completing/ending a job, posting
    /// and spawning its items are the host's. Otherwise a client's copy could fail a job, move it to another phase or
    /// spawn duplicate job items on its own; the host's job record (and spawns) bring every change instead.
    /// </summary>
    [HarmonyPatch]
    static class HostRunsJobs
    {
        static IEnumerable<MethodBase> TargetMethods() =>
            JobMethods("GameWorldLoop", "ObjectiveStateLoop", "HandleObjectiveProgress", nameof(SideJob.TriggerFail),
                       nameof(SideJob.Complete), nameof(SideJob.End), nameof(SideJob.PostJob), "SpawnItems");

        static bool Prefix() => !IsClient || _applying;
    }

    /// <summary>The named methods on SideJob and every subclass that overrides them (overrides may skip base).</summary>
    static IEnumerable<MethodBase> JobMethods(params string[] names)
    {
        var types = typeof(SideJob).Assembly.GetTypes().Where(t => typeof(SideJob).IsAssignableFrom(t));
        foreach (var t in types)
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (names.Contains(m.Name) && !m.IsAbstract) yield return m;
    }
}
