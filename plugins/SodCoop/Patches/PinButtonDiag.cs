using System;
using HarmonyLib;
using UnityEngine;

namespace SodCoop.Patches;

/// <summary>
/// Diagnostics: on the client, releasing a window's pin needle throws a NullReferenceException inside
/// <c>PinFolderButtonController.ForcePointerUp</c> (Player.log, every attempt) and the note never lands on the board.
/// That method reads <c>CasePanelController.Instance</c>, the button's <c>parentWindow</c>, the window's
/// <c>pinOverlay</c>/<c>pinColour</c>, <c>InterfaceControls.Instance</c> and the button's <c>icon</c>; log which is
/// missing before the game runs it. Read only. A UI callback, not an animation event, so it is safe to hook.
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

    [HarmonyPatch(typeof(PinFolderButtonController), nameof(PinFolderButtonController.ForcePointerUp))]
    static class ForcePointerUp
    {
        static void Prefix(PinFolderButtonController __instance)
        {
            try
            {
                var panel = CasePanelController.Instance;
                var w = __instance.parentWindow;
                Plugin.Event($"PIN release on {PathOf(__instance.transform)}: placement={__instance.placementActive} " +
                             $"panel={(Dead(panel) ? "MISSING" : "ok")} activeCase={(panel?.activeCase != null ? panel.activeCase.id.ToString() : "none")} " +
                             $"linkMode={(panel != null && panel.customLinkSelectionMode)} parentWindow={(Dead(w) ? "MISSING" : w.name)} " +
                             $"pinned={(w != null && !w.WasCollected && w.pinned)} element={(w != null && !w.WasCollected && w.currentPinnedCaseElement != null ? "yes" : "no")} " +
                             $"pinOverlay={(w == null || w.WasCollected ? "-" : Dead(w.pinOverlay) ? "MISSING" : "ok")} " +
                             $"pinColour={(w == null || w.WasCollected ? "-" : Dead(w.pinColour) ? "MISSING" : "ok")} " +
                             $"icon={(Dead(__instance.icon) ? "MISSING" : "ok")} controls={(Dead(InterfaceControls.Instance) ? "MISSING" : "ok")}");
            }
            catch (Exception e) { Plugin.Event($"PIN diag failed: {e.Message}"); }
        }
    }
}
