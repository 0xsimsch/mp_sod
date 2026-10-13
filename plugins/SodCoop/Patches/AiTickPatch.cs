using HarmonyLib;
using SodCoop.Core;

namespace SodCoop.Patches;

// A remote player's body is driven by network poses, never by the citizen AI. The body is still a
// Citizen in CityData.citizenDirectory, so citizen-wide loops keep reaching it; block every AI
// entry point that would make it act as an *observer*. It must stay perceivable as a *target*.

[HarmonyPatch(typeof(NewAIController), nameof(NewAIController.AITick))]
static class AiTickPatch
{
    static bool Prefix(NewAIController __instance) =>
        !PlayerRegistry.IsRemoteBody(__instance.human) && !Puppet.NpcPuppets.IsDriven(__instance.human);
}

[HarmonyPatch(typeof(NewAIController), nameof(NewAIController.UpdateTickRate))]
static class AiTickRatePatch
{
    static bool Prefix(NewAIController __instance) => !PlayerRegistry.IsRemoteBody(__instance.human);
}

/// <summary>
/// Tested (2026-10-12): without this the puppet ran its own SightingCheck, "saw" the citizen chasing
/// it and started pursuing her (P1.ai.persuitTarget = resident), which also corrupted her chase.
/// </summary>
[HarmonyPatch(typeof(Actor), nameof(Actor.SightingCheck))]
static class NoSightingByRemoteBody
{
    // Also host-driven citizens on a client: perception is the host's job.
    static bool Prefix(Actor __instance) => !Puppet.Doubles.IsBorrowed(__instance) && !Puppet.NpcPuppets.IsDriven(__instance);
}

[HarmonyPatch(typeof(NewAIController), nameof(NewAIController.SetPersue))]
static class NoPersueByRemoteBody
{
    static bool Prefix(NewAIController __instance) => !PlayerRegistry.IsRemoteBody(__instance.human);
}

[HarmonyPatch(typeof(NewAIController), nameof(NewAIController.SetPersueTarget))]
static class NoPersueTargetByRemoteBody
{
    static bool Prefix(NewAIController __instance, Actor newTarget) =>
        newTarget == null || !PlayerRegistry.IsRemoteBody(__instance.human);
}
