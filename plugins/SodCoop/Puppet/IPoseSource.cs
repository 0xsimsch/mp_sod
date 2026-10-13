namespace SodCoop.Puppet;

/// <summary>
/// Supplies poses for a remote player's body. The prototype uses <see cref="GhostReplaySource"/>
/// and <see cref="FixedPoseSource"/>; the network layer will add a source fed by received messages.
/// </summary>
public interface IPoseSource
{
    /// <summary>Called every frame. Return false when there's nothing new to apply.</summary>
    bool TryGetPose(float now, out PlayerPose pose);
}

/// <summary>Holds the body at one spot (used for the trespass test).</summary>
public sealed class FixedPoseSource : IPoseSource
{
    readonly PlayerPose _pose;

    public FixedPoseSource(PlayerPose pose) => _pose = pose;

    public bool TryGetPose(float now, out PlayerPose pose)
    {
        pose = _pose;
        return true;
    }
}
