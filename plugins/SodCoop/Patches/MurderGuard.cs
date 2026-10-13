using HarmonyLib;
using SodCoop.Core;

namespace SodCoop.Patches;

/// <summary>
/// A remote player's body is a borrowed citizen, so the host's murder director could pick it as the next murderer or
/// victim: the "citizen" would then be steered by the director while a real player drives it. After each pick, if
/// the choice is a player body, pick again (a few tries; the director retries on its own schedule otherwise).
/// </summary>
static class MurderGuard
{
    const int Tries = 8;
    static bool _repicking;

    static bool IsPlayerBody(Human h) => h != null && Puppet.Doubles.IsBorrowed(h);

    [HarmonyPatch(typeof(MurderController), nameof(MurderController.PickNewMurderer))]
    static class Murderer
    {
        static void Postfix(MurderController __instance)
        {
            if (_repicking || !IsPlayerBody(__instance.currentMurderer)) return;
            _repicking = true;
            try
            {
                for (int i = 0; i < Tries && IsPlayerBody(__instance.currentMurderer); i++) __instance.PickNewMurderer();
                Plugin.Event(IsPlayerBody(__instance.currentMurderer)
                    ? "MURDER director keeps picking a player's body as murderer; leaving it to the next pick"
                    : "MURDER director picked a player's body as murderer; picked someone else");
                if (IsPlayerBody(__instance.currentMurderer)) __instance.currentMurderer = null;
            }
            finally { _repicking = false; }
        }
    }

    [HarmonyPatch(typeof(MurderController), nameof(MurderController.PickNewVictim))]
    static class Victim
    {
        static void Postfix(MurderController __instance)
        {
            if (_repicking || !IsPlayerBody(__instance.currentVictim)) return;
            _repicking = true;
            try
            {
                for (int i = 0; i < Tries && IsPlayerBody(__instance.currentVictim); i++) __instance.PickNewVictim();
                Plugin.Event(IsPlayerBody(__instance.currentVictim)
                    ? "MURDER director keeps picking a player's body as victim; leaving it to the next pick"
                    : "MURDER director picked a player's body as victim; picked someone else");
                if (IsPlayerBody(__instance.currentVictim)) __instance.currentVictim = null;
            }
            finally { _repicking = false; }
        }
    }
}
