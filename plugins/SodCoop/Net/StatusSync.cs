using System;
using System.Collections.Generic;
using HarmonyLib;
using SodCoop.Core;
using UnityEngine;

namespace SodCoop.Net;

/// <summary>
/// The crime status of a client player, which the host's world decides but the client must feel (MsgType 41):
/// <list type="bullet">
/// <item><b>Trespass with keys and guest passes</b> (client → host): the host judged a client's body with
///   <c>Human.IsTrespassing</c> on a borrowed citizen, ignoring the client's keyring (<c>Actor.keyring</c>) and guest passes
///   (<c>GameplayController.guestPasses</c>), so a client with a key was an intruder. The client's own game already
///   answers this correctly (<c>Player.IsTrespassing</c>), so it sends its verdict and the host uses it for that body.</item>
/// <item><b>Pursuit</b> (host → client, 1 Hz): the citizens chasing a client's body on the host. The client marks its
///   puppets of them (<c>NewAIController.persuit/persuitTarget</c> = its own Player) so its own status/HUD code sees the chase.</item>
/// <item><b>Wanted in building</b> (both ways, host relays): <c>StatusController.SetWantedInBuilding</c>.</item>
/// <item><b>Fines</b> (host → client): a fine the host books (<c>StatusController.AddFineRecord</c>) at an address where a
///   client's body is and the host player isn't belongs to that client: moved there.</item>
/// </list>
/// </summary>
public static class StatusSync
{
    const byte KindTrespass = 1; // client → host: bool trespassing, int escalation
    const byte KindPursuers = 2; // host → client: int n, n × int humanId
    const byte KindWanted = 3;   // both: int buildingId, float time
    const byte KindFine = 4;     // host → client: int addressId, int objectId, byte crime, bool confirmed, int penalty

    [ThreadStatic] static bool _applying;

    // host: each client's own trespass verdict
    static readonly Dictionary<PlayerId, (bool trespassing, int escalation, float at)> _verdicts = new();
    const float VerdictMaxAge = 6f;

    // client: last sent verdict / puppets we marked as pursuers
    static bool _sentTrespass;
    static int _sentEscalation = -1;
    static float _nextTrespassSend;
    static readonly HashSet<int> _markedPursuers = new();

    static float _nextPursuers;

    static NetSession S => NetSession.Current;
    static bool IsHost => S?.Role == NetSession.SessionRole.Host;
    static bool IsClient => S?.Role == NetSession.SessionRole.Client;

    /// <summary>Per frame from NetSession.Update (both roles, after welcome on clients).</summary>
    public static void Tick(float now)
    {
        if (S == null || !World.Ready) return;
        if (IsClient) SendTrespassVerdict(now);
        else if (now >= _nextPursuers) { _nextPursuers = now + 1f; SendPursuers(); }
    }

    static void SendTrespassVerdict(float now)
    {
        var me = Player.Instance;
        if (me == null || me.currentRoom == null) return;
        // The game's own verdict for our player (keys, guest passes, opening hours…).
        int e = 0;
        bool t;
        try { t = me.IsTrespassing(me.currentRoom, out e, true); }
        catch { return; }
        if (t == _sentTrespass && e == _sentEscalation && now < _nextTrespassSend) return;
        _sentTrespass = t;
        _sentEscalation = e;
        _nextTrespassSend = now + 2f; // keep-alive: the host only trusts fresh verdicts
        S.SendExtra(ExtraWire.Write(ExtraWire.Status, KindTrespass, w => { w.Write(t); w.Write(e); }));
    }

    static void SendPursuers()
    {
        var citizens = CityData.Instance?.citizenDirectory;
        if (citizens == null) return;
        foreach (var pl in PlayerRegistry.All)
        {
            if (pl.IsLocal || pl.RemoteBody?.Human == null || !S.TryPeerOf(pl.Id, out var peer)) continue;
            var body = pl.RemoteBody.Human;
            var ids = new List<int>();
            for (int i = 0; i < citizens.Count; i++)
            {
                var c = citizens[i];
                var ai = c != null ? c.ai : null;
                if (ai == null || !ai.persuit || ai.persuitTarget == null || ai.persuitTarget.Pointer != body.Pointer) continue;
                ids.Add(c.humanID);
            }
            S.SendExtra(ExtraWire.Write(ExtraWire.Status, KindPursuers, w => { w.Write(ids.Count); foreach (var id in ids) w.Write(id); }), peer);
        }
    }

    internal static void OnPacket(NetSession s, PeerId from, PlayerId? fromPlayer, byte[] data)
    {
        using var r = ExtraWire.Read(data, out var kind);
        switch (kind)
        {
            case KindTrespass when s.Role == NetSession.SessionRole.Host && fromPlayer != null:
                bool t = r.ReadBoolean();
                int e = r.ReadInt32();
                if (!_verdicts.TryGetValue(fromPlayer.Value, out var old) || old.trespassing != t)
                    Plugin.Event($"STATUS {fromPlayer} says it is {(t ? $"trespassing (escalation {e})" : "not trespassing")} (its keys/guest passes)");
                _verdicts[fromPlayer.Value] = (t, e, Time.unscaledTime);
                break;
            case KindPursuers when s.Role == NetSession.SessionRole.Client:
                int n = r.ReadInt32();
                var now = new HashSet<int>();
                for (int i = 0; i < n; i++) now.Add(r.ReadInt32());
                ApplyPursuers(now);
                break;
            case KindWanted:
                int buildingId = r.ReadInt32();
                float time = r.ReadSingle();
                if (s.Role == NetSession.SessionRole.Host)
                {
                    if (fromPlayer == null) return;
                    s.SendExtraExcept(data, from);
                }
                ApplyWanted(buildingId, time);
                break;
            case KindFine when s.Role == NetSession.SessionRole.Client:
                int addressId = r.ReadInt32(), objectId = r.ReadInt32();
                var crime = (StatusController.CrimeType)r.ReadByte();
                bool confirmed = r.ReadBoolean();
                int penalty = r.ReadInt32();
                ApplyFine(addressId, objectId, crime, confirmed, penalty);
                break;
        }
    }

    static void ApplyPursuers(HashSet<int> ids)
    {
        var me = Player.Instance;
        var dict = CityData.Instance?.citizenDictionary;
        if (me == null || dict == null) return;
        foreach (var id in _markedPursuers)
            if (!ids.Contains(id) && dict.TryGetValue(id, out var h) && h?.ai != null && h.ai.persuitTarget != null && h.ai.persuitTarget.Pointer == me.Pointer)
            {
                h.ai.persuit = false;
                h.ai.persuitTarget = null;
            }
        foreach (var id in ids)
            if (dict.TryGetValue(id, out var h) && h?.ai != null)
            {
                h.ai.persuit = true;
                h.ai.persuitTarget = me.Cast<Actor>();
            }
        if (ids.Count > 0 && _markedPursuers.Count == 0) Notify.Show("You're being chased", InterfaceControls.Icon.run);
        _markedPursuers.Clear();
        _markedPursuers.UnionWith(ids);
    }

    static NewBuilding FindBuilding(int id)
    {
        var list = HighlanderSingleton<CityBuildings>.Instance?.buildingDirectory;
        if (list == null) return null;
        for (int i = 0; i < list.Count; i++) if (list[i] != null && list[i].buildingID == id) return list[i];
        return null;
    }

    static void ApplyWanted(int buildingId, float time)
    {
        var b = FindBuilding(buildingId);
        if (b == null || StatusController.Instance == null) return;
        _applying = true;
        try { StatusController.Instance.SetWantedInBuilding(b, time); }
        finally { _applying = false; }
        Plugin.Event($"STATUS wanted in building {buildingId} for {time:F2}");
    }

    static void ApplyFine(int addressId, int objectId, StatusController.CrimeType crime, bool confirmed, int penalty)
    {
        var sc = StatusController.Instance;
        if (sc == null || CityData.Instance?.addressDictionary == null || !CityData.Instance.addressDictionary.TryGetValue(addressId, out var address)) return;
        Interactable obj = null;
        if (objectId >= 0) CityData.Instance.savableInteractableDictionary?.TryGetValue(objectId, out obj);
        _applying = true;
        try { sc.AddFineRecord(address, obj, crime, confirmed, penalty, false); }
        finally { _applying = false; }
        Plugin.Event($"STATUS fined for {crime} at address {addressId} (by the host's world)");
        Notify.Show($"Fine: {crime}", InterfaceControls.Icon.money);
    }

    // ------------------------------------------------------------------ patches

    /// <summary>Host: a client's body is judged by that client's own verdict (keys, guest passes) while it is fresh.</summary>
    [HarmonyPatch(typeof(Human), nameof(Human.IsTrespassing))]
    static class Verdict
    {
        static bool Prefix(Human __instance, ref bool __result, ref int trespassEscalation)
        {
            if (!IsHost) return true;
            var owner = PlayerRegistry.RemoteOwnerOf(__instance);
            if (owner == null || !_verdicts.TryGetValue(owner.Id, out var v) || Time.unscaledTime - v.at > VerdictMaxAge) return true;
            __result = v.trespassing;
            trespassEscalation = v.escalation;
            return false;
        }
    }

    [HarmonyPatch(typeof(StatusController), nameof(StatusController.SetWantedInBuilding))]
    static class Wanted
    {
        static void Postfix(NewBuilding b, float time)
        {
            if (_applying || S == null || b == null || !World.Ready) return;
            int id = b.buildingID;
            S.SendExtra(ExtraWire.Write(ExtraWire.Status, KindWanted, w => { w.Write(id); w.Write(time); }));
        }
    }

    /// <summary>Host: a fine at an address where only a client's body is goes to that client.</summary>
    [HarmonyPatch(typeof(StatusController), nameof(StatusController.AddFineRecord))]
    static class Fine
    {
        static bool Prefix(NewAddress address, Interactable obj, StatusController.CrimeType crime, bool confirm, int forcedPenalty)
        {
            if (!IsHost || _applying || address == null || !World.Ready) return true;
            var host = Player.Instance;
            if (host != null && host.currentGameLocation != null && host.currentGameLocation.thisAsAddress != null &&
                host.currentGameLocation.thisAsAddress.Pointer == address.Pointer) return true; // the host is there: the host's fine
            foreach (var pl in PlayerRegistry.All)
            {
                var body = pl.RemoteBody?.Human;
                var loc = body != null ? body.currentGameLocation : null;
                if (pl.IsLocal || loc?.thisAsAddress == null || loc.thisAsAddress.Pointer != address.Pointer || !S.TryPeerOf(pl.Id, out var peer)) continue;
                int addressId = address.id, objId = obj != null ? obj.id : -1;
                S.SendExtra(ExtraWire.Write(ExtraWire.Status, KindFine, w =>
                {
                    w.Write(addressId); w.Write(objId); w.Write((byte)crime); w.Write(confirm); w.Write(forcedPenalty);
                }), peer);
                Plugin.Event($"STATUS fine {crime} at address {addressId} is {pl.Id}'s: sent there");
                return false; // not the host's fine
            }
            return true;
        }
    }

    public static void Forget(PlayerId id) => _verdicts.Remove(id);
}
