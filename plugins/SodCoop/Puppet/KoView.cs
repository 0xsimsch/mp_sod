using System;
using SodCoop.Core;
using SodCoop.Net;
using UnityEngine;

namespace SodCoop.Puppet;

/// <summary>
/// While the local player is knocked out in a session, watch the partner in third person: a copy of the game's camera
/// (keeps HDRP setup, post-processing and culling settings) without its scripts, drawn over the main camera and placed
/// behind and above the partner's body. Recovery progress is drawn by <see cref="SessionOverlay"/>.
/// </summary>
public static class KoView
{
    static Camera _cam;
    static bool _active;
    static Vector3 _smoothPos;

    public static bool Active => _active;

    /// <summary>Knocked out and still waiting: once the game fades back in, the player sees their own view again.</summary>
    static bool LocalKnockedOut => Player.Instance != null && Player.Instance.playerKOInProgress && !RestSync.KoWaking;

    static Human Partner()
    {
        foreach (var p in PlayerRegistry.All)
            if (!p.IsLocal && p.RemoteBody?.Human != null && !p.RemoteBody.Human.WasCollected) return p.RemoteBody.Human;
        return null;
    }

    /// <summary>Per frame, after everything else moved (CoopBehaviour.LateUpdate).</summary>
    public static void LateUpdate()
    {
        var partner = NetSession.Current != null && LocalKnockedOut ? Partner() : null;
        if (partner == null) { Stop(); return; }
        if (!_active && !Start()) return;

        var t = partner.transform;
        var head = t.position + Vector3.up * 1.6f;
        var want = head - t.forward * 2.8f + Vector3.up * 0.9f;
        // Don't put the camera inside a wall: pull it in front of whatever is between it and the head.
        if (Physics.Linecast(head, want, out var hit)) want = hit.point + (head - want).normalized * 0.25f;
        _smoothPos = Vector3.Lerp(_smoothPos == Vector3.zero ? want : _smoothPos, want, Mathf.Clamp01(Time.deltaTime * 6f));
        _cam.transform.position = _smoothPos;
        _cam.transform.rotation = Quaternion.LookRotation(head - _smoothPos, Vector3.up);
    }

    static bool Start()
    {
        var main = Camera.main;
        if (main == null) return false;
        try
        {
            var go = UnityEngine.Object.Instantiate(main.gameObject);
            go.name = "SodCoop KO view";
            // Only the camera itself (and HDRP's per-camera data) may stay: no game scripts, no second audio listener.
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb.GetIl2CppType().Name != "HDAdditionalCameraData") UnityEngine.Object.Destroy(mb);
            foreach (var al in go.GetComponentsInChildren<AudioListener>(true)) UnityEngine.Object.Destroy(al);
            for (int i = go.transform.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(go.transform.GetChild(i).gameObject);
            _cam = go.GetComponent<Camera>();
            _cam.depth = main.depth + 10;
            _cam.enabled = true;
            _smoothPos = Vector3.zero;
            _active = true;
            Plugin.Event("KO view: watching the partner while recovering");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Event($"KO view could not start: {e.Message}");
            Stop();
            return false;
        }
    }

    public static void Stop()
    {
        if (!_active && _cam == null) return;
        if (_cam != null && !_cam.WasCollected) UnityEngine.Object.Destroy(_cam.gameObject);
        _cam = null;
        if (_active) Plugin.Event("KO view: back to first person");
        _active = false;
    }
}
