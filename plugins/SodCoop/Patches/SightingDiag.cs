using System;
using System.Collections.Generic;
using HarmonyLib;
using SodCoop.Core;
using UnityEngine;

namespace SodCoop.Patches;

/// <summary>
/// Diagnostics: do citizens near a remote body run SightingCheck at all, and what are the
/// puppet's perception-relevant flags? Dumped every few seconds by <see cref="Dump"/>.
/// </summary>
static class SightingDiag
{
    static readonly Dictionary<IntPtr, int> _checksNearRemote = new();

    [HarmonyPatch(typeof(Actor), nameof(Actor.SightingCheck))]
    static class Count
    {
        static void Postfix(Actor __instance)
        {
            try
            {
                if (__instance == null || __instance.isPlayer) return;
                foreach (var p in PlayerRegistry.All)
                {
                    var body = p.RemoteBody?.Human;
                    if (body == null) continue;
                    if (Vector3.Distance(__instance.transform.position, body.transform.position) > 15f) continue;
                    _checksNearRemote.TryGetValue(__instance.Pointer, out var n);
                    _checksNearRemote[__instance.Pointer] = n + 1;
                }
            }
            catch { /* diagnostics must never break the game */ }
        }
    }

    static string Who(Actor a)
    {
        if (a == null) return "none";
        var owner = PlayerRegistry.OwnerOf(a);
        if (owner != null) return owner.Id.ToString();
        var h = a.TryCast<Human>();
        return h != null ? h.humanID.ToString() : a.name;
    }

    static int _calibrationLogs;

    /// <summary>Movement values of a real walking citizen, to calibrate the puppet's animation.</summary>
    static void LogWalkingCitizenCalibration()
    {
        if (_calibrationLogs >= 5 || Player.Instance == null) return;
        var citizens = CityData.Instance.citizenDirectory;
        for (int i = 0; i < citizens.Count; i++)
        {
            var c = citizens[i];
            if (c == null || PlayerRegistry.IsRemoteBody(c) || !c.isMoving || c.currentMovementSpeed <= 0.01f) continue;
            if (Vector3.Distance(c.transform.position, Player.Instance.transform.position) > 40f) continue;
            _calibrationLogs++;
            Plugin.Event($"CALIB citizen {c.humanID} running={c.isRunning} normSpeed={c.currentNormalizedSpeed:F2} moveSpeed={c.currentMovementSpeed:F2} " +
                         $"walk={c.movementWalkSpeed:F2} run={c.movementRunSpeed:F2} ratio={c.walkingSpeedRatio:F2} mult={c.speedMultiplier:F2}");
            return;
        }
    }

    public static void Dump()
    {
        foreach (var p in PlayerRegistry.All)
        {
            var h = p.RemoteBody?.Human;
            if (h == null) continue;
            int esc = 0;
            bool tr = h.currentRoom != null && h.IsTrespassing(h.currentRoom, out esc, true);
            var animator = h.animationController != null ? h.animationController.mainAnimator : null;
            Plugin.Event($"DIAG {p} anim moveSpeed={(animator != null ? animator.GetFloat("moveSpeed") : -1f):F2} walkAnimSpeed={(animator != null ? animator.GetFloat("walkAnimSpeed") : -1f):F2} animatorEnabled={(animator != null && animator.enabled)} aiKo={(h.ai != null && h.ai.ko)} bodyKO={p.RemoteBody.KnockedOut}");
            Plugin.Event($"DIAG {p} ownAi.persuit={(h.ai != null && h.ai.persuit)}->{Who(h.ai?.persuitTarget)} persuedBy={h.persuedBy?.Count ?? 0} room={(h.currentRoom != null ? h.currentRoom.roomID : -1)} isTrespassing={h.isTrespassing} " +
                         $"areaActive={h.illegalAreaActive} actionActive={h.illegalActionActive} status={h.illegalStatus} " +
                         $"esc={h.trespassingEscalation} callNow={tr}/{esc} seenByOthers={h.isSeenByOthers} visible={h.visible} " +
                         $"stealth={h.stealthMode} vis={h.overallVisibility:F2} inVisibleActors={CityData.Instance.visibleActors.Contains(h.Cast<Actor>())} " +
                         Puppet.RemoteRoomLoader.Describe(h.currentRoom));
        }
        foreach (var p in PlayerRegistry.All)
        {
            var h = p.RemoteBody?.Human;
            if (h == null) continue;
            var target = h.Cast<Actor>();
            var citizens = CityData.Instance.citizenDirectory;
            var rows = new List<string>();
            for (int i = 0; i < citizens.Count; i++)
            {
                var c = citizens[i];
                if (c == null || c.Pointer == h.Pointer) continue;
                float d = Vector3.Distance(c.transform.position, h.transform.position);
                if (d > 20f) continue;
                float sees = 0f;
                bool has = c.seesIllegal != null && c.seesIllegal.TryGetValue(target, out sees);
                var ai = c.ai;
                rows.Add($"{c.humanID}[d={d:F0} room={(c.currentRoom != null ? c.currentRoom.roomID : -1)} asleep={(c.isAsleep ? 1 : 0)} " +
                         $"sees={(has ? sees.ToString("F2") : "-")} persuit={(ai != null && ai.persuit ? 1 : 0)}" +
                         $"->{Who(ai?.persuitTarget)} " +
                         $"react={(ai != null ? ai.reactionState.ToString() : "-")} rate={(ai != null ? ai.tickRate.ToString() : "-")}]");
            }
            Plugin.Event($"DIAG observers of {p}: {string.Join(" ", rows)}");
        }
        LogWalkingCitizenCalibration();
        if (_checksNearRemote.Count == 0) { Plugin.Event("DIAG no SightingCheck by citizens within 15 m of a remote body"); return; }
        var parts = new List<string>();
        foreach (var kv in _checksNearRemote)
        {
            var a = new Actor(kv.Key);
            var h = a.TryCast<Human>();
            parts.Add($"{(h != null ? h.humanID : -1)}:{kv.Value}");
        }
        Plugin.Event($"DIAG SightingCheck calls near remote (humanID:count) {string.Join(" ", parts)}");
        _checksNearRemote.Clear();
    }
}
