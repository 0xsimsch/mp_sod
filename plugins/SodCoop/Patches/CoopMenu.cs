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

    // The header/footer lines are the row's own "EmbelishmentTop"/"EmbelishmentBottom" children (red line sprites;
    // MainMenuController.topBarRend/bottomBarRend are NOT them - those are the screen-edge TopGradient/
    // ButtomGradient vignettes, learned the hard way via a scene dump 2026-10-16). They sit at fixed spots, so our
    // added buttons push the column past the footer into it. Remember how far each one originally is from the
    // top/bottom edge of the visible button column, then place them relative to that edge on every canvas frame.
    static RectTransform _row;
    static ButtonController _template;
    static RectTransform _embelTop, _embelBottom;
    static float _topGap, _bottomGap;
    static bool _barsHome;
    static float _pendTop = float.NaN, _pendBottom = float.NaN; // previous capture attempt
    static int _pendCount = -1;
    static int _streak;          // consecutive equal samples
    static float _streakStart;   // when that streak began

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
        _template = template;
        CaptureBars(menu, template); // before our copies go in: the row then holds exactly the game's buttons
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
        TryCaptureBars(); // every 30 frames via the menu step; no-op once this launch's offsets are validated
    }

    const string GapTopKey = "sodcoop.menuGapTop", GapBottomKey = "sodcoop.menuGapBottom"; // cached between game runs
    static bool _validated; // this launch's measurement accepted (even when placement already runs from cache)

    /// <summary>Measures the bar offsets against the game's own buttons (our copies excluded, so capture can
    /// also succeed later than Build).</summary>
    static void CaptureBars(MainMenuController menu, ButtonController template)
    {
        _barsHome = false;
        _validated = false;
        _barFailLogged = false;
        _pendTop = float.NaN; _pendBottom = float.NaN; _pendCount = -1; _streak = 0;
        // Not "transform.parent as RectTransform": the interop wrapper of parent is typed Transform and the
        // cast would fail even though it is one. GetComponent finds the real RectTransform.
        var parent = template.transform.parent;
        _row = parent == null ? null : parent.GetComponent<RectTransform>();
        _embelTop = FindChild(_row, "EmbelishmentTop");
        _embelBottom = FindChild(_row, "EmbelishmentBottom");
        // The gaps are constants for a given game version and canvas setup, so placement may start from the
        // cached values on frame one (no wrong-position phase); TryCaptureBars below still runs and refreshes
        // the cache whenever its own measurement says they changed.
        if ((_embelTop != null || _embelBottom != null) && PlayerPrefs.HasKey(GapTopKey) && PlayerPrefs.HasKey(GapBottomKey))
        {
            _topGap = PlayerPrefs.GetFloat(GapTopKey);
            _bottomGap = PlayerPrefs.GetFloat(GapBottomKey);
            if (!float.IsNaN(_topGap) && !float.IsNaN(_bottomGap))
            {
                _barsHome = true;
                Plugin.Event($"MENU bars use cached offsets: top {_topGap:F1} / bottom {_bottomGap:F1}");
            }
        }
        TryCaptureBars();
    }

    static RectTransform FindChild(RectTransform row, string name)
    {
        if (row == null) return null;
        for (int i = 0; i < row.childCount; i++)
        {
            var child = row.GetChild(i);
            if (child != null && child.name == name) return child.GetComponent<RectTransform>();
        }
        return null;
    }

    // Placement hook: postfixing the engine's per-frame canvas callback beats every writer - Update scripts,
    // coroutines, LateUpdates and the layout rebuilds of CanvasUpdateRegistry all run before it. (The
    // willRenderCanvases event itself is not subscribable from managed code: the interop delegate's only
    // constructor takes a native IntPtr.)
    [HarmonyPatch(typeof(UnityEngine.Canvas), nameof(UnityEngine.Canvas.SendWillRenderCanvases))]
    static class CanvasWillRender
    {
        static void Postfix() => PlaceBars();
    }

    static bool _placedLogged;

    /// <summary>Retried from every Refresh until accepted. The game plays its buttons in one by one when the
    /// menu opens, so the offsets are only accepted once a streak of >= 3 identical samples spanning >= 0.6 s
    /// says the layout has settled - at any frame rate. Also runs when placement started from the cache, to
    /// verify and refresh it.</summary>
    static void TryCaptureBars()
    {
        if (_validated || _template == null || _template.WasCollected) return;
        if (_row == null) { FailOnce("MENU bar capture failed: no RectTransform row above Load Game"); return; }
        if (!_row.gameObject.activeInHierarchy) return; // menu closed or another panel is up: wait
        if (_embelTop == null || _embelTop.WasCollected) _embelTop = FindChild(_row, "EmbelishmentTop");
        if (_embelBottom == null || _embelBottom.WasCollected) _embelBottom = FindChild(_row, "EmbelishmentBottom");
        if (_embelTop == null && _embelBottom == null) { FailOnce("MENU bar capture failed: no Embelishment children in the row"); return; }
        // Measure against the vanilla column: our copies sit IN the list (between Load Game and Settings) and
        // shift the game buttons down, so measuring around them would bake that shift into the gap and the
        // follow would be a no-op. Hide them for one layout pass, measure, put them back.
        List<GameObject> hidden = null;
        foreach (var e in _entries)
            if (e.Button != null && !e.Button.WasCollected && e.Button.gameObject.activeSelf)
            { hidden ??= new List<GameObject>(); hidden.Add(e.Button.gameObject); e.Button.gameObject.SetActive(false); }
        UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(_row);
        bool ok = RowExtents(true, out float top, out float bottom, out int count);
        if (hidden != null) foreach (var g in hidden) g.SetActive(true);
        if (ok) UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(_row); // restore the shifted layout
        if (!ok) { FailOnce("MENU bar capture failed: no visible game button in the row"); return; }
        float t = _embelTop != null && !_embelTop.WasCollected ? _embelTop.position.y - top : float.NaN;
        float b = _embelBottom != null && !_embelBottom.WasCollected ? _embelBottom.position.y - bottom : float.NaN;
        // The intro plays the buttons in roughly every 0.2 s, and two readings between two of those steps are
        // identical too - so accept only a streak of >= 3 equal samples spanning >= 0.6 s. (A fixed per-sample
        // minimum spacing fails at high frame rates: the 30-frame Refresh tick is just 0.2 s at 144 fps.)
        bool same = count == _pendCount && SameGap(t, _pendTop) && SameGap(b, _pendBottom);
        if (!same)
        {
            _pendTop = t; _pendBottom = b; _pendCount = count;
            _streak = 1; _streakStart = Time.unscaledTime;
            return;
        }
        _streak++;
        if (_streak < 3 || Time.unscaledTime - _streakStart < 0.6f) return; // still settling; decide later
        bool fresh = !_barsHome;
        bool changed = fresh || Mathf.Abs(t - _topGap) > 2f || Mathf.Abs(b - _bottomGap) > 2f;
        _topGap = t; _bottomGap = b;
        _barsHome = true;
        _validated = true;
        if (!float.IsNaN(t) && !float.IsNaN(b))
        {
            PlayerPrefs.SetFloat(GapTopKey, t);
            PlayerPrefs.SetFloat(GapBottomKey, b);
            PlayerPrefs.Save();
        }
        Plugin.Event($"MENU bar offsets {(fresh ? "captured" : changed ? "updated" : "confirmed")} (row {_row.name}, {count} buttons): gaps top {_topGap:F1} / bottom {_bottomGap:F1}; column {top:F1}..{bottom:F1}");
    }

    static bool SameGap(float a, float b)
        => float.IsNaN(a) && float.IsNaN(b) || !float.IsNaN(a) && !float.IsNaN(b) && Mathf.Abs(a - b) < 2f;

    static bool _barFailLogged;
    static void FailOnce(string msg)
    {
        if (_barFailLogged) return;
        _barFailLogged = true;
        Plugin.Event(msg);
    }

    /// <summary>World-space y-range and center x of a UI rect. Not GetWorldCorners(Vector3[]): the array
    /// parameter does not survive the IL2CPP interop call (measured 2026-10-16: corners stayed all-zero).</summary>
    static void WorldEdges(RectTransform rt, out float lo, out float hi, out float cx, out float halfW)
    {
        var r = rt.rect;
        var a = rt.TransformPoint(new Vector3(r.xMin, r.yMin, 0f));
        var b = rt.TransformPoint(new Vector3(r.xMax, r.yMax, 0f));
        lo = Mathf.Min(a.y, b.y); hi = Mathf.Max(a.y, b.y);
        cx = (a.x + b.x) * 0.5f; halfW = Mathf.Max(1f, Mathf.Abs(b.x - a.x) * 0.5f);
    }

    /// <summary>Top and bottom edge (world y) and count of the visible button column. Only buttons in the
    /// template's own column (same x) count, so buttons outside the list can't throw it off.</summary>
    static bool RowExtents(bool gameButtonsOnly, out float top, out float bottom, out int count)
    {
        top = float.MinValue; bottom = float.MaxValue; count = 0;
        bool any = false;
        bool inColumn = _template != null && !_template.WasCollected && _template.gameObject.activeInHierarchy;
        float cx = 0f, half = 0f;
        if (inColumn)
        {
            var trt = _template.transform.GetComponent<RectTransform>();
            if (trt != null) WorldEdges(trt, out _, out _, out cx, out half);
            else inColumn = false;
        }
        for (int i = 0; i < _row.childCount; i++)
        {
            var child = _row.GetChild(i);
            if (child == null || !child.gameObject.activeSelf) continue;
            if (gameButtonsOnly && child.name.StartsWith("SodCoop")) continue;
            var bc = child.GetComponent<ButtonController>();
            if (bc == null || bc.WasCollected) continue;
            var crt = child.GetComponent<RectTransform>();
            if (crt == null) continue;
            WorldEdges(crt, out float lo, out float hi, out float bx, out _);
            if (inColumn && Mathf.Abs(bx - cx) > half) continue;
            count++;
            if (!any) { top = hi; bottom = lo; any = true; }
            else { top = Mathf.Max(top, hi); bottom = Mathf.Min(bottom, lo); }
        }
        return any;
    }

    /// <summary>Keeps the header/footer lines hugging the button column as buttons appear and vanish (host/join
    /// vs. invite/leave). Runs on the engine's per-canvas-frame callback, after every Update, LateUpdate and
    /// layout rebuild, so the last write of the frame is ours.</summary>
    static void PlaceBars()
    {
        try
        {
            if (!_barsHome || _row == null || !_row.gameObject.activeInHierarchy) return;
            if (!RowExtents(false, out float top, out float bottom, out _)) return;
            if (_embelTop != null && !_embelTop.WasCollected)
            {
                var p = _embelTop.position;
                p.y = top + _topGap;
                _embelTop.position = p;
                if (!_placedLogged) { _placedLogged = true; Plugin.Event($"MENU bars re-placed: top {p.y:F1}, bottom {bottom + _bottomGap:F1}"); }
            }
            if (_embelBottom != null && !_embelBottom.WasCollected)
            {
                var p = _embelBottom.position;
                p.y = bottom + _bottomGap;
                _embelBottom.position = p;
            }
        }
        catch (Exception e) { FailOnce($"MENU bar follow failed: {e.Message}"); } // this fires per canvas frame - log once
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
