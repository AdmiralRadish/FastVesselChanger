using System;
using System.Collections.Generic;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Xml;
using UnityEngine;

#pragma warning disable CS8618, CS8600, CS8601, CS8625, CS8603, CS8604

public partial class FastVesselChanger
{
    // =========================================================================
    // HullcamVDS integration — fields
    // =========================================================================

    // Reflection cache (static — discovered once per KSP session, survives scene reloads)
    private static bool _hullcamChecked = false;
    // True once we've found the HullcamVDS assembly by name (set independently of type resolution
    // so the UI section can still appear even if GetType/GetTypes failed to resolve the type).
    private static bool _hullcamInstalled = false;
    private static Type _hullcamModuleType = null;
    private static MethodInfo _hullcamActivateMethod = null;
    private static MethodInfo _hullcamDeactivateMethod = null;
    private static FieldInfo _hullcamCurrentCamField = null;
    private static PropertyInfo _hullcamIsActiveProp = null;
    private static FieldInfo _hullcamIsActiveField = null;
    private static FieldInfo _hullcamCameraNameField = null;
    // Per-vessel hull cam settings (static, survives vessel-switch addon recreation like _vesselZooms)
    private static Dictionary<Guid, VesselHullcamSettings> _vesselHullcamSettings = new Dictionary<Guid, VesselHullcamSettings>();

    // Cached hull cam list for the active vessel — avoids re-scanning parts every OnGUI call.
    // Invalidated when vessel changes (checked via ID) or when RebuildHullCamRotation is called.
    private List<HullCamEntry> _cachedVesselCams = new List<HullCamEntry>();
    private Guid _cachedVesselCamsId = Guid.Empty;

    // Hull cam state for the active vessel (instance — reset on each addon recreation)
    private bool _hullcamAutoActive = false;
    private float _hullcamInterval = 10f;
    private string _hullcamIntervalText = "10";
    private bool _hullcamIncludeExternal = true;
    private HashSet<uint> _hullcamSelectedIds = new HashSet<uint>();
    private List<object> _hullcamRotation = new List<object>(); // null slot = external / standard camera
    private int _hullcamRotationIndex = -1;
    private float _hullcamLastSwitchRealtime = 0f;
    private Vector2 _hullcamScrollPos = Vector2.zero;
    private bool _lastHullcamSectionVisible = true;  // init true so first DrawHullcamSection call always triggers a resize
    private bool _showHullcamSection = true;   // whether the section is expanded (user pref)
    private bool _lastShowHullcamSection = true;
    // Tracks which module WE last activated; only this module is ever deactivated by our code.
    // Null means we have not activated any hull cam in this instance's lifetime.
    private object _hullcamLastActivatedModule = null;

    // The vessel this FVC instance was created to manage. Set once in Start() and NEVER changed.
    // SyncCurrentHullcamStateToDict() uses this instead of FlightGlobals.ActiveVessel so that
    // OnDestroy() always writes to the correct vessel even after SetActiveVessel() has already
    // changed FlightGlobals.ActiveVessel to the NEW vessel.
    private Guid _instanceVesselId = Guid.Empty;

    // =========================================================================
    // HullcamVDS integration — nested types and helper methods
    // =========================================================================

    private class VesselHullcamSettings
    {
        public bool hullcamEnabled = false;
        public float hullcamInterval = 10f;
        public bool includeExternal = true;
        public HashSet<uint> selectedFlightIds = new HashSet<uint>();
    }

    private struct HullCamEntry { public Part part; public object module; }

    void DetectHullcamVDS()
    {
        if (_hullcamChecked) return;
        _hullcamChecked = true;

        var allAsms = AppDomain.CurrentDomain.GetAssemblies();
        Debug.Log("[FastVesselChanger] HullcamVDS scan: checking " + allAsms.Length + " loaded assemblies");
        foreach (var asm in allAsms)
        {
            var shortName = asm.GetName().Name;

            // Match by assembly short name — HullcamVDS ships as "HullcamVDSContinued"
            if (shortName != "HullcamVDSContinued" && shortName != "HullcamVDS" && shortName != "HullCameraVDS")
                continue;

            _hullcamInstalled = true;
            Debug.Log("[FastVesselChanger] HullcamVDS assembly FOUND: " + asm.GetName().FullName);

            // asm.GetType(name) can return null in KSP's custom-loader context even when the type
            // IS present. Try it first, then fall back to a full GetTypes() scan.
            _hullcamModuleType = asm.GetType("HullcamVDS.MuMechModuleHullCamera");
            Debug.Log("[FastVesselChanger] HullcamVDS GetType direct result: " + (_hullcamModuleType != null ? "FOUND" : "null"));
            if (_hullcamModuleType == null)
            {
                try
                {
                    Debug.Log("[FastVesselChanger] HullcamVDS: falling back to GetTypes() scan");
                    foreach (var t in asm.GetTypes())
                    {
                        if (t != null && t.FullName == "HullcamVDS.MuMechModuleHullCamera")
                        {
                            _hullcamModuleType = t;
                            Debug.Log("[FastVesselChanger] HullcamVDS: type found via GetTypes() scan");
                            break;
                        }
                    }
                }
                catch (ReflectionTypeLoadException rtle)
                {
                    Debug.LogWarning("[FastVesselChanger] HullcamVDS GetTypes() threw ReflectionTypeLoadException — scanning partial results. " + rtle.Message);
                    foreach (var t in rtle.Types)
                    {
                        if (t != null && t.FullName == "HullcamVDS.MuMechModuleHullCamera")
                        {
                            _hullcamModuleType = t;
                            Debug.Log("[FastVesselChanger] HullcamVDS: type found in partial GetTypes() results");
                            break;
                        }
                    }
                }
            }

            if (_hullcamModuleType != null)
            {
                var instF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var statF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

                // ActivateCamera() is the public KSPEvent — it calls the private Activate()
                // toggle internally: if camActive==true it restores main cam, else activates.
                _hullcamActivateMethod   = _hullcamModuleType.GetMethod("ActivateCamera", instF)
                                        ?? _hullcamModuleType.GetMethod("Activate", instF);

                // LeaveCamera() is the static "restore main camera" function. There is no
                // per-instance DeactivateCamera or Deactivate method in HullcamVDS.
                _hullcamDeactivateMethod = _hullcamModuleType.GetMethod("LeaveCamera",
                                                statF | BindingFlags.NonPublic)
                                        ?? _hullcamModuleType.GetMethod("LeaveCamera", statF);

                // sCurrentCamera is the static field tracking the active camera instance.
                _hullcamCurrentCamField  = _hullcamModuleType.GetField("sCurrentCamera", statF)
                                        ?? _hullcamModuleType.GetField("CurrentCamera", statF)
                                        ?? _hullcamModuleType.GetField("currentCamera", statF);

                // camActive is the [KSPField] bool indicating whether this camera is currently active.
                _hullcamIsActiveField    = _hullcamModuleType.GetField("camActive", instF)
                                        ?? _hullcamModuleType.GetField("isActive", instF)
                                        ?? _hullcamModuleType.GetField("IsActive", instF);
                // No IsActive property in this version — prop will be null, field is the source of truth.
                _hullcamIsActiveProp     = _hullcamModuleType.GetProperty("IsActive", instF)
                                        ?? _hullcamModuleType.GetProperty("isActive", instF);

                _hullcamCameraNameField  = _hullcamModuleType.GetField("cameraName", instF)
                                        ?? _hullcamModuleType.GetField("CameraName", instF);

                Debug.Log("[FastVesselChanger] HullcamVDS type resolved:"
                    + "\n  ActivateCamera=" + (_hullcamActivateMethod?.Name ?? "null")
                    + "\n  LeaveCamera=" + (_hullcamDeactivateMethod?.Name ?? "null")
                    + "\n  sCurrentCamera=" + (_hullcamCurrentCamField?.Name ?? "null")
                    + "\n  camActive=" + (_hullcamIsActiveField?.Name ?? "null")
                    + "\n  cameraName=" + (_hullcamCameraNameField?.Name ?? "null"));
            }
            else
            {
                Debug.LogWarning("[FastVesselChanger] HullcamVDS assembly present but MuMechModuleHullCamera type NOT resolved"
                    + " — camera activation/deactivation disabled; vessel cam detection via instance FullName still works.");
            }
            break;
        }

        if (!_hullcamInstalled)
            Debug.Log("[FastVesselChanger] HullcamVDS NOT detected (no matching assembly found) — hull cam integration inactive");
        else
            Debug.Log("[FastVesselChanger] HullcamVDS detection complete: installed=" + _hullcamInstalled + " typeResolved=" + (_hullcamModuleType != null));
    }

    List<HullCamEntry> GetHullCamsOnVessel(Vessel v)
    {
        var result = new List<HullCamEntry>();
        if (v == null || v.parts == null) return result;
        foreach (var part in v.parts)
        {
            if (part == null || part.Modules == null) continue;
            foreach (PartModule mod in part.Modules)
            {
                if (mod == null) continue;
                var fullName = mod.GetType().FullName;
                // Use StartsWith so we match MuMechModuleHullCamera AND its subclass
                // MuMechModuleHullCameraZoom (which is what every actual camera part uses).
                // String comparison avoids assembly-identity issues from KSP's custom loader.
                if (fullName != null && fullName.StartsWith("HullcamVDS.MuMechModuleHullCamera"))
                {
                    result.Add(new HullCamEntry { part = part, module = mod });
                    break; // one camera module per part
                }
            }
        }
        return result;
    }

    string GetHullCamDisplayName(Part part, object module)
    {
        if (_hullcamCameraNameField != null && module != null)
        {
            try
            {
                var n = _hullcamCameraNameField.GetValue(module) as string;
                if (!string.IsNullOrEmpty(n) && n != "HullCamera") return n;
            }
            catch { }
        }
        return part?.partInfo?.title ?? part?.partName ?? "Hull Camera";
    }

    void ActivateHullCam(object module)
    {
        if (module == null)
        {
            Debug.LogWarning("[FastVesselChanger] ActivateHullCam: module is null, skipping");
            return;
        }
        if (_hullcamActivateMethod == null)
        {
            Debug.LogWarning("[FastVesselChanger] ActivateHullCam: _hullcamActivateMethod is null — type resolution failed?");
            return;
        }
        try
        {
            var camName = _hullcamCameraNameField != null ? (_hullcamCameraNameField.GetValue(module) as string ?? "?") : "?";
            Debug.Log("[FastVesselChanger] ActivateHullCam: invoking ActivateCamera on '" + camName + "'");
            _hullcamActivateMethod.Invoke(module, null);
            _hullcamLastActivatedModule = module;
            Debug.Log("[FastVesselChanger] ActivateHullCam: success");

            // Cancel any pending zoom restore — FlightCamera.fetch will be null while a
            // hull cam owns the camera, so the zoom restore loop in Update() would spin
            // uselessly (and in older builds, flood the log every frame causing a hang).
            // Zoom is handled separately by RestoreZoomAfterHullcamDeactivate() on deactivation.
            if (_pendingZoomRestore)
            {
                Debug.Log("[FastVesselChanger] ActivateHullCam: cancelling pending zoom restore (hull cam now owns camera)");
                _pendingZoomRestore = false;
                _pendingZoomVesselId = Guid.Empty;
                _pendingZoomDeadlineRealtime = 0f;
            }
        }
        catch (Exception e) { Debug.LogWarning("[FastVesselChanger] HullCam ActivateCamera failed: " + e.GetType().Name + ": " + e.Message); }
    }

    void DeactivateCurrentHullCam()
    {
        // Only deactivate a module that WE activated. Never blindly deactivate native hullcam state.
        if (_hullcamLastActivatedModule == null)
        {
            Debug.Log("[FastVesselChanger] DeactivateCurrentHullCam: nothing to deactivate (_hullcamLastActivatedModule is null)");
            return;
        }
        Debug.Log("[FastVesselChanger] DeactivateCurrentHullCam: deactivating...");
        try
        {
            if (_hullcamDeactivateMethod != null)
            {
                // LeaveCamera() is a static method — invoke with null instance.
                // It calls RestoreMainCamera() which resets the FlightCamera back to normal.
                Debug.Log("[FastVesselChanger] DeactivateCurrentHullCam: calling LeaveCamera() (static)");
                _hullcamDeactivateMethod.Invoke(null, null);
            }
            else if (_hullcamActivateMethod != null)
            {
                // Fallback: ActivateCamera() is a toggle. If camActive==true, calling it
                // will call RestoreMainCamera() (the deactivation path). Guard on camActive
                // so we don't accidentally re-activate a camera we're trying to leave.
                bool isActive = false;
                if (_hullcamIsActiveField != null)
                    isActive = (bool)(_hullcamIsActiveField.GetValue(_hullcamLastActivatedModule) ?? false);
                Debug.Log("[FastVesselChanger] DeactivateCurrentHullCam: LeaveCamera unavailable; camActive=" + isActive + " — " + (isActive ? "calling ActivateCamera toggle" : "skipping (not active)"));
                if (isActive)
                    _hullcamActivateMethod.Invoke(_hullcamLastActivatedModule, null);
            }
            else
            {
                Debug.LogWarning("[FastVesselChanger] DeactivateCurrentHullCam: no deactivation method available — hull cam state may persist");
            }
        }
        catch (Exception e) { Debug.LogWarning("[FastVesselChanger] HullCam deactivate failed: " + e.GetType().Name + ": " + e.Message); }
        _hullcamLastActivatedModule = null;
    }

    void RebuildHullCamRotation(Vessel v)
    {
        _hullcamRotation.Clear();
        // Invalidate the hull cam cache so DrawHullcamSection re-scans on next frame
        _cachedVesselCamsId = Guid.Empty;
        if (_hullcamIncludeExternal)
            _hullcamRotation.Add(null); // null slot = external / standard orbit camera
        if (v != null)
        {
            foreach (var entry in GetHullCamsOnVessel(v))
                if (_hullcamSelectedIds.Contains(entry.part.flightID))
                    _hullcamRotation.Add(entry.module);
        }
        if (_hullcamRotation.Count == 0)
            _hullcamRotation.Add(null); // safety: always have at least an external slot
        if (_hullcamRotationIndex >= _hullcamRotation.Count)
            _hullcamRotationIndex = 0;
    }

    // Called after DeactivateCurrentHullCam() to restore the FlightCamera zoom that was
    // active before hull cams took over. HullcamVDS's LeaveCamera() resets the camera
    // position but not the zoom distance, so we re-apply our saved value.
    void RestoreZoomAfterHullcamDeactivate()
    {
        if (_instanceVesselId == Guid.Empty) return;
        float savedZoom;
        if (!_vesselZooms.TryGetValue(_instanceVesselId, out savedZoom)) return;
        var cam = FlightCamera.fetch;
        if (cam == null) return;

        // Cache the distance field if not already done.
        if (!_camDistFieldSearched)
        {
            _camDistFieldSearched = true;
            _camDistField = cam.GetType().GetField("distance",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        }

        cam.SetDistanceImmediate(savedZoom);
        _camDistField?.SetValue(cam, savedZoom);

        // Use the lockout mechanism to enforce the value for a short window, the same
        // way the vessel-switch zoom restore does — LeaveCamera() can trigger camera
        // movement that would otherwise overwrite our applied value.
        _zoomLockoutVesselId = _instanceVesselId;
        _zoomLockoutTarget = savedZoom;
        _zoomLockoutUntilRealtime = Time.realtimeSinceStartup + ZOOM_LOCKOUT_SECONDS;
        Debug.Log("[FastVesselChanger] RestoreZoomAfterHullcamDeactivate: zoom=" + savedZoom);
    }

    void CycleToNextHullCam()
    {
        if (_hullcamRotation.Count == 0) return;
        _hullcamRotationIndex = (_hullcamRotationIndex + 1) % _hullcamRotation.Count;
        var mod = _hullcamRotation[_hullcamRotationIndex];
        if (mod == null)
        {
            // Only restore zoom if we were actually coming from an active hull cam;
            // if no hull cam was active there is nothing to restore and we must NOT
            // start a lockout (which would block the scroll wheel for 2 seconds).
            bool wasHullCamActive = _hullcamLastActivatedModule != null;
            DeactivateCurrentHullCam();
            if (wasHullCamActive) RestoreZoomAfterHullcamDeactivate();
        }
        else
        {
            ActivateHullCam(mod);
        }
    }

    VesselHullcamSettings GetOrCreateHullcamSettings(Guid vesselId)
    {
        VesselHullcamSettings s;
        if (!_vesselHullcamSettings.TryGetValue(vesselId, out s))
        {
            s = new VesselHullcamSettings();
            _vesselHullcamSettings[vesselId] = s;
        }
        return s;
    }

    void SyncCurrentHullcamStateToDict()
    {
        if (_instanceVesselId == Guid.Empty) return;
        var s = GetOrCreateHullcamSettings(_instanceVesselId);
        s.hullcamEnabled     = _hullcamAutoActive;
        s.hullcamInterval    = _hullcamInterval;
        s.includeExternal    = _hullcamIncludeExternal;
        s.selectedFlightIds  = new HashSet<uint>(_hullcamSelectedIds);
        Debug.Log("[FastVesselChanger] SyncCurrentHullcamStateToDict: vesselId=" + _instanceVesselId
            + " hullcamEnabled=" + s.hullcamEnabled
            + " selectedCams=" + s.selectedFlightIds.Count);
    }

    void ApplyVesselHullcamSettings(Vessel v)
    {
        if (v == null) return;
        // Do NOT call DeactivateCurrentHullCam() here — we have not activated anything yet and
        // blindly deactivating would break native HullcamVDS key controls on the active vessel.
        var s = GetOrCreateHullcamSettings(v.id);
        Debug.Log("[FastVesselChanger] ApplyVesselHullcamSettings: vessel='" + v.vesselName
            + "' id=" + v.id
            + " hullcamEnabled=" + s.hullcamEnabled
            + " interval=" + s.hullcamInterval
            + " selectedCams=" + s.selectedFlightIds.Count
            + " (dictEntries=" + _vesselHullcamSettings.Count + ")");
        _hullcamAutoActive      = s.hullcamEnabled;
        _hullcamInterval        = Mathf.Max(1f, s.hullcamInterval);
        _hullcamIntervalText    = _hullcamInterval.ToString("F0");
        _hullcamIncludeExternal = s.includeExternal;
        _hullcamSelectedIds     = new HashSet<uint>(s.selectedFlightIds);
        _hullcamLastSwitchRealtime = Time.realtimeSinceStartup;
        _hullcamRotationIndex   = -1;
        RebuildHullCamRotation(v);
        if (_hullcamAutoActive && _hullcamRotation.Count > 0)
        {
            _hullcamRotationIndex = 0;
            var firstMod = _hullcamRotation[0];
            if (firstMod == null) DeactivateCurrentHullCam();
            else                  ActivateHullCam(firstMod);
        }
    }

    void LoadHullcamSettingsFromScenario(FastVesselChangerScenario scen)
    {
        // Guard: don't wipe the static dict if the scenario isn't ready yet.
        // The dict may already hold good data from a previous instance and we
        // should NOT clear it only to fail the load immediately after.
        if (scen == null)
        {
            Debug.Log("[FastVesselChanger] LoadHullcamSettingsFromScenario: scenario null, keeping "
                + _vesselHullcamSettings.Count + " existing entries");
            return;
        }
        Debug.Log("[FastVesselChanger] LoadHullcamSettingsFromScenario: scenario has entries="
            + scen.vesselHullcamEntries.Count + " cams=" + scen.vesselHullcamSelectedCams.Count);
        _vesselHullcamSettings.Clear();
        // format: "guid|hullcamEnabled|interval|includeExternal"
        foreach (var entry in scen.vesselHullcamEntries)
        {
            var parts = entry.Split('|');
            if (parts.Length < 4) continue;
            Guid vesselId;
            if (!Guid.TryParse(parts[0], out vesselId)) continue;
            var s = new VesselHullcamSettings();
            bool enabled = false;  bool.TryParse(parts[1], out enabled);  s.hullcamEnabled = enabled;
            float interval = 10f;  float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out interval);  s.hullcamInterval = Mathf.Max(1f, interval);
            bool incExt = true;    bool.TryParse(parts[3], out incExt);   s.includeExternal = incExt;
            _vesselHullcamSettings[vesselId] = s;
        }
        // format: "guid|flightId"
        foreach (var entry in scen.vesselHullcamSelectedCams)
        {
            var parts = entry.Split('|');
            if (parts.Length < 2) continue;
            Guid vesselId;
            if (!Guid.TryParse(parts[0], out vesselId)) continue;
            uint flightId;
            if (!uint.TryParse(parts[1], out flightId)) continue;
            VesselHullcamSettings s;
            if (!_vesselHullcamSettings.TryGetValue(vesselId, out s))
            {
                s = new VesselHullcamSettings();
                _vesselHullcamSettings[vesselId] = s;
            }
            s.selectedFlightIds.Add(flightId);
        }
    }

    // =========================================================================
    // HullcamVDS integration — Update and DrawWindow delegates
    // Called from the main partial's Update() and DrawWindow() respectively.
    // =========================================================================

    void UpdateHullcam()
    {
        // Hull cam auto-cycling — independent of the vessel-switch timer
        if (_hullcamAutoActive && _hullcamInstalled && _hullcamRotation.Count > 0)
        {
            float hcInterval = Mathf.Max(1f, _hullcamInterval);
            if (Time.realtimeSinceStartup - _hullcamLastSwitchRealtime >= hcInterval)
            {
                _hullcamLastSwitchRealtime = Time.realtimeSinceStartup;
                CycleToNextHullCam();
            }
        }
    }

    void DrawHullcamSection()
    {
        // Shown automatically when HullcamVDS is installed and the active vessel has camera parts.
        if (!_hullcamInstalled) return;

        var hcVessel = FlightGlobals.ActiveVessel;
        var hcVesselId = hcVessel?.id ?? Guid.Empty;
        if (hcVesselId != _cachedVesselCamsId)
        {
            _cachedVesselCamsId = hcVesselId;
            _cachedVesselCams = hcVessel != null ? GetHullCamsOnVessel(hcVessel) : new List<HullCamEntry>();
        }
        var vesselCams = _cachedVesselCams;
        bool hcVisible = vesselCams.Count > 0;
        if (hcVisible != _lastHullcamSectionVisible)
        {
            Debug.Log("[FastVesselChanger] DrawHullcamSection: visibility changed to " + hcVisible
                + " (installed=" + _hullcamInstalled + ", cams=" + vesselCams.Count + ", vessel='" + (hcVessel?.vesselName ?? "null") + "')");
            windowRect.height = 0; // force window to resize
            _lastHullcamSectionVisible = hcVisible;
        }
        if (!hcVisible) return;

        GUILayout.Space(4);
        GUILayout.Box("", GUILayout.Height(1), GUILayout.ExpandWidth(true));
        GUILayout.Space(4);

        // Collapsible header button — mirrors the Camera Controls pattern
        if (GUILayout.Button((_showHullcamSection ? "[-] " : "[+] ") + "Hull Cameras"))
        {
            _showHullcamSection = !_showHullcamSection;
            windowRect.height = 0;
            SaveUserPrefs();
        }
        if (!_showHullcamSection) return;

        GUILayout.BeginVertical("box");

        // ON/OFF + interval row
        GUILayout.BeginHorizontal();
        GUILayout.Label("Hull Cameras:", GUILayout.Width(95));
        if (GUILayout.Button(_hullcamAutoActive ? "ON" : "OFF", GUILayout.Width(40)))
        {
            _hullcamAutoActive = !_hullcamAutoActive;
            if (!_hullcamAutoActive)
            {
                // Turning off — restore external camera and zoom if a hull cam was active.
                bool wasHullCamActive = _hullcamLastActivatedModule != null;
                DeactivateCurrentHullCam();
                if (wasHullCamActive) RestoreZoomAfterHullcamDeactivate();
            }
            else
            {
                RebuildHullCamRotation(hcVessel);
                _hullcamLastSwitchRealtime = Time.realtimeSinceStartup;
                _hullcamRotationIndex = 0;
                if (_hullcamRotation.Count > 0)
                {
                    var mod0 = _hullcamRotation[0];
                    if (mod0 == null) DeactivateCurrentHullCam();
                    else              ActivateHullCam(mod0);
                }
            }
            SyncCurrentHullcamStateToDict();
            SaveToScenario();
        }
        GUILayout.Label("Interval:", GUILayout.Width(52));
        string newHcText = GUILayout.TextField(_hullcamIntervalText, GUILayout.Width(40));
        if (newHcText != _hullcamIntervalText)
        {
            _hullcamIntervalText = newHcText;
            float hcIParsed;
            if (float.TryParse(_hullcamIntervalText, NumberStyles.Float, CultureInfo.InvariantCulture, out hcIParsed) && hcIParsed >= 1f)
            {
                _hullcamInterval = hcIParsed;
                SyncCurrentHullcamStateToDict();
                SaveToScenario();
            }
        }
        GUILayout.Label("s", GUILayout.Width(14));
        GUILayout.EndHorizontal();

        if (_hullcamAutoActive)
        {
            float hcEffective = Mathf.Max(1f, _hullcamInterval);
            float hcRemaining = Mathf.Max(0f, hcEffective - (Time.realtimeSinceStartup - _hullcamLastSwitchRealtime));
            GUILayout.Label("Next camera in: " + hcRemaining.ToString("F0") + "s", GUILayout.Width(200));
        }

        // Camera pick list (scrollable)
        _hullcamScrollPos = GUILayout.BeginScrollView(_hullcamScrollPos, GUILayout.Height(110f));

        // External camera entry
        bool newExternal = GUILayout.Toggle(_hullcamIncludeExternal, "  External Camera");
        if (newExternal != _hullcamIncludeExternal)
        {
            _hullcamIncludeExternal = newExternal;
            RebuildHullCamRotation(hcVessel);
            SyncCurrentHullcamStateToDict();
            SaveToScenario();
        }

        // Individual hull camera entries
        foreach (var hce in vesselCams)
        {
            bool camSel = _hullcamSelectedIds.Contains(hce.part.flightID);
            bool newCamSel = GUILayout.Toggle(camSel, "  " + GetHullCamDisplayName(hce.part, hce.module));
            if (newCamSel != camSel)
            {
                if (newCamSel) _hullcamSelectedIds.Add(hce.part.flightID);
                else           _hullcamSelectedIds.Remove(hce.part.flightID);
                Debug.Log("[FastVesselChanger] HullCam checkbox: '" + GetHullCamDisplayName(hce.part, hce.module)
                    + "' flightID=" + hce.part.flightID + " selected=" + newCamSel
                    + " (_hullcamSelectedIds.Count now=" + _hullcamSelectedIds.Count + ")");
                RebuildHullCamRotation(hcVessel);
                SyncCurrentHullcamStateToDict();
                SaveToScenario();
            }
        }

        GUILayout.EndScrollView();
        GUILayout.EndVertical();
    }
}
