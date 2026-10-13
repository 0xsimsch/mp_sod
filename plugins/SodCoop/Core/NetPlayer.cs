using SodCoop.Puppet;

namespace SodCoop.Core;

public enum PlayerLifeState
{
    Alive,
    /// <summary>Design decision: dead players stay dead and spectate.</summary>
    Dead,
}

/// <summary>
/// Everything we track per player. Per-player state lives here, never in game singletons.
/// </summary>
public sealed class NetPlayer
{
    public PlayerId Id { get; }
    public string DisplayName { get; set; }

    /// <summary>The player sitting at this machine.</summary>
    public bool IsLocal { get; }

    public PlayerLifeState LifeState { get; set; } = PlayerLifeState.Alive;

    /// <summary>
    /// Body in the game world. For the local player this is <c>Player.Instance</c>; for remote
    /// players it's a possessed citizen driven by network poses.
    /// </summary>
    public Human Body => IsLocal ? Player.Instance : RemoteBody?.Human;

    /// <summary>Set for remote players only.</summary>
    public RemotePlayerBody RemoteBody { get; internal set; }

    /// <summary>
    /// Design decision: only the player who committed a crime is blamed. Counters for now;
    /// replaced by the real wanted/fine model once the witness patches exist.
    /// </summary>
    public int TimesSeenDoingIllegal { get; internal set; }
    public int TimesPursued { get; internal set; }

    public NetPlayer(PlayerId id, string displayName, bool isLocal)
    {
        Id = id;
        DisplayName = displayName;
        IsLocal = isLocal;
    }

    public override string ToString() => $"{Id} \"{DisplayName}\"{(IsLocal ? " (local)" : "")}";
}
