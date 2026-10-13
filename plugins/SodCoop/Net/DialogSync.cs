using System;
using System.IO;
using System.Text;
using HarmonyLib;
using SodCoop.Core;

namespace SodCoop.Net;

/// <summary>
/// Conversations in co-op.
/// <list type="bullet">
/// <item>Job dialog options ("ask X about the job"): a side job adds them to citizens with
///   <c>SideJob.AddDialogOption</c> → <c>EvidenceWitness.AddDialogOption</c> and remembers them in <c>SideJob.dialog</c>.
///   <c>SideJob.SetupNonSerializedData</c> re-adds every remembered option (0x747cb0 calls EvidenceWitness::AddDialogOption),
///   and JobSync calls it for every job record, so the client gets them. Because records repeat every 10 s, the options
///   would pile up: <see cref="DedupeJobDialog"/> removes the copies.</item>
/// <item>Outcomes: the conversation runs on the speaker's machine (TalkSync holds the citizen for them). A client's
///   <c>DialogController.ExecuteDialog</c> (the single entry for every chosen option: success roll, cost, the preset's
///   effect, job/murder hooks) is replayed on the host with the client's body as speaker and the client's result forced
///   (<c>ForceSuccess</c>), so the citizen, job and murder state change there too. <c>OnDialogEnd</c> (job photo reveal,
///   cover-up/ransom decisions) is replayed the same way. While replaying, the host's own money, inventory, windows,
///   pausing and inspection are left alone: those effects belong to the client and already happened there.</item>
/// <item>Not replayed: options that cost money or use the phone UI (per-player economy and keypads), and anything the
///   other syncs already carry (found evidence/facts → CaseSync, items → ItemSync, the client's guest passes and keys →
///   StatusSync's trespass verdict). Citizen attitude (<c>Human.SpeechHistory</c>) stays per machine.</item>
/// </list>
/// </summary>
public static class DialogSync
{
    public const byte MsgDialogExecute = 45;
    public const byte MsgDialogEnd = 46;
    const ushort Magic = 0x4D53; // same header as Protocol.Write

    [ThreadStatic] static bool _applying;
    /// <summary>True while the host replays a client's dialog (side effects on the host's own player are skipped).</summary>
    public static bool Replaying => _applying;

    public static int Sent { get; private set; }
    public static int Applied { get; private set; }

    static NetSession Session => NetSession.Current;
    static bool IsClient => Session?.Role == NetSession.SessionRole.Client;
    static bool IsHost => Session?.Role == NetSession.SessionRole.Host;
    static bool IsLocal(Actor a) => a != null && Player.Instance != null && a.Pointer == Player.Instance.Pointer;

    // ---------------------------------------------------------------- job dialog options

    /// <summary>
    /// After a job record is applied (SetupNonSerializedData re-adds its options): keep one option per
    /// (citizen, key, preset, job). Call from JobSync.Apply after SetupNonSerializedData / LoadJob.
    /// </summary>
    public static void DedupeJobDialog(SideJob job)
    {
        if (job?.dialog == null) return;
        int removed = 0;
        try
        {
            for (int i = 0; i < job.dialog.Count; i++)
            {
                var ad = job.dialog[i];
                var h = ad?.GetHuman();
                var ew = h?.evidenceEntry;
                if (ew?.dialogOptions == null || !ew.dialogOptions.TryGetValue(ad.key, out var list) || list == null) continue;
                string preset = ad.dialogRef;
                bool seen = false;
                for (int k = 0; k < list.Count; k++)
                {
                    var o = list[k];
                    if (o?.preset == null || o.preset.name != preset || o.jobRef == null || o.jobRef.jobID != job.jobID) continue;
                    if (!seen) { seen = true; continue; }
                    list.RemoveAt(k--);
                    removed++;
                }
            }
        }
        catch (Exception e) { Plugin.Event($"DIALOG dedupe for job {job.jobID} failed: {e.Message}"); }
        if (removed > 0) Plugin.Event($"DIALOG job {job.jobID}: removed {removed} repeated dialog option(s)");
    }

    // ---------------------------------------------------------------- wire

    static byte[] WriteExecute(int humanId, string preset, int jobId, int roomId, bool success)
    {
        using var ms = new MemoryStream(64);
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(Magic); w.Write(MsgDialogExecute);
        w.Write(humanId); w.Write(preset ?? ""); w.Write(jobId); w.Write(roomId); w.Write(success);
        w.Flush();
        return ms.ToArray();
    }

    static byte[] WriteEnd(string preset, int targetHumanId, int targetInteractableId, int jobId)
    {
        using var ms = new MemoryStream(64);
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(Magic); w.Write(MsgDialogEnd);
        w.Write(preset ?? ""); w.Write(targetHumanId); w.Write(targetInteractableId); w.Write(jobId);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Host: a client's dialog message (type 45 or 46).</summary>
    public static void OnPacket(PlayerId from, byte[] data)
    {
        if (!IsHost || !World.Ready || data.Length < 3) return;
        try
        {
            using var ms = new MemoryStream(data, 3, data.Length - 3);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            if (data[2] == MsgDialogExecute) ReplayExecute(from, r.ReadInt32(), r.ReadString(), r.ReadInt32(), r.ReadInt32(), r.ReadBoolean());
            else if (data[2] == MsgDialogEnd) ReplayEnd(from, r.ReadString(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
        }
        catch (Exception e) { Plugin.Event($"DIALOG packet from {from} failed: {e.Message}"); }
    }

    // ---------------------------------------------------------------- client: report

    /// <summary>Per-player options stay local: money (bribes, purchases, fees, loans) and the phone/keypad UI.</summary>
    static bool Worth(EvidenceWitness.DialogOption o)
    {
        var p = o?.preset;
        if (p == null) return false;
        if (o.jobRef != null || p.isJobDetails) return true;
        return p.cost <= 0 && !p.usePercentageCost && !p.telephoneCallOption;
    }

    [HarmonyPatch(typeof(DialogController), nameof(DialogController.ExecuteDialog))]
    static class Execute
    {
        static void Postfix(EvidenceWitness.DialogOption dialog, Interactable saysTo, Actor saidBy, bool __result)
        {
            if (!IsClient || _applying || !World.Ready || !IsLocal(saidBy) || !Worth(dialog)) return;
            try
            {
                var target = saysTo?.isActor?.TryCast<Human>() ?? saysTo?.belongsTo;
                if (target == null) return;
                Session?.SendWorld(WriteExecute(target.humanID, dialog.preset.name, dialog.jobRef != null ? dialog.jobRef.jobID : -1,
                    dialog.roomRef != null ? dialog.roomRef.roomID : -1, __result));
                Sent++;
                Plugin.Event($"DIALOG '{dialog.preset.name}' with citizen {target.humanID} ({(__result ? "success" : "fail")}); sent to the host");
            }
            catch (Exception e) { Plugin.Event($"DIALOG report failed: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(DialogController), nameof(DialogController.OnDialogEnd))]
    static class End
    {
        static void Postfix(string dialogPresetStr, Interactable saysToInteractable, Actor saidBy, int jobRef)
        {
            if (!IsClient || _applying || !World.Ready || !IsLocal(saidBy) || string.IsNullOrEmpty(dialogPresetStr)) return;
            try
            {
                var target = saysToInteractable?.isActor?.TryCast<Human>() ?? saysToInteractable?.belongsTo;
                Session?.SendWorld(WriteEnd(dialogPresetStr, target != null ? target.humanID : -1,
                    saysToInteractable != null ? saysToInteractable.id : -1, jobRef));
                Sent++;
            }
            catch (Exception e) { Plugin.Event($"DIALOG end report failed: {e.Message}"); }
        }
    }

    // ---------------------------------------------------------------- host: replay

    static Human BodyOf(PlayerId p) => PlayerRegistry.TryGet(p, out var np) ? np.RemoteBody?.Human : null;

    static SideJob JobById(int id) =>
        id >= 0 && SideJobController.Instance?.allJobsDictionary != null && SideJobController.Instance.allJobsDictionary.TryGetValue(id, out var j) ? j : null;

    static void ReplayExecute(PlayerId from, int humanId, string presetName, int jobId, int roomId, bool success)
    {
        var dict = CityData.Instance?.citizenDictionary;
        if (dict == null || !dict.TryGetValue(humanId, out var h) || h == null) { Plugin.Event($"DIALOG citizen {humanId} unknown here"); return; }
        var body = BodyOf(from);
        if (body == null) return;
        var job = JobById(jobId);
        var option = FindOption(h, presetName, job);
        if (option == null)
        {
            if (!Toolbox.Instance.LoadDataFromResources<DialogPreset>(presetName, out var preset) || preset == null)
            {
                Plugin.Event($"DIALOG preset '{presetName}' not found");
                return;
            }
            NewRoom room = null;
            if (roomId >= 0) CityData.Instance.roomDictionary?.TryGetValue(roomId, out room);
            option = new EvidenceWitness.DialogOption { preset = preset, jobRef = job, roomRef = room };
        }
        _applying = true;
        try
        {
            DialogController.Instance.ExecuteDialog(option, h.interactable, h.currentNode, body,
                success ? DialogController.ForceSuccess.success : DialogController.ForceSuccess.fail, null);
            Applied++;
            Plugin.Event($"DIALOG {from} '{presetName}' with citizen {humanId} replayed ({(success ? "success" : "fail")})");
        }
        catch (Exception e) { Plugin.Event($"DIALOG replay of '{presetName}' failed: {e.Message}"); }
        finally { _applying = false; }
    }

    static EvidenceWitness.DialogOption FindOption(Human h, string presetName, SideJob job)
    {
        var options = h.evidenceEntry?.dialogOptions;
        if (options == null) return null;
        foreach (var kv in options)
        {
            var list = kv.Value;
            if (list == null) continue;
            for (int i = 0; i < list.Count; i++)
            {
                var o = list[i];
                if (o?.preset == null || o.preset.name != presetName) continue;
                if (job != null && (o.jobRef == null || o.jobRef.jobID != job.jobID)) continue;
                return o;
            }
        }
        return null;
    }

    static void ReplayEnd(PlayerId from, string presetStr, int targetHumanId, int targetInteractableId, int jobId)
    {
        var body = BodyOf(from);
        if (body == null) return;
        Interactable target = null;
        if (targetHumanId >= 0 && CityData.Instance.citizenDictionary.TryGetValue(targetHumanId, out var th)) target = th?.interactable;
        if (target == null && targetInteractableId >= 0) CityData.Instance.savableInteractableDictionary?.TryGetValue(targetInteractableId, out target);
        _applying = true;
        try
        {
            DialogController.Instance.OnDialogEnd(null, presetStr, target, body, jobId);
            Applied++;
            Plugin.Event($"DIALOG {from} ended '{presetStr}' (job {jobId}) replayed");
        }
        catch (Exception e) { Plugin.Event($"DIALOG end replay of '{presetStr}' failed: {e.Message}"); }
        finally { _applying = false; }
    }

    // While replaying a client's choice on the host, the host's own player isn't the one talking: leave its money,
    // inventory, windows, pause state and inspection alone (they already changed on the client).

    [HarmonyPatch(typeof(GameplayController), nameof(GameplayController.AddMoney))]
    static class NoHostMoney { static bool Prefix() => !_applying; }

    [HarmonyPatch(typeof(FirstPersonItemController), nameof(FirstPersonItemController.EmptySlot))]
    static class NoHostInventory { static bool Prefix() => !_applying; }

    [HarmonyPatch(typeof(InterfaceController), nameof(InterfaceController.SpawnWindow))]
    static class NoHostWindow { static bool Prefix() => !_applying; }

    [HarmonyPatch(typeof(SessionData), nameof(SessionData.PauseGame))]
    static class NoHostPause { static bool Prefix() => !_applying; }

    [HarmonyPatch(typeof(ActionController), nameof(ActionController.Inspect))]
    static class NoHostInspect { static bool Prefix() => !_applying; }
}
