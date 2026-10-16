using System.Collections.Generic;
using HarmonyLib;
using SodCoop.Core;
using SodCoop.Net;

namespace SodCoop.Patches;

/// <summary>
/// Design: in a session the world pauses only while *every* player is paused (menu, case board, dialog…);
/// otherwise it keeps running for everyone, also behind the menus of the players who are paused.
///
/// The game has one flag, <c>SessionData.play</c>, for two things: "my UI is paused" and "the simulation runs".
/// Each player's own pause is tracked from <c>SessionData::PauseGame</c>/<c>ResumeGame</c> (34 call sites) as
/// <see cref="LocalPaused"/>; clients report it to the host, the host decides <see cref="WorldPaused"/> and sends
/// it with TimeSync. <c>play</c> then follows the world, except inside <c>InputController.Update</c> (the Esc menu,
/// notebook and desktop-mode Resume keys live there), which sees the player's own state. Without that split
/// (0.3.1–0.3.3) a paused player with the world running was stuck on the case board: <c>ResumeGame</c> starts
/// with <c>if (play) return;</c> (0x57F3B4) and the Resume key checks <c>play</c> too.
/// </summary>
static class PauseSync
{
    /// <summary>This player's own UI has the game paused.</summary>
    public static bool LocalPaused { get; private set; }

    /// <summary>The session's world is paused (everyone is paused). Host: decided here; client: from TimeSync.</summary>
    public static bool WorldPaused { get; private set; }

    /// <summary>Client: <see cref="LocalPaused"/> changed and the host hasn't been told yet.</summary>
    public static bool ReportPending { get; set; }

    static readonly Dictionary<PeerId, bool> _clientPaused = new();
    static bool _inInput;
    /// <summary>The game paused/resumed inside the input code (and restarted or stopped the AI for its own view).</summary>
    static bool _toggledInInput;
    static bool _simPlay;

    static bool InSession => NetSession.Current != null && World.Ready;

    /// <summary>Put <c>play</c> on the world's state (outside the input code).</summary>
    public static void ApplyWorld()
    {
        var sd = SessionData.Instance;
        if (sd == null || _inInput || !InSession) return;
        bool play = !WorldPaused;
        if (sd.play == play) return;
        sd.play = play;
        CitizenBehaviour.Instance?.GameSpeedChange();
    }

    /// <summary>Host, per frame: the world is paused only if the host and every connected client are.</summary>
    public static void HostTick(IEnumerable<PeerId> clients)
    {
        bool all = LocalPaused;
        foreach (var c in clients)
            if (!_clientPaused.TryGetValue(c, out var p) || !p) { all = false; break; }
        SetWorldPaused(all);
    }

    public static void SetWorldPaused(bool paused)
    {
        if (paused == WorldPaused) { ApplyWorld(); return; }
        WorldPaused = paused;
        if (NetSession.Current?.Role == NetSession.SessionRole.Host) TimeSync.Dirty = true;
        Plugin.Event(paused ? "PAUSE world paused (every player is in a menu)" : "PAUSE world running");
        ApplyWorld();
    }

    public static void OnClientPaused(PeerId peer, bool paused)
    {
        _clientPaused[peer] = paused;
        Plugin.Event($"PAUSE {peer} {(paused ? "opened a menu" : "is back in the game")}");
    }

    public static bool IsClientPaused(PeerId peer) => _clientPaused.TryGetValue(peer, out var p) && p;

    public static void Forget(PeerId peer) => _clientPaused.Remove(peer);

    /// <summary>Session over: the world is ours again.</summary>
    public static void Reset()
    {
        _clientPaused.Clear();
        WorldPaused = false;
        _pauseHoldsMovement = false;
        _frozenSince = -1f;
        var sd = SessionData.Instance;
        // Hand the game back the state its UI expects.
        if (sd != null && sd.play == LocalPaused)
        {
            sd.play = !LocalPaused;
            CitizenBehaviour.Instance?.GameSpeedChange();
        }
    }

    static void SetLocal(bool paused)
    {
        if (LocalPaused == paused) return;
        LocalPaused = paused;
        ReportPending = true;
        Plugin.Event($"PAUSE local {(paused ? "paused" : "resumed")} ({UiState()})");
    }

    static string UiState()
    {
        try
        {
            var ic = InterfaceController.Instance;
            var sd = SessionData.Instance;
            return $"desktop={(ic != null && ic.desktopMode)} board={(ic != null && ic.showDesktopCaseBoard)} " +
                   $"menu={(MainMenuController.Instance != null && MainMenuController.Instance.mainMenuActive)} delay={(sd != null ? sd.pauseUnpauseDelay : -1)}";
        }
        catch { return "?"; }
    }

    /// <summary>The player has the case board/notebook/map (desktop mode) or the Esc menu open.</summary>
    static bool MenuOpen()
    {
        var ic = InterfaceController.Instance;
        var mm = MainMenuController.Instance;
        return (ic != null && ic.desktopMode) || (mm != null && mm.mainMenuActive);
    }

    static float _mismatchSince = -1f;
    static bool _stuckLogged;

    /// <summary>
    /// Per frame: keep <see cref="LocalPaused"/> honest. The game ignores a pause within its pause/unpause delay
    /// (<c>SessionData.pauseUnpauseDelay</c>, "Pause ignored due to pause/unpause delay"), so opening the board right
    /// after closing something left it open while unpaused: the camera followed the mouse behind it and nothing could
    /// be pinned. A menu open for 0.4 s while not paused is paused properly (delay overridden). The opposite (paused
    /// with no board or menu: dialogs and some screens pause without them) is only logged, as a lead for "stuck".
    /// </summary>
    public static void Watchdog(float now)
    {
        var sd = SessionData.Instance;
        if (sd == null || !InSession || _inInput) return;
        bool open = MenuOpen();
        if (open == LocalPaused) { _mismatchSince = -1f; _stuckLogged = false; return; }
        if (_mismatchSince < 0f) { _mismatchSince = now; return; }
        if (now - _mismatchSince < 0.4f) return;
        if (open)
        {
            Plugin.Event($"PAUSE a menu is open but we aren't paused ({UiState()}): pausing now");
            sd.play = true; // what the game expects before a pause (our prefix sets it from LocalPaused anyway)
            sd.PauseGame(false, true, false);
            _mismatchSince = -1f;
        }
        else if (!_stuckLogged && now - _mismatchSince > 3f)
        {
            _stuckLogged = true;
            var ic = InteractionController.Instance;
            Plugin.Event($"PAUSE paused for 3 s with no board or menu open ({UiState()} lockedIn={(ic?.lockedInInteraction != null ? ic.lockedInInteraction.name : "none")}) — a dialog, or stuck?");
        }
        MovementCheck(now);
    }

    /// <summary>The game's own pause took the player's movement away (Player.OnPauseChange) and hasn't given it back.</summary>
    static bool _pauseHoldsMovement;
    static bool _inPauseChange;

    /// <summary>
    /// <c>Player.OnPauseChange</c> (0x44D3D0): with <c>play</c> false it saves <c>fps.enableMovement</c> into
    /// <c>pausedRememberPlayerMovement</c> and disables movement; with <c>play</c> true it restores the saved value
    /// (skipped entirely in dialog/<c>interactionMode</c> or floor edit). In single player nothing runs while paused, so
    /// the saved value stays right. Here the world keeps running behind the board: a transition that had movement off
    /// (searching the victim, SearchEnter/Exit) finished while the board was open and turned movement on, then closing
    /// the board restored the saved "off" and the player couldn't walk.
    /// </summary>
    [HarmonyPatch(typeof(Player), "OnPauseChange")]
    static class PlayerPauseChange
    {
        static void Prefix() => _inPauseChange = true;

        static void Postfix()
        {
            _inPauseChange = false;
            var ic = InteractionController.Instance;
            var sd = SessionData.Instance;
            if (ic == null || sd == null || ic.interactionMode || sd.isFloorEdit) return; // the game skipped it
            _pauseHoldsMovement = !sd.play;
        }
    }

    /// <summary>
    /// While the pause holds the movement, a change from anything else (a transition ending, a phone, a cutscene) goes
    /// into the saved value instead, so the resume applies it: the order single player gets.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.EnablePlayerMovement))]
    static class MovementWhilePaused
    {
        static bool Prefix(Player __instance, bool val)
        {
            if (_inPauseChange || !_pauseHoldsMovement || !InSession) return true;
            if (__instance.pausedRememberPlayerMovement != val)
                Plugin.Event($"PAUSE movement turned {(val ? "on" : "off")} behind the menu: applied when it closes");
            __instance.pausedRememberPlayerMovement = val;
            return false;
        }
    }

    static float _frozenSince = -1f;

    /// <summary>
    /// Safety net: not paused, no menu, and nothing that locks the player (transition, seat/search/computer, dialog,
    /// phone, cutscene, hiding, vent, bed, KO, death), yet movement is off for 3 s: turn it back on, and log the state.
    /// </summary>
    static void MovementCheck(float now)
    {
        try
        {
            var p = Player.Instance;
            var ic = InteractionController.Instance;
            var cut = CutSceneController.Instance;
            bool explained = p == null || p.fps == null || p.fps.enableMovement || LocalPaused || MenuOpen() ||
                             p.transitionActive || p.playerKOInProgress || p.isDead || p.isStunned || p.isHiding ||
                             p.inAirVent || p.isInBed || p.activeCall != null || p.isLockpicking ||
                             ic == null || ic.lockedInInteraction != null || ic.interactionMode || ic.currentlyDragging != null ||
                             (cut != null && cut.cutSceneActive);
            if (explained) { _frozenSince = -1f; return; }
            if (_frozenSince < 0f) { _frozenSince = now; return; }
            if (now - _frozenSince < 3f) return;
            _frozenSince = -1f;
            Plugin.Event($"PAUSE movement was off for 3 s with nothing holding it ({UiState()} remembered={p.pausedRememberPlayerMovement} " +
                         $"transition={(p.currentTransition != null ? p.currentTransition.name : "-")}): turned it back on");
            p.EnablePlayerMovement(true);
        }
        catch { /* best effort */ }
    }

    [HarmonyPatch(typeof(SessionData), nameof(SessionData.PauseGame))]
    static class Pause
    {
        // Show the game the paused/unpaused state its UI is in, so it runs (or skips) like in single player.
        static void Prefix(SessionData __instance)
        {
            if (InSession && !_inInput) __instance.play = !LocalPaused;
        }

        static void Postfix(SessionData __instance, bool delayOverride)
        {
            if (TimeSync.Applying) return;
            if (InSession && __instance.play && !delayOverride && __instance.pauseUnpauseDelay > 0)
                Plugin.Event($"PAUSE the game ignored a pause (pause/unpause delay {__instance.pauseUnpauseDelay})");
            SetLocal(!__instance.play);
            if (_inInput) _toggledInInput = true; else ApplyWorld();
        }
    }

    [HarmonyPatch(typeof(SessionData), nameof(SessionData.ResumeGame))]
    static class Resume
    {
        static void Prefix(SessionData __instance)
        {
            if (InSession && !_inInput) __instance.play = !LocalPaused;
        }

        static void Postfix(SessionData __instance)
        {
            if (TimeSync.Applying) return;
            SetLocal(!__instance.play);
            if (_inInput) _toggledInInput = true; else ApplyWorld();
        }
    }

    [HarmonyPatch(typeof(InputController), nameof(InputController.Update))]
    static class Input
    {
        static void Prefix()
        {
            var sd = SessionData.Instance;
            if (sd == null || !InSession) return;
            _inInput = true;
            _toggledInInput = false;
            _simPlay = sd.play;
            sd.play = !LocalPaused; // just the flag: the AI keeps following the world
        }

        static void Postfix()
        {
            if (!_inInput) return;
            _inInput = false;
            var sd = SessionData.Instance;
            if (sd == null) return;
            sd.play = !WorldPaused;
            // Restart/stop the AI only on a real change: the world's state moved, or the game's own
            // PauseGame/ResumeGame just set the AI for its view.
            if (_toggledInInput || sd.play != _simPlay) CitizenBehaviour.Instance?.GameSpeedChange();
            _toggledInInput = false;
        }
    }
}
