using System;
using HarmonyLib;
using SodCoop.Core;

namespace SodCoop.Patches;

/// <summary>
/// Observation only: what happens when a chasing citizen reaches a remote player's body.
/// Later, damage to a remote body must be forwarded to that player over the network instead of
/// being applied to the borrowed citizen.
/// </summary>
static class CombatLog
{
    static string Who(Actor a)
    {
        if (a == null) return "null";
        var owner = PlayerRegistry.OwnerOf(a);
        if (owner != null) return owner.Id.ToString();
        var h = a.TryCast<Human>();
        return h != null ? $"citizen {h.humanID}" : a.name;
    }

    static void Safe(Action log)
    {
        try { log(); }
        catch (Exception e) { Plugin.Event($"log failed: {e.Message}"); }
    }

    [HarmonyPatch(typeof(NewAIController), nameof(NewAIController.StartAttack))]
    static class Attack
    {
        static void Postfix(NewAIController __instance, Actor newAttackTarget) => Safe(() =>
        {
            if (PlayerRegistry.OwnerOf(newAttackTarget) == null && !PlayerRegistry.IsRemoteBody(__instance.human)) return;
            Plugin.Event($"ATTACK attacker={Who(__instance.human)} target={Who(newAttackTarget)}");
        });
    }

    [HarmonyPatch(typeof(Citizen), nameof(Citizen.RecieveDamage))]
    static class Damage
    {
        static void Postfix(Citizen __instance, float amount, Actor fromWho) => Safe(() =>
        {
            if (!PlayerRegistry.IsRemoteBody(__instance)) return;
            Plugin.Event($"DAMAGE to={Who(__instance)} amount={amount:F2} from={Who(fromWho)} " +
                         $"health={__instance.currentHealth:F2}/{__instance.maximumHealth:F2} dead={__instance.isDead}");
        });
    }

    [HarmonyPatch(typeof(NewAIController), nameof(NewAIController.SetKO))]
    static class Ko
    {
        static void Postfix(NewAIController __instance, bool val) => Safe(() =>
        {
            if (!PlayerRegistry.IsRemoteBody(__instance.human)) return;
            Plugin.Event($"KO body={Who(__instance.human)} val={val}");
        });
    }
}
