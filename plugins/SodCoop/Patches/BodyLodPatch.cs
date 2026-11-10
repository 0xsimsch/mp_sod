using HarmonyLib;
using SodCoop.Core;

namespace SodCoop.Patches;

/// <summary>
/// The game runs LOD on citizens by distance (<c>Human.UpdateLODs</c>, <c>Actor.distantLOD</c> /
/// <c>meshesLOD1</c>): past the threshold the static LOD meshes take over and the animator stops — seen
/// for remote players at ~20 m: the body was still clearly visible, but the walk animation froze while
/// RemotePlayerBody.LateApply kept multiplying its neck offset onto the frozen bone (head turning, pitch
/// axis). A remote player body is what the players watch, so the LOD pass skips it entirely; a few bodies
/// at full detail cost nothing.
/// </summary>
[HarmonyPatch(typeof(Human), nameof(Human.UpdateLODs))]
static class BodyLodPatch
{
    static bool Prefix(Human __instance) => !PlayerRegistry.IsRemoteBody(__instance);
}
