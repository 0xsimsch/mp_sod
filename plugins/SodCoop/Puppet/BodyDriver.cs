using UnityEngine;

namespace SodCoop.Puppet;

/// <summary>
/// Drives a citizen body from poses instead of its AI: transform, node/room bookkeeping and the walk
/// animation. Shared by remote player bodies and network-driven citizens (<see cref="NpcPuppets"/>).
/// The caller is responsible for turning the citizen's AI off.
/// </summary>
public sealed class BodyDriver
{
    public Human Human { get; }
    Vector3Int _lastNodeCoord = new(int.MinValue, 0, 0);

    public BodyDriver(Human human) => Human = human;

    /// <summary>The donor may have been sitting, in bed, smoking…; a driven body starts neutral.</summary>
    public void ResetAnimation()
    {
        var anim = Human.animationController;
        if (anim == null) return;
        if (Human.isInBed) anim.SetInBed(false, false, false, true);
        anim.SetIdleAnimationState(CitizenAnimationController.IdleAnimationState.none);
        anim.SetArmsBoolState(CitizenAnimationController.ArmsBoolSate.none);
    }

    Vector3 _lastPos;
    float _lastPoseTime = -1f;
    float _speed;

    /// <summary>
    /// Drive the walk cycle. <c>CitizenAnimationController.UpdateMovementSpeed</c> feeds the animator
    /// <c>moveSpeed</c> ← <c>Human.currentNormalizedSpeed</c> and <c>walkAnimSpeed</c> ←
    /// <c>Human.currentMovementSpeed</c>; normally the citizen AI sets those while path following.
    /// </summary>
    void UpdateAnimation(PlayerPose pose)
    {
        var anim = Human.animationController;
        if (anim == null) return;
        if (_lastPoseTime >= 0f && pose.Time > _lastPoseTime)
        {
            var d = pose.Position - _lastPos;
            d.y = 0f;
            float measured = d.magnitude / (pose.Time - _lastPoseTime);
            _speed = Mathf.Lerp(_speed, measured, 0.3f);
        }
        else if (!pose.Moving) _speed = Mathf.Lerp(_speed, 0f, 0.3f);
        if (_speed < 0.05f) _speed = 0f;
        _lastPos = pose.Position;
        _lastPoseTime = pose.Time;

        float run = Human.movementRunSpeed > 0.1f ? Human.movementRunSpeed : 5f;
        Human.currentMovementSpeed = _speed;
        Human.currentNormalizedSpeed = Mathf.Clamp01(_speed / run);
        Human.desiredNormalizedSpeed = Human.currentNormalizedSpeed;
        anim.UpdateMovementSpeed();
    }

    /// <summary>Move the body to a pose, keeping the game's node/room bookkeeping right.</summary>
    public void Apply(PlayerPose pose)
    {
        UpdateAnimation(pose);
        var city = CityData.Instance;
        if (city != null)
        {
            var coord = city.RealPosToNodeInt(pose.Position);
            if (coord != _lastNodeCoord)
            {
                // Teleport updates currentNode / currentRoom / gameLocation for us.
                var node = Toolbox.Instance.FindClosestValidNodeToWorldPosition(pose.Position, false, true, false, default, false, 0, false, 200);
                if (node != null) Human.Teleport(node, null, true, false, false);
                _lastNodeCoord = coord;
            }
        }

        Human.transform.position = pose.Position;
        Human.transform.rotation = Quaternion.Euler(0f, pose.Yaw, 0f);
        Human.isCrouched = pose.Crouched;
        Human.isRunning = pose.Running;
        Human.isMoving = pose.Moving;
    }
}
