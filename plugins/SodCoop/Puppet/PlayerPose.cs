using UnityEngine;

namespace SodCoop.Puppet;

/// <summary>
/// Snapshot of a player's body, as a remote client would send it. Kept small on purpose:
/// it becomes the unreliable movement message later.
/// </summary>
public struct PlayerPose
{
    public float Time;
    public Vector3 Position;
    public float Yaw;
    public bool Crouched;
    public bool Running;
    public bool Moving;

    public static PlayerPose FromHuman(Human h, float time) => new()
    {
        Time = time,
        Position = h.transform.position,
        Yaw = h.transform.eulerAngles.y,
        Crouched = h.isCrouched,
        Running = h.isRunning,
        Moving = h.isMoving,
    };
}
