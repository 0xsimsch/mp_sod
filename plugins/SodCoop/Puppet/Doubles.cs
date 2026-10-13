using System;
using System.Collections.Generic;
using SodCoop.Core;
using UnityEngine;

namespace SodCoop.Puppet;

/// <summary>
/// One citizen per player is borrowed to show that player on the other machines. Before this, each machine picked its
/// own: the host's pick for a client's body walked around as an ordinary citizen on that client, and the client's pick
/// for the host's body walked around on the host ("characters that only appear on one side").
/// Now the host assigns every player's citizen (its own included) and sends the list; every machine borrows exactly
/// those for the other players, and hides the one that stands for itself (invisible, AI off, never synced or picked).
/// </summary>
public static class Doubles
{
    sealed class Hidden
    {
        public Human Human;
        public bool AiWasEnabled;
        public bool TickRateWasDisabled;
    }

    static readonly Dictionary<IntPtr, Hidden> _hidden = new();

    /// <summary>A citizen standing in for the local player elsewhere: kept out of sight and out of every sync.</summary>
    public static bool IsHidden(Actor a) => a != null && _hidden.ContainsKey(a.Pointer);

    /// <summary>Any citizen that stands for a player, here or elsewhere.</summary>
    public static bool IsBorrowed(Actor a) => PlayerRegistry.IsRemoteBody(a) || IsHidden(a);

    static Doubles() => World.Reloaded += () => _hidden.Clear(); // those citizens are gone with the old world

    public static void Hide(Human h, string why)
    {
        if (h == null || h.WasCollected || _hidden.ContainsKey(h.Pointer)) return;
        var e = new Hidden { Human = h, AiWasEnabled = h.ai != null && h.ai.enabled, TickRateWasDisabled = h.ai != null && h.ai.disableTickRateUpdate };
        NpcPuppets.Forget(h.humanID);
        if (h.ai != null) { h.ai.disableTickRateUpdate = true; h.ai.enabled = false; }
        h.SetVisible(false, true);
        _hidden[h.Pointer] = e;
        Plugin.Event($"DOUBLE hid citizen {h.humanID} \"{h.citizenName}\" ({why})");
    }

    public static void UnhideAll()
    {
        foreach (var e in _hidden.Values)
        {
            var h = e.Human;
            if (h == null || h.WasCollected) continue;
            if (h.ai != null) { h.ai.disableTickRateUpdate = e.TickRateWasDisabled; h.ai.enabled = e.AiWasEnabled; }
            h.SetVisible(true, true);
        }
        if (_hidden.Count > 0) Plugin.Event($"DOUBLE showed {_hidden.Count} hidden citizen(s) again");
        _hidden.Clear();
    }

    /// <summary>Keep only the given citizen hidden (the assignment for the local player changed).</summary>
    public static void HideOnly(Human h, string why)
    {
        if (h != null && _hidden.ContainsKey(h.Pointer) && _hidden.Count == 1) return;
        UnhideAll();
        Hide(h, why);
    }

    /// <summary>Every 0.5 s: room changes and culling may show them again.</summary>
    public static void Tick()
    {
        foreach (var e in _hidden.Values)
        {
            var h = e.Human;
            if (h == null || h.WasCollected) continue;
            if (h.visible) h.SetVisible(false, true);
            if (h.ai != null && h.ai.enabled) h.ai.enabled = false;
        }
    }
}
