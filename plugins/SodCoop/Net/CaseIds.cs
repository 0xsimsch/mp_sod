using HarmonyLib;
using SodCoop.Core;

namespace SodCoop.Net;

/// <summary>
/// Cases are numbered by one static counter (<c>Case.assignCaseID</c>) that a client inherits from the host's save, so a
/// case created on a client got the same id as the host's next one, and the two records overwrote each other (seen:
/// "New Murder Case" and "Scope-Out Assassin" both case 4; the murder case and its crime-scene objective vanished).
/// <list type="bullet">
/// <item>The murder case: a client discovering the victim asks the host, which runs the discovery and creates the one case.</item>
/// <item>Cover-up and kidnapping cases: murder-director events, host only.</item>
/// <item>Any other case a client creates (its own custom case…): ids from the client's own range (player id × 1,000,000).</item>
/// </list>
/// </summary>
public static class CaseIds
{
    const int Range = 1_000_000;

    static NetSession Session => NetSession.Current;
    static bool IsClient => Session?.Role == NetSession.SessionRole.Client;

    /// <summary>Host: a client found the current victim.</summary>
    public static void OnClientDiscovery(PlayerId who)
    {
        var mc = MurderController.Instance;
        if (mc == null) return;
        Plugin.Event($"CASE {who} discovered the victim: running the discovery here (the murder case goes out to everyone)");
        _applying = true;
        try { mc.OnVictimDiscovery(); }
        finally { _applying = false; }
    }

    [System.ThreadStatic] static bool _applying;

    [HarmonyPatch(typeof(MurderController), nameof(MurderController.OnVictimDiscovery))]
    static class Discovery
    {
        static bool Prefix()
        {
            if (!IsClient || _applying) return true;
            Session?.SendVictimDiscovered();
            Plugin.Event("CASE found the victim: asking the host to open the murder case");
            return false;
        }
    }

    [HarmonyPatch(typeof(MurderController), nameof(MurderController.TriggerCoverUpObjective))]
    static class CoverUp { static bool Prefix() => !IsClient; }

    [HarmonyPatch(typeof(MurderController), nameof(MurderController.TriggerKidnappingCase))]
    static class Kidnapping { static bool Prefix() => !IsClient; }

    [HarmonyPatch(typeof(CasePanelController), nameof(CasePanelController.CreateNewCase))]
    static class ClientRange
    {
        static void Prefix()
        {
            var local = PlayerRegistry.Local;
            if (!IsClient || local == null || local.Id.Value == 0) return;
            int floor = local.Id.Value * Range;
            if (Case.assignCaseID < floor) Case.assignCaseID = floor;
        }
    }
}
