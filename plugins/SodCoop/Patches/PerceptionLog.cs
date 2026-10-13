using System;
using System.Collections.Generic;
using HarmonyLib;
using SodCoop.Core;

namespace SodCoop.Patches;

/// <summary>
/// Observation only: logs whenever the game's perception and crime code reacts to a remote
/// player's body. This tells us which existing systems already treat the puppet as a player.
/// These hooks are also where per-player blame will attach later.
/// </summary>
static class PerceptionLog
{
    static string Who(Actor a)
    {
        if (a == null) return "null";
        var owner = PlayerRegistry.OwnerOf(a);
        if (owner != null) return owner.ToString();
        var h = a.TryCast<Human>();
        return h != null ? $"citizen {h.humanID} \"{h.citizenName}\"" : a.name;
    }

    static void Safe(Action log)
    {
        try { log(); }
        catch (Exception e) { Plugin.Event($"log failed: {e.Message}"); }
    }

    // A citizen (__instance) notices newTarget doing something illegal.
    [HarmonyPatch(typeof(Actor), nameof(Actor.AddToSeesIllegal))]
    static class SeesIllegal
    {
        static void Postfix(Actor __instance, Actor newTarget, float focus) => Safe(() =>
        {
            var owner = PlayerRegistry.OwnerOf(newTarget);
            if (owner == null) return;
            owner.TimesSeenDoingIllegal++;
            Plugin.Event($"SEES-ILLEGAL observer={Who(__instance)} target={owner} focus={focus:F2}");
        });
    }

    // Pursuit bookkeeping: NewAIController::SetPersueTarget calls target.AddPersuedBy(pursuer).
    [HarmonyPatch(typeof(Actor), nameof(Actor.AddPersuedBy))]
    static class PursuedBy
    {
        static void Postfix(Actor __instance, Actor newTarget) => Safe(() =>
        {
            var a = PlayerRegistry.OwnerOf(__instance);
            var b = PlayerRegistry.OwnerOf(newTarget);
            if (a == null && b == null) return;
            (a ?? b).TimesPursued++;
            var pursuerAi = newTarget?.ai;
            Plugin.Event($"PURSUED-BY pursued={Who(__instance)} pursuer={Who(newTarget)} " +
                         $"(pursuer.ai.persuitTarget={Who(pursuerAi?.persuitTarget)})");
        });
    }

    // Citizen-side trespass check, which a possessed citizen still uses. Only changes are logged.
    [HarmonyPatch(typeof(Human), nameof(Human.IsTrespassing))]
    static class Trespassing
    {
        static readonly Dictionary<IntPtr, (bool, int)> _last = new();

        static void Postfix(Human __instance, NewRoom room, ref int trespassEscalation, bool __result)
        {
            int escalation = trespassEscalation;
            Safe(() =>
            {
                if (!PlayerRegistry.IsRemoteBody(__instance)) return;
                var now = (__result, escalation);
                if (_last.TryGetValue(__instance.Pointer, out var prev) && prev == now) return;
                _last[__instance.Pointer] = now;
                Plugin.Event($"TRESPASS body={Who(__instance)} room={(room != null ? room.roomID : -1)} result={__result} escalation={escalation}");
            });
        }
    }

    // The local player's camera spotted a remote body (expected to fire; useful sanity check).
    [HarmonyPatch(typeof(Actor), nameof(Actor.SpottedByPlayer))]
    static class Spotted
    {
        static readonly HashSet<IntPtr> _seen = new();

        static void Postfix(Actor __instance) => Safe(() =>
        {
            if (!PlayerRegistry.IsRemoteBody(__instance) || !_seen.Add(__instance.Pointer)) return;
            Plugin.Event($"SPOTTED-BY-LOCAL-PLAYER body={Who(__instance)} (first time)");
        });
    }
}
