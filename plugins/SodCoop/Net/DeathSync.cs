using System;
using HarmonyLib;

namespace SodCoop.Net;

/// <summary>
/// Host-authoritative deaths. The host reports every <c>Human.Murder</c>. Clients don't run their own murder
/// director (<c>MurderController.Update</c> is skipped), so murders only ever start on the host.
///
/// Replay limit (found by testing): <c>Human.Death</c>'s constructor dereferences the
/// <c>MurderController.Murder</c> record immediately, so <c>Human.Murder</c> can't run without the host's murder
/// record. Until murder records are synced, a client replays the death visually (dead state, ragdoll, AI
/// off) and only calls <c>Human.Murder</c> when it has the matching record.
/// </summary>
public static class DeathSync
{
    [ThreadStatic] static bool _applying;

    public static int Sent { get; private set; }
    public static int Applied { get; private set; }

    static bool IsClient => NetSession.Current?.Role == NetSession.SessionRole.Client;

    [HarmonyPatch(typeof(Human), nameof(Human.Murder))]
    static class Report
    {
        static void Postfix(Human __instance, Human killer, MurderController.Murder murder, Interactable weapon)
        {
            var s = NetSession.Current;
            if (s == null || s.Role != NetSession.SessionRole.Host || _applying || __instance == null || !Core.World.Ready) return;
            try
            {
                s.SendDeath(new DeathMsg
                {
                    VictimId = __instance.humanID,
                    KillerId = EncodeKiller(killer),
                    MurderId = murder != null ? murder.murderID : -1,
                    WeaponId = weapon != null ? weapon.id : -1,
                    Position = new Vec3(__instance.transform.position.x, __instance.transform.position.y, __instance.transform.position.z),
                    Yaw = __instance.transform.eulerAngles.y,
                });
                Sent++;
            }
            catch (Exception e) { Plugin.Event($"death sync failed: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(MurderController), "Update")]
    static class NoMurderDirectorOnClients
    {
        static bool Prefix() => !IsClient;
    }

    public static int EncodeKiller(Human killer)
    {
        if (killer == null) return -1;
        var owner = Core.PlayerRegistry.OwnerOf(killer);
        return owner != null ? -(owner.Id.Value + 2) : killer.humanID;
    }

    public static Human DecodeKiller(int id)
    {
        if (id <= -2) return Core.PlayerRegistry.TryGet(new Core.PlayerId((byte)(-id - 2)), out var p) ? p.Body : null;
        if (id < 0) return null;
        CityData.Instance.citizenDictionary.TryGetValue(id, out var h);
        return h;
    }

    /// <summary>Dead without the game's murder bookkeeping: no AI, ragdoll as a corpse.</summary>
    static void ShowDead(Human victim)
    {
        victim.isDead = true;
        if (victim.ai != null) victim.ai.enabled = false;
        var anim = victim.animationController;
        if (anim != null)
        {
            anim.SetDead(true);
            anim.SetRagdoll(true, true);
        }
    }

    public static void Apply(DeathMsg m)
    {
        if (!Core.World.Ready) return;
        var dict = CityData.Instance?.citizenDictionary;
        if (dict == null || !dict.TryGetValue(m.VictimId, out var victim) || victim == null || victim.isDead) return;
        var killer = DecodeKiller(m.KillerId);
        MurderController.Murder murder = null;
        var murders = MurderController.Instance?.activeMurders;
        if (murders != null && m.MurderId >= 0)
            for (int i = 0; i < murders.Count; i++) if (murders[i] != null && murders[i].murderID == m.MurderId) { murder = murders[i]; break; }
        Interactable weapon = null;
        if (m.WeaponId >= 0) CityData.Instance.savableInteractableDictionary?.TryGetValue(m.WeaponId, out weapon);

        Puppet.NpcPuppets.Forget(m.VictimId);
        _applying = true;
        try
        {
            // Put the victim where the host's victim died, so the crime scene is the same place on every machine.
            var pos = new UnityEngine.Vector3(m.Position.X, m.Position.Y, m.Position.Z);
            if ((victim.transform.position - pos).sqrMagnitude > 0.25f)
            {
                var node = Toolbox.Instance.FindClosestValidNodeToWorldPosition(pos, false, true, false, default, false, 0, false, 200);
                if (node != null) victim.Teleport(node, null, true, false, false);
                victim.transform.position = pos;
                Plugin.Event($"DEATH citizen {m.VictimId} moved to the host's crime scene");
            }
            victim.transform.rotation = UnityEngine.Quaternion.Euler(0f, m.Yaw, 0f);
            if (murder != null && killer != null)
            {
                if (victim.ai != null) victim.ai.enabled = true;
                victim.Murder(killer, true, murder, weapon, 0f);
                Plugin.Event($"DEATH citizen {m.VictimId} murdered by {m.KillerId} (murder {m.MurderId}) — full replay");
            }
            else
            {
                ShowDead(victim);
                Plugin.Event($"DEATH citizen {m.VictimId} (murder {m.MurderId} not on this machine) — visual only");
            }
            Applied++;
        }
        catch (Exception e) { Plugin.Event($"DEATH {m.VictimId} failed: {e.Message}"); }
        finally { _applying = false; }
    }
}
