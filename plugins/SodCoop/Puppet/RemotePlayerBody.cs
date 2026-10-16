using SodCoop.Core;
using UnityEngine;

namespace SodCoop.Puppet;

/// <summary>
/// Host-side body of a remote player. Prototype: possesses an existing citizen (already
/// fully built: model, outfit, animator, colliders), turns its AI off and flags it
/// <c>isPlayer</c> so the game's perception code treats it as a player.
/// </summary>
public sealed class RemotePlayerBody
{
    public NetPlayer Owner { get; }
    public Citizen Human { get; private set; }
    public IPoseSource PoseSource { get; set; }

    /// <summary>
    /// The remote player is doing something illegal (lockpicking, searching, …). Will come from
    /// the network (the client's <c>InteractionController.SetIllegalActionActive</c>).
    /// </summary>
    public bool IllegalActionActive { get; set; }

    const float IllegalStatusInterval = 0.25f;
    float _nextIllegalStatus;

    bool _originalIsPlayer;
    bool _originalDisableTickRateUpdate;
    bool _originalAiEnabled;

    RemotePlayerBody(NetPlayer owner, Citizen donor)
    {
        Owner = owner;
        Human = donor;
        _driver = new BodyDriver(donor);
    }

    /// <summary>
    /// A citizen we can borrow as a body without breaking the current case: never the murderer or the
    /// victim, and nobody within 30 m of <paramref name="avoidNear"/> (so nobody vanishes in view).
    /// </summary>
    public static Citizen PickDonor(Vector3 avoidNear)
    {
        var citizens = CityData.Instance?.citizenDirectory;
        var murder = MurderController.Instance;
        if (citizens == null) return null;
        for (int i = 0; i < citizens.Count; i++)
        {
            var c = citizens[i];
            if (c == null || c.isDead || c.ai == null || c.isPlayer || Doubles.IsBorrowed(c) || NpcPuppets.IsDriven(c)) continue;
            if (!c.gameObject.activeInHierarchy) continue;
            if (murder != null && ((murder.currentMurderer != null && murder.currentMurderer.Pointer == c.Pointer) ||
                                   (murder.currentVictim != null && murder.currentVictim.Pointer == c.Pointer))) continue;
            if (Vector3.Distance(c.transform.position, avoidNear) < 30f) continue;
            return c;
        }
        return null;
    }

    public static RemotePlayerBody Possess(NetPlayer owner, Citizen donor)
    {
        var body = new RemotePlayerBody(owner, donor);
        body.Generation = Core.World.Generation;
        body._originalIsPlayer = donor.isPlayer;
        body._originalDisableTickRateUpdate = donor.ai != null && donor.ai.disableTickRateUpdate;

        donor.isPlayer = true;
        if (donor.ai != null)
        {
            // AITick only makes decisions; path following runs in the component's own Update.
            // Disabling the component stops both (tested: with only AITick blocked, the body kept
            // walking its last route).
            body._originalAiEnabled = donor.ai.enabled;
            donor.ai.disableTickRateUpdate = true;
            donor.ai.enabled = false;
        }

        body.ResetAnimation();
        body.DropCitizenProps();
        body.ArmWith(null);
        owner.RemoteBody = body;
        PlayerRegistry.BindRemoteBody(owner, donor);
        Plugin.Event($"{owner} possessed citizen {donor.humanID} \"{donor.citizenName}\"");
        Plugin.Event($"{owner} body outfit at possess: {body.OutfitStatus()}");
        return body;
    }

    /// <summary>
    /// Guard against whatever the game re-applies to the donor (seen: the host's body invisible on a client).
    /// Visibility is only re-evaluated on a room change, and <c>SetVisible</c> early-outs on an unchanged value,
    /// so a body possessed while its room was hidden could stay invisible; force it when its room is shown.
    /// </summary>
    string _lastOutfitStatus;

    /// <summary>
    /// The citizen may have been holding something for its own routine (a cup, a newspaper…) when we borrowed it;
    /// the player's own held item is shown separately (<see cref="SetHeld"/>), so drop the citizen's props.
    /// </summary>
    void DropCitizenProps()
    {
        var ai = Human.ai;
        if (ai == null) return;
        try
        {
            bool had = ai.spawnedRightItem != null || ai.spawnedLeftItem != null;
            if (ai.spawnedRightItem != null) ai.DespawnRightItem();
            if (ai.spawnedLeftItem != null) ai.DespawnLeftItem();
            if (had) Plugin.Event($"{Owner} body: dropped the citizen's hand props");
        }
        catch (System.Exception e) { Plugin.Event($"{Owner} body: could not drop hand props: {e.Message}"); }
    }

    void KeepShown()
    {
        try
        {
            if (Human.ai != null && Human.ai.enabled && !KnockedOut) Human.ai.enabled = false;
            // Force it on when near: the body's currentRoom can be stale after we move it, so the room check
            // alone left bodies invisible (two-machine test). Showing one through a wall beats not showing it.
            var cam = Camera.main;
            float dist = cam != null ? Vector3.Distance(cam.transform.position, Human.transform.position) : 0f;
            if (Hidden) { if (Human.visible) Human.SetVisible(false, true); }
            else if (!Human.visible && dist < 80f) Human.SetVisible(true, true);
            _marker.Ensure(Human, Owner.Id.Value);
            DropCitizenProps();
            if (!_combatShown) ArmWith(null);
            var outfit = OutfitStatus();
            if (outfit != _lastOutfitStatus) { _lastOutfitStatus = outfit; Plugin.Event($"{Owner} body outfit: {outfit}, {dist:F0} m, visible={Human.visible}"); }
        }
        catch (System.Exception e) { Plugin.Event($"body visibility guard for {Owner} failed: {e.Message}"); }
    }

    // --- where the player looks and what's in their hands (from the pose; applied after the Animator)

    float _lookPitch, _lookYaw, _shownPitch, _shownYaw;
    bool _hasLook;
    int _heldId = -1;
    Net.HeldKind _held;
    GameObject _heldVisual;

    /// <summary>Hiding in a closet/under a bed or crawling in a vent: not shown, and (on the host) the game's own
    /// <c>isHiding</c>/<c>inAirVent</c> keep citizens from spotting the body.</summary>
    public bool Hidden { get; private set; }
    bool _flashlightOn;
    GameObject _flashlight;

    public void SetPlayerFlags(Net.PoseFlags f)
    {
        if (Human == null || Human.WasCollected) return;
        bool hiding = (f & Net.PoseFlags.PlayerHiding) != 0, vent = (f & Net.PoseFlags.PlayerInVent) != 0;
        Human.isHiding = hiding;
        Human.inAirVent = vent;
        if (Hidden != (hiding || vent))
        {
            Hidden = hiding || vent;
            Plugin.Event($"{Owner} {(hiding ? "is hiding" : vent ? "is in a vent" : "came out")}");
            KeepShown();
        }
        _flashlightOn = (f & Net.PoseFlags.PlayerFlashlight) != 0;
        _sitting = (f & Net.PoseFlags.PlayerSitting) != 0;
        _lying = (f & Net.PoseFlags.PlayerLying) != 0;
    }

    bool _sitting, _lying, _sitShown, _lieShown;
    Net.PlayerActivity _activity;
    CitizenAnimationController.ArmsBoolSate _armsShown = (CitizenAnimationController.ArmsBoolSate)255;
    bool _phoneShown;

    public void SetActivity(Net.PlayerActivity a) => _activity = a;

    // --- punches and weapon swings (a counter in every pose, so a lost packet doesn't lose the swing)

    const float CombatHold = 1.5f;
    int _swings = -1;
    float _combatUntil;
    bool _combatShown;

    public void SetSwings(byte s)
    {
        if (_swings < 0 || Human == null || Human.WasCollected) { _swings = s & 0x7F; return; } // first pose: nothing to replay
        if ((s & 0x7F) == _swings) return;
        _swings = s & 0x7F;
        if (KnockedOut || Hidden) return;
        var anim = Human.animationController;
        if (anim == null) return;
        try
        {
            if (!_weaponDrawn) ArmWith((s & 0x80) != 0 ? HeldWeapon() : null);
            if (!_combatShown) { anim.SetInCombat(true); _combatShown = true; }
            anim.AttackTrigger();
            _combatUntil = Time.time + CombatHold;
        }
        catch (System.Exception e) { Plugin.Event($"{Owner} swing failed: {e.Message}"); }
    }

    int _blocks = -1;

    /// <summary>The player blocked (counter in every pose, like swings).</summary>
    public void SetBlocks(byte b)
    {
        if (_blocks < 0 || Human == null || Human.WasCollected) { _blocks = b; return; }
        if (b == _blocks) return;
        _blocks = b;
        if (KnockedOut || Hidden) return;
        try
        {
            var anim = Human.animationController;
            if (anim == null) return;
            if (!_combatShown) { anim.SetInCombat(true); _combatShown = true; }
            anim.BlockTrigger(0.5f, false);
            _combatUntil = Time.time + CombatHold;
        }
        catch (System.Exception e) { Plugin.Event($"{Owner} block failed: {e.Message}"); }
    }

    bool _weaponDrawn;

    /// <summary>A drawn weapon (gun, knife, bat…) is shown the way citizens show one: the combat stance with that weapon.</summary>
    void UpdateDrawnWeapon()
    {
        var w = _held is Net.HeldKind.Item or Net.HeldKind.Raised ? HeldWeapon() : null;
        bool drawn = w != null && w.type != MurderWeaponPreset.WeaponType.fists && !_sitShown && !_lieShown && !Hidden;
        if (drawn == _weaponDrawn) return;
        _weaponDrawn = drawn;
        _armsShown = (CitizenAnimationController.ArmsBoolSate)255;
        var anim = Human.animationController;
        if (drawn) { ArmWith(w); anim?.SetInCombat(true); }
        else if (!_combatShown) { anim?.SetInCombat(false); ArmWith(null); }
    }

    /// <summary>Drop the fighting stance a moment after the last swing (kept while a weapon is drawn).</summary>
    void UpdateCombat()
    {
        if (!_combatShown || Time.time < _combatUntil || _weaponDrawn) return;
        _combatShown = false;
        Human.animationController?.SetInCombat(false);
        ArmWith(null);
    }

    static MurderWeaponPreset _fists;

    /// <summary>The game's bare-hands weapon preset (type fists), found once among the loaded presets.</summary>
    static MurderWeaponPreset Fists()
    {
        if (_fists != null && !_fists.WasCollected) return _fists;
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<MurderWeaponPreset>()))
        {
            var w = o.TryCast<MurderWeaponPreset>();
            if (w != null && w.type == MurderWeaponPreset.WeaponType.fists) { _fists = w; break; }
        }
        if (_fists == null) Plugin.Event("no fists weapon preset found; player bodies keep the citizen's weapon");
        return _fists;
    }

    /// <summary>The weapon preset of what the player holds, if it is a weapon.</summary>
    MurderWeaponPreset HeldWeapon()
    {
        if (_heldId < 0 || CityData.Instance == null || !CityData.Instance.savableInteractableDictionary.TryGetValue(_heldId, out var it)) return null;
        return it?.preset?.weapon;
    }

    /// <summary>
    /// Player bodies fight with what the player holds, bare hands by default. A borrowed citizen keeps its own weapon
    /// (seen: the combat stance of a gun owner showed an invisible gun when the host punched). The player's item is
    /// drawn separately (<see cref="SetHeld"/>), so only the preset (stance and attack animation) changes, no props.
    /// </summary>
    void ArmWith(MurderWeaponPreset weapon)
    {
        var ai = Human.ai;
        if (ai == null) return;
        try
        {
            weapon ??= Fists();
            if (weapon == null || (ai.currentWeaponPreset != null && ai.currentWeaponPreset.Pointer == weapon.Pointer)) return;
            ai.currentWeapon = null;
            ai.currentWeaponPreset = weapon;
            Plugin.Event($"{Owner} body armed with {weapon.name}");
        }
        catch (System.Exception e) { Plugin.Event($"{Owner} body: could not set the weapon: {e.Message}"); }
    }

    /// <summary>Arms and idle for what the partner is doing (activity first, else what's in their hands).</summary>
    void UpdateActivity()
    {
        var anim = Human.animationController;
        if (anim == null) return;
        var arms = _activity switch
        {
            Net.PlayerActivity.Lockpicking => CitizenAnimationController.ArmsBoolSate.armsLocking,
            Net.PlayerActivity.Typing => CitizenAnimationController.ArmsBoolSate.armsTyping,
            Net.PlayerActivity.Consuming => CitizenAnimationController.ArmsBoolSate.armsConsuming,
            Net.PlayerActivity.Smoking => CitizenAnimationController.ArmsBoolSate.armsSmoking,
            Net.PlayerActivity.Reading => CitizenAnimationController.ArmsBoolSate.armsReading,
            Net.PlayerActivity.Searching => CitizenAnimationController.ArmsBoolSate.armsUse,
            // A drawn weapon is shown by the combat stance (UpdateDrawnWeapon); a plain item just sits in the hand;
            // raising an item (scanner, camera, aiming) is the generic "using something" arms pose.
            _ => _held == Net.HeldKind.Raised && !_weaponDrawn ? CitizenAnimationController.ArmsBoolSate.armsUse : CitizenAnimationController.ArmsBoolSate.none,
        };
        if (arms != _armsShown) { anim.SetArmsBoolState(arms); _armsShown = arms; }
        // The phone is an idle pose; sitting/lying/kneeling own the idle state while they're shown.
        bool phone = _activity == Net.PlayerActivity.Phone && !_sitShown && !_lieShown && !_crouchIdleShown;
        if (phone != _phoneShown)
        {
            anim.SetIdleAnimationState(phone ? CitizenAnimationController.IdleAnimationState.telephone : CitizenAnimationController.IdleAnimationState.none);
            _phoneShown = phone;
        }
    }

    /// <summary>The partner sits or lies down: the game's citizen sitting idle / in-bed pose (applied in LateApply).</summary>
    void UpdateSeated()
    {
        var anim = Human.animationController;
        if (anim == null) return;
        bool sit = _sitting && !Hidden, lie = _lying && !Hidden;
        if (sit != _sitShown)
        {
            anim.SetIdleAnimationState(sit ? CitizenAnimationController.IdleAnimationState.sitting : CitizenAnimationController.IdleAnimationState.none);
            _sitShown = sit;
            _crouchIdleShown = false; // the crouch kneel and the seat share the idle state
        }
        if (lie != _lieShown)
        {
            Human.isInBed = lie;
            anim.SetInBed(lie, false, false, false);
            _lieShown = lie;
        }
    }

    void ResetSeated()
    {
        if (Human == null || Human.WasCollected) return;
        var anim = Human.animationController;
        if (_phoneShown) anim?.SetIdleAnimationState(CitizenAnimationController.IdleAnimationState.none);
        _phoneShown = false;
        if (_armsShown != (CitizenAnimationController.ArmsBoolSate)255) anim?.SetArmsBoolState(CitizenAnimationController.ArmsBoolSate.none);
        if (_sitShown) anim?.SetIdleAnimationState(CitizenAnimationController.IdleAnimationState.none);
        if (_lieShown) { Human.isInBed = false; anim?.SetInBed(false, false, false, true); }
        _sitShown = _lieShown = false;
    }

    /// <summary>The partner's flashlight: a copy of the game's own first-person flashlight light (HDRP light setup included).</summary>
    void UpdateFlashlight()
    {
        bool want = _flashlightOn && !Hidden;
        if (want && _flashlight == null)
        {
            var src = FirstPersonItemController.Instance != null ? FirstPersonItemController.Instance.flashLightObject : null;
            if (src == null) return;
            _flashlight = Object.Instantiate(src);
            _flashlight.name = $"SodCoop flashlight {Owner}";
        }
        if (_flashlight == null) return;
        if (_flashlight.activeSelf != want) _flashlight.SetActive(want);
        if (!want) return;
        var neck = Human.neckTransform != null ? Human.neckTransform : Human.transform;
        _flashlight.transform.position = neck.position + Vector3.up * 0.05f;
        _flashlight.transform.rotation = Quaternion.Euler(_shownPitch, _shownYaw, 0f);
    }

    public void SetLookAndHeld(float pitch, float lookYaw, int heldId, Net.HeldKind held)
    {
        _lookPitch = pitch;
        _lookYaw = lookYaw;
        if (!_hasLook) { _shownPitch = pitch; _shownYaw = lookYaw; _hasLook = true; }
        if (heldId != _heldId || held != _held) SetHeld(heldId, held);
    }

    /// <summary>
    /// Per frame, after the Animator (LateUpdate): turn the neck toward where the player looks, on top of the animated
    /// pose. Citizens turn their heads the same way (<c>NewAIController.ClampNeckRotation</c> writes the neck bone), but
    /// that runs in their AI, which is off for a borrowed body. Also keeps a carried object in front of the body.
    /// </summary>
    public void LateApply()
    {
        if (Human == null || Human.WasCollected || KnockedOut) return;
        try
        {
            if (_hasLook)
            {
                float k = Mathf.Clamp01(Time.deltaTime * 12f); // smooth the 20 Hz updates
                _shownPitch = Mathf.LerpAngle(_shownPitch, _lookPitch, k);
                _shownYaw = Mathf.LerpAngle(_shownYaw, _lookYaw, k);
                var neck = Human.neckTransform;
                if (neck != null)
                {
                    var body = Human.transform;
                    float yaw = Mathf.Clamp(Mathf.DeltaAngle(body.eulerAngles.y, _shownYaw), -70f, 70f);
                    float pitch = Mathf.Clamp(_shownPitch, -45f, 45f);
                    var delta = Quaternion.AngleAxis(yaw, Vector3.up) * Quaternion.AngleAxis(pitch, body.right);
                    neck.rotation = delta * neck.rotation;
                }
            }
            UpdateFlashlight();
            if (_sitting || _lying || _sitShown || _lieShown) UpdateSeated();
            if (!_sitShown && !_lieShown) UpdateCrouch();
            UpdateDrawnWeapon();
            UpdateActivity();
            if (_held == Net.HeldKind.Carried && _heldId >= 0 && !_heldWasThrown &&
                CityData.Instance.savableInteractableDictionary.TryGetValue(_heldId, out var carried) && carried?.controller != null)
            {
                // Picked up while still flying/settling here (e.g. right after a throw): it waited until it came to rest
                // before following the hands. Stop its physics now; a later throw turns them back on.
                if (carried.controller.physicsOn)
                    using (Net.ItemSync.Suppress()) carried.controller.SetPhysics(false, null);
                var t = Human.transform;
                carried.controller.transform.position = t.position + t.forward * 0.6f + Vector3.up * 1.1f;
                carried.controller.transform.rotation = t.rotation;
            }
        }
        catch (System.Exception e) { Plugin.Event($"look/held for {Owner} failed: {e.Message}"); _hasLook = false; }
    }

    /// <summary>
    /// Show what the player holds: a visual-only copy of a drawn item in the right hand (no colliders, rigidbodies or
    /// interaction), and the matching arm animation. A carried physics object is the real object, moved in
    /// <see cref="LateApply"/>; its resting place is synced by ItemSync when it's dropped.
    /// </summary>
    bool _heldWasThrown;

    /// <summary>ItemSync: this player threw the object it was carrying (don't pull it back into the hands).</summary>
    public void OnThrew(int id)
    {
        if (id != _heldId) return;
        _heldWasThrown = true;
        try { if (!KnockedOut && !Hidden) Human?.animationController?.ThrowTrigger(); }
        catch (System.Exception e) { Plugin.Event($"{Owner} throw animation failed: {e.Message}"); }
    }

    void SetHeld(int id, Net.HeldKind kind)
    {
        if (id != _heldId || kind != _held) _heldWasThrown = false;
        _heldId = id;
        _held = kind;
        if (_heldVisual != null) { Object.Destroy(_heldVisual); _heldVisual = null; }
        if (Human == null || Human.WasCollected) return;
        try
        {
            var anim = Human.animationController;
            if (kind is Net.HeldKind.Item or Net.HeldKind.Raised && id >= 0 &&
                CityData.Instance.savableInteractableDictionary.TryGetValue(id, out var it) && it?.preset?.prefab != null)
            {
                var hand = Human.outfitController != null ? Human.outfitController.GetBodyAnchor(CitizenOutfitController.CharacterAnchor.HandRight) : null;
                if (hand != null)
                {
                    _heldVisual = Object.Instantiate(it.preset.prefab, hand);
                    _heldVisual.transform.localPosition = Vector3.zero;
                    _heldVisual.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
                    foreach (var c in _heldVisual.GetComponentsInChildren<Collider>()) Object.Destroy(c);
                    foreach (var r in _heldVisual.GetComponentsInChildren<Rigidbody>()) Object.Destroy(r);
                    foreach (var ic in _heldVisual.GetComponentsInChildren<InteractableController>()) Object.Destroy(ic);
                }
            }
            if (anim != null)
            {
                anim.SetCarryingItem(kind == Net.HeldKind.Carried);
                if (kind == Net.HeldKind.Carried) anim.SetCarryItemType(1);
            }
            _armsShown = (CitizenAnimationController.ArmsBoolSate)255; // re-evaluate the arms (held item vs activity)
        }
        catch (System.Exception e) { Plugin.Event($"held item for {Owner} failed: {e.Message}"); }
    }

    // --- crouching. Citizens have no crouch animation (no crouch parameter in their animator; "crouch" only exists for
    // the player's camera). Standing still: the kneeling "check pulse" idle. Moving: the walk, squashed to 70 % height
    // (feet stay on the ground, the transform's origin is at the feet).
    const float CrouchWalkScale = 0.7f;
    bool _crouchIdleShown;
    float _shownScaleY = 1f;

    void UpdateCrouch()
    {
        bool crouched = Human.isCrouched && !Hidden;
        // checkPulse kneels and reaches for the floor: right when the player looks down at something, wrong otherwise.
        bool kneel = crouched && !Human.isMoving && _shownPitch > 30f;
        var anim = Human.animationController;
        if (anim != null && kneel != _crouchIdleShown)
        {
            anim.SetIdleAnimationState(kneel ? CitizenAnimationController.IdleAnimationState.checkPulse : CitizenAnimationController.IdleAnimationState.none);
            _crouchIdleShown = kneel;
        }
        float target = crouched && !kneel ? CrouchWalkScale : 1f;
        _shownScaleY = Mathf.MoveTowards(_shownScaleY, target, Time.deltaTime * 3f);
        var model = Human.transform;
        var s = model.localScale;
        if (Mathf.Abs(s.y - _shownScaleY) > 0.001f) model.localScale = new Vector3(s.x, _shownScaleY, s.z);
    }

    void ResetCrouch()
    {
        if (Human == null || Human.WasCollected) return;
        if (_crouchIdleShown) Human.animationController?.SetIdleAnimationState(CitizenAnimationController.IdleAnimationState.none);
        _crouchIdleShown = false;
        var s = Human.transform.localScale;
        Human.transform.localScale = new Vector3(s.x, 1f, s.z);
        _shownScaleY = 1f;
    }

    public void Release()
    {
        ResetSeated();
        ResetCrouch();
        SetHeld(-1, Net.HeldKind.None);
        if (_flashlight != null) { Object.Destroy(_flashlight); _flashlight = null; }
        _marker.Remove();
        if (Human == null) return;
        if (KnockedOut) SetKnockedOut(false); // don't leave the citizen in a forced one-hour KO
        Human.isPlayer = _originalIsPlayer;
        Human.isTrespassing = Human.illegalAreaActive = Human.illegalActionActive = false;
        Human.trespassingEscalation = 0;
        if (Human.illegalStatus) { Human.illegalStatus = false; Human.SetStealthMode(false); }
        if (Human.ai != null)
        {
            Human.ai.disableTickRateUpdate = _originalDisableTickRateUpdate;
            Human.ai.enabled = _originalAiEnabled;
            try { Human.ai.UpdateCurrentWeapon(); } catch (System.Exception e) { Plugin.Event($"{Owner}: weapon restore failed: {e.Message}"); } // its own weapon again, not fists
        }
        var list = CityData.Instance?.visibleActors;
        if (list != null && !Human.visible) list.Remove(Human.Cast<Actor>());
        PlayerRegistry.UnbindRemoteBody(Human);
        Plugin.Event($"{Owner} released citizen {Human.humanID}");
        Owner.RemoteBody = null;
        Human = null;
    }

    public bool KnockedOut { get; private set; }

    /// <summary>
    /// Mirror the owner's KO: the body goes down (citizen KO ragdoll) and stops being an illegal target,
    /// so citizens stop attacking it. Poses are ignored while knocked out.
    /// </summary>
    public void SetKnockedOut(bool ko)
    {
        if (Human == null || KnockedOut == ko) return;
        KnockedOut = ko;
        if (Human.ai != null)
        {
            Human.ai.enabled = true;   // SetKO drives the ragdoll through the controller
            Human.ai.SetKO(ko, Human.transform.position, Human.transform.forward, true, 3600f, false, 1f);
            Human.ai.enabled = false;
        }
        // Tested: SetKO(false) alone leaves the ragdoll on, because getting up is driven by the (disabled) AI.
        if (!ko && Human.animationController != null)
        {
            Human.animationController.SetRagdoll(false, false);
            if (Human.animationController.mainAnimator != null) Human.animationController.mainAnimator.enabled = true;
            ResetAnimation();
        }
        if (ko) IllegalActionActive = false;
    }

    /// <summary>The world the donor citizen belongs to; a body from an older world points at a destroyed citizen.</summary>
    public int Generation { get; private set; }

    /// <summary>One line for the overlay: which citizen, how far, whether the game shows it.</summary>
    public string Describe()
    {
        if (Human == null || Human.WasCollected) return "no citizen (gone)";
        var cam = Camera.main;
        float dist = cam != null ? Vector3.Distance(cam.transform.position, Human.transform.position) : -1f;
        var room = Human.currentRoom;
        return $"citizen {Human.humanID}, {dist:F0} m, visible={Human.visible}, room {(room != null ? room.roomID.ToString() : "-")}, map marker={_marker.Status}\n" +
               $"    outfit: {OutfitStatus()}";
    }

    /// <summary>Diagnostics for bodies seen with only head, hands and feet: is the outfit loaded, how many meshes render.</summary>
    public string OutfitStatus()
    {
        try
        {
            var oc = Human.outfitController;
            string outfit = oc == null ? "no controller" : $"current={oc.currentOutfit} loaded={oc.loadedOutfit}";
            int on = 0, total = 0, skinOn = 0, skinTotal = 0;
            foreach (var r in Human.GetComponentsInChildren<Renderer>(true))
            {
                bool shown = r.enabled && r.gameObject.activeInHierarchy;
                total++; if (shown) on++;
                if (r.TryCast<SkinnedMeshRenderer>() != null) { skinTotal++; if (shown) skinOn++; }
            }
            return $"{outfit}, renderers {on}/{total}, skinned {skinOn}/{skinTotal}";
        }
        catch (System.Exception e) { return $"? ({e.Message})"; }
    }

    public bool Stale => Human == null || Human.WasCollected || Generation != Core.World.Generation;

    float _nextVisibilityGuard;
    readonly MapMarker _marker = new();

    public void Update(float now)
    {
        if (Human == null || Human.WasCollected) return;
        if (now >= _nextVisibilityGuard)
        {
            _nextVisibilityGuard = now + 0.5f;
            KeepShown();
        }
        UpdateCombat();
        if (KnockedOut) return;
        if (PoseSource != null && PoseSource.TryGetPose(now, out var pose)) Apply(pose);
        // Perception is the host's job; on a client the body is only shown.
        if (Net.NetSession.Current?.Role == Net.NetSession.SessionRole.Client) return;
        if (now >= _nextIllegalStatus)
        {
            _nextIllegalStatus = now + IllegalStatusInterval;
            KeepPerceivable();
            UpdateIllegalStatus();
        }
    }

    /// <summary>
    /// <c>Actor::SightingCheck</c> only considers actors in <c>CityData.visibleActors</c>, and
    /// <c>Actor::SetVisible</c> drops an actor from that list when the *host* culls it (A2). A remote
    /// player is usually far from the host, so keep its body in the list. <c>visible</c> itself stays
    /// untouched: <c>SetVisible</c> early-outs on an unchanged value, so faking it would stop the
    /// model from ever showing again.
    /// </summary>
    void KeepPerceivable()
    {
        var list = CityData.Instance?.visibleActors;
        var actor = Human.Cast<Actor>();
        if (list != null && !list.Contains(actor)) list.Add(actor);
    }

    /// <summary>
    /// Port of <c>Player::UpdateIllegalStatus</c> (0x461D20) for the puppet.
    /// Witnesses (<c>Actor::SightingCheck</c>) read the *seen* actor's <c>illegalStatus</c>,
    /// <c>illegalAreaActive</c>, <c>illegalActionActive</c> and <c>trespassingEscalation</c>.
    /// Only <c>Player</c> computes trespassing into those flags, so without this a remote body is
    /// never "seen doing something illegal" (coop_run1..3: TRESPASS true, but no SEES-ILLEGAL).
    /// </summary>
    void UpdateIllegalStatus()
    {
        int escalation = 0;
        bool trespassing = Human.currentRoom != null && Human.IsTrespassing(Human.currentRoom, out escalation, true);
        Human.isTrespassing = trespassing;
        Human.illegalAreaActive = trespassing;
        Human.trespassingEscalation = escalation;
        Human.illegalActionActive = IllegalActionActive;

        bool everywhereIllegal = Game.Instance != null && Game.Instance.everywhereIllegal;
        bool status = trespassing || everywhereIllegal || IllegalActionActive || Human.forceTarget;
        if (status == Human.illegalStatus) return;

        Human.illegalStatus = status;
        // Player does this too: actors in stealth mode get per-frame visibility (light/stealth) updates.
        // Side effect in the game code: resets Player.Instance's field at 0x818 when isPlayer; harmless so far.
        Human.SetStealthMode(status);
        Plugin.Event($"ILLEGAL-STATUS {Owner} -> {status} (trespassing={trespassing} esc={escalation} action={IllegalActionActive})");
    }

    readonly BodyDriver _driver;

    /// <summary>Move the body to a pose (see <see cref="BodyDriver.Apply"/>).</summary>
    public void Apply(PlayerPose pose) => _driver.Apply(pose);

    void ResetAnimation() => _driver.ResetAnimation();
}
