using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using SodCoop.Core;
using SodCoop.Net;

namespace SodCoop;

[BepInPlugin(Guid, "SoD Co-op", Net.NetSession.ModVersion)]
public class Plugin : BasePlugin
{
    /// <summary>[Debug] TestKeys: the prototype test keys are on (off for normal play).</summary>
    public static bool TestKeys { get; private set; }

    public const string Guid = "mp_sod.coop";

    internal static Plugin Instance;
    static string _eventLog;

    public override void Load()
    {
        Instance = this;
        _eventLog = Path.Combine(Paths.BepInExRootPath, "sodcoop.log");
        // Keep the previous run's log: after a failed test, people restart before sending it.
        try { if (File.Exists(_eventLog)) File.Copy(_eventLog, Path.Combine(Paths.BepInExRootPath, "sodcoop.prev.log"), true); }
        catch (IOException) { }
        File.WriteAllText(_eventLog, "");

        var maxPlayers = Config.Bind("Session", "MaxPlayers", 2, "Maximum players per session (host included).");
        PlayerRegistry.MaxPlayers = maxPlayers.Value;
        NetConfig.Bind(Config);
        TestKeys = Config.Bind("Debug", "TestKeys", false,
            "Prototype test keys (Insert, Home, End, Delete, PageUp, PageDown and their Ctrl variants, Ctrl+F11). " +
            "Off for play: PageUp teleports you to the other player, Delete removes their body, End puts it in a far home.").Value;

        PatchEach(new Harmony(Guid));

        ClassInjector.RegisterTypeInIl2Cpp<CoopBehaviour>();
        AddComponent<CoopBehaviour>();
        ClassInjector.RegisterTypeInIl2Cpp<SessionOverlay>();
        AddComponent<SessionOverlay>();
        Event($"SoD Co-op {Net.NetSession.ModVersion} loaded (protocol v{Net.Protocol.Version}, MaxPlayers={PlayerRegistry.MaxPlayers}, command line: {string.Join(" ", System.Environment.GetCommandLineArgs())})");
    }

    /// <summary>
    /// Apply the Harmony patches class by class: PatchAll stops at the first patch whose target doesn't resolve,
    /// which would take the whole mod down. A broken patch is logged and skipped; everything else still loads.
    /// </summary>
    static void PatchEach(Harmony harmony)
    {
        int ok = 0, failed = 0, skipped = 0;
        // Testing aid: BepInEx/sodcoop_skip_patches lists name fragments (one per line) of patch classes not to apply.
        var skipFile = Path.Combine(Paths.BepInExRootPath, "sodcoop_skip_patches");
        var skip = File.Exists(skipFile) ? File.ReadAllLines(skipFile) : Array.Empty<string>();
        foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
        {
            if (type.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0) continue;
            if (Array.Exists(skip, f => f.Trim().Length > 0 && type.FullName.Contains(f.Trim()))) { skipped++; continue; }
            try { harmony.CreateClassProcessor(type).Patch(); ok++; }
            catch (Exception e)
            {
                failed++;
                Event($"PATCH {type.FullName} not applied: {(e.InnerException ?? e).Message}");
            }
        }
        Event($"PATCH {ok} patch classes applied{(failed > 0 ? $", {failed} FAILED (see above)" : "")}{(skipped > 0 ? $", {skipped} skipped by sodcoop_skip_patches" : "")}");
    }

    /// <summary>Log to the BepInEx log and to BepInEx/sodcoop.log.</summary>
    internal static void Event(string msg)
    {
        Instance?.Log.LogInfo(msg);
        try { File.AppendAllText(_eventLog, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n"); }
        catch { /* logging must never break the game */ }
    }
}
