using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Il2CppInterop.Runtime;
using SodCoop.Core;
using UnityEngine;

namespace SodCoop.Net;

/// <summary>
/// Per-player progress for a client. Joining loads the host's save, which carries the *host's* money, lockpicks,
/// social credit, sync disks, passcodes, apartments and inventory, and the host's save never stores the client's.
/// So a client keeps its own profile per host world (host SteamID + city seed) in BepInEx/sodcoop_profiles/&lt;key&gt;.json:
/// <list type="bullet">
/// <item>Before loading the host's world: capture what the client has now (if it was in a world of its own).</item>
/// <item>After the load (once the host's items were handed back): restore the saved profile for this host world, else
/// the captured state; with neither, keep the save's money/lockpicks/social credit but drop the host's sync disks.</item>
/// <item>Saved every 2 minutes, when leaving, and when the host goes away.</item>
/// </list>
/// Passcodes and apartments refer to this host world, so they're only restored from a profile of the same world.
/// Inventory items are recreated from their presets in the client's id range (SpawnSync replicates them to the host).
/// </summary>
public static class Profiles
{
    const float SaveInterval = 120f;

    /// <summary>Plain data for System.Text.Json (fields included).</summary>
    public sealed class ProfileData
    {
        public int Money = -1, Lockpicks = -1, SocialCredit = -1;
        public List<string> Upgrades = new();   // JsonUtility form of UpgradesController.Upgrades
        public List<string> Passcodes = new();  // JsonUtility form of GameplayController.Passcode
        public List<int> Apartments = new();    // NewAddress.id
        public List<string> Items = new();      // InteractablePreset names of non-static inventory slots
        public string SavedAt = "";
    }

    static readonly JsonSerializerOptions Json = new() { IncludeFields = true, WriteIndented = true };

    static ProfileData _beforeJoin;
    static bool _active;
    static float _nextSave;

    static string Dir => Path.Combine(BepInEx.Paths.BepInExRootPath, "sodcoop_profiles");

    static string Key()
    {
        var s = NetSession.Current;
        ulong host = s?.HostSteamId ?? 0;
        var city = CityData.Instance;
        string seed = city != null && !string.IsNullOrEmpty(city.seed) ? city.seed : city?.cityName ?? "city";
        foreach (var c in Path.GetInvalidFileNameChars()) seed = seed.Replace(c, '_');
        return $"{host}_{seed}";
    }

    static string FileFor(string key) => Path.Combine(Dir, key + ".json");

    // ------------------------------------------------------------------ capture / apply

    static ProfileData Capture()
    {
        var p = new ProfileData { SavedAt = DateTime.Now.ToString("s") };
        var gc = GameplayController.Instance;
        if (gc != null)
        {
            p.Money = gc.money;
            p.Lockpicks = gc.lockPicks;
            p.SocialCredit = gc.socialCredit;
            if (gc.acquiredPasscodes != null)
                for (int i = 0; i < gc.acquiredPasscodes.Count; i++)
                    if (gc.acquiredPasscodes[i] != null) p.Passcodes.Add(JsonUtility.ToJson(gc.acquiredPasscodes[i]));
        }
        var uc = UpgradesController.Instance;
        if (uc?.upgrades != null)
            for (int i = 0; i < uc.upgrades.Count; i++)
                if (uc.upgrades[i] != null) p.Upgrades.Add(JsonUtility.ToJson(uc.upgrades[i]));
        var me = Player.Instance;
        if (me?.apartmentsOwned != null)
            for (int i = 0; i < me.apartmentsOwned.Count; i++)
                if (me.apartmentsOwned[i] != null) p.Apartments.Add(me.apartmentsOwned[i].id);
        var fpc = FirstPersonItemController.Instance;
        if (fpc?.slots != null)
            for (int i = 0; i < fpc.slots.Count; i++)
            {
                var slot = fpc.slots[i];
                if (slot == null || slot.isStatic != FirstPersonItemController.InventorySlot.StaticSlot.nonStatic) continue;
                var it = slot.GetInteractable();
                if (it?.preset != null) p.Items.Add(it.preset.name);
            }
        return p;
    }

    /// <param name="sameWorld">The profile was made in this host world (passcodes and apartments refer to it).</param>
    static void Apply(ProfileData p, bool sameWorld, string source)
    {
        var gc = GameplayController.Instance;
        if (gc != null)
        {
            if (p.Money >= 0) gc.SetMoney(p.Money);
            if (p.Lockpicks >= 0) { gc.lockPicks = p.Lockpicks; gc.AddLockpicks(0, false); } // AddLockpicks refreshes the display
            if (p.SocialCredit >= 0) gc.SetSocialCredit(p.SocialCredit);
            if (sameWorld && gc.acquiredPasscodes != null)
            {
                gc.acquiredPasscodes.Clear();
                foreach (var json in p.Passcodes)
                {
                    var code = JsonUtility.FromJson(json, Il2CppType.Of<GameplayController.Passcode>())?.TryCast<GameplayController.Passcode>();
                    if (code != null) gc.acquiredPasscodes.Add(code);
                }
            }
        }
        ApplyUpgrades(p.Upgrades);
        if (sameWorld) ApplyApartments(p.Apartments);
        RecreateItems(p.Items);
        Plugin.Event($"PROFILE restored from {source}: money {p.Money}, lockpicks {p.Lockpicks}, social credit {p.SocialCredit}, " +
                     $"{p.Upgrades.Count} sync disk(s), {p.Items.Count} item(s){(sameWorld ? $", {p.Passcodes.Count} passcode(s), {p.Apartments.Count} apartment(s)" : "")}");
    }

    /// <summary>What a save load does: replace the list, then let the controllers recompute the effects.</summary>
    static void ApplyUpgrades(List<string> upgrades)
    {
        var uc = UpgradesController.Instance;
        if (uc?.upgrades == null) return;
        try
        {
            uc.upgrades.Clear();
            foreach (var json in upgrades)
            {
                var u = JsonUtility.FromJson(json, Il2CppType.Of<UpgradesController.Upgrades>())?.TryCast<UpgradesController.Upgrades>();
                if (u != null) uc.upgrades.Add(u);
            }
            uc.UpdateUpgrades();
            uc.UpdateActivation();
            UpgradeEffectController.Instance?.OnSyncDiskChange(true);
        }
        catch (Exception e) { Plugin.Event($"PROFILE sync disks not restored: {e.Message}"); }
    }

    static void ApplyApartments(List<int> ids)
    {
        var me = Player.Instance;
        var dict = CityData.Instance?.addressDictionary;
        if (me?.apartmentsOwned == null || dict == null) return;
        foreach (var id in ids)
            if (dict.TryGetValue(id, out var a) && a != null && !me.apartmentsOwned.Contains(a)) me.apartmentsOwned.Add(a);
    }

    /// <summary>New objects from the presets, picked up into free slots (client id range; SpawnSync tells the host).</summary>
    static void RecreateItems(List<string> presets)
    {
        var fpc = FirstPersonItemController.Instance;
        var me = Player.Instance;
        if (fpc == null || me == null || InteractableCreator.Instance == null || Toolbox.Instance == null) return;
        int made = 0;
        foreach (var name in presets)
        {
            try
            {
                var preset = Toolbox.Instance.GetInteractablePreset(name);
                if (preset == null) { Plugin.Event($"PROFILE unknown item preset {name}"); continue; }
                var it = InteractableCreator.Instance.CreateWorldInteractable(preset, me, me, null, me.transform.position + Vector3.up, Vector3.zero, null, null, "");
                if (it != null && fpc.PickUpItem(it, false, false, false, false, false)) made++;
            }
            catch (Exception e) { Plugin.Event($"PROFILE item {name} not recreated: {e.Message}"); }
        }
        if (made > 0) Plugin.Event($"PROFILE recreated {made}/{presets.Count} inventory item(s)");
    }

    // ------------------------------------------------------------------ call sites

    /// <summary>Client, in SnapshotSync.LoadJoinSave before the host's world loads: remember what we have now.</summary>
    public static void CaptureBeforeJoin()
    {
        _active = false;
        bool inWorld = CityData.Instance != null && SessionData.Instance != null && SessionData.Instance.play && Player.Instance != null;
        _beforeJoin = inWorld ? Capture() : null;
        if (_beforeJoin != null) Plugin.Event($"PROFILE captured our own state before joining (money {_beforeJoin.Money}, {_beforeJoin.Upgrades.Count} sync disk(s), {_beforeJoin.Items.Count} item(s))");
    }

    /// <summary>Client, in SnapshotSync.CleanupAfterLoad after GiveBackHostInventory: put our own state back.</summary>
    public static void RestoreAfterJoin()
    {
        try
        {
            var file = FileFor(Key());
            if (File.Exists(file))
            {
                var p = JsonSerializer.Deserialize<ProfileData>(File.ReadAllText(file), Json);
                if (p != null) Apply(p, true, $"profile {Path.GetFileName(file)} ({p.SavedAt})");
            }
            else if (_beforeJoin != null) Apply(_beforeJoin, false, "the state we had before joining");
            else
            {
                // Fresh player joining from the main menu: the save's money etc. stay (SnapshotSync's rule), but the
                // host's sync disks aren't ours.
                ApplyUpgrades(new List<string>());
                Plugin.Event("PROFILE no profile for this host world yet: starting with the save's money, no sync disks");
            }
        }
        catch (Exception e) { Plugin.Event($"PROFILE restore failed: {e.Message}"); }
        _beforeJoin = null;
        _active = true;
        _nextSave = Time.unscaledTime + SaveInterval;
    }

    /// <summary>Client: write the profile now (periodic tick, leaving, host gone).</summary>
    public static void SaveNow(string why)
    {
        var s = NetSession.Current;
        if (!_active || s == null || s.Role != NetSession.SessionRole.Client || !World.Ready || Player.Instance == null) return;
        try
        {
            Directory.CreateDirectory(Dir);
            var key = Key();
            File.WriteAllText(FileFor(key), JsonSerializer.Serialize(Capture(), Json));
            Plugin.Event($"PROFILE saved {key} ({why})");
        }
        catch (Exception e) { Plugin.Event($"PROFILE save failed: {e.Message}"); }
    }

    /// <summary>Client, per frame while welcomed.</summary>
    public static void Tick(float now)
    {
        if (!_active || now < _nextSave) return;
        _nextSave = now + SaveInterval;
        SaveNow("periodic");
    }

    /// <summary>Client session over: save and stop.</summary>
    public static void OnLeave(string why)
    {
        SaveNow(why);
        _active = false;
    }
}
