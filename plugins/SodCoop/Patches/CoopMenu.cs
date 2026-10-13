using System;
using System.Collections.Generic;
using HarmonyLib;
using SodCoop.Net;
using UnityEngine;

namespace SodCoop.Patches;

/// <summary>
/// Co-op entries in the game's own main/Esc menu, made from copies of its "Load Game" button (same look, sounds and
/// highlight): Host Co-op / Join Friend when no session runs; Invite Friend (host) and Leave Co-op during one.
/// The copies get a fresh click event (the original's persistent "open Load Game" call must not come along) and lose
/// <c>MenuAutoTextController</c>, which would put the localized "Load Game" back on them.
/// </summary>
static class CoopMenu
{
    sealed class Entry
    {
        public ButtonController Button;
        public string Label;
        public Func<bool> Visible;
        public Func<bool> Enabled;
    }

    static readonly List<Entry> _entries = new();
    static int _builtFor; // MainMenuController instance id the buttons belong to

    [HarmonyPatch(typeof(MainMenuController), nameof(MainMenuController.EnableMainMenu))]
    static class Show
    {
        static void Postfix(MainMenuController __instance, bool val)
        {
            if (!val) return;
            try { Build(__instance); Refresh(); }
            catch (Exception e) { Plugin.Event($"MENU co-op buttons failed: {e.Message}"); }
        }
    }

    static bool InSession => NetSession.Current != null;
    static bool IsHost => NetSession.Current?.Role == NetSession.SessionRole.Host;

    static void Build(MainMenuController menu)
    {
        if (_builtFor == menu.GetInstanceID() && _entries.Count > 0 && _entries[0].Button != null) return;
        _entries.Clear();
        var template = menu.loadGameButton;
        if (template == null) { Plugin.Event("MENU no Load Game button to copy"); return; }
        int index = template.transform.GetSiblingIndex();
        Add(template, ++index, "Host Co-op", () => !InSession, () => Core.World.Ready, Host);
        Add(template, ++index, "Join Friend", () => !InSession, () => true, Join);
        Add(template, ++index, "Invite Friend", () => IsHost, () => true, () => NetSession.Current?.OpenInvite());
        Add(template, ++index, "Leave Co-op", () => InSession, () => true, Leave);
        _builtFor = menu.GetInstanceID();
        Plugin.Event($"MENU added {_entries.Count} co-op buttons");
    }

    static void Add(ButtonController template, int siblingIndex, string label, Func<bool> visible, Func<bool> enabled, Action onClick)
    {
        var go = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent);
        go.name = "SodCoop " + label;
        go.transform.SetSiblingIndex(siblingIndex);
        foreach (var auto in go.GetComponentsInChildren<MenuAutoTextController>(true)) UnityEngine.Object.Destroy(auto);
        var bc = go.GetComponent<ButtonController>();
        if (bc == null) { UnityEngine.Object.Destroy(go); return; }
        if (bc.button != null)
        {
            bc.button.onClick = new UnityEngine.UI.Button.ButtonClickedEvent();
            bc.button.onClick.AddListener(onClick);
        }
        var entry = new Entry { Button = bc, Label = label, Visible = visible, Enabled = enabled };
        SetLabel(entry);
        _entries.Add(entry);
    }

    /// <summary>
    /// Every text in the copy, again on each refresh: something in the game re-labels menu buttons after we set ours
    /// (first in-game test showed "Load Game" on all copies).
    /// </summary>
    static void SetLabel(Entry e)
    {
        foreach (var t in e.Button.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true))
            if (t.text != e.Label) t.text = e.Label;
    }

    /// <summary>Show the entries that fit the session state (called when the menu opens and twice a second).</summary>
    public static void Refresh()
    {
        foreach (var e in _entries)
        {
            if (e.Button == null || e.Button.WasCollected) continue;
            bool vis = e.Visible();
            if (e.Button.gameObject.activeSelf != vis) e.Button.gameObject.SetActive(vis);
            if (vis) { e.Button.SetInteractable(e.Enabled()); SetLabel(e); }
        }
    }

    static void Host()
    {
        NetConfig.Mode.Value = NetConfig.NetMode.HostSteam;
        CoopBehaviour.StartOrInvite();
        Refresh();
    }

    static void Join()
    {
        NetConfig.Mode.Value = NetConfig.NetMode.JoinSteam;
        NetConfig.JoinLobby.Value = 0;
        CoopBehaviour.StartOrInvite();
        Refresh();
    }

    static void Leave()
    {
        NetSession.Current?.Dispose();
        SessionOverlay.Flash("left the co-op session");
        Refresh();
    }
}
