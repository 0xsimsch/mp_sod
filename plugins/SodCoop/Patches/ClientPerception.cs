using HarmonyLib;
using SodCoop.Core;
using SodCoop.Net;

namespace SodCoop.Patches;

/// <summary>
/// Item 4 (SYNC_PRIORITIES): perception of *players* is the host's job. On a client, citizens beyond the host's sync
/// radius run the client's own AI; without this they spotted, chased and reported players on their own (police the
/// host never knew about) and dropped the chase the moment the host took them over. Citizens keep perceiving each
/// other, sounds and bodies; only acting on a player is refused. Host-driven puppets (AI off) are also kept from
/// re-running hit, trip and throw effects that their replayed animations would otherwise trigger here.
/// </summary>
static class ClientPerception
{
    static bool IsClient => NetSession.Current?.Role == NetSession.SessionRole.Client;

    /// <summary>The local player or another player's body.</summary>
    static bool IsPlayer(Actor a) =>
        a != null && ((Player.Instance != null && a.Pointer == Player.Instance.Pointer) || PlayerRegistry.IsRemoteBody(a));

    static bool Driven(NewAIController ai) => ai != null && Puppet.NpcPuppets.IsDriven(ai.human);

    [HarmonyPatch(typeof(NewAIController), nameof(NewAIController.SetPersue))]
    static class NoLocalPersuit
    {
        static bool Prefix(Actor newTarget) => !(IsClient && IsPlayer(newTarget));
    }

    [HarmonyPatch(typeof(NewAIController), nameof(NewAIController.SetPersueTarget))]
    static class NoLocalPersuitTarget
    {
        static bool Prefix(Actor newTarget) => !(IsClient && IsPlayer(newTarget));
    }

    [HarmonyPatch(typeof(NewAIController), nameof(NewAIController.Investigate))]
    static class NoLocalInvestigatePlayer
    {
        static bool Prefix(Actor newTarget) => !(IsClient && IsPlayer(newTarget));
    }

    /// <summary>Enforcers are dispatched by the host only (its enforcers reach the client as driven citizens).</summary>
    [HarmonyPatch(typeof(GameplayController), nameof(GameplayController.CallEnforcers))]
    static class NoLocalEnforcers
    {
        static bool Prefix()
        {
            if (!IsClient) return true;
            Plugin.Event("CIT blocked a local enforcer call (the host dispatches police)");
            return false;
        }
    }

    // Replayed attack animations on host-driven puppets must not hurt the client player locally: the host already
    // decided the hit and sends it as a Damage message. Guarded where damage lands, not in the animation events:
    // patching CitizenAnimationEvents.MeleeAttackTrigger/TripKO (and ThrowObject) crashed the game natively the
    // moment a client session started (0.7.0, reproduced and bisected).

    /// <summary>Set while applying a hit the host forwarded.</summary>
    internal static bool ApplyingNetworkDamage;

    [HarmonyPatch(typeof(Player), nameof(Player.RecieveDamage))]
    static class NoLocalPuppetHits
    {
        static bool Prefix(Actor fromWho)
        {
            if (!IsClient || ApplyingNetworkDamage || fromWho == null) return true;
            var h = fromWho.TryCast<Human>();
            return h == null || !Puppet.NpcPuppets.IsDriven(h);
        }
    }
}
