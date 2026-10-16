using System;
using HarmonyLib;
using UnityEngine;

namespace SodCoop.Patches;

/// <summary>
/// Counts the local player's punches and weapon swings, so the other players' copy of our body can swing too
/// (sent in every pose as <see cref="Net.PoseMsg.Swings"/>).
/// <c>FirstPersonItemController.OnInteraction</c> starts a swing by setting the first-person animator's
/// <c>Punch</c> (fists) or <c>Attack</c> (weapon) trigger. The hit itself lands later in
/// <c>FirstPersonAnimationEvents.Punch</c> → <c>MeleeAttack</c>, an animation event we must not hook
/// (see Patches/ClientPerception.cs), so watch the triggers around the input handler instead.
/// </summary>
static class SwingCapture
{
    /// <summary>Low 7 bits: swing counter (wraps). Bit 7: the last swing was with a weapon.</summary>
    public static byte Swings { get; private set; }

    /// <summary>Blocks so far (wrapping counter).</summary>
    public static byte Blocks { get; private set; }

    // Block() is called from OnInteraction (input), not from an animation event, so it can be hooked.
    [HarmonyPatch(typeof(FirstPersonItemController), nameof(FirstPersonItemController.Block))]
    static class BlockPatch { static void Postfix() => Blocks++; }

    static readonly int PunchId = Animator.StringToHash("Punch");
    static readonly int AttackId = Animator.StringToHash("Attack");

    static Animator Fp() => InterfaceController.Instance != null ? InterfaceController.Instance.firstPersonAnimator : null;

    [HarmonyPatch(typeof(FirstPersonItemController), nameof(FirstPersonItemController.OnInteraction))]
    static class OnInteraction
    {
        static void Prefix(out (bool punch, bool attack) __state)
        {
            __state = default;
            try { var a = Fp(); if (a != null) __state = (a.GetBool(PunchId), a.GetBool(AttackId)); }
            catch (Exception) { }
        }

        static void Postfix((bool punch, bool attack) __state)
        {
            try
            {
                var a = Fp();
                if (a == null) return;
                // A trigger reads true until the animator consumes it: count only the ones this call set.
                bool punch = !__state.punch && a.GetBool(PunchId), attack = !__state.attack && a.GetBool(AttackId);
                if (!punch && !attack) return;
                Swings = (byte)(((Swings + 1) & 0x7F) | (attack ? 0x80 : 0));
            }
            catch (Exception e) { Plugin.Event($"swing capture failed: {e.Message}"); }
        }
    }
}
