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
    internal const int Range = 1_000_000;

    static NetSession Session => NetSession.Current;
    static bool IsClient => Session?.Role == NetSession.SessionRole.Client;

    /// <summary>Host: a client found the current victim.</summary>
    public static void OnClientDiscovery(PlayerId who, int victimHumanId)
    {
        var mc = MurderController.Instance;
        if (mc == null) return;
        // The game's own check (ActionController.Search) wants the searched body to be the victim of an active murder.
        // The client checked that against its copy of the murders; check it against ours: otherwise OnVictimDiscovery
        // would open an empty murder case for an old body.
        if (!HasDeadVictim(mc, victimHumanId))
        {
            Plugin.Event($"CASE {who} reported citizen {victimHumanId} as a victim, but no active murder here has that dead victim: ignored");
            return;
        }
        Plugin.Event($"CASE {who} discovered the victim: running the discovery here (the murder case goes out to everyone)");
        _applying = true;
        try { mc.OnVictimDiscovery(); }
        finally { _applying = false; }
    }

    static bool HasDeadVictim(MurderController mc, int victimHumanId)
    {
        try
        {
            var murders = mc.activeMurders;
            var citizens = CityData.Instance?.citizenDictionary;
            if (murders == null || citizens == null) return true; // can't tell: behave as before
            for (int i = 0; i < murders.Count; i++)
            {
                var m = murders[i];
                if (m == null || m.victimID != victimHumanId) continue;
                if (citizens.TryGetValue(m.victimID, out var victim) && victim != null && victim.isDead) return true;
            }
            return false;
        }
        catch (System.Exception e)
        {
            Plugin.Event($"CASE victim check failed ({e.Message}); running the discovery anyway");
            return true;
        }
    }

    /// <summary>
    /// Client: the next case id from this player's range. Not below the ids already there: after a rejoin the
    /// counter comes from the host's save again (the host's counter never moves for a client's cases), and starting
    /// at the range's floor again would reuse the id of a case this player made last time.
    /// </summary>
    static void EnsureClientRange()
    {
        var local = PlayerRegistry.Local;
        if (!IsClient || local == null || local.Id.Value == 0) return;
        int floor = local.Id.Value * Range;
        int top = floor + Range;
        try
        {
            var panel = CasePanelController.Instance;
            foreach (var list in new[] { panel?.activeCases, panel?.archivedCases })
            {
                if (list == null) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    var c = list[i];
                    if (c != null && c.id >= floor && c.id < top) floor = c.id + 1;
                }
            }
        }
        catch (System.Exception e) { Plugin.Event($"CASE id range scan failed: {e.Message}"); }
        if (Case.assignCaseID < floor) Case.assignCaseID = floor;
    }

    [System.ThreadStatic] static bool _applying;

    [HarmonyPatch(typeof(MurderController), nameof(MurderController.OnVictimDiscovery))]
    static class Discovery
    {
        static bool Prefix()
        {
            if (!IsClient || _applying) return true;
            // Only our own player's search of a body is a discovery to report. The other caller
            // (GameplayController.NewMurderCaseNotify, an enforcer call) is this machine's citizens finding a body:
            // citizens are the host's, and it runs its own.
            if (!_inPlayerSearch)
            {
                Plugin.Event("CASE victim discovery here that isn't our player's search: left to the host");
                return false;
            }
            Session?.SendVictimDiscovered(_searchVictim);
            Plugin.Event($"CASE found the victim (citizen {_searchVictim}): asking the host to open the murder case");
            return false;
        }
    }

    [System.ThreadStatic] static bool _inPlayerSearch;
    [System.ThreadStatic] static int _searchVictim;

    /// <summary>Client: who our player is searching, for the discovery the search may trigger (ActionController.Search
    /// calls MurderController.OnVictimDiscovery when the body is an active murder's victim).</summary>
    [HarmonyPatch(typeof(ActionController), nameof(ActionController.Search))]
    static class PlayerSearch
    {
        static void Prefix(Interactable what, Actor who)
        {
            _inPlayerSearch = false;
            _searchVictim = -1;
            if (!IsClient) return;
            try
            {
                if (who == null || Player.Instance == null || who.Pointer != Player.Instance.Pointer) return;
                var body = what?.isActor?.TryCast<Human>();
                if (body == null) return;
                _searchVictim = body.humanID;
                _inPlayerSearch = true;
            }
            catch (System.Exception e) { Plugin.Event($"CASE search check failed: {e.Message}"); }
        }

        static void Finalizer() => _inPlayerSearch = false;
    }

    [HarmonyPatch(typeof(MurderController), nameof(MurderController.TriggerCoverUpObjective))]
    static class CoverUp { static bool Prefix() => !IsClient; }

    [HarmonyPatch(typeof(MurderController), nameof(MurderController.TriggerKidnappingCase))]
    static class Kidnapping { static bool Prefix() => !IsClient; }

    [HarmonyPatch(typeof(CasePanelController), nameof(CasePanelController.CreateNewCase))]
    static class ClientRange
    {
        static void Prefix() => EnsureClientRange();
    }

    /// <summary>
    /// The "No active case → New Case" popup (pressing a pin needle or the board's sticky-note button without a case)
    /// goes straight to OnCreateNewCustomCase, which constructs the Case itself and never passes CreateNewCase: that
    /// case took the next id of the host's counter and collided with the host's next murder/side-job case.
    /// </summary>
    [HarmonyPatch(typeof(CasePanelController), nameof(CasePanelController.OnCreateNewCustomCase))]
    static class ClientRangeCustom
    {
        static void Prefix() => EnsureClientRange();
    }
}
