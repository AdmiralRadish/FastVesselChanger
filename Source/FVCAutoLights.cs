// FVCAutoLights.cs — Auto-lights in shadow feature (partial class of FastVesselChanger)
//
// When the active vessel transitions from sunlight to shadow, and auto-lights is
// enabled for that vessel (default: ON), the mod fires the Light action group.
// When the vessel exits shadow, the mod turns lights OFF.
//
// Per-vessel toggle: a small light-bulb icon button (white = ON, dark = OFF) in the vessel list row.
// Persisted in XML user prefs (FastVesselChanger.xml) inside the player section.

using System;
using System.Collections.Generic;
using System.Xml;
using UnityEngine;

#pragma warning disable CS8618, CS8600, CS8601, CS8625, CS8603, CS8604

public partial class FastVesselChanger
{
    // =====================================================================
    // Auto-Lights — Static fields (survive FLIGHT→FLIGHT addon destruction)
    // =====================================================================

    // Auto-lights per-vessel state is now stored inside _vesselSettings (see FVCVesselSettings.cs).
    //   autoLightsEnabled   → VesselSettings.autoLightsEnabled  (default true)
    //   modManagedLightsActive → VesselSettings.lightsManagedByMod (runtime-only)

    // =====================================================================
    // Auto-Lights — Instance fields (reset each addon lifecycle)
    // =====================================================================

    private bool  _autoLightsPrevInShadow = false;
    private float _shadowCheckTimer       = 0f;
    private const float SHADOW_CHECK_INTERVAL = 0.5f; // real seconds between checks

    // =====================================================================
    // Initialization / cleanup
    // =====================================================================

    private void InitAutoLights()
    {
        // No-op: _vesselSettings is initialized at declaration in FVCVesselSettings.cs.
    }

    // =====================================================================
    // Ongoing shadow monitoring — called from Update()
    // =====================================================================

    private void UpdateAutoLights()
    {
        _shadowCheckTimer -= Time.deltaTime;
        if (_shadowCheckTimer > 0f) return;
        _shadowCheckTimer = SHADOW_CHECK_INTERVAL;

        var vessel = FlightGlobals.ActiveVessel;
        if (vessel == null) return;

        bool inShadow = IsVesselInShadow(vessel);
        if (inShadow == _autoLightsPrevInShadow) return; // no transition
        _autoLightsPrevInShadow = inShadow;

        if (!GetVesselAutoLightsEnabled(vessel.id)) return;

        if (inShadow)
        {
            // Entered shadow — turn lights on and record that the mod did it.
            vessel.ActionGroups.SetGroup(KSPActionGroup.Light, true);
            GetOrCreateVesselSettings(vessel.id).lightsManagedByMod = true;
            Debug.Log("[FVC] AutoLights: " + vessel.vesselName + " entered shadow — lights ON");
        }
        else
        {
            // Exited shadow — auto-lights owns day/night transitions and turns lights off.
            vessel.ActionGroups.SetGroup(KSPActionGroup.Light, false);
            GetOrCreateVesselSettings(vessel.id).lightsManagedByMod = false;
            Debug.Log("[FVC] AutoLights: " + vessel.vesselName + " exited shadow — lights OFF");
        }
    }

    // =====================================================================
    // Called from OnFlightReady — sync initial state without stomping on
    // lights the player already had on.
    // =====================================================================

    private void SyncAutoLightsOnVesselReady()
    {
        var vessel = FlightGlobals.ActiveVessel;
        if (vessel == null) return;

        bool inShadow = IsVesselInShadow(vessel);
        _autoLightsPrevInShadow = inShadow;

        if (!inShadow) return;
        if (!GetVesselAutoLightsEnabled(vessel.id)) return;

        bool lightsCurrentlyOn = vessel.ActionGroups[KSPActionGroup.Light];

        if (!lightsCurrentlyOn)
        {
            // Already in shadow and lights are off — turn them on.
            vessel.ActionGroups.SetGroup(KSPActionGroup.Light, true);
            GetOrCreateVesselSettings(vessel.id).lightsManagedByMod = true;
            Debug.Log("[FVC] AutoLights: " + vessel.vesselName + " loaded in shadow with lights OFF — lights ON");
        }
        // If lights are already ON we do NOT add to _modManagedLightsActive.
        // That means we won't auto-turn them off when exiting shadow, which
        // correctly respects the player's pre-existing manual choice.
    }

    // =====================================================================
    // Shadow geometry  — works for any star/planet configuration (RSS-safe)
    // =====================================================================

    /// <summary>
    /// Returns true when the vessel is in the geometric shadow cast by any
    /// non-star celestial body (cylindrical approximation against each body's
    /// equatorial radius).  Runs in O(n_bodies) with purely double-precision
    /// vector math — safe to call every 0.5 s on the main thread.
    /// </summary>
    private static bool IsVesselInShadow(Vessel vessel)
    {
        if (vessel == null) return false;
        var pFetch = Planetarium.fetch;
        if (pFetch == null) return false;
        var sun = pFetch.Sun;
        if (sun == null) return false;

        Vector3d vesselPos = vessel.GetWorldPos3D();
        Vector3d toSun    = sun.position - vesselPos;
        double   distToSun = toSun.magnitude;
        if (distToSun < 1.0) return false;            // degenerate / on top of sun

        Vector3d toSunUnit = toSun / distToSun;

        foreach (CelestialBody body in FlightGlobals.Bodies)
        {
            if (body == sun) continue;

            Vector3d toBody = body.position - vesselPos;

            // Project body centre onto the vessel→sun ray.
            double along = Vector3d.Dot(toSunUnit, toBody);

            // The body must sit BETWEEN the vessel and the sun.
            if (along <= 0.0 || along >= distToSun) continue;

            // Perpendicular distance from the ray to the body's centre —
            // if less than the body's radius, the body blocks the sun.
            Vector3d perp = toBody - toSunUnit * along;
            if (perp.magnitude < body.Radius)
                return true;
        }

        return false;
    }

    // =====================================================================
    // Per-vessel enabled state
    // =====================================================================

    private static bool GetVesselAutoLightsEnabled(Guid vesselId)
    {
        VesselSettings vs;
        if (_vesselSettings.TryGetValue(vesselId, out vs)) return vs.autoLightsEnabled;
        return true; // default ON
    }

    private static void SetVesselAutoLightsEnabled(Guid vesselId, bool enabled)
    {
        GetOrCreateVesselSettings(vesselId).autoLightsEnabled = enabled;
    }

    // =====================================================================
    // Vessel list UI — called once per row inside the scroll view
    // =====================================================================

    // Light-bulb icons: created once, reused across IMGUI frames.
    // ON  = white disc (lit)   OFF = dark grey disc (unlit)
    // Size 14×14 — not subject to DXT compression so no pow-2 restriction.
    private static Texture2D _lightIconOn;
    private static Texture2D _lightIconOff;
    private static Texture2D _dayNightDotDay;
    private static Texture2D _dayNightDotNight;
    private static readonly Dictionary<Guid, bool> _dayNightDotCache = new Dictionary<Guid, bool>();
    private static float _dayNightDotCacheNextRefresh = 0f;

    private static void EnsureLightIcons()
    {
        if (_lightIconOn  == null) _lightIconOn  = BuildLightIcon(isOn: true);
        if (_lightIconOff == null) _lightIconOff = BuildLightIcon(isOn: false);
        if (_dayNightDotDay == null) _dayNightDotDay = BuildDayNightDot(isDay: true);
        if (_dayNightDotNight == null) _dayNightDotNight = BuildDayNightDot(isDay: false);
    }

    private static Texture2D BuildDayNightDot(bool isDay)
    {
        const int S = 10;
        const float c = (S - 1) * 0.5f;
        const float r = 3.35f;
        const float outline = 0.9f;

        Color fill = isDay
            ? new Color(1f, 1f, 1f, 1f)
            : new Color(0f, 0f, 0f, 1f);
        Color border = isDay
            ? new Color(0f, 0f, 0f, 0.75f)
            : new Color(1f, 1f, 1f, 0.75f);
        Color clear = new Color(0f, 0f, 0f, 0f);

        var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Bilinear;

        for (int y = 0; y < S; y++)
        {
            for (int x = 0; x < S; x++)
            {
                float dx = x - c;
                float dy = y - c;
                float d = Mathf.Sqrt(dx * dx + dy * dy);

                if (d <= r)
                    tex.SetPixel(x, y, fill);
                else if (d <= r + outline)
                    tex.SetPixel(x, y, border);
                else
                    tex.SetPixel(x, y, clear);
            }
        }

        tex.Apply();
        return tex;
    }

    private void DrawDayNightDot(Vessel v)
    {
        EnsureLightIcons();

        bool inShadow = GetCachedShadowState(v);
        var icon = inShadow ? _dayNightDotNight : _dayNightDotDay;
        var tooltip = inShadow
            ? "Night/Shadow state: NIGHT"
            : "Night/Shadow state: DAY";

        GUILayout.Label(new GUIContent(icon, tooltip), GUILayout.Width(14), GUILayout.Height(14));
    }

    private static bool GetCachedShadowState(Vessel vessel)
    {
        if (vessel == null) return false;

        if (Time.realtimeSinceStartup >= _dayNightDotCacheNextRefresh)
        {
            _dayNightDotCache.Clear();
            _dayNightDotCacheNextRefresh = Time.realtimeSinceStartup + SHADOW_CHECK_INTERVAL;
        }

        bool inShadow;
        if (_dayNightDotCache.TryGetValue(vessel.id, out inShadow))
            return inShadow;

        inShadow = IsVesselInShadow(vessel);
        _dayNightDotCache[vessel.id] = inShadow;
        return inShadow;
    }

    /// <summary>
    /// Draws a sun icon: solid circle with 8 equidistant rays radiating outward.
    /// ON  → white  on transparent background.
    /// OFF → dark grey on transparent background.
    /// Anti-aliased via per-pixel distance feathering.
    /// </summary>
    private static Texture2D BuildLightIcon(bool isOn)
    {
        const int   S         = 14;
        const float cx        = (S - 1) * 0.5f;   // 6.5
        const float cy        = (S - 1) * 0.5f;
        const float circleR   = 3.0f;   // radius of the central disc
        const float rayInner  = 4.5f;   // gap: ray starts this far from centre
        const float rayOuter  = 6.5f;   // ray ends here (near texture edge)
        const float rayHalfW  = 0.85f;  // half-width of each ray in pixels
        const float aa        = 0.6f;   // anti-alias feather width
        const int   RAY_COUNT = 8;

        Color fill = isOn
            ? new Color(1f,    1f,    1f,    1f)
            : new Color(0.25f, 0.25f, 0.25f, 1f);
        Color clear = new Color(0f, 0f, 0f, 0f);

        // Pre-compute ray direction unit vectors (angles 0°, 45°, …, 315°)
        var cosA = new float[RAY_COUNT];
        var sinA = new float[RAY_COUNT];
        for (int k = 0; k < RAY_COUNT; k++)
        {
            float a = k * Mathf.PI / 4f;
            cosA[k] = Mathf.Cos(a);
            sinA[k] = Mathf.Sin(a);
        }

        var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Bilinear;

        for (int y = 0; y < S; y++)
        {
            for (int x = 0; x < S; x++)
            {
                float vx   = x - cx;
                float vy   = y - cy;
                float dist = Mathf.Sqrt(vx * vx + vy * vy);

                // Central disc — alpha falls to 0 over the aa band outside circleR
                float alpha = Mathf.Clamp01((circleR + aa - dist) / aa);

                // 8 rays — take the max contribution over all directions
                for (int k = 0; k < RAY_COUNT; k++)
                {
                    float along =  vx * cosA[k] + vy * sinA[k];
                    float perp  = Mathf.Abs(-vx * sinA[k] + vy * cosA[k]);

                    if (along < rayInner - aa || along > rayOuter + aa) continue;

                    float aAlong = Mathf.Clamp01((along - (rayInner - aa)) / aa)
                                 * Mathf.Clamp01(((rayOuter + aa) - along)  / aa);
                    float aPerp  = Mathf.Clamp01((rayHalfW + aa - perp)     / aa);
                    float rayA   = aAlong * aPerp;
                    if (rayA > alpha) alpha = rayA;
                }

                tex.SetPixel(x, y, alpha > 0f ? new Color(fill.r, fill.g, fill.b, alpha) : clear);
            }
        }

        tex.Apply();
        return tex;
    }

    private void DrawAutoLightsButton(Vessel v)
    {
        EnsureLightIcons();
        bool autoEnabled = GetVesselAutoLightsEnabled(v.id);
        var  icon        = autoEnabled ? _lightIconOn : _lightIconOff;
        var  tooltip     = autoEnabled
            ? "Auto-lights in shadow: ON\n(click to disable for this vessel)"
            : "Auto-lights in shadow: OFF\n(click to enable for this vessel)";

        bool newEnabled = GUILayout.Toggle(autoEnabled, new GUIContent(icon, tooltip),
                                           GUI.skin.button, GUILayout.Width(24), GUILayout.Height(22));
        if (newEnabled != autoEnabled)
        {
            SetVesselAutoLightsEnabled(v.id, newEnabled);

            bool lightsCurrentlyOn = v.ActionGroups[KSPActionGroup.Light];
            bool inShadowNow = IsVesselInShadow(v);
            var toggleVS = GetOrCreateVesselSettings(v.id);

            if (!newEnabled)
            {
                // User disabled auto-lights — force immediate OFF if currently on.
                if (lightsCurrentlyOn)
                {
                    v.ActionGroups.SetGroup(KSPActionGroup.Light, false);
                    Debug.Log("[FVC] AutoLights: user disabled auto-lights on " + v.vesselName + " — lights OFF");
                }

                toggleVS.lightsManagedByMod = false;
            }
            else
            {
                // User enabled auto-lights — force immediate ON if currently off.
                if (!lightsCurrentlyOn)
                {
                    v.ActionGroups.SetGroup(KSPActionGroup.Light, true);
                    Debug.Log("[FVC] AutoLights: user enabled auto-lights on " + v.vesselName + " — lights ON");
                }

                // After user-enable, auto-lights owns the next day/night transitions.
                toggleVS.lightsManagedByMod = true;
            }

            // User toggles intentionally bypass day/night gating. Re-sync transition baseline.
            if (v == FlightGlobals.ActiveVessel) _autoLightsPrevInShadow = inShadowNow;

            SaveUserPrefs();
        }
    }

    // =====================================================================
    // XML persistence — now handled by LoadUserPrefs/SaveUserPrefs via
    // the consolidated VesselSettings section in FastVesselChanger.cs.
    // These stub methods are retained for any legacy call sites that may
    // not yet have been removed from the codebase.
    // =====================================================================

    [System.Obsolete("Replaced by VesselSettings XML section in LoadUserPrefs")]
    private void LoadAutoLightsFromXml(XmlElement playerSection) { }

    [System.Obsolete("Replaced by VesselSettings XML section in SaveUserPrefs")]
    private void SaveAutoLightsToXml(XmlDocument doc, XmlElement playerSection) { }
}
