using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using SodCoop.Core;
using UnityEngine;

namespace SodCoop.Net;

/// <summary>
/// Sleeping, waiting and being knocked out all go through <c>Player.SetSpendingTimeMode(true)</c>, which speeds the clock
/// up (<c>SessionData.SetTimeSpeed</c>). In a session time is shared, so:
/// <list type="bullet">
/// <item>Time only skips while <b>every</b> player rests (sleeps, waits or is KO'd). The host decides: its own rest speeds the
///   clock up only then; otherwise the host's clock stays at normal speed. Clients report their rest state (msg 52) and
///   follow the host's clock and speed through TimeSync (their own SetTimeSpeed stays blocked there).</item>
/// <item>A player resting alone sees "waiting for &lt;partner&gt;", and the partner a notice.</item>
/// <item>KO: the game's recovery waits for game time the KO would normally skip. When time isn't skipping, a KO'd player
///   recovers after <see cref="KoRealSeconds"/> real seconds instead of lying there for minutes.</item>
/// </list>
/// Clients need TimeSync.BlockSpendTime gone (it refused a client's sleep/wait entirely); the speed-up inside is still
/// blocked by TimeSync.BlockSpeed.
/// </summary>
public static class RestSync
{
    public const byte MsgRestState = 52;
    const ushort Magic = 0x4D53;
    const float KoRealSeconds = 20f;

    public enum RestKind : byte { None = 0, Sleep = 1, Wait = 2, KO = 3 }

    static readonly Dictionary<PlayerId, RestKind> _clientRest = new();
    static RestKind _lastLocal;
    static SessionData.TimeSpeed _restSpeed = SessionData.TimeSpeed.veryFast;
    static bool _settingSpeed;
    static float _koSince = -1f;

    /// <summary>Local KO recovery, 0..1, or -1 when not KO'd (for the KO view).</summary>
    public static float KoProgress => _koSince < 0f ? -1f : Mathf.Clamp01((Time.unscaledTime - _koSince) / KoRealSeconds);
    public static float KoSecondsLeft => _koSince < 0f ? 0f : Mathf.Max(0f, KoRealSeconds - (Time.unscaledTime - _koSince));

    public static int Sent { get; private set; }

    /// <summary>True on the host while everyone rests (time may skip).</summary>
    public static bool AllResting { get; private set; }

    static NetSession Session => NetSession.Current;
    static bool IsHost => Session?.Role == NetSession.SessionRole.Host;

    static RestKind LocalRest()
    {
        var p = Player.Instance;
        if (p == null) return RestKind.None;
        if (p.playerKOInProgress) return RestKind.KO;
        if (!p.spendingTimeMode) return RestKind.None;
        return p.isAsleep ? RestKind.Sleep : RestKind.Wait;
    }

    static string Verb(RestKind k) => k switch { RestKind.Sleep => "sleeping", RestKind.Wait => "waiting", RestKind.KO => "knocked out", _ => "awake" };

    // ------------------------------------------------------------------ wire

    static byte[] Write(RestKind kind)
    {
        using var ms = new MemoryStream(8);
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(Magic); w.Write(MsgRestState); w.Write((byte)kind);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Host: a client's rest state.</summary>
    public static void OnPacket(PlayerId from, byte[] d)
    {
        if (d == null || d.Length < 4) return;
        var kind = (RestKind)d[3];
        _clientRest.TryGetValue(from, out var before);
        _clientRest[from] = kind;
        if (kind != before) Plugin.Event($"REST {from} is {Verb(kind)}");
    }

    public static void Forget(PlayerId id) => _clientRest.Remove(id);

    public static void Reset()
    {
        _clientRest.Clear();
        _lastLocal = RestKind.None;
        AllResting = false;
        _koSince = -1f;
    }

    // ------------------------------------------------------------------ per frame

    /// <summary>Both roles, per frame while in a session with a world.</summary>
    public static void Tick(float now, IEnumerable<PlayerId> clients)
    {
        var s = Session;
        if (s == null || !World.Ready) return;
        var local = LocalRest();

        if (local != _lastLocal)
        {
            if (local != RestKind.None && local != RestKind.KO)
                Notify.Broadcast($"{Notify.NameOf(PlayerRegistry.Local?.Id.Value ?? 0)} is {Verb(local)} — rest too to let time pass", InterfaceControls.Icon.agent);
            if (s.Role == NetSession.SessionRole.Client) { s.SendWorld(Write(local)); Sent++; }
            Plugin.Event($"REST we are {Verb(local)}");
            _lastLocal = local;
        }

        if (IsHost) HostTick(local, clients);
        KoFallback(now, local);
    }

    static void HostTick(RestKind hostRest, IEnumerable<PlayerId> clients)
    {
        bool any = false, all = hostRest != RestKind.None;
        foreach (var c in clients)
        {
            any = true;
            if (!_clientRest.TryGetValue(c, out var k) || k == RestKind.None) all = false;
        }
        if (!any) { AllResting = hostRest != RestKind.None; return; } // alone: the game's own behaviour
        if (all != AllResting)
        {
            AllResting = all;
            Plugin.Event(all ? "REST everyone rests: time passes" : "REST not everyone rests: time at normal speed");
            if (all) Notify.Broadcast("Everyone is resting — time passes", InterfaceControls.Icon.agent);
        }
        var sd = SessionData.Instance;
        if (sd == null || hostRest == RestKind.None) return; // an awake host runs its clock as usual
        var want = AllResting ? _restSpeed : SessionData.TimeSpeed.normal;
        if (sd.currentTimeSpeed != want) SetSpeed(sd, want);
    }

    static void SetSpeed(SessionData sd, SessionData.TimeSpeed speed)
    {
        _settingSpeed = true;
        try { sd.SetTimeSpeed(speed); }
        finally { _settingSpeed = false; }
        TimeSync.Dirty = true;
    }

    /// <summary>
    /// A KO waits for game time that only skips when everyone rests: recover after a short real time instead. The game
    /// counts <c>Player.KOTimePassed</c> up to <c>KOTime</c> in game time and wakes the player in <c>Player.Update</c>; with
    /// time at normal speed that took many minutes ("Recovering…" barely moving). So the fallback fills the counter and
    /// the game does its own wake-up. (Before 0.9.35 it called <c>TriggerPlayerRecovery</c>, which returns at once while
    /// <c>playerKOInProgress</c> is set: the player stayed down and it repeated every 20 s.)
    /// </summary>
    static bool _koFilled;

    static void KoFallback(float now, RestKind local)
    {
        if (local != RestKind.KO) { _koSince = -1f; _koFilled = false; return; }
        if (_koSince < 0f) _koSince = now;
        var sd = SessionData.Instance;
        bool skipping = sd != null && sd.currentTimeSpeed > SessionData.TimeSpeed.normal && (!IsHost || AllResting);
        if (skipping || now - _koSince < KoRealSeconds) return;
        var p = Player.Instance;
        if (p == null) return;
        if (!_koFilled)
        {
            _koFilled = true;
            try
            {
                float before = p.KOTimePassed;
                p.KOTimePassed = Mathf.Max(p.KOTimePassed, p.KOTime);
                Plugin.Event($"REST KO for {KoRealSeconds:F0} s (time doesn't skip while others are up): recovery counter {before:F2} -> {p.KOTimePassed:F2} of {p.KOTime:F2}, the game wakes us up");
            }
            catch (Exception e) { Plugin.Event($"REST KO recovery failed: {e.Message}"); }
        }
        else if (now - _koSince > KoRealSeconds + 10f && _koSince > 0f)
        {
            Plugin.Event($"REST still knocked out 10 s after filling the recovery counter ({p.KOTimePassed:F2} of {p.KOTime:F2})");
            _koSince = now - KoRealSeconds; // look again in 10 s
            _koFilled = false;               // and fill again (the game may have reset it)
        }
    }

    // ------------------------------------------------------------------ host: the game's own speed-up

    /// <summary>
    /// Host: entering rest speeds the clock up. Remember that speed for when everyone rests, and keep normal speed
    /// until then (HostTick restores it on its own afterwards).
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.SetSpendingTimeMode))]
    static class HostRest
    {
        static void Postfix(bool val)
        {
            if (!val || !IsHost || _settingSpeed || !World.Ready) return;
            var sd = SessionData.Instance;
            if (sd == null) return;
            if (sd.currentTimeSpeed > SessionData.TimeSpeed.normal) _restSpeed = sd.currentTimeSpeed;
            bool others = false;
            foreach (var p in PlayerRegistry.All) if (!p.IsLocal) { others = true; break; }
            if (others && !AllResting && sd.currentTimeSpeed != SessionData.TimeSpeed.normal)
            {
                SetSpeed(sd, SessionData.TimeSpeed.normal);
                Notify.Show("Waiting for your partner to rest too", InterfaceControls.Icon.agent);
            }
        }
    }
}
