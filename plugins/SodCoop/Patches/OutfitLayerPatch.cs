using HarmonyLib;
using SodCoop.Core;
using UnityEngine;

namespace SodCoop.Patches;

/// <summary>
/// The "only the face is drawn" bug, root cause (disassembly of CitizenOutfitController.AddMeshRenderer,
/// RVA 0x686FF0, game 0.9.51): every clothing part the controller adds gets its gameObject set to layer
/// CitizenModels (24) — but when the wearer is <c>isPlayer</c> and not a scene poser, that is overwritten
/// with layer Player (20), which the scene camera culls (it is meant for the player's own dedicated
/// camera). We possess a borrowed citizen with <c>isPlayer = true</c> so the game treats the body as a
/// player, so every outfit rebuild after that — outfit sync, the game's day/night outfit change,
/// RemotePlayerBody.RepairOutfit — restamps its clothes onto the culled layer. The face (eyes, mouth,
/// eyebrows) is built in Citizen.Awake before we possess and is never re-added, so only it stayed visible
/// (0.9.51 two-machine test: the NOT DRAWN log shows the body renderers on layer 20, culled, face on 24).
///
/// Fix: parts (re)added onto a body the mod drives go back from Player to CitizenModels. The real player's
/// own body is not registered in PlayerRegistry and keeps layer 20 for its camera.
/// </summary>
[HarmonyPatch(typeof(CitizenOutfitController), "AddMeshRenderer")]
static class OutfitLayerPatch
{
    static void Postfix(CitizenOutfitController __instance, MeshRenderer rend)
    {
        if (__instance == null || rend == null) return;
        if (!PlayerRegistry.IsRemoteBody(__instance.human)) return;
        var go = rend.gameObject;
        if (go != null && go.layer == ModelLayers.Player) go.layer = ModelLayers.CitizenModels;
    }
}
