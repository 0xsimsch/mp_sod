using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SodCoop.Patches;

/// <summary>
/// Replaces the game's menu/splash backdrop with the image shipped in the DLL (Assets/menu_bg.png, embedded
/// resource). The backdrop sprite "menuBG" is used by more than MainMenuController.backgroundImage: the splash
/// scene "Press Any Button" (level0: its own "Background Image" next to Logo/PressAnyButtonText) has a separate
/// copy, so a whole sweep runs on every scene load - each Image whose sprite or RawImage whose texture is still
/// named "menuBG" gets ours. An EnableMainMenu catch-up re-applies if Unity collected a copy when its scene
/// unloaded and the scene never reloads.
/// </summary>
static class MenuBackground
{
    const string ResourceName = "SodCoop.menu_bg.png"; // LogicalName in SodCoop.csproj
    const string VanillaName = "menuBG"; // how the game names the backdrop sprite and texture (sharedassets0)

    static Texture2D _tex;
    static Sprite _sprite;
    static bool _failed;
    static bool _appliedLogged;

    /// <summary>Called from Plugin.Load: sweep the scene that may already be up (plugin load vs. first scene).</summary>
    public static void Install() => Sweep(UnityEngine.SceneManagement.SceneManager.GetActiveScene());

    // Scene-change watcher, ticked from CoopBehaviour.Update: the interop exposes sceneLoaded only as a
    // UnityAction field whose delegate type just takes an IntPtr constructor (like the canvas events, see
    // analysis/MENU_RESULTS.md), so we cannot subscribe - checking the active scene handle is equally good.
    static int _sceneHandle = -1;

    public static void Tick()
    {
        if (_failed) return;
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (scene.handle == _sceneHandle) return;
        _sceneHandle = scene.handle;
        Sweep(scene);
    }

    static void Sweep(Scene scene)
    {
        if (_failed) return;
        try
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root == null) continue;
                foreach (var img in root.GetComponentsInChildren<UnityEngine.UI.Image>(true))
                    Replace(img);
                foreach (var raw in root.GetComponentsInChildren<UnityEngine.UI.RawImage>(true))
                    Replace(raw);
            }
        }
        catch (Exception e)
        {
            _failed = true;
            Plugin.Event($"MENU background sweep failed: {e.Message}");
        }
    }

    static void Replace(UnityEngine.UI.Image img)
    {
        if (img == null || img.WasCollected) return;
        var cur = img.sprite;
        if (cur == _sprite) return; // already ours
        if (cur == null || cur.WasCollected || cur.name != VanillaName) return; // not the backdrop
        EnsureSprite();
        if (_sprite == null) return;
        img.sprite = _sprite;
        LogApplied();
    }

    static void Replace(UnityEngine.UI.RawImage img)
    {
        if (img == null || img.WasCollected) return;
        var cur = img.texture;
        if (cur == _tex) return;
        if (cur == null || cur.WasCollected || cur.name != VanillaName) return;
        EnsureSprite();
        if (_tex == null) return;
        img.texture = _tex;
        LogApplied();
    }

    // Catch-up for an open menu whose backdrop copy got collected with an older scene (no new sceneLoaded).
    [HarmonyPatch(typeof(MainMenuController), nameof(MainMenuController.EnableMainMenu))]
    static class Show
    {
        static void Postfix(MainMenuController __instance, bool val)
        {
            if (_failed || !val) return;
            try { Replace(__instance.backgroundImage); }
            catch (Exception e)
            {
                _failed = true; // decode problems won't fix themselves; the game keeps the vanilla background
                Plugin.Event($"MENU background failed: {e.Message}");
            }
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

    static void LogApplied()
    {
        if (_appliedLogged) return;
        _appliedLogged = true;
        Plugin.Event("MENU background replaced with the bundled image");
    }
}
