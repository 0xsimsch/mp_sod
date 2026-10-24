using UnityEngine;

namespace SodCoop.Core;

/// <summary>
/// GameObject layers of citizen models. The scene camera does not render the Player layer; the game
/// itself moves an <c>isPlayer</c> citizen's outfit parts there for its dedicated player camera
/// (see Patches/OutfitLayerPatch.cs). A citizen we possess as a remote player's body must stay on
/// CitizenModels to keep being drawn. Resolved by name, with the layer indices from the shipped
/// project settings as fallback.
/// </summary>
public static class ModelLayers
{
    static int _citizen = -2, _player = -2;

    public static int CitizenModels => Resolve(ref _citizen, "CitizenModels", 24);
    public static int Player => Resolve(ref _player, "Player", 20);

    static int Resolve(ref int cache, string name, int fallback)
    {
        if (cache == -2)
        {
            int layer = LayerMask.NameToLayer(name);
            cache = layer >= 0 ? layer : fallback;
        }
        return cache;
    }
}
