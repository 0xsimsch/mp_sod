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

/// <summary>
/// The game switches a citizen's AI component on by itself whenever something needs per-frame work
/// (<c>NewAIController.SetUpdateEnabled(true)</c>, from <c>CitizenAnimationController.SetArmsBoolState</c> /
/// <c>SetUmbrella</c> — which we call ourselves, or reach through <c>Human.OnNodeChange</c> when a driven body
/// changes node — <c>Human.SetDesiredSpeed</c>, <c>HearIllegal</c>, …). While it is on,
/// <c>CitizenBehaviour.LateUpdate</c> runs that citizen's <c>FrequentUpdate</c>: path following, facing, neck and
/// attack updates from its *local* AI state, every frame on top of the poses we apply (seen as flickering /
/// twitching citizens on a client), and it only switches itself off again when the citizen is idle, which a
/// driven one that "is moving" never is. Keep it off for every body we drive; a knocked-out one may run (the
/// game's KO update), and so may our own direct <c>ai.enabled = true</c> around SetKO.
/// </summary>
[HarmonyPatch(typeof(NewAIController), nameof(NewAIController.SetUpdateEnabled))]
static class KeepAiOff
{
    static bool Prefix(NewAIController __instance, bool val)
    {
        if (!val) return true;
        var h = __instance.human;
        if (h == null) return true;
        if (Puppet.NpcPuppets.IsDrivenAwake(h) || Puppet.Doubles.IsHidden(h)) return false;
        var owner = PlayerRegistry.RemoteOwnerOf(h);
        return owner?.RemoteBody == null || owner.RemoteBody.KnockedOut;
    }
}
