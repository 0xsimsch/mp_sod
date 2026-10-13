using HarmonyLib;
using SodCoop.Core;
using SodCoop.Net;
using UnityEngine;

namespace SodCoop.Patches;

/// <summary>
/// Host: damage a citizen deals to a remote player's body is sent to that player (who applies it to
/// its own <c>Player</c> and runs its own KO flow) instead of hurting the borrowed citizen.
/// Tested without this: the puppet took citizen damage and got a citizen KO (PERCEPTION_RESULTS.md).
/// Without a network session (F5 ghost) damage still lands on the puppet, for testing.
/// </summary>
[HarmonyPatch(typeof(Citizen), nameof(Citizen.RecieveDamage))]
static class DamageForward
{
    /// <summary>Host is applying a client's hit: let it through.</summary>
    internal static bool ApplyingRemoteHit;

    static bool Prefix(Citizen __instance, float amount, Actor fromWho, Vector3 damagePosition, Vector3 damageDirection)
    {
        var session = NetSession.Current;
        if (session == null || ApplyingRemoteHit) return true;
        if (!PlayerRegistry.IsRemoteBody(__instance))
        {
            // Client: our player hit a citizen. Citizens are the host's: send the hit there instead of applying it
            // here (seen: a client KO'd a citizen and the host's citizen stayed up). The KO comes back with the pose sync.
            if (session.Role == NetSession.SessionRole.Client && fromWho != null && Player.Instance != null && fromWho.Pointer == Player.Instance.Pointer)
                return !session.ForwardCitizenHit(__instance, amount, damagePosition, damageDirection);
            return true;
        }
        // Clients: local citizens never hurt another player's body; the host decides who gets hit.
        if (session.Role == NetSession.SessionRole.Client) return false;
        return !session.ForwardDamage(__instance, amount, fromWho, damagePosition, damageDirection);
    }
}
