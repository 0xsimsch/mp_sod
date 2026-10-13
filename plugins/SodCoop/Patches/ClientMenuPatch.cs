using HarmonyLib;
using SodCoop.Net;

namespace SodCoop.Patches;

/// <summary>
/// In a session the host owns the world, so a client's Esc menu can't save, load or start a new game.
/// Save/Load are greyed out when the menu opens; New Game and Load are also refused as a backstop.
/// </summary>
static class ClientMenuPatch
{
    static bool IsClient => NetSession.Current?.Role == NetSession.SessionRole.Client;

    [HarmonyPatch(typeof(MainMenuController), nameof(MainMenuController.EnableMainMenu))]
    static class Buttons
    {
        static void Postfix(MainMenuController __instance, bool val)
        {
            if (!val) return;
            bool allowed = !IsClient;
            __instance.saveGameButton?.SetInteractable(allowed);
            __instance.loadGameButton?.SetInteractable(allowed);
        }
    }

    [HarmonyPatch(typeof(MainMenuController), nameof(MainMenuController.SetMenuComponent), typeof(MainMenuController.Component))]
    static class NoNewGame
    {
        static bool Prefix(MainMenuController.Component newComponent)
        {
            if (!IsClient || newComponent != MainMenuController.Component.newGameSelect) return true;
            Plugin.Event("MENU blocked: clients can't start a new game during a session (leave with Ctrl+Delete first)");
            return false;
        }
    }

    // The menu buttons call the int overload.
    [HarmonyPatch(typeof(MainMenuController), nameof(MainMenuController.SetMenuComponent), typeof(int))]
    static class NoNewGameInt
    {
        static bool Prefix(int newComponent)
        {
            if (!IsClient || newComponent != (int)MainMenuController.Component.newGameSelect) return true;
            Plugin.Event("MENU blocked: clients can't start a new game during a session (leave with Ctrl+Delete first)");
            return false;
        }
    }

    // Not CaptureSaveStateAsync: it returns a Task, and skipping it would hand callers a null task to await.
    [HarmonyPatch(typeof(MainMenuController), nameof(MainMenuController.LoadGame))]
    static class NoLoad
    {
        static bool Prefix()
        {
            if (!IsClient) return true;
            Plugin.Event("MENU blocked: clients can't load a save during a session");
            return false;
        }
    }
}
