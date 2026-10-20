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
/// <item>KO never skips time in a session. The game's KO (Player.Update): fall and fade to black, then teleport to a bed,
///   then wait <c>KOTime</c> game hours with the clock forced to veryFast, then fade in and wake. Here the clock stays at
///   normal speed (<see cref="NoKoSpeedUp"/>), and the wait counter runs as fast as it would at veryFast, so a KO
///   lasts as long as in single player (<see cref="KoFallback"/>), and the game's own end of the wait and wake-up undo the KO state (speed, fade,
///   progress bar, disabled actions, pause, playerKOInProgress).</item>
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
    static float _koSince = -1f;   // KO wait started (in the bed), real time

    /// <summary>Local KO recovery, 0..1, or -1 when not KO'd or already waking up (for the KO view).</summary>
    public static float KoProgress
    {
        get
        {
            var p = Player.Instance;
            if (_koSince < 0f || KoWaking || p == null || p.KOTime <= 0f) return -1f;
            return Mathf.Clamp01(p.KOTimePassed / p.KOTime);
        }
    }

    /// <summary>Estimated from the progress so far.</summary>
    public static float KoSecondsLeft
    {
        get
        {
            float p = KoProgress;
            if (p <= 0.02f) return 0f;
            return (Time.unscaledTime - _koSince) * (1f - p) / p;
        }
    }

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
        // A KO'd player doesn't count as resting: a KO never skips time in a session.
        bool any = false, all = hostRest is not (RestKind.None or RestKind.KO);
        foreach (var c in clients)
        {
            any = true;
            if (!_clientRest.TryGetValue(c, out var k) || k is RestKind.None or RestKind.KO) all = false;
        }
        if (!any) { AllResting = hostRest != RestKind.None; return; } // alone: the game's own behaviour
        if (all != AllResting)
        {
            AllResting = all;
            Plugin.Event(all ? "REST everyone rests: time passes" : "REST not everyone rests: time at normal speed");
            if (all) Notify.Broadcast("Everyone is resting — time passes", InterfaceControls.Icon.agent);
        }
        var sd = SessionData.Instance;
        if (sd == null || hostRest is RestKind.None or RestKind.KO) return; // an awake (or KO'd) host runs its clock as usual
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
    /// The KO wait counts <c>Player.KOTimePassed</c> up to <c>KOTime</c> in game hours; the game forces veryFast for it, so
    /// it lasts a few real seconds. Here the clock stays at normal speed, so add the difference each frame: the counter
    /// advances as if the clock ran at veryFast (<c>GameplayControls.timeMultipliers</c>) and the KO takes as long as in
    /// single player. Only once the player lies in the bed (<c>playerKOFadeOut</c>: teleport and heal done), and only up to
    /// just below <c>KOTime</c>: the game's own increment must cross it inside its wait branch, which then ends the wait
    /// (time speed, fade in, progress bar, interaction action) and later wakes the player (playerKOInProgress off, pause
    /// and actions enabled again). Filling the counter up to <c>KOTime</c> would skip that branch and leave the player
    /// knocked out for good (0.9.35). <c>TriggerPlayerRecovery</c> does nothing during a KO.
    /// </summary>
    static void KoFallback(float now, RestKind local)
    {
        if (local != RestKind.KO) { _koSince = -1f; return; }
        if (!LocalKoInSession()) return;
        var p = Player.Instance;
        if (!p.playerKOFadeOut || p.KOTimePassed >= p.KOTime) return;
        if (_koSince < 0f)
        {
            _koSince = now;
            Plugin.Event($"REST KO: waiting {p.KOTime:F2} game hours at veryFast pace, time stays at normal speed");
        }
        var sd = SessionData.Instance;
        var mults = GameplayControls.Instance?.timeMultipliers;
        if (sd == null || mults == null || mults.Count <= (int)SessionData.TimeSpeed.veryFast || sd.currentTimeMultiplier <= 0f) return;
        float ratio = mults[(int)SessionData.TimeSpeed.veryFast] / sd.currentTimeMultiplier;
        if (ratio <= 1f) return;
        float extra = (float)sd.gameTimePassedThisFrame * (ratio - 1f);
        float target = Mathf.Min(p.KOTimePassed + extra, p.KOTime - 1e-4f);
        if (target > p.KOTimePassed) p.KOTimePassed = target;
    }

    /// <summary>A KO in a session never speeds up the clock (the game sets veryFast for the KO wait).</summary>
    [HarmonyPatch(typeof(SessionData), nameof(SessionData.SetTimeSpeed))]
    static class NoKoSpeedUp
    {
        static bool Prefix(SessionData.TimeSpeed newTimeSpeed) =>
            _settingSpeed || newTimeSpeed <= SessionData.TimeSpeed.normal || !LocalKoInSession();
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
