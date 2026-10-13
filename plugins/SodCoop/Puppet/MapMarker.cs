using System;
using UnityEngine;

namespace SodCoop.Puppet;

/// <summary>
/// A marker for another player on the city map. Uses the map's own tracking (<c>MapController.AddNewTrackedObject</c>
/// with isDynamic = true: the map moves it with the object every frame) on the remote player's body, which already
/// follows that player's real position, so the marker is in sync on every machine. Icon: the map's own player icon,
/// tinted per player.
/// </summary>
sealed class MapMarker
{
    static readonly Color[] Colours =
    {
        new(1f, 0.55f, 0.1f),  // P0 (host): orange
        new(0.2f, 0.85f, 1f),  // P1: cyan
        new(0.45f, 1f, 0.3f),  // P2: green
        new(1f, 0.35f, 0.9f),  // P3: magenta
    };

    Transform _tracked;
    int _generation = -1;
    string _why = "";

    public string Status => _tracked != null ? "on" : _why.Length > 0 ? _why : "waiting";

    void Why(string reason, int playerId)
    {
        if (_why == reason) return;
        _why = reason;
        Plugin.Event($"MAP marker for P{playerId} not added yet: {reason}");
    }

    /// <summary>Add the marker if the map exists and it isn't there yet (the map may appear after the body).</summary>
    public void Ensure(Human body, int playerId)
    {
        var map = MapController.Instance;
        if (map == null) { Why("no map yet", playerId); return; }
        if (body == null) return;
        var t = body.transform;
        if (_tracked != null && _generation == Core.World.Generation && _tracked.Pointer == t.Pointer) return;
        // The map's own player icon (playerCharacterRect) isn't there in this build (seen: "map has no player icon"),
        // so use it if present, else a round marker drawn here.
        var icon = map.playerCharacterRect != null ? map.playerCharacterRect.GetComponentInChildren<UnityEngine.UI.Image>(true) : null;
        var sprite = icon != null && icon.sprite != null ? icon.sprite : DotSprite();
        var size = icon != null && icon.sprite != null ? map.playerCharacterRect.sizeDelta : new Vector2(28f, 28f);
        try
        {
            Remove();
            map.AddNewTrackedObject(t, sprite, size, Colours[Math.Abs(playerId) % Colours.Length], true, null);
            _tracked = t;
            _generation = Core.World.Generation;
            Plugin.Event($"MAP marker added for P{playerId}");
        }
        catch (Exception e) { Plugin.Event($"MAP marker for P{playerId} failed: {e.Message}"); _tracked = null; }
    }

    static Sprite _dot;

    /// <summary>A white disc with a dark ring (tinted per player by the map), made once.</summary>
    static Sprite DotSprite()
    {
        if (_dot != null) return _dot;
        const int n = 64;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        float c = (n - 1) / 2f;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
                var col = d > 1f ? new Color(0, 0, 0, 0) : d > 0.78f ? new Color(0.05f, 0.05f, 0.05f, 1f) : Color.white;
                tex.SetPixel(x, y, col);
            }
        tex.Apply();
        _dot = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f));
        _dot.hideFlags = HideFlags.DontUnloadUnusedAsset;
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        return _dot;
    }

    public void Remove()
    {
        if (_tracked == null) return;
        try
        {
            // A body from an older world: its transform (and the map) are gone already.
            if (_generation == Core.World.Generation && !_tracked.WasCollected) MapController.Instance?.RemoveTrackedObject(_tracked);
        }
        catch (Exception e) { Plugin.Event($"MAP marker removal failed: {e.Message}"); }
        _tracked = null;
    }
}
