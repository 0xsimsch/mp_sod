using System;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace SodProbe;

/// <summary>
/// Measurement plugin for open questions A1 (AI tick rate vs. player distance)
/// and A2 (what culling disables). Writes BepInEx/sodprobe.csv, one row per
/// citizen per sample.
/// </summary>
[BepInPlugin(Guid, "SoD Probe", "0.1.0")]
public class Plugin : BasePlugin
{
    public const string Guid = "mp_sod.probe";

    internal static Plugin Instance;

    public override void Load()
    {
        Instance = this;
        ClassInjector.RegisterTypeInIl2Cpp<ProbeBehaviour>();
        AddComponent<ProbeBehaviour>();
        Log.LogInfo("SoD Probe loaded");
    }
}

public class ProbeBehaviour : MonoBehaviour
{
    const float SampleInterval = 3f;

    float _nextSample;
    StreamWriter _csv;
    int _sample;

    public ProbeBehaviour(IntPtr ptr) : base(ptr) { }

    void Start()
    {
        var path = Path.Combine(Paths.BepInExRootPath, "sodprobe.csv");
        _csv = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
        _csv.WriteLine(string.Join(",",
            "sample", "realTime", "gameTime", "paused",
            "humanId", "name", "dist", "sameBuilding", "sameRoom",
            "tickRate", "desiredRate", "aiLastUpdated", "aiTimeSinceUpdate", "aiDelta",
            "visible", "roomVisible", "roomGeomLoaded", "goActive",
            "renderersOn", "renderersTotal", "collidersOn", "collidersTotal", "animatorOn",
            "isDead", "isAsleep", "onStreet",
            "posX", "posY", "posZ"));
        Plugin.Instance.Log.LogInfo($"SoD Probe writing to {path}");
    }

    void Update()
    {
        try
        {
            if (Input.GetKeyDown(KeyCode.F8)) TeleportToFarCitizen();
            if (Input.GetKeyDown(KeyCode.F9)) TeleportBack();
        }
        catch (Exception e) { Plugin.Instance.Log.LogError($"teleport failed: {e}"); }

        if (Time.realtimeSinceStartup < _nextSample) return;
        _nextSample = Time.realtimeSinceStartup + SampleInterval;

        try { Sample(); }
        catch (Exception e) { Plugin.Instance.Log.LogError($"sample failed: {e}"); }
    }

    void Sample()
    {
        var city = CityData.Instance;
        var player = Player.Instance;
        var session = SessionData.Instance;
        if (city == null || player == null || session == null || city.citizenDirectory == null) return;

        var playerPos = player.transform.position;
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        _sample++;

        var citizens = city.citizenDirectory;
        for (int i = 0; i < citizens.Count; i++)
        {
            var c = citizens[i];
            if (c == null) continue;

            var pos = c.transform.position;
            var ai = c.ai;
            var room = c.currentRoom;

            int rOn = 0, rTot = 0, colOn = 0, colTot = 0;
            foreach (var r in c.GetComponentsInChildren<Renderer>(true)) { rTot++; if (r.enabled && r.gameObject.activeInHierarchy) rOn++; }
            foreach (var col in c.GetComponentsInChildren<Collider>(true)) { colTot++; if (col.enabled && col.gameObject.activeInHierarchy) colOn++; }
            var anim = c.GetComponentInChildren<Animator>(true);

            sb.Clear();
            sb.Append(_sample).Append(',')
              .Append(Time.realtimeSinceStartup.ToString("F2", inv)).Append(',')
              .Append(session.gameTime.ToString("F4", inv)).Append(',')
              .Append(!session.play).Append(',')
              .Append(c.humanID).Append(',')
              .Append('"').Append(c.citizenName?.Replace("\"", "'")).Append('"').Append(',')
              .Append(Vector3.Distance(pos, playerPos).ToString("F1", inv)).Append(',')
              .Append(c.currentBuilding != null && c.currentBuilding == player.currentBuilding).Append(',')
              .Append(room != null && room == player.currentRoom).Append(',')
              .Append(ai != null ? ai.tickRate.ToString() : "").Append(',')
              .Append(ai != null ? ai.desiredTickRate.ToString() : "").Append(',')
              .Append(ai != null ? ai.lastUpdated.ToString("F4", inv) : "").Append(',')
              .Append(ai != null ? ai.timeSinceLastUpdate.ToString("F4", inv) : "").Append(',')
              .Append(ai != null ? ai.delta.ToString("F4", inv) : "").Append(',')
              .Append(c.visible).Append(',')
              .Append(room != null && room.isVisible).Append(',')
              .Append(room != null && room.geometryLoaded).Append(',')
              .Append(c.gameObject.activeInHierarchy).Append(',')
              .Append(rOn).Append(',').Append(rTot).Append(',')
              .Append(colOn).Append(',').Append(colTot).Append(',')
              .Append(anim != null && anim.enabled && anim.gameObject.activeInHierarchy).Append(',')
              .Append(c.isDead).Append(',')
              .Append(c.isAsleep).Append(',')
              .Append(c.isOnStreet).Append(',')
              .Append(pos.x.ToString("F1", inv)).Append(',')
              .Append(pos.y.ToString("F1", inv)).Append(',')
              .Append(pos.z.ToString("F1", inv));
            _csv.WriteLine(sb.ToString());
        }

        // Player row, humanId -1, for reference.
        _csv.WriteLine($"{_sample},{Time.realtimeSinceStartup.ToString("F2", inv)},{session.gameTime.ToString("F4", inv)},{!session.play},-1,\"PLAYER\",0,True,True,,,,,,,,,,,,,,,,,,{playerPos.x.ToString("F1", inv)},{playerPos.y.ToString("F1", inv)},{playerPos.z.ToString("F1", inv)}");
    }

    // --- Controlled experiment: move the player next to one far, idle, indoor citizen. ---

    NewNode _returnNode;
    Citizen _target;

    void TeleportToFarCitizen()
    {
        var player = Player.Instance;
        var citizens = CityData.Instance?.citizenDirectory;
        if (player == null || citizens == null) return;

        Citizen best = null;
        float bestDist = 0;
        for (int i = 0; i < citizens.Count; i++)
        {
            var c = citizens[i];
            if (c == null || c.isDead || c.isAsleep || c.isOnStreet || c.visible || c.ai == null || c.currentRoom == null) continue;
            if (c.ai.tickRate != NewAIController.AITickRate.veryLow) continue;
            if (c.currentBuilding != null && c.currentBuilding == player.currentBuilding) continue;
            float d = Vector3.Distance(c.transform.position, player.transform.position);
            if (d > 60f && d < 250f && d > bestDist) { best = c; bestDist = d; }
        }
        if (best == null) { Event("F8 no suitable target"); return; }

        var node = player.FindSafeTeleport(best.currentRoom, false);
        if (node == null) { Event($"F8 no safe node in room of {best.humanID}"); return; }

        _target = best;
        _returnNode = player.currentNode;
        Event($"F8 BEFORE target={best.humanID} \"{best.citizenName}\" dist={bestDist:F1} {DescribeTarget()}");
        player.Teleport(node, null, true, false, true);
        Event($"F8 AFTER-TELEPORT target={best.humanID} {DescribeTarget()}");
    }

    void TeleportBack()
    {
        if (_returnNode == null) { Event("F9 nothing to return to"); return; }
        Event($"F9 BEFORE target={_target?.humanID} {DescribeTarget()}");
        Player.Instance.Teleport(_returnNode, null, true, false, true);
        Event($"F9 AFTER-TELEPORT target={_target?.humanID} {DescribeTarget()}");
    }

    string DescribeTarget()
    {
        var c = _target;
        if (c == null) return "";
        var room = c.currentRoom;
        int colOn = 0, colTot = 0;
        if (room != null)
            foreach (var col in room.GetComponentsInChildren<Collider>(true)) { colTot++; if (col.enabled && col.gameObject.activeInHierarchy) colOn++; }
        return $"dist={Vector3.Distance(c.transform.position, Player.Instance.transform.position):F1} " +
               $"rate={c.ai?.tickRate} desired={c.ai?.desiredTickRate} visible={c.visible} " +
               $"room={(room != null ? room.roomID : -1)} roomVisible={room?.isVisible} geomLoaded={room?.geometryLoaded} " +
               $"roomGoActive={(room != null && room.gameObject.activeInHierarchy)} roomColliders={colOn}/{colTot} " +
               $"walls={(room?.combinedWalls != null)} sample={_sample}";
    }

    void Event(string msg)
    {
        Plugin.Instance.Log.LogInfo(msg);
        File.AppendAllText(Path.Combine(Paths.BepInExRootPath, "sodprobe_events.log"),
            $"{DateTime.Now:HH:mm:ss} t={Time.realtimeSinceStartup:F2} {msg}\n");
    }

    void OnDestroy() => _csv?.Dispose();
}
