using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using SodCoop.Core;
using UnityEngine;

namespace SodCoop.Net;

/// <summary>
/// Sleeping and waiting go through <c>Player.SetSpendingTimeMode(true)</c>, which speeds the clock up
/// (<c>SessionData.SetTimeSpeed</c>). In a session time is shared, so:
/// <list type="bullet">
/// <item>Time only skips while <b>every</b> player sleeps or waits. The host decides: its own rest speeds the clock up only
///   then; otherwise the host's clock stays at normal speed. Clients report their rest state (msg 52) and follow the
///   host's clock and speed through TimeSync (their own SetTimeSpeed stays blocked there).</item>
/// <item>A player resting alone sees "waiting for &lt;partner&gt;", and the partner a notice.</item>
/// <item>A KO'd player counts as resting while someone else sleeps or waits: then time skips for everyone (and the
///   KO ends sooner). Everyone KO'd skips time too. A KO alone (someone else awake) never skips time in a session. The game's KO (Player.Update): fall and fade to black, then teleport to a bed,
///   then wait <c>KOTime</c> game hours with the clock forced to veryFast, then fade in and wake. Here the clock stays at
///   normal speed (<see cref="NoKoSpeedUp"/>), so the wait (1.5 game hours) takes 10 real minutes instead of 15 s.
///   The game's own end of the wait and wake-up undo the KO state (fade, progress bar, disabled actions, pause,
///   playerKOInProgress).</item>
/// </list>
/// Clients need TimeSync.BlockSpendTime gone (it refused a client's sleep/wait entirely); the speed-up inside is still
/// blocked by TimeSync.BlockSpeed.
/// </summary>
public static class RestSync
{
    public const byte MsgRestState = 52;
    const ushort Magic = 0x4D53;

    public enum RestKind : byte { None = 0, Sleep = 1, Wait = 2, KO = 3 }

    static readonly Dictionary<PlayerId, RestKind> _clientRest = new();
    static RestKind _lastLocal;
    static SessionData.TimeSpeed _restSpeed = SessionData.TimeSpeed.veryFast;
    static bool _settingSpeed;

    public static int Sent { get; private set; }

    /// <summary>True on the host while everyone rests (time may skip).</summary>
    public static bool AllResting { get; private set; }

    /// <summary>The local KO's wait is over: the game is fading back in (or the player is up).</summary>
    public static bool KoWaking
    {
        get
        {
            var p = Player.Instance;
            return p != null && p.playerKOInProgress && p.playerKOFadeOut && p.KOTimePassed >= p.KOTime;
        }
    }

    static NetSession Session => NetSession.Current;

    /// <summary>In a session with someone else: a KO must not speed up the shared clock.</summary>
    static bool LocalKoInSession()
    {
        var p = Player.Instance;
        if (p == null || !p.playerKOInProgress || Session == null) return false;
        foreach (var np in PlayerRegistry.All) if (!np.IsLocal) return true;
        return false;
    }
    static bool IsHost => Session?.Role == NetSession.SessionRole.Host;

    static RestKind LocalRest()
    {
        var p = Player.Instance;
        if (p == null) return RestKind.None;
        if (p.playerKOInProgress) return KoWaking ? RestKind.None : RestKind.KO;
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
    }

    static void HostTick(RestKind hostRest, IEnumerable<PlayerId> clients)
    {
        // A KO'd player goes along with the others' rest (sleep, wait or KO), but a KO alone never skips time in a session.
        bool any = false, all = hostRest != RestKind.None;
        foreach (var c in clients)
        {
            any = true;
            _clientRest.TryGetValue(c, out var k);
            if (k == RestKind.None) all = false;
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
        // A KO'd host speeds up only once the game's KO wait has started (progress bar up): the game sets that up only
        // while the speed isn't veryFast yet.
        bool koWaiting = hostRest != RestKind.KO
            || (Player.Instance.playerKOFadeOut && InteractionController.Instance != null && InteractionController.Instance.activeInteractionAction);
        var want = AllResting && koWaiting ? _restSpeed : SessionData.TimeSpeed.normal;
        if (sd.currentTimeSpeed != want) SetSpeed(sd, want);
    }

    static void SetSpeed(SessionData sd, SessionData.TimeSpeed speed)
    {
        _settingSpeed = true;
        try { sd.SetTimeSpeed(speed); }
        finally { _settingSpeed = false; }
        TimeSync.Dirty = true;
    }

    /// <summary>A KO in a session never speeds up the clock (the game sets veryFast for the KO wait). The host's clock
    /// (TimeSync on a client) and the host's own rest speed-up still get through.</summary>
    [HarmonyPatch(typeof(SessionData), nameof(SessionData.SetTimeSpeed))]
    static class NoKoSpeedUp
    {
        static bool Prefix(SessionData.TimeSpeed newTimeSpeed) =>
            _settingSpeed || TimeSync.Applying || newTimeSpeed <= SessionData.TimeSpeed.normal || !LocalKoInSession();
    }

    /// <summary>
    /// The game's KO wait starts with "if the speed isn't veryFast: set veryFast, show the fast-forward arrow, the KO
    /// progress bar and the KO message". With the speed kept at normal that would run every frame (message and speed
    /// request each frame). Once it ran (the progress bar's interaction action is active), Player.Update sees veryFast
    /// for the rest of the wait; the real speed is put back right after.
    /// </summary>
    [HarmonyPatch(typeof(Player), "Update")]
    static class KoWaitOnce
    {
        static void Prefix(Player __instance, out int __state)
        {
            __state = -1;
            var sd = SessionData.Instance;
            if (sd == null || !__instance.playerKOInProgress || !__instance.playerKOFadeOut) return;
            if (sd.currentTimeSpeed == SessionData.TimeSpeed.veryFast || !LocalKoInSession()) return;
            var ic = InteractionController.Instance;
            if (ic == null || !ic.activeInteractionAction) return;
            __state = (int)sd.currentTimeSpeed;
            sd.currentTimeSpeed = SessionData.TimeSpeed.veryFast;
        }

        static void Postfix(int __state)
        {
            if (__state < 0) return;
            var sd = SessionData.Instance;
            // The end of the wait sets normal speed itself (blocked on clients, so the stand-in may still be there).
            if (sd != null && sd.currentTimeSpeed == SessionData.TimeSpeed.veryFast) sd.currentTimeSpeed = (SessionData.TimeSpeed)__state;
            // Time isn't fast-forwarding: no fast-forward arrow.
            var arrow = InterfaceControls.Instance != null ? InterfaceControls.Instance.fastForwardArrow : null;
            if (arrow != null && arrow.gameObject.activeSelf) arrow.gameObject.SetActive(false);
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
            bool others = false, othersRest = true;
            foreach (var p in PlayerRegistry.All)
            {
                if (p.IsLocal) continue;
                others = true;
                if (!_clientRest.TryGetValue(p.Id, out var k) || k == RestKind.None) othersRest = false;
            }
            // Partners all asleep, waiting or KO'd: HostTick lets time pass on its next frame.
            if (others && !othersRest && !AllResting && sd.currentTimeSpeed != SessionData.TimeSpeed.normal)
            {
                SetSpeed(sd, SessionData.TimeSpeed.normal);
                Notify.Show("Waiting for your partner to rest too", InterfaceControls.Icon.agent);
            }
        }
    }
}
