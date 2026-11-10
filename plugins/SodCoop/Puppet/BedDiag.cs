using System.Collections.Generic;
using UnityEngine;

namespace SodCoop.Puppet;

/// <summary>
/// Diagnostics for "the partner floats above the bed" (0.9.56): where the game puts a real citizen who lies in a bed
/// (transform vs. the bed's usage point vs. the floor, and how high the animation lifts the torso), and the same for a
/// partner's lying body. One line per citizen/bed, so a play session tells us the right offset.
/// </summary>
public static class BedDiag
{
    static readonly HashSet<int> _citizensLogged = new();

    static float Bone(Human h, CitizenOutfitController.CharacterAnchor a)
    {
        var oc = h.outfitController;
        var t = oc != null ? oc.GetBodyAnchor(a) : null;
        return t != null ? t.position.y : float.NaN;
    }

    static string Body(Human h) =>
        $"transform y={h.transform.position.y:F2} torso y={Bone(h, CitizenOutfitController.CharacterAnchor.lowerTorso):F2} " +
        $"head y={Bone(h, CitizenOutfitController.CharacterAnchor.Head):F2} node floor y={(h.currentNode != null ? h.currentNode.position.y.ToString("F2") : "?")}";

    /// <summary>Host: a citizen in bed that we're about to stream (once per citizen, at most 8, after it settled).</summary>
    public static void Citizen(Citizen c)
    {
        if (!c.isInBed || _citizensLogged.Count >= 8 || _citizensLogged.Contains(c.humanID)) return;
        try
        {
            var it = c.ai != null && c.ai.currentAction != null ? c.ai.currentAction.interactable : null;
            if (it == null) return;
            _citizensLogged.Add(c.humanID);
            string use = "none";
            if (it.usagePoint != null)
            {
                var u = it.usagePoint.GetUsageWorldPosition(c.transform.position, c);
                use = $"({u.x:F2}, {u.y:F2}, {u.z:F2})";
            }
            Plugin.Event($"BEDDIAG citizen {c.humanID} in {(it.preset != null ? it.preset.name : "?")} #{it.id} (low={c.isInLowBed}): " +
                         $"{Body(c)} pos=({c.transform.position.x:F2}, {c.transform.position.z:F2}) yaw={c.transform.eulerAngles.y:F0} " +
                         $"usage point={use} furniture yaw={(it.controller != null ? it.controller.transform.eulerAngles.y.ToString("F0") : "?")}");
        }
        catch (System.Exception e) { Plugin.Event($"BEDDIAG citizen failed: {e.Message}"); }
    }

    /// <summary>A partner's body, a moment after it lay down.</summary>
    public static void Partner(string owner, Human h) =>
        Plugin.Event($"BEDDIAG partner {owner} lying: {Body(h)} pos=({h.transform.position.x:F2}, {h.transform.position.z:F2}) yaw={h.transform.eulerAngles.y:F0}");
}
