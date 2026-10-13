using HarmonyLib;
using SodCoop.Net;

namespace SodCoop.Patches;

/// <summary>Report the local player's KO / recovery to the session (see RemotePlayerBody.SetKnockedOut).</summary>
static class KoReport
{
    [HarmonyPatch(typeof(Player), nameof(Player.TriggerPlayerKO))]
    static class Ko
    {
        static void Postfix() => NetSession.Current?.SendLocalKO(true);
    }

    [HarmonyPatch(typeof(Player), nameof(Player.TriggerPlayerRecovery))]
    static class Recovery
    {
        static void Postfix() => NetSession.Current?.SendLocalKO(false);
    }
}
