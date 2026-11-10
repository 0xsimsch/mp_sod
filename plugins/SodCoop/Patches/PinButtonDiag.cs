using System;
using HarmonyLib;
using UnityEngine;

namespace SodCoop.Patches;

/// <summary>
/// On the client, a note window's pin needle (<c>GameCanvas/WindowCanvas/Note/PinButton</c>) had no
/// <c>parentWindow</c>: pressing it never started a placement and releasing it threw a NullReferenceException in
/// <c>PinFolderButtonController.ForcePointerUp</c>, so the note never reached the board (and so never the host).
/// Before the button handles a press, click or release, give it the window it sits in. Logs each repair once per
/// button and the state at release. UI callbacks, not animation events, so they are safe to hook.
/// </summary>
static class PinButtonDiag
{
    static bool Dead(UnityEngine.Object o) => o == null || o.WasCollected;

    static string PathOf(Transform t)
    {
        var s = t != null ? t.name : "?";
        for (int i = 0; i < 4 && t != null && t.parent != null; i++) { t = t.parent; s = t.name + "/" + s; }
        return s;
    }

    static void Repair(PinFolderButtonController b, bool quiet = false)
    {
        try
        {
            if (!Dead(b.parentWindow)) return;
            var w = b.GetComponentInParent<InfoWindow>();
            if (Dead(w)) { if (!quiet) Plugin.Event($"PIN {PathOf(b.transform)} has no parent window and none above it"); return; }
            b.parentWindow = w;
            Plugin.Event($"PIN {PathOf(b.transform)}: parent window was missing; set to {w.name}");
        }
        catch (Exception e) { Plugin.Event($"PIN repair failed: {e.Message}"); }
    }

    [HarmonyPatch(typeof(PinFolderButtonController), nameof(PinFolderButtonController.OnPointerDown))]
    static class PointerDown { static void Prefix(PinFolderButtonController __instance) => Repair(__instance); }

    /// <summary>
    /// The needle's redraw (pinned / unpinned look) returns at once without a parent window, and the window calls it
    /// whenever its pin state changes (InfoWindow.OnWindowPinnedChange): a pin made without pressing this needle (new
    /// sticky note, the other player's pin, the board rebuilt from a case record) left the needle looking unpinned
    /// until the first press repaired it. Repair before every redraw too (quietly: this runs often).
    /// </summary>
    [HarmonyPatch(typeof(PinFolderButtonController), nameof(PinFolderButtonController.VisualUpdate))]
    static class Redraw { static void Prefix(PinFolderButtonController __instance) => Repair(__instance, true); }

    [HarmonyPatch(typeof(PinFolderButtonController), nameof(PinFolderButtonController.OnLeftClick))]
    static class LeftClick { static void Prefix(PinFolderButtonController __instance) => Repair(__instance); }

    [HarmonyPatch(typeof(PinFolderButtonController), nameof(PinFolderButtonController.ForcePointerUp))]
    static class ForcePointerUp
    {
        static void Prefix(PinFolderButtonController __instance)
        {
            Repair(__instance);
            try
            {
                var panel = CasePanelController.Instance;
                var w = __instance.parentWindow;
                Plugin.Event($"PIN release on {PathOf(__instance.transform)}: placement={__instance.placementActive} " +
                             $"activeCase={(panel?.activeCase != null ? panel.activeCase.id.ToString() : "none")} " +
                             $"parentWindow={(Dead(w) ? "MISSING" : w.name)} pinned={(!Dead(w) && w.pinned)} " +
                             $"pinOverlay={(Dead(w) ? "-" : Dead(w.pinOverlay) ? "MISSING" : "ok")} " +
                             $"pinColour={(Dead(w) ? "-" : Dead(w.pinColour) ? "MISSING" : "ok")}");
            }
            catch (Exception e) { Plugin.Event($"PIN diag failed: {e.Message}"); }
        }
    }
}
