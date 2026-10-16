using System;
using SodCoop.Net;
using UnityEngine;

namespace SodCoop;

/// <summary>
/// Top-left text box with the session state (role, lobby, each peer's Steam connection, world transfer),
/// shown on host and client whenever a session is running. Ctrl+F10 toggles it.
/// </summary>
public class SessionOverlay : MonoBehaviour
{
    public static bool Visible = true;
    const float Refresh = 0.5f; // Steam status queries allocate; no need to run them every frame

    string _text = "";
    float _next;
    static string _flash = "";
    static float _flashUntil;

    /// <summary>Show a short message for a few seconds, with or without a session.</summary>
    public static void Flash(string message)
    {
        _flash = message;
        _flashUntil = Time.unscaledTime + 6f;
    }
    GUIStyle _style;
    GUIStyle _koStyle;
    Texture2D _white;

    /// <summary>While knocked out in a session: "Knocked out: recovering… 12 s" and a bar, bottom centre.</summary>
    void DrawKoProgress()
    {
        float p = Net.RestSync.KoProgress;
        if (p < 0f || Net.NetSession.Current == null) return;
        if (_koStyle == null)
        {
            _koStyle = new GUIStyle(GUI.skin.label) { fontSize = Math.Max(18, Screen.height / 45), alignment = TextAnchor.MiddleCenter };
            _koStyle.normal.textColor = Color.white;
            _white = new Texture2D(1, 1);
            _white.SetPixel(0, 0, Color.white);
            _white.Apply();
        }
        float w = Screen.width * 0.3f, h = Screen.height * 0.012f, x = (Screen.width - w) / 2f, y = Screen.height * 0.82f;
        string partner = Puppet.KoView.Active ? " (watching your partner)" : "";
        GUI.Label(new Rect(x - w, y - h * 5f, w * 3f, h * 4f), $"Knocked out: recovering… {Net.RestSync.KoSecondsLeft:F0} s{partner}", _koStyle);
        var old = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(new Rect(x, y, w, h), _white);
        GUI.color = new Color(0.85f, 0.15f, 0.15f, 0.95f);
        GUI.DrawTexture(new Rect(x, y, w * p, h), _white);
        GUI.color = old;
    }

    GUIStyle _tagStyle;
    bool _tagFailed;
    const float NametagRange = 60f;

    /// <summary>The partner's name above their body (also through walls), smaller with distance; not while paused.</summary>
    void DrawNametags()
    {
        var cam = Camera.main;
        if (cam == null || NetSession.Current == null || Patches.PauseSync.LocalPaused) return;
        if (_tagStyle == null)
        {
            _tagStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.LowerCenter, fontStyle = FontStyle.Bold, wordWrap = false };
        }
        foreach (var p in Core.PlayerRegistry.All)
        {
            var body = p.RemoteBody;
            if (p.IsLocal || body == null || body.Hidden || body.Human == null) continue;
            var neck = body.Human.neckTransform;
            var top = neck != null ? neck.position + Vector3.up * 0.4f : body.Human.transform.position + Vector3.up * 2.1f;
            float dist = Vector3.Distance(cam.transform.position, top);
            if (dist > NametagRange) continue;
            var sp = cam.WorldToScreenPoint(top);
            if (sp.z <= 0f) continue; // behind the camera
            _tagStyle.fontSize = Math.Max(12, (int)(Screen.height / 40f * Mathf.Lerp(1f, 0.5f, dist / NametagRange)));
            var text = new GUIContent(string.IsNullOrEmpty(p.DisplayName) ? "Player" : p.DisplayName);
            var size = _tagStyle.CalcSize(text);
            var r = new Rect(sp.x - size.x / 2f, Screen.height - sp.y - size.y, size.x, size.y);
            float alpha = Mathf.Lerp(1f, 0.6f, dist / NametagRange);
            _tagStyle.normal.textColor = new Color(0f, 0f, 0f, alpha * 0.8f);
            GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), text, _tagStyle);
            _tagStyle.normal.textColor = new Color(1f, 1f, 1f, alpha);
            GUI.Label(r, text, _tagStyle);
        }
    }

    public SessionOverlay(IntPtr ptr) : base(ptr) { }

    void Update()
    {
        if (Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + Refresh;
        if (CoopBehaviour.TraceFrames > 0) Plugin.Event($"TRACE f{Time.frameCount} overlay text >");
        try { _text = NetSession.Current?.OverlayText() ?? ""; }
        catch (Exception e) { _text = $"SoD Co-op overlay error: {e.Message}"; }
        if (Time.unscaledTime < _flashUntil) _text = _text.Length > 0 ? $"{_flash}\n{_text}" : $"SoD Co-op: {_flash}";
    }

    void OnGUI()
    {
        DrawKoProgress();
        try { DrawNametags(); }
        catch (Exception e) { if (!_tagFailed) { _tagFailed = true; Plugin.Event($"nametags failed: {e.Message}"); } }
        if (CoopBehaviour.TraceFrames > 0) Plugin.Event($"TRACE f{Time.frameCount} overlay OnGUI ({_text.Length} chars)");
        if (!Visible || _text.Length == 0) return;
        if (_style == null)
        {
            _style = new GUIStyle(GUI.skin.box) { fontSize = Math.Max(14, Screen.height / 70), alignment = TextAnchor.UpperLeft, wordWrap = false };
            _style.normal.textColor = Color.white;
            _style.padding = new RectOffset { left = 8, right = 8, top = 6, bottom = 6 };
        }
        var content = new GUIContent(_text);
        var size = _style.CalcSize(content);
        GUI.Box(new Rect(10, 10, size.x, size.y), content, _style);
    }
}
