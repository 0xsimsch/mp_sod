using System;
using HarmonyLib;
using SodCoop.Core;
using AITickRate = NewAIController.AITickRate;

namespace SodCoop.Patches;

/// <summary>
/// A1 fix. <c>Human::UpdateTickRateOnProx</c> (0x767720) rates a citizen's AI by where it is
/// relative to <c>Player.Instance</c>:
///   same room → veryHigh; same game location → high; same building and floor → medium;
///   same building, or both outside any building → low; otherwise veryLow.
/// It runs on the citizen's own room change and, for citizens around the host, from
/// <c>Player::OnRoomChange</c>. With remote players, use the best rate over all player bodies,
/// and re-rate citizens around a remote player when *it* changes room.
/// </summary>
static class TickRatePatch
{
    static bool HasRemotePlayers()
    {
        foreach (var p in PlayerRegistry.All) if (!p.IsLocal && p.RemoteBody?.Human != null) return true;
        return false;
    }

    static bool Same(UnityEngine.Object a, UnityEngine.Object b) =>
        a == null ? b == null : b != null && a.Pointer == b.Pointer;

    /// <summary>Rate of <paramref name="c"/> relative to one player body (the decoded original rule).</summary>
    public static AITickRate RateFor(Human c, Human player)
    {
        if (player == null) return AITickRate.veryLow;
        if (c.currentRoom != null && Same(c.currentRoom, player.currentRoom)) return AITickRate.veryHigh;
        if (c.currentGameLocation != null && Same(c.currentGameLocation, player.currentGameLocation)) return AITickRate.high;
        if (c.currentBuilding != null && Same(c.currentBuilding, player.currentBuilding))
        {
            var f1 = c.currentGameLocation != null ? c.currentGameLocation.floor : null;
            var f2 = player.currentGameLocation != null ? player.currentGameLocation.floor : null;
            return f1 != null && Same(f1, f2) ? AITickRate.medium : AITickRate.low;
        }
        return Same(c.currentBuilding, player.currentBuilding) ? AITickRate.low : AITickRate.veryLow;
    }

    public static AITickRate BestRate(Human c)
    {
        var best = AITickRate.veryLow;
        foreach (var p in PlayerRegistry.All)
        {
            var r = RateFor(c, p.Body);
            if (r > best) best = r;
        }
        return best;
    }

    [HarmonyPatch(typeof(Human), nameof(Human.UpdateTickRateOnProx))]
    static class OnProx
    {
        static bool Prefix(Human __instance)
        {
            if (!HasRemotePlayers()) return true; // single player: untouched
            try
            {
                if (__instance.ai == null || Puppet.Doubles.IsBorrowed(__instance)) return false;
                __instance.ai.SetDesiredTickRate(BestRate(__instance), false);
                return false;
            }
            catch (Exception e)
            {
                Plugin.Event($"tick rate patch failed, falling back to original: {e.Message}");
                return true;
            }
        }
    }

    /// <summary>
    /// Equivalent of <c>Player::OnRoomChange</c>'s re-rating for a remote body: re-rate every citizen in
    /// the building (or street location) the body left or entered.
    /// </summary>
    [HarmonyPatch(typeof(Human), nameof(Human.OnRoomChange))]
    static class RemoteBodyMoved
    {
        static void Postfix(Human __instance)
        {
            try
            {
                if (!PlayerRegistry.IsRemoteBody(__instance)) return;
                var citizens = CityData.Instance?.citizenDirectory;
                if (citizens == null) return;
                int n = 0;
                for (int i = 0; i < citizens.Count; i++)
                {
                    var c = citizens[i];
                    if (c == null || c.ai == null || c.isDead || c.Pointer == __instance.Pointer) continue;
                    bool near = (c.currentBuilding != null && (Same(c.currentBuilding, __instance.currentBuilding) || Same(c.currentBuilding, __instance.previousBuilding)))
                                || (c.currentGameLocation != null && (Same(c.currentGameLocation, __instance.currentGameLocation) || Same(c.currentGameLocation, __instance.previousGameLocation)));
                    if (!near) continue;
                    c.UpdateTickRateOnProx();
                    n++;
                }
                if (n > 0) Plugin.Event($"TICKRATE re-rated {n} citizens around {PlayerRegistry.RemoteOwnerOf(__instance)} (room {(__instance.currentRoom != null ? __instance.currentRoom.roomID : -1)})");
            }
            catch (Exception e) { Plugin.Event($"tick rate re-rate failed: {e.Message}"); }
        }
    }
}
