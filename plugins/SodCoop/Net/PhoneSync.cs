using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using SodCoop.Core;
using UnityEngine;

namespace SodCoop.Net;

/// <summary>Wire format for <see cref="PhoneSync"/> (message type 44, same 3-byte header as Protocol: magic, type).</summary>
public enum PhoneEventKind : byte
{
    /// <summary>Host → clients: a call was created (mirror it so the phone rings here).</summary>
    Created = 1,
    /// <summary>Host → clients: a call changed state (started = someone answered; ended).</summary>
    State = 2,
    /// <summary>Client → host: our player answered the phone of call (From, To).</summary>
    Answer = 3,
    /// <summary>Client → host: our player hung up call (From, To).</summary>
    HangUp = 4,
    /// <summary>Client → host: our player dialled To from phone From (a real telephone, not a fake/job number).</summary>
    Dial = 5,
}

public struct PhoneEventMsg
{
    public PhoneEventKind Kind;
    public byte ByPlayer;
    /// <summary>Telephone numbers (deterministic from the city, the same on every machine).</summary>
    public int From, To;
    /// <summary>Humans encoded like <see cref="DeathSync.EncodeKiller"/>: humanID, -1 none, ≤ -2 player.</summary>
    public int Caller, IntendedReceiver, Receiver;
    public byte State;
    public byte CallType;
    public string Dds, Dialog;
    public int Job;
    public byte ConvoType;
    public bool Specific;
}

public static class PhoneProtocol
{
    public const byte MsgTypePhone = 44;
    const ushort Magic = 0x4D53;

    public static byte[] Write(PhoneEventMsg m)
    {
        using var ms = new MemoryStream(64);
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(Magic); w.Write(MsgTypePhone);
        w.Write((byte)m.Kind); w.Write(m.ByPlayer);
        w.Write(m.From); w.Write(m.To);
        w.Write(m.Caller); w.Write(m.IntendedReceiver); w.Write(m.Receiver);
        w.Write(m.State); w.Write(m.CallType);
        w.Write(m.Dds ?? ""); w.Write(m.Dialog ?? "");
        w.Write(m.Job); w.Write(m.ConvoType); w.Write(m.Specific);
        w.Flush();
        return ms.ToArray();
    }

    public static PhoneEventMsg Read(byte[] d)
    {
        using var ms = new MemoryStream(d, 3, d.Length - 3);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        return new PhoneEventMsg
        {
            Kind = (PhoneEventKind)r.ReadByte(), ByPlayer = r.ReadByte(),
            From = r.ReadInt32(), To = r.ReadInt32(),
            Caller = r.ReadInt32(), IntendedReceiver = r.ReadInt32(), Receiver = r.ReadInt32(),
            State = r.ReadByte(), CallType = r.ReadByte(),
            Dds = r.ReadString(), Dialog = r.ReadString(),
            Job = r.ReadInt32(), ConvoType = r.ReadByte(), Specific = r.ReadBoolean(),
        };
    }
}

/// <summary>
/// Telephones, host-authoritative, so phone parts of side jobs work for either player:
/// <list type="bullet">
/// <item>Calls are created on the host only (citizens calling, jobs' goose-chase calls, cover-up calls). Clients block
///   their own non-player calls (<c>CreateNewCall</c>, <c>ActionController.CallSomeone</c>) and their citizens answering.</item>
/// <item>The host sends calls whose phones are near a client (or that involve a player or a job); the client creates the
///   same call locally, so the phone rings there, with a long ring time (the host ends it). A job call's
///   <c>gooseChaseCall</c> is pointed at the mirror so the client's own job logic recognises it when answered.</item>
/// <item>A client answering runs the game's own flow locally (the caller's dialog plays for the player who answered),
///   tells the host (which marks its call answered without opening any dialog on the host) and pushes the side job's
///   record when the call advanced it. Hang-ups go the same way.</item>
/// <item>A client dialling a real number runs locally and asks the host to place the same call; when the host's citizen
///   answers, the client's call is answered by that citizen and the conversation runs on the client.</item>
/// <item>Players are interchangeable as intended receivers: a job call meant for "the player" can be answered by either.</item>
/// </list>
/// On the host, remote bodies in a call are briefly not <c>isPlayer</c> while the call changes state, so the host's
/// own player never gets the client's phone dialog or active call.
/// </summary>
public static class PhoneSync
{
    const float InterestRadius = 80f;
    const float MirrorRingTime = 600f;
    const byte NoPlayer = 255;

    [ThreadStatic] static bool _applying;
    /// <summary>Host: applying a client's phone action; no dialog for the host's own player.</summary>
    static bool _suppressHostDialog;
    /// <summary>Host: calls (from, to) clients mirror, so their state changes are sent.</summary>
    static readonly HashSet<(int, int)> _mirrored = new();

    public static int Sent { get; private set; }
    public static int Applied { get; private set; }

    static NetSession Session => NetSession.Current;
    static bool IsHost => Session?.Role == NetSession.SessionRole.Host;
    static bool IsClient => Session?.Role == NetSession.SessionRole.Client;
    static bool InSession => Session != null && World.Ready;
    static bool IsLocal(Actor a) => a != null && Player.Instance != null && a.Pointer == Player.Instance.Pointer;
    static byte LocalId => PlayerRegistry.Local != null ? PlayerRegistry.Local.Id.Value : NoPlayer;

    static void Send(PhoneEventMsg m)
    {
        var s = Session;
        if (s == null) return;
        try { s.SendWorld(PhoneProtocol.Write(m)); Sent++; }
        catch (Exception e) { Plugin.Event($"PHONE send failed: {e.Message}"); }
    }

    static Telephone PhoneByNumber(int number)
    {
        var dict = CityData.Instance?.phoneDictionary;
        return dict != null && dict.TryGetValue(number, out var t) ? t : null;
    }

    static TelephoneController.PhoneCall FindCall(int from, int to)
    {
        var calls = TelephoneController.Instance?.activeCalls;
        if (calls == null) return null;
        for (int i = calls.Count - 1; i >= 0; i--)
        {
            var c = calls[i];
            if (c != null && c.from == from && c.to == to) return c;
        }
        return null;
    }

    static int Enc(Human h) => h == null ? -1 : DeathSync.EncodeKiller(h);

    /// <summary>A phone near any other player's body (host side), or a call that involves a player or a job.</summary>
    static bool Relevant(TelephoneController.PhoneCall c)
    {
        if (c.source != null && c.source.job >= 0) return true; // CallSource.job defaults to -1 (ctor at 0x534430)
        if (Doubleish(c.callerNS) || Doubleish(c.intendedReceiverNS) || Doubleish(c.recevierNS)) return true;
        foreach (var p in PlayerRegistry.All)
        {
            var body = p.RemoteBody?.Human;
            if (body == null) continue;
            var pos = body.transform.position;
            if (Near(c.fromNS, pos) || Near(c.toNS, pos)) return true;
        }
        return false;
    }

    static bool Doubleish(Human h) => h != null && (h.isPlayer || PlayerRegistry.IsRemoteBody(h));

    static bool Near(Telephone t, Vector3 pos) =>
        t?.interactable != null && (t.interactable.wPos - pos).sqrMagnitude < InterestRadius * InterestRadius;

    static PhoneEventMsg Describe(TelephoneController.PhoneCall c, PhoneEventKind kind)
    {
        var src = c.source;
        return new PhoneEventMsg
        {
            Kind = kind, ByPlayer = LocalId, From = c.from, To = c.to,
            Caller = Enc(c.callerNS), IntendedReceiver = Enc(c.intendedReceiverNS), Receiver = Enc(c.recevierNS),
            State = (byte)c.state,
            CallType = src != null ? (byte)src.callType : (byte)0,
            Dds = src?.dds ?? "", Dialog = src?.dialogGreeting != null ? src.dialogGreeting.name : src?.dialog ?? "",
            Job = src != null ? src.job : -1,
            ConvoType = src != null ? (byte)src.convoType : (byte)0,
            Specific = c.specRecevier,
        };
    }

    // ------------------------------------------------------------------ receive

    public static void OnPacket(PlayerId? sender, byte[] data)
    {
        if (!InSession) return;
        PhoneEventMsg m;
        try { m = PhoneProtocol.Read(data); }
        catch (Exception e) { Plugin.Event($"PHONE bad packet: {e.Message}"); return; }
        _applying = true;
        try
        {
            switch (m.Kind)
            {
                case PhoneEventKind.Created when IsClient: MirrorCreated(m); break;
                case PhoneEventKind.State when IsClient: MirrorState(m); break;
                case PhoneEventKind.Answer when IsHost && sender != null: HostAnswer(sender.Value, m); break;
                case PhoneEventKind.HangUp when IsHost && sender != null: HostHangUp(sender.Value, m); break;
                case PhoneEventKind.Dial when IsHost && sender != null: HostDial(sender.Value, m); break;
            }
            Applied++;
        }
        catch (Exception e) { Plugin.Event($"PHONE {m.Kind} {m.From}->{m.To} failed: {e.Message}"); }
        finally { _applying = false; _suppressHostDialog = false; }
    }

    /// <summary>"The player" on this machine for a player-encoded human: players are interchangeable as receivers.</summary>
    static Human DecodeForHere(int enc) => enc <= -2 ? Player.Instance : DeathSync.DecodeKiller(enc);

    static TelephoneController.CallSource BuildSource(PhoneEventMsg m)
    {
        var src = new TelephoneController.CallSource((TelephoneController.CallType)m.CallType, m.Dds ?? "");
        src.dialog = m.Dialog ?? "";
        src.job = m.Job;
        src.convoType = (InteractionController.ConversationType)m.ConvoType;
        if (!string.IsNullOrEmpty(m.Dialog) && Toolbox.Instance != null && Toolbox.Instance.LoadDataFromResources<DialogPreset>(m.Dialog, out var preset))
            src.dialogGreeting = preset;
        return src;
    }

    static void MirrorCreated(PhoneEventMsg m)
    {
        if (FindCall(m.From, m.To) != null) return; // ours already (e.g. we dialled it)
        var from = PhoneByNumber(m.From);
        var to = PhoneByNumber(m.To);
        if (to == null) { Plugin.Event($"PHONE no telephone {m.To} here; call not mirrored"); return; }
        var call = TelephoneController.Instance.CreateNewCall(from, to, DecodeForHere(m.Caller), DecodeForHere(m.IntendedReceiver),
            BuildSource(m), MirrorRingTime, m.Specific);
        if (call == null) return;
        // A job's goose-chase call: point the job at the mirror, so the job logic here recognises it when answered.
        if (m.Job >= 0 && SideJobController.Instance?.allJobsDictionary != null &&
            SideJobController.Instance.allJobsDictionary.TryGetValue(m.Job, out var job) && job != null)
            job.gooseChaseCall = call;
        Plugin.Event($"PHONE call {m.From}->{m.To} rings here (from the host{(m.Job >= 0 ? $", job {m.Job}" : "")})");
    }

    static void MirrorState(PhoneEventMsg m)
    {
        var call = FindCall(m.From, m.To);
        if (call == null) return;
        var state = (TelephoneController.CallState)m.State;
        if (state == TelephoneController.CallState.ended)
        {
            call.EndCall();
            Plugin.Event($"PHONE call {m.From}->{m.To} ended (host)");
            return;
        }
        if (state != TelephoneController.CallState.started || call.state == TelephoneController.CallState.started) return;
        if (m.Receiver <= -2 && m.Receiver == DeathSync.EncodeKiller(Player.Instance)) return; // we answered it ourselves
        var receiver = DeathSync.DecodeKiller(m.Receiver);
        if (receiver == null || call.toNS == null) return;
        call.toNS.SetTelephoneAnswered(receiver); // e.g. the citizen we dialled picked up: the conversation runs here
        Plugin.Event($"PHONE call {m.From}->{m.To} answered by {m.Receiver} (host)");
    }

    static Human BodyOf(PlayerId id) => PlayerRegistry.TryGet(id, out var p) ? p.Body : null;

    static void HostAnswer(PlayerId who, PhoneEventMsg m)
    {
        var call = FindCall(m.From, m.To);
        var body = BodyOf(who);
        if (call == null || body == null || call.toNS == null) { Plugin.Event($"PHONE {who} answered {m.From}->{m.To}, which the host no longer has"); return; }
        _suppressHostDialog = true;
        call.toNS.SetTelephoneAnswered(body);
        if (Player.Instance != null && Player.Instance.activeCall != null && Player.Instance.activeCall.Pointer == call.Pointer)
            Player.Instance.activeCall = null; // the host's player isn't on this call
        Plugin.Event($"PHONE {who} answered call {m.From}->{m.To}");
    }

    static void HostHangUp(PlayerId who, PhoneEventMsg m)
    {
        var call = FindCall(m.From, m.To);
        if (call == null) return;
        _suppressHostDialog = true;
        call.EndCall();
        Plugin.Event($"PHONE {who} hung up call {m.From}->{m.To}");
    }

    static void HostDial(PlayerId who, PhoneEventMsg m)
    {
        if (FindCall(m.From, m.To) != null) return;
        var from = PhoneByNumber(m.From);
        var to = PhoneByNumber(m.To);
        var body = BodyOf(who);
        if (from == null || to == null) { Plugin.Event($"PHONE {who} dialled {m.To}: no such telephone on the host"); return; }
        _suppressHostDialog = true;
        TelephoneController.Instance.CreateNewCall(from, to, body, null, BuildSource(m), 0.1f, false);
        Plugin.Event($"PHONE {who} dialled {m.From}->{m.To}; placed on the host");
    }

    // ------------------------------------------------------------------ host: report calls

    [HarmonyPatch(typeof(TelephoneController), nameof(TelephoneController.CreateNewCall),
        new[] { typeof(Telephone), typeof(Telephone), typeof(Human), typeof(Human), typeof(TelephoneController.CallSource), typeof(float), typeof(bool) })]
    static class Create
    {
        /// <summary>Clients: only the host starts calls; ours are the local player's own dialling and mirrors.</summary>
        static bool Prefix(Human caller, ref TelephoneController.PhoneCall __result)
        {
            if (!IsClient || !World.Ready || _applying || IsLocal(caller)) return true;
            __result = null;
            return false;
        }

        static void Postfix(TelephoneController.PhoneCall __result, Human caller)
        {
            if (__result == null || !InSession) return;
            try
            {
                if (IsHost)
                {
                    if (!Relevant(__result)) return;
                    _mirrored.Add((__result.from, __result.to));
                    Send(Describe(__result, PhoneEventKind.Created));
                }
                else if (IsClient && !_applying && IsLocal(caller))
                {
                    // Our player dialled. A fake number (job poster, …) is all local; a real phone needs the host's citizen.
                    var fakes = TelephoneController.Instance?.fakeTelephoneDictionary;
                    if (fakes != null && fakes.ContainsKey(__result.to)) return;
                    if (PhoneByNumber(__result.to) == null) return;
                    Send(Describe(__result, PhoneEventKind.Dial));
                }
            }
            catch (Exception e) { Plugin.Event($"PHONE report failed: {e.Message}"); }
        }
    }

    /// <summary>
    /// Host: a remote body in a call is briefly not <c>isPlayer</c> while the call changes state, so the game doesn't
    /// run the player phone dialog / active call for the host's own player. Then report the change to mirrors.
    /// </summary>
    [HarmonyPatch(typeof(TelephoneController.PhoneCall), nameof(TelephoneController.PhoneCall.SetCallState))]
    static class State
    {
        static void Prefix(TelephoneController.PhoneCall __instance, out List<Human> __state)
        {
            __state = null;
            if (!IsHost || !World.Ready) return;
            foreach (var h in new[] { __instance.callerNS, __instance.recevierNS, __instance.intendedReceiverNS })
            {
                if (h == null || !h.isPlayer || !PlayerRegistry.IsRemoteBody(h)) continue;
                h.isPlayer = false;
                (__state ??= new List<Human>()).Add(h);
            }
        }

        static void Postfix(TelephoneController.PhoneCall __instance, TelephoneController.CallState newState, List<Human> __state)
        {
            if (__state != null) foreach (var h in __state) if (h != null && !h.WasCollected) h.isPlayer = true;
            if (!IsHost || !World.Ready) return;
            var key = (__instance.from, __instance.to);
            if (!_mirrored.Contains(key)) return;
            if (newState != TelephoneController.CallState.started && newState != TelephoneController.CallState.ended) return;
            Send(Describe(__instance, PhoneEventKind.State));
            if (newState == TelephoneController.CallState.ended) _mirrored.Remove(key);
        }
    }

    /// <summary>Host: no phone dialog for the host's player while applying a client's phone action.</summary>
    [HarmonyPatch(typeof(InteractionController), nameof(InteractionController.SetDialog))]
    static class NoHostDialog
    {
        static bool Prefix() => !(_suppressHostDialog && IsHost);
    }

    // ------------------------------------------------------------------ client: block local calls, report the player's actions

    /// <summary>Clients: their citizens don't place calls (the host's do; they arrive as mirrors).</summary>
    [HarmonyPatch(typeof(ActionController), nameof(ActionController.CallSomeone))]
    static class NoLocalCitizenCalls
    {
        static bool Prefix(Actor who) => !(IsClient && World.Ready && !IsLocal(who));
    }

    /// <summary>Clients: their citizens don't answer phones; the host's answer arrives as a State message.</summary>
    [HarmonyPatch(typeof(NewAIController), nameof(NewAIController.AnswerPhone))]
    static class NoLocalCitizenAnswers
    {
        static bool Prefix() => !(IsClient && World.Ready && !_applying);
    }

    [HarmonyPatch(typeof(ActionController), nameof(ActionController.AnswerTelephone))]
    static class Answered
    {
        static void Postfix(Actor who)
        {
            if (!IsClient || !World.Ready || _applying || !IsLocal(who)) return;
            var call = Player.Instance.activeCall;
            if (call == null) return;
            Send(new PhoneEventMsg { Kind = PhoneEventKind.Answer, ByPlayer = LocalId, From = call.from, To = call.to, Job = -1, Caller = -1, IntendedReceiver = -1, Receiver = -1 });
            Plugin.Event($"PHONE answered call {call.from}->{call.to}; told the host");
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.OnReturnFromAnswerPhone))]
    static class HungUp
    {
        static void Prefix(out (int from, int to)? __state)
        {
            __state = null;
            var call = Player.Instance != null ? Player.Instance.activeCall : null;
            if (IsClient && World.Ready && !_applying && call != null) __state = (call.from, call.to);
        }

        static void Postfix((int from, int to)? __state)
        {
            if (__state == null) return;
            Send(new PhoneEventMsg { Kind = PhoneEventKind.HangUp, ByPlayer = LocalId, From = __state.Value.from, To = __state.Value.to, Job = -1, Caller = -1, IntendedReceiver = -1, Receiver = -1 });
        }
    }

    /// <summary>
    /// Client: the player's call advanced a side job here (e.g. answering the goose-chase phone). Jobs are the host's,
    /// and JobSync only reports client-side info changes, so push the job's record (the host applies and relays it).
    /// </summary>
    [HarmonyPatch(typeof(SideJob), nameof(SideJob.OnPlayerCall))]
    static class JobCall
    {
        static void Prefix(SideJob __instance, out bool __state) => __state = __instance.gooseChaseCallTriggered;

        static void Postfix(SideJob __instance, bool __state)
        {
            if (!IsClient || !World.Ready || __instance.gooseChaseCallTriggered == __state) return;
            try
            {
                Session?.SendWorld(Protocol.JobRecord(__instance.GetIl2CppType().Name, JsonUtility.ToJson(__instance)));
                Plugin.Event($"PHONE job {__instance.jobID}: goose-chase call answered here; sent the job to the host");
            }
            catch (Exception e) { Plugin.Event($"PHONE job push failed: {e.Message}"); }
        }
    }

    public static void Reset() => _mirrored.Clear();
}
