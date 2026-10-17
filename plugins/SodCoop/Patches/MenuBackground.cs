using System;
using HarmonyLib;
using UnityEngine;

namespace SodCoop.Patches;

/// <summary>
/// Replaces the main menu's background image with one shipped in the DLL (Assets/menu_bg.png, embedded
/// resource). Per analysis/MENU_RESULTS.md the menu backdrop is the UI Image MainMenuController.backgroundImage
/// behind every menu panel (a still, not a live city view), so setting its sprite once per menu scene replaces
/// it everywhere, splash included. The sprite is built on demand and rebuilt after Unity collected a previous
/// menu scene's copy.
/// </summary>
static class MenuBackground
{
    const string ResourceName = "SodCoop.menu_bg.png"; // LogicalName in SodCoop.csproj

    static Texture2D _tex;
    static Sprite _sprite;
    static bool _failed;
    static bool _appliedLogged;

    // Earliest point the Inspector-assigned backgroundImage exists; before the menu's first drawn frame, so
    // even the splash never shows the game's own backdrop.
    [HarmonyPatch(typeof(MainMenuController), nameof(MainMenuController.Start))]
    static class MenuStart
    {
        static void Postfix(MainMenuController __instance) => Apply(__instance);
    }

    // Safety net: re-opened or rebuilt menus run EnableMainMenu with the panel about to show; if Start ever
    // missed (null field, patch order), this catches up before anything is visible.
    [HarmonyPatch(typeof(MainMenuController), nameof(MainMenuController.EnableMainMenu))]
    static class Show
    {
        static void Postfix(MainMenuController __instance, bool val)
        {
            if (val) Apply(__instance);
        }
    }

    static void Apply(MainMenuController menu)
    {
        if (_failed) return;
        try
        {
            var img = menu.backgroundImage;
            if (img == null || img.WasCollected) return; // not ready yet: the other hook retries
            if (_sprite != null && !_sprite.WasCollected && img.sprite == _sprite) return; // already ours
            EnsureSprite();
            if (_sprite == null) return;
            img.sprite = _sprite;
            if (!_appliedLogged)
            {
                _appliedLogged = true;
                Plugin.Event("MENU background replaced with the bundled image");
            }
        }
        catch (Exception e)
        {
            _failed = true; // decode problems won't fix themselves; the game keeps the vanilla background
            Plugin.Event($"MENU background failed: {e.Message}");
        }
    }

    /// <summary>Decode the embedded PNG into a texture + full-rect sprite (Simple-style, no border). Kept in
    /// statics; recreated if the object was collected when its menu scene unloaded.</summary>
    static void EnsureSprite()
    {
        if (_sprite != null && !_sprite.WasCollected && _tex != null && !_tex.WasCollected) return;
        byte[] bytes;
        using (var res = typeof(MenuBackground).Assembly.GetManifestResourceStream(ResourceName))
        {
            if (res == null)
            {
                _failed = true;
                Plugin.Event($"MENU background failed: embedded resource {ResourceName} not found");
                return;
            }
            bytes = new byte[res.Length];
            int off = 0;
            while (off < bytes.Length)
            {
                int n = res.Read(bytes, off, bytes.Length - off);
                if (n <= 0) break;
                off += n;
            }
        }
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        ImageConversion.LoadImage(tex, bytes); // sizes the texture from the PNG itself
        tex.name = "sodcoopMenuBG";
        _tex = tex;
        _sprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f));
        _sprite.name = "sodcoopMenuBG";
        Plugin.Event($"MENU background texture ready: {tex.width}x{tex.height} from {bytes.Length / 1024} KB png");
    }
}
