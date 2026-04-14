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
    private static FieldInfo _hullcamCamEnabledField = null;
    // Per-vessel hull cam settings: now stored inside _vesselSettings (see FVCVesselSettings.cs)

    // Cached hull cam list for the active vessel — avoids re-scanning parts every OnGUI call.
    // Invalidated when vessel changes (checked via ID) or when RebuildHullCamRotation is called.
    private List<HullCamEntry> _cachedVesselCams = new List<HullCamEntry>();
    private Guid _cachedVesselCamsId = Guid.Empty;
    private float _cachedVesselCamsTime = 0f;

    // Hull cam state for the active vessel (instance — reset on each addon recreation)
    private const float HULLCAM_MIN_INTERVAL = 10f;
    private bool _hullcamAutoActive = false;
    private float _hullcamInterval = HULLCAM_MIN_INTERVAL;
    private string _hullcamIntervalText = "10";
    private bool _hullcamIncludeExternal = true;
    private HashSet<uint> _hullcamSelectedIds = new HashSet<uint>();
    private List<object> _hullcamRotation = new List<object>(); // null slot = external / standard camera
    private int _hullcamRotationIndex = -1;
    private float _hullcamLastSwitchRealtime = 0f;
    private Vector2 _hullcamScrollPos = Vector2.zero;
    private bool _lastHullcamSectionVisible = true;  // init true so first DrawHullcamSection call always triggers a resize
    private bool _showHullcamSection = false;   // whether the section is expanded (user pref, hidden by default)
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
        // Cameras the user disabled via the PAW right-click menu.  HullcamVDS
        // does not persist camEnabled across FLIGHT→FLIGHT scene reloads in LMP,
        // so FVC tracks and re-applies the disabled state after vessel switch.
        public HashSet<uint> disabledFlightIds = new HashSet<uint>();
    }

    private struct HullCamEntry { public Part part; public object module; public bool disabled; }

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

                _hullcamCamEnabledField  = _hullcamModuleType.GetField("camEnabled", instF)
                                        ?? _hullcamModuleType.GetField("CamEnabled", instF);

                Debug.Log("[FastVesselChanger] HullcamVDS type resolved:"
                    + "\n  ActivateCamera=" + (_hullcamActivateMethod?.Name ?? "null")
                    + "\n  LeaveCamera=" + (_hullcamDeactivateMethod?.Name ?? "null")
                    + "\n  sCurrentCamera=" + (_hullcamCurrentCamField?.Name ?? "null")
                    + "\n  camActive=" + (_hullcamIsActiveField?.Name ?? "null")
                    + "\n  cameraName=" + (_hullcamCameraNameField?.Name ?? "null")
                    + "\n  camEnabled=" + (_hullcamCamEnabledField?.Name ?? "null"));
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
                    result.Add(new HullCamEntry { part = part, module = mod, disabled = IsHullCamDisabled(mod) });
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

    bool IsHullCamDisabled(object module)
    {
        if (module == null) return true;
        // Check HullcamVDS camEnabled field (the "Enable/Disable Camera" part action state)
        if (_hullcamCamEnabledField != null)
        {
            try
            {
                var val = _hullcamCamEnabledField.GetValue(module);
                if (val is bool b && !b) return true;
            }
            catch { }
        }
        // Check KSP's standard PartModule enable flags
        var pm = module as PartModule;
        if (pm != null && (!pm.isEnabled || !pm.moduleIsEnabled)) return true;
        return false;
    }

    void ActivateHullCam(object module)
    {
        if (module == null)
        {
            Debug.LogWarning("[FastVesselChanger] ActivateHullCam: module is null, skipping");
            return;
        }
        // Guard: refuse activation if the flight scene isn't fully ready or if a
        // scene transition is in progress.  Activating a hull cam during scene
        // teardown corrupts HullcamVDS's static sCurrentCamera reference, causing
        // the destination scene to freeze.
        if (!_switchWatchdogFlightReady || HighLogic.LoadedScene != GameScenes.FLIGHT)
        {
            Debug.LogWarning("[FastVesselChanger] ActivateHullCam: scene not ready (flightReady="
                + _switchWatchdogFlightReady + " scene=" + HighLogic.LoadedScene + "), skipping");
            return;
        }
        if (_hullcamActivateMethod == null)
        {
            Debug.LogWarning("[FastVesselChanger] ActivateHullCam: _hullcamActivateMethod is null — type resolution failed?");
            return;
        }
        try
        {
            // Snapshot the current external-camera zoom BEFORE hull cam takes over, so
            // RestoreZoomAfterHullcamDeactivate() has the correct value to restore.
            // Only capture when transitioning from external → hull cam (not cam → cam).
            if (_hullcamLastActivatedModule == null && _instanceVesselId != Guid.Empty)
            {
                var fc = FlightCamera.fetch;
                if (fc != null)
                    GetOrCreateVesselSettings(_instanceVesselId).cameraZoom = fc.Distance;
            }

            // Guard against stale camActive state — HullcamVDS's ActivateCamera() is a
            // toggle: if camActive is already true it DEACTIVATES instead of activating.
            // This happens when cycling lands on a camera that was never explicitly deactivated
            // (e.g. LeaveCamera() was called but didn't clear the per-module camActive flag,
            //  or the cycling timer re-visits the already-active camera after a rotation rebuild).
            if (_hullcamIsActiveField != null)
            {
                try
                {
                    bool alreadyActive = (bool)(_hullcamIsActiveField.GetValue(module) ?? false);
                    if (alreadyActive)
                    {
                        if (VERBOSE_DIAGNOSTICS) Debug.Log("[FastVesselChanger] ActivateHullCam: camActive was already true on target module — resetting to false before activation");
                        _hullcamIsActiveField.SetValue(module, false);
                    }
                }
                catch (Exception ex) { Debug.LogWarning("[FastVesselChanger] ActivateHullCam: failed to check/reset camActive: " + ex.Message); }
            }

            var camName = _hullcamCameraNameField != null ? (_hullcamCameraNameField.GetValue(module) as string ?? "?") : "?";
            if (VERBOSE_DIAGNOSTICS) Debug.Log("[FastVesselChanger] ActivateHullCam: invoking ActivateCamera on '" + camName + "'");
            _hullcamActivateMethod.Invoke(module, null);
            _hullcamLastActivatedModule = module;
            if (VERBOSE_DIAGNOSTICS) Debug.Log("[FastVesselChanger] ActivateHullCam: success");

            // Update camera log file immediately
            WriteCameraLogFile();

            // Cancel any pending zoom restore — FlightCamera.fetch will be null while a
            // hull cam owns the camera, so the zoom restore in the end-of-frame coroutine
            // would be unable to apply it.  Zoom is handled separately by
            // RestoreZoomAfterHullcamDeactivate() on deactivation.
            if (_pendingZoomRestore)
            {
                if (VERBOSE_DIAGNOSTICS) Debug.Log("[FastVesselChanger] ActivateHullCam: cancelling pending zoom restore (hull cam now owns camera)");
                _pendingZoomRestore = false;
                _pendingZoomVesselId = Guid.Empty;
                _pendingZoomFramesRemaining = 0;
            }
        }
        catch (Exception e) { Debug.LogWarning("[FastVesselChanger] HullCam ActivateCamera failed: " + e.GetType().Name + ": " + e.Message); }
    }

    void DeactivateCurrentHullCam()
    {
        // Only deactivate a module that WE activated. Never blindly deactivate native hullcam state.
        if (_hullcamLastActivatedModule == null)
        {
            if (VERBOSE_DIAGNOSTICS) Debug.Log("[FastVesselChanger] DeactivateCurrentHullCam: nothing to deactivate (_hullcamLastActivatedModule is null)");
            return;
        }
        if (VERBOSE_DIAGNOSTICS) Debug.Log("[FastVesselChanger] DeactivateCurrentHullCam: deactivating...");
        try
        {
            if (_hullcamDeactivateMethod != null)
            {
                // LeaveCamera() is a static method — invoke with null instance.
                // It calls RestoreMainCamera() which resets the FlightCamera back to normal.
                if (VERBOSE_DIAGNOSTICS) Debug.Log("[FastVesselChanger] DeactivateCurrentHullCam: calling LeaveCamera() (static)");
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
                if (VERBOSE_DIAGNOSTICS) Debug.Log("[FastVesselChanger] DeactivateCurrentHullCam: LeaveCamera unavailable; camActive=" + isActive + " — " + (isActive ? "calling ActivateCamera toggle" : "skipping (not active)"));
                if (isActive)
                    _hullcamActivateMethod.Invoke(_hullcamLastActivatedModule, null);
            }
            else
            {
                Debug.LogWarning("[FastVesselChanger] DeactivateCurrentHullCam: no deactivation method available — hull cam state may persist");
            }
        }
        catch (Exception e) { Debug.LogWarning("[FastVesselChanger] HullCam deactivate failed: " + e.GetType().Name + ": " + e.Message); }

        // Clear camActive on the module we just deactivated.  LeaveCamera() (static) may
        // not always reset the per-module flag, leaving it stale at true.  If the cycling
        // timer later re-visits this module, ActivateCamera() would toggle OFF instead of ON.
        if (_hullcamIsActiveField != null && _hullcamLastActivatedModule != null)
        {
            try { _hullcamIsActiveField.SetValue(_hullcamLastActivatedModule, false); }
            catch { }
        }

        _hullcamLastActivatedModule = null;

        // Update camera log file immediately (now showing FlightCamera mode)
        WriteCameraLogFile();
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
                if (!entry.disabled && _hullcamSelectedIds.Contains(entry.part.flightID))
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
        VesselSettings rz;
        if (!_vesselSettings.TryGetValue(_instanceVesselId, out rz) || float.IsNaN(rz.cameraZoom)) return;
        float savedZoom = rz.cameraZoom;
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

        if (VERBOSE_DIAGNOSTICS) Debug.Log("[FastVesselChanger] RestoreZoomAfterHullcamDeactivate: zoom=" + savedZoom);
    }

    void CycleToNextHullCam()
    {
        if (_hullcamRotation.Count == 0) return;
        _hullcamRotationIndex = (_hullcamRotationIndex + 1) % _hullcamRotation.Count;
        var mod = _hullcamRotation[_hullcamRotationIndex];
        if (mod == null)
        {
            // External camera slot — only deactivate if a hull cam is currently active.
            if (_hullcamLastActivatedModule != null)
            {
                DeactivateCurrentHullCam();
                RestoreZoomAfterHullcamDeactivate();
            }
            // else: already in external view, nothing to do
        }
        else
        {
            // Hull camera slot — skip if this camera is already active (prevents
            // pointless re-activation and the ActivateCamera toggle confusion).
            if (mod != _hullcamLastActivatedModule)
                ActivateHullCam(mod);
        }
    }

    VesselHullcamSettings GetOrCreateHullcamSettings(Guid vesselId) => GetOrCreateHullcam(vesselId);

    void SyncCurrentHullcamStateToDict()
    {
        if (_instanceVesselId == Guid.Empty) return;
        var s = GetOrCreateHullcamSettings(_instanceVesselId);
        s.hullcamEnabled     = _hullcamAutoActive;
        s.hullcamInterval    = _hullcamInterval;
        s.includeExternal    = _hullcamIncludeExternal;
        s.selectedFlightIds  = new HashSet<uint>(_hullcamSelectedIds);

        // Snapshot which cameras are currently disabled so we can re-apply
        // the state after a vessel switch (HullcamVDS doesn't persist camEnabled
        // across FLIGHT→FLIGHT in LMP).
        //
        // Do NOT rely solely on FlightGlobals.ActiveVessel: during a user-initiated
        // (non-FVC) vessel switch, OnDestroy() fires after FlightGlobals.ActiveVessel
        // has already changed to the new vessel.  Search FlightGlobals.Vessels by
        // GUID instead — the old vessel's parts are still alive at OnDestroy time.
        Vessel syncVessel = null;
        if (FlightGlobals.ActiveVessel?.id == _instanceVesselId)
        {
            syncVessel = FlightGlobals.ActiveVessel;
        }
        else
        {
            var allVessels = FlightGlobals.Vessels;
            if (allVessels != null)
            {
                for (int _vi = 0; _vi < allVessels.Count; _vi++)
                {
                    var _fv = allVessels[_vi];
                    if (_fv != null && _fv.id == _instanceVesselId) { syncVessel = _fv; break; }
                }
            }
        }

        if (syncVessel != null)
        {
            s.disabledFlightIds.Clear();
            foreach (var entry in GetHullCamsOnVessel(syncVessel))
            {
                if (entry.disabled)
                    s.disabledFlightIds.Add(entry.part.flightID);
            }
        }

        if (VERBOSE_DIAGNOSTICS) Debug.Log("[FastVesselChanger] SyncCurrentHullcamStateToDict: vesselId=" + _instanceVesselId
            + " hullcamEnabled=" + s.hullcamEnabled
            + " selectedCams=" + s.selectedFlightIds.Count
            + " disabledCams=" + s.disabledFlightIds.Count);
    }

    void ApplyVesselHullcamSettings(Vessel v)
    {
        if (v == null) return;
        // Do NOT call DeactivateCurrentHullCam() here — we have not activated anything yet and
        // blindly deactivating would break native HullcamVDS key controls on the active vessel.
        var s = GetOrCreateHullcamSettings(v.id);
        if (VERBOSE_DIAGNOSTICS) Debug.Log("[FastVesselChanger] ApplyVesselHullcamSettings: vessel='" + v.vesselName
            + "' id=" + v.id
            + " hullcamEnabled=" + s.hullcamEnabled
            + " interval=" + s.hullcamInterval
            + " includeExternal=" + s.includeExternal
            + " selectedCams=" + s.selectedFlightIds.Count
            + " (dictEntries=" + _vesselSettings.Count + ")");
        _hullcamAutoActive      = s.hullcamEnabled;
        _hullcamInterval        = Mathf.Max(HULLCAM_MIN_INTERVAL, s.hullcamInterval);
        _hullcamIntervalText    = _hullcamInterval.ToString("F0");
        _hullcamIncludeExternal = s.includeExternal;
        _hullcamSelectedIds     = new HashSet<uint>(s.selectedFlightIds);
        _hullcamLastSwitchRealtime = Time.realtimeSinceStartup;
        _hullcamRotationIndex   = -1;
        RebuildHullCamRotation(v);
        // Do NOT activate any hull cam here — this runs during Start(), before
        // onFlightReady.  If the scene is stuck in an NRE cascade (Waterfall etc.),
        // activating a cam on half-initialized parts would worsen the situation.
        // Hull cam auto-activation is deferred to ActivateHullcamIfReady(), which is
        // called from OnFlightReady() once the scene is confirmed healthy.
    }

    /// <summary>
    /// Activates the first hull cam in the rotation, if hullcam auto-cycling is enabled.
    /// Called from OnFlightReady() — only after the scene is fully initialized and healthy.
    /// This prevents activating cameras on half-loaded vessels during NRE cascades.
    /// </summary>
    void ActivateHullcamIfReady()
    {
        // Re-apply disabled camera state from our persisted data.  HullcamVDS does
        // not persist camEnabled across FLIGHT→FLIGHT in LMP, so we restore it here
        // once parts are fully loaded.  This runs even when auto-cycling is off.
        RestoreDisabledCameraState();

        if (!_hullcamInstalled || !_hullcamAutoActive || _hullcamRotation.Count == 0) return;
        var v = FlightGlobals.ActiveVessel;
        if (v == null) return;

        // Rebuild rotation now that parts are fully loaded (Start-time scan may have
        // seen incomplete part lists)
        RebuildHullCamRotation(v);
        if (_hullcamRotation.Count == 0) return;

        _hullcamRotationIndex = 0;
        _hullcamLastSwitchRealtime = Time.realtimeSinceStartup;
        var firstMod = _hullcamRotation[0];
        if (firstMod == null)
            DeactivateCurrentHullCam();  // external cam slot — no-op if nothing active
        else
            ActivateHullCam(firstMod);

        // Clear the blackout overlay now that the hull cam (or external fallback)
        // is rendering — the stock FlightCamera flash is no longer visible.
        _pendingHullcamBlackout = false;
    }

    /// <summary>
    /// Re-apply the disabled camera state that was tracked by FVC.
    /// HullcamVDS's camEnabled [KSPField] doesn't survive FLIGHT→FLIGHT
    /// scene reloads in LMP (the server overwrites the vessel with the
    /// un-modified state).  FVC persists which cameras the user disabled
    /// and re-disables them via reflection after each vessel switch.
    /// </summary>
    void RestoreDisabledCameraState()
    {
        if (!_hullcamInstalled || _hullcamCamEnabledField == null) return;
        var v = FlightGlobals.ActiveVessel;
        if (v == null) return;
        VesselSettings rvs;
        if (!_vesselSettings.TryGetValue(v.id, out rvs) || rvs.hullcam == null) return;
        var s = rvs.hullcam;
        if (s.disabledFlightIds.Count == 0) return;

        int restored = 0;
        foreach (var entry in GetHullCamsOnVessel(v))
        {
            if (!entry.disabled && s.disabledFlightIds.Contains(entry.part.flightID))
            {
                // Camera was disabled by user but lost its state — re-disable it
                try
                {
                    _hullcamCamEnabledField.SetValue(entry.module, false);
                    restored++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[FastVesselChanger] RestoreDisabledCameraState: "
                        + "failed to set camEnabled on flightID=" + entry.part.flightID
                        + ": " + ex.Message);
                }
            }
        }
        if (restored > 0)
        {
            Debug.Log("[FastVesselChanger] RestoreDisabledCameraState: re-disabled "
                + restored + " camera(s) on " + v.vesselName);
            // Invalidate cache so the UI picks up the change
            _cachedVesselCamsId = Guid.Empty;
            RebuildHullCamRotation(v);
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
                + _vesselSettings.Count + " existing entries");
            return;
        }
        Debug.Log("[FastVesselChanger] LoadHullcamSettingsFromScenario: scenario has entries="
            + scen.vesselHullcamEntries.Count + " cams=" + scen.vesselHullcamSelectedCams.Count);
        // format: "guid|hullcamEnabled|interval|includeExternal"
        foreach (var entry in scen.vesselHullcamEntries)
        {
            var parts = entry.Split('|');
            if (parts.Length < 4) continue;
            Guid vesselId;
            if (!Guid.TryParse(parts[0], out vesselId)) continue;
            var s = GetOrCreateHullcam(vesselId);
            bool enabled = false;  bool.TryParse(parts[1], out enabled);  s.hullcamEnabled = enabled;
            float interval = 10f;  float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out interval);  s.hullcamInterval = Mathf.Max(1f, interval);
            bool incExt = true;    bool.TryParse(parts[3], out incExt);   s.includeExternal = incExt;
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
            GetOrCreateHullcam(vesselId).selectedFlightIds.Add(flightId);
        }
    }

    // =========================================================================
    // HullcamVDS integration — Update and DrawWindow delegates
    // Called from the main partial's Update() and DrawWindow() respectively.
    // =========================================================================

    void UpdateHullcam()
    {
        // Detect native hull cam deactivation — the user pressed Escape, O/P past
        // the camera list, CAMERA_NEXT, or used the part menu to leave hullcam view.
        // All HullcamVDS exit paths set the static sCurrentCamera to null.  If we
        // had previously activated a hull cam (_hullcamLastActivatedModule != null)
        // but sCurrentCamera is now null, the user left via native keys.
        //
        // Skip all hullcam processing if the watchdog is armed and flight isn't ready —
        // the scene is in an NRE cascade and we must not touch camera state.
        if (_switchWatchdogRealtime > 0f && !_switchWatchdogFlightReady)
            return;

        if (_hullcamInstalled && _hullcamLastActivatedModule != null && _hullcamCurrentCamField != null)
        {
            object currentCam = _hullcamCurrentCamField.GetValue(null);
            if (currentCam == null)
            {
                if (VERBOSE_DIAGNOSTICS) Debug.Log("[FastVesselChanger] UpdateHullcam: native hull cam deactivation detected — restoring zoom");
                // Clear camActive on the module that was natively deactivated, in case the
                // native exit path didn't reset it.  Prevents toggle mis-fire on next activation.
                if (_hullcamIsActiveField != null)
                {
                    try { _hullcamIsActiveField.SetValue(_hullcamLastActivatedModule, false); }
                    catch { }
                }
                _hullcamLastActivatedModule = null;
                RestoreZoomAfterHullcamDeactivate();
                WriteCameraLogFile();
            }
            else if (currentCam != _hullcamLastActivatedModule)
            {
                // User cycled to a different camera via native keys (O/P).
                // Track the new module so we stay in sync.
                _hullcamLastActivatedModule = currentCam;
                WriteCameraLogFile();
            }
        }

        // Hull cam auto-cycling — independent of the vessel-switch timer
        if (_hullcamAutoActive && _hullcamInstalled && _hullcamRotation.Count > 0)
        {
            float hcInterval = Mathf.Max(HULLCAM_MIN_INTERVAL, _hullcamInterval);
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
        if (hcVesselId != _cachedVesselCamsId || Time.realtimeSinceStartup - _cachedVesselCamsTime > 2f)
        {
            _cachedVesselCamsId = hcVesselId;
            _cachedVesselCamsTime = Time.realtimeSinceStartup;
            _cachedVesselCams = hcVessel != null ? GetHullCamsOnVessel(hcVessel) : new List<HullCamEntry>();

            // Silently rebuild rotation to pick up disabled-state changes
            // from PAW toggles.  Unlike RebuildHullCamRotation(), this does
            // NOT invalidate the cache we just refreshed.
            if (_hullcamAutoActive)
            {
                _hullcamRotation.Clear();
                if (_hullcamIncludeExternal)
                    _hullcamRotation.Add(null);
                foreach (var entry in _cachedVesselCams)
                    if (!entry.disabled && _hullcamSelectedIds.Contains(entry.part.flightID))
                        _hullcamRotation.Add(entry.module);
                if (_hullcamRotation.Count == 0)
                    _hullcamRotation.Add(null);
                if (_hullcamRotationIndex >= _hullcamRotation.Count)
                    _hullcamRotationIndex = 0;
            }
        }
        var vesselCams = _cachedVesselCams;
        bool hcVisible = vesselCams.Count > 0;
        if (hcVisible != _lastHullcamSectionVisible)
        {
            if (VERBOSE_DIAGNOSTICS) Debug.Log("[FastVesselChanger] DrawHullcamSection: visibility changed to " + hcVisible
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
            windowRect.height = 0; // force resize: countdown label appears/disappears
        }
        GUILayout.Label("Interval:", GUILayout.Width(52));
        string newHcText = GUILayout.TextField(_hullcamIntervalText, GUILayout.Width(40));
        if (newHcText != _hullcamIntervalText)
        {
            _hullcamIntervalText = newHcText;
            float hcIParsed;
            if (float.TryParse(_hullcamIntervalText, NumberStyles.Float, CultureInfo.InvariantCulture, out hcIParsed) && hcIParsed >= HULLCAM_MIN_INTERVAL)
            {
                _hullcamInterval = hcIParsed;
                SyncCurrentHullcamStateToDict();
                SaveToScenario();
            }
        }
        GUILayout.Label("s", GUILayout.Width(14));
        GUILayout.EndHorizontal();

        // Countdown label — only shown when auto-cycling is ON.
        // No placeholder needed when OFF: the ON/OFF button consumes the
        // click event, so removing this row cannot misdeliver a click to
        // the scroll view below.
        if (_hullcamAutoActive)
        {
            float hcEffective = Mathf.Max(HULLCAM_MIN_INTERVAL, _hullcamInterval);
            float hcRemaining = Mathf.Max(0f, hcEffective - (Time.realtimeSinceStartup - _hullcamLastSwitchRealtime));
            GUILayout.Label("Next camera in: " + hcRemaining.ToString("F0") + "s",
                GUILayout.Width(200));
        }

        // Camera pick list (scrollable)
        _hullcamScrollPos = GUILayout.BeginScrollView(_hullcamScrollPos, GUILayout.Height(110f));

        // External camera entry
        bool newExternal = GUILayout.Toggle(_hullcamIncludeExternal, "  External Camera");
        if (newExternal != _hullcamIncludeExternal)
        {
            if (VERBOSE_DIAGNOSTICS) Debug.Log("[FastVesselChanger] External Camera toggle changed: " + _hullcamIncludeExternal + " → " + newExternal);
            _hullcamIncludeExternal = newExternal;
            RebuildHullCamRotation(hcVessel);
            SyncCurrentHullcamStateToDict();
            SaveToScenario();
        }

        // Individual hull camera entries — enabled first, disabled sorted to bottom
        var sortedCams = vesselCams.OrderBy(c => c.disabled ? 1 : 0).ToList();
        foreach (var hce in sortedCams)
        {
            if (hce.disabled)
            {
                // Show disabled cameras grayed-out at the bottom but DO NOT remove
                // them from _hullcamSelectedIds.  RebuildHullCamRotation() already
                // skips disabled cameras regardless of checked state, so keeping
                // the selection preserves the user's choice: when the camera becomes
                // enabled again (e.g. after a vessel switch reload) it is immediately
                // included in the rotation without manual re-checking.
                GUI.enabled = false;
                GUILayout.Toggle(false, "  " + GetHullCamDisplayName(hce.part, hce.module) + "  [DISABLED]");
                GUI.enabled = true;
            }
            else
            {
                bool camSel = _hullcamSelectedIds.Contains(hce.part.flightID);
                bool newCamSel = GUILayout.Toggle(camSel, "  " + GetHullCamDisplayName(hce.part, hce.module));
                if (newCamSel != camSel)
                {
                    if (newCamSel) _hullcamSelectedIds.Add(hce.part.flightID);
                    else           _hullcamSelectedIds.Remove(hce.part.flightID);
                    if (VERBOSE_DIAGNOSTICS) Debug.Log("[FastVesselChanger] HullCam checkbox: '" + GetHullCamDisplayName(hce.part, hce.module)
                        + "' flightID=" + hce.part.flightID + " selected=" + newCamSel
                        + " (_hullcamSelectedIds.Count now=" + _hullcamSelectedIds.Count + ")");
                    RebuildHullCamRotation(hcVessel);
                    SyncCurrentHullcamStateToDict();
                    SaveToScenario();
                }
            }
        }

        GUILayout.EndScrollView();
        GUILayout.EndVertical();
    }
}
