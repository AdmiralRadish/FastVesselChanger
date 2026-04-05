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

// FVCLunaHelper is defined in FVCLunaHelper.cs
// FastVesselChangerScenario and FVCPersistenceHelpers are defined in FVCScenario.cs
// HullcamVDS integration (fields, types, methods) is in FVCHullcam.cs


[KSPAddon(KSPAddon.Startup.Flight, false)]
public partial class FastVesselChanger : MonoBehaviour
{
    private static FastVesselChanger _activeInstance;
    private const string USER_PREFS_FILE_NAME = "FastVesselChanger.xml";

    // Minimum time (seconds) between any two switches to prevent rapid switching
    private const double MINIMUM_SWITCH_INTERVAL = 5.0;

    private Rect windowRect = new Rect(20, 80, 400, 500);
    private Vector2 scrollPos = Vector2.zero;

    private const float BASE_SCROLL_HEIGHT = 350f;
    private const float FIXED_WINDOW_WIDTH = 400f;
    private const float MIN_WINDOW_HEIGHT = 240f;
    private bool showWindow = true;
    private bool showTypeFilter = false; // Toggle for vessel type filter section
    private bool lastShowTypeFilter = false; // Track previous state to detect changes
    private bool showCameraControls = true; // Toggle for camera controls section
    private bool lastShowCameraControls = false; // Track previous state to detect changes
    private bool _lastShowPresets = false; // Track preset section state for height recalc
    private string vesselSearchText = ""; // Live search filter for vessel list

    // Lazy-initialized GUIStyle for current vessel label (bold + slightly larger)
    private GUIStyle _currentVesselStyle = null;

    private Dictionary<Guid, bool> selected = new Dictionary<Guid, bool>();
    private Dictionary<string, bool> vesselTypeFilter = new Dictionary<string, bool>(); // Vessel type filtering
    private int switchInterval = 240; // default to 4 minutes
    private string switchIntervalText = "240"; // Text buffer for the input field to avoid getting stuck
    private int pendingInterval = -1; // interval typed by user but deferred (would fire immediately)
    private bool autoEnabled = false; // default to disabled
    private double lastSwitchTime = 0.0; // universal time of last switch (any switch)
    private Guid lastSwitchedVesselId = Guid.Empty; // Track the last switched vessel to avoid re-switching

    // Flight UI state tracking
    private bool userPreferredUIVisible = true; // Kept in sync with KSP's onShowUI/onHideUI events (F2)
    private static bool _staticUserPreferredUIVisible = true; // Survives FLIGHT→FLIGHT scene reloads

    // Camera auto-rotation
    private bool cameraRotEnabled = false;
    private bool cameraRotRandomEnabled = false; // randomize X/Y rates on each vessel switch
    private float cameraRotXRate = 0f;   // pitch deg/s (positive = up)
    private float cameraRotYRate = 0f;   // orbit deg/s (positive = right)
    private string cameraRotXText = "0";
    private string cameraRotYText = "0";
    private const float EXPANDED_MIN_PITCH = -100000f;
    private const float EXPANDED_MAX_PITCH = 100000f;
    private const float CAMERA_LOWER_POLE = -1.5707964f;
    private const float CAMERA_POLE_WRAP_THRESHOLD = 0.0005f;
    // Grounded vessels can collide/pivot before reaching the true lower pole; use earlier handoff.
    private const float CAMERA_LOWER_HANDOFF_GROUNDED = -0.10f;
    private bool _downGroundBypassLatched = false;

    // Widened pitch limits — cached originals restored when leaving flight
    private bool _pitchLimitsWidened = false;
    private float _origMinPitch = float.NaN;
    private float _origMaxPitch = float.NaN;

    // Static: survives addon destruction/recreation during vessel switches.
    // Used to carry randomized rates past scenario reloads (which restore on-disk values).
    private static bool _pendingRandomRates = false;
    private static float _pendingRandomX = 0f;
    private static float _pendingRandomY = 0f;

    // Shuffle bag IDs loaded from XML, resolved against cycleList in RestoreShuffleBag
    private static List<string> _loadedShuffleBagIds = new List<string>();

    // Per-vessel auto-switch intervals — keyed by vessel ID, survives vessel switches within a session
    private static Dictionary<Guid, int> _vesselSwitchIntervals = new Dictionary<Guid, int>();

    // Per-vessel zoom levels — keyed by vessel ID, survives vessel switches within a session
    private static Dictionary<Guid, float> _vesselZooms = new Dictionary<Guid, float>();
    // Pending zoom — applied every end-of-frame for multiple frames to override deferred
    // FlightCamera.SetTarget() calls that fire after onFlightReady.
    private static Guid _pendingZoomVesselId = Guid.Empty;
    private static float _pendingZoom = 0f;
    private static bool _pendingZoomRestore = false;
    private static int _pendingZoomFramesRemaining = 0;
    private const int ZOOM_RESTORE_FRAMES = 35; // ~0.58s at 60fps (reduced from 45 to reduce zoom lockout duration)
    private static FieldInfo _camDistField = null;  // cached reflection handle for FlightCamera.distance
    private static bool _camDistFieldSearched = false; // true once we've attempted the lookup

    // Per-vessel camera aim target — flightID of the Part the camera orbits around
    private static Dictionary<Guid, uint> _vesselCameraTargets = new Dictionary<Guid, uint>();
    private static uint _pendingCameraTarget = 0;
    private static Guid _pendingCameraTargetVesselId = Guid.Empty;

    // Per-vessel "Control From Here" reference — flightID of the control reference Part
    // Uses its own frame counter (longer than zoom) because LMP continuously syncs
    // referenceTransformId from the server, so the override must persist longer.
    private static Dictionary<Guid, uint> _vesselControlFromHere = new Dictionary<Guid, uint>();
    private static uint _pendingControlFromHere = 0;
    private static Guid _pendingControlFromHereVesselId = Guid.Empty;
    private static int _pendingControlFromHereFrames = 0;
    private const int CONTROL_RESTORE_FRAMES = 180; // ~3s at 60fps — longer than zoom to outlast LMP syncs

    /// <summary>
    /// Properly applies "Control From Here" for any part type.
    /// For docking ports, calls ModuleDockingNode.MakeReferenceTransform() so the
    /// Part's reference direction is set to the docking axis (controlTransform)
    /// before the vessel-level reference is updated.  Without this, the navball
    /// uses the Part's default orientation instead of the docking direction.
    /// For all other parts (command modules, etc.), falls back to
    /// vessel.SetReferenceTransform(part) which is sufficient.
    /// </summary>
    private static void ApplyControlFromHere(Vessel vessel, Part controlPart)
    {
        var dockingNode = controlPart.FindModuleImplementing<ModuleDockingNode>();
        if (dockingNode != null)
            dockingNode.MakeReferenceTransform();
        else
            vessel.SetReferenceTransform(controlPart);
    }

    // Hullcam blackout: draws a full-screen black overlay between scene start and
    // hullcam activation to hide the stock FlightCamera flash on vessels that have
    // hull cam auto-cycling enabled.
    private static bool _pendingHullcamBlackout = false;
    private static float _hullcamBlackoutDeadline = 0f; // safety timeout (realtime)
    private static Texture2D _blackTex = null;

    // Post-switch health monitor: logs when a vessel switch was initiated
    // but the game never reached a healthy state.  Informational only — does
    // NOT take recovery actions (no LoadScene, no force-quit).
    private static float _switchWatchdogRealtime = 0f;
    private static Guid _switchWatchdogTargetId = Guid.Empty;
    private static bool _switchWatchdogFlightReady = true; // true once GameEvents.onFlightReady fires after a switch
    private const float SWITCH_WATCHDOG_TIMEOUT = 30f;

    // Cached reflection handles for UIMasterController (the class that actually hides/shows KSP's HUD).
    // _uiMasterInstance is resolved lazily on first use because UIMasterController.Instance is not
    // assigned yet when Start() runs — KSP initialises it after addon startup.
    private PropertyInfo _uiInstanceProp = null;
    private object _uiMasterInstance = null;
    private MethodInfo _uiHideMethod = null;
    private MethodInfo _uiShowMethod = null;
    private FieldInfo _uiQuickHideField = null;  // KSP's internal F2 toggle field (found via enumeration)
    // Guard: prevents onShowUI/onHideUI callbacks from double-flipping
    // userPreferredUIVisible when our button/F2 handler calls InvokeHideUI/ShowUI.
    private bool _uiToggleGuard = false;

    private List<Vessel> cycleList = new List<Vessel>();      // full selected vessel list
    private List<Vessel> shuffleRemaining = new List<Vessel>(); // vessels not yet visited this round
    private object _appButton = null;
    private static object _sharedAppButton = null;
    private bool _isAddingAppButton = false;
    // Cache for app button SetTrue/SetFalse methods — discovered once per session
    private static MethodInfo _appButtonSetTrueMethod  = null;
    private static MethodInfo _appButtonSetFalseMethod = null;
    private static bool _appButtonMethodsSearched = false;
    private Coroutine _retryButtonCoroutine = null;
    private Coroutine _cameraPitchOverrideCoroutine = null;

    // Twitch overlay file writer
    private Coroutine _twitchFileWriterCoroutine = null;
    private bool _twitchWriterStartupLogged = false;
    private bool _writeLMPPlayersLog = false;  // persist to XML user prefs
    private bool _writeVesselLog = false;      // persist to XML user prefs
    private bool _writeCameraLog = false;       // persist to XML user prefs
    private static readonly bool VERBOSE_DIAGNOSTICS = false;
    private static float _sceneLoadGraceRealtime = 0f; // block phantom GUI clicks for 2s after onFlightReady
    private static float _uiHideDeadlineRealtime = 0f;  // realtime deadline for per-frame hide re-assertions
    private const float UI_HIDE_DURATION = 5f; // seconds of aggressive re-hiding after scene load
    private const string TWITCH_PLAYERS_FILE = "players_online.txt";
    private const string TWITCH_VESSEL_FILE = "current_vessel.txt";
    private const string TWITCH_CAMERA_FILE = "CURRENT_CAMERA.txt";
    private const float TWITCH_WRITE_INTERVAL = 15f;

    // Guard against multiple switches in the same frame
    private int lastFrameCount = -1;

    // Flight-ready gate: Update(), OnGUI(), and coroutines are passive until this is true.
    // Set by OnFlightReady() once the flight scene is fully initialized.
    private bool _flightReady = false;

    // Set in OnDestroy to prevent SaveToScenario from snapshotting stale camera
    // state during scene teardown.  SwitchToVessel already captured correct values.
    private bool _isDestroying = false;

    // Diagnostic heartbeat — logs periodically so we can tell if the main thread is alive
    private float _lastHeartbeatRealtime = 0f;
    private const float HEARTBEAT_INTERVAL = 5f;

    // Cached sorted vessel list for OnGUI — rebuilt only when vessel count changes
    private List<Vessel> _cachedSortedVessels = new List<Vessel>();
    private int _cachedVesselCount = -1;

    // HullcamVDS fields are declared in FastVesselChanger.Hullcam.cs (partial class)

    void Awake()
    {
        Debug.Log("[FastVesselChanger] Awake() enter frame=" + Time.frameCount + " t=" + Time.realtimeSinceStartup.ToString("F3"));
        try
        {
            if (_activeInstance != null && _activeInstance != this)
            {
                Debug.LogWarning("[FastVesselChanger] Duplicate Flight addon instance detected; destroying duplicate.");
                Destroy(this);
                return;
            }

            _activeInstance = this;

            // Restore UI preference from static (survives FLIGHT→FLIGHT scene reloads)
            userPreferredUIVisible = _staticUserPreferredUIVisible;

            // Cache UIMasterController reflection handles early so InvokeHideUI works before Start()
            try { CacheUIMasterController(); }
            catch (Exception e) { Debug.LogWarning("[FastVesselChanger] CacheUIMasterController in Awake failed: " + e.Message); }

            // Load just the UIVisible preference from disk.  Critical for Space Center → Flight
            // transitions where _staticUserPreferredUIVisible is stale (true) but the saved
            // pref on disk is false.  Must run BEFORE the hide check below.
            LoadEarlyUIPreference();

            // Hide the HUD immediately in Awake() — before any frames render — to prevent
            // the visible "flash" that occurs when the UI is briefly shown during scene load.
            if (!userPreferredUIVisible)
            {
                _uiHideDeadlineRealtime = Time.realtimeSinceStartup + UI_HIDE_DURATION;
                try { InvokeHideUI(); }
                catch (Exception e) { Debug.LogWarning("[FastVesselChanger] Awake early HideUI failed: " + e.Message); }
            }

            // ALL event registration deferred to Start() for safety.
            // GameEvents may not be fully initialised during Awake() in heavily-modded installs.

            Debug.Log("[FastVesselChanger] Awake() complete frame=" + Time.frameCount + " t=" + Time.realtimeSinceStartup.ToString("F3"));
        }
        catch (Exception e)
        {
            Debug.LogError("[FastVesselChanger] Awake() FAILED: " + e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace);
        }
    }

    void VerboseLog(string message, bool warning = false)
    {
        if (VERBOSE_DIAGNOSTICS)
        {
            if (warning)
                Debug.LogWarning(message);
            else
                Debug.Log(message);
        }
    }



    void Start()
    {
        // Awake() may have called Destroy(this) if a duplicate was detected, but Unity still
        // invokes Start() on the same frame (destruction is deferred to end-of-frame).
        if (_activeInstance != this)
        {
            Debug.LogWarning("[FastVesselSwitcher] Start() called on non-active duplicate instance; aborting.");
            return;
        }

        Debug.Log("[FastVesselChanger] Start() enter frame=" + Time.frameCount + " t=" + Time.realtimeSinceStartup.ToString("F3"));

        // Register ONLY onFlightReady.  ALL other initialization (event handlers, coroutines,
        // AppLauncher button, assembly scanning, file I/O) is deferred until the flight scene
        // is fully ready.  This keeps FVC completely passive during the init phase so it cannot
        // interfere with other mods' startup sequences.
        try { GameEvents.onFlightReady.Add(OnFlightReady); }
        catch (Exception e) { Debug.LogError("[FastVesselChanger] Failed to register onFlightReady: " + e.Message); }

        // Register UI visibility callbacks early so we catch onShowUI events
        // during the entire scene load, not just after OnFlightReady.
        try { GameEvents.onShowUI.Add(OnShowUI_Instance); } catch (Exception e) { Debug.LogError("[FastVesselChanger] Failed to register onShowUI in Start: " + e.Message); }
        try { GameEvents.onHideUI.Add(OnHideUI_Instance); } catch (Exception e) { Debug.LogError("[FastVesselChanger] Failed to register onHideUI in Start: " + e.Message); }

        // Reset UI-hide deadline — extends coverage from Awake's initial set.
        // LateUpdate() handles the actual per-frame InvokeHideUI.
        if (!userPreferredUIVisible)
            _uiHideDeadlineRealtime = Time.realtimeSinceStartup + UI_HIDE_DURATION;

        // Reset watchdog timer if still armed from a previous switch
        if (_switchWatchdogRealtime > 0f && _switchWatchdogTargetId != Guid.Empty)
        {
            _switchWatchdogRealtime = Time.realtimeSinceStartup;
            Debug.Log("[FastVesselChanger] Watchdog still armed — reset timer to scene start");
        }

        Debug.Log("[FastVesselChanger] started — Start() complete (waiting for onFlightReady) frame=" + Time.frameCount + " t=" + Time.realtimeSinceStartup.ToString("F3"));
    }

    void CacheUIMasterController()
    {
        try
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                // KSP's UIMasterController lives in the KSP.UI namespace (Assembly-CSharp).
                // Assembly.GetType requires the fully-qualified name.
                var t = a.GetType("KSP.UI.UIMasterController");
                if (t == null) continue;

                _uiInstanceProp = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                _uiHideMethod = t.GetMethod("HideUI", BindingFlags.Public | BindingFlags.Instance);
                _uiShowMethod = t.GetMethod("ShowUI", BindingFlags.Public | BindingFlags.Instance);

                // F2 toggles a separate boolean inside UIMasterController that
                // HideUI/ShowUI do NOT update.  We must find and sync this field
                // so F2 stays in phase after our button calls HideUI/ShowUI.
                // Enumerate ALL boolean fields and try known name patterns.
                var instF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                string[] quickHideNames = {
                    "isUIShowing",
                    "quickHide", "uiQuickHideActive", "_quickHideActive",
                    "quickHideActive", "_quickHide", "bQuickHide",
                    "isQuickHiding", "UIQuickHide", "_uiQuickHide",
                    "uiHidden", "_uiHidden"
                };
                foreach (var name in quickHideNames)
                {
                    _uiQuickHideField = t.GetField(name, instF);
                    if (_uiQuickHideField != null) break;
                }
                // Log all bool fields so we can identify the correct one if
                // none of the known names matched.
                var allBoolFields = t.GetFields(instF);
                var boolFieldNames = new System.Text.StringBuilder();
                foreach (var f in allBoolFields)
                {
                    if (f.FieldType == typeof(bool))
                        boolFieldNames.Append(f.Name).Append(", ");
                }
                Debug.Log("[FastVesselChanger] UIMasterController type found:"
                    + " HideUI=" + (_uiHideMethod != null)
                    + ", ShowUI=" + (_uiShowMethod != null)
                    + ", quickHideField=" + (_uiQuickHideField?.Name ?? "NOT FOUND")
                    + ", allBoolFields=[" + boolFieldNames.ToString().TrimEnd(',', ' ') + "]");
                break;
            }
            if (_uiInstanceProp == null)
                Debug.LogWarning("[FastVesselChanger] UIMasterController type not found — falling back to event firing");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[FastVesselChanger] CacheUIMasterController failed: " + e.Message);
        }
    }

    // Resolves UIMasterController.Instance on first successful call and caches it.
    // Called lazily rather than at Start() because KSP assigns the instance after addon startup.
    object GetUIMasterInstance()
    {
        if (_uiMasterInstance != null) return _uiMasterInstance;
        if (_uiInstanceProp == null) return null;
        try { _uiMasterInstance = _uiInstanceProp.GetValue(null); }
        catch { }
        return _uiMasterInstance;
    }

    // Calls UIMasterController.HideUI() — this is what KSP's F2 handler uses internally.
    // UIMasterController.HideUI() fires GameEvents.onHideUI internally, so we must NOT
    // also fire the event — double-firing causes ManeuverTool's AppUIInputPanel.RefreshUI()
    // to process twice per call, amplifying a stock KSP NullRef bug.
    // We only fire the event directly as a fallback when UIMasterController is unavailable.
    void InvokeHideUI()
    {
        var inst = GetUIMasterInstance();
        if (inst != null && _uiHideMethod != null)
        {
            try { _uiHideMethod.Invoke(inst, null); return; }
            catch (Exception e) { Debug.LogWarning("[FastVesselChanger] UIMasterController.HideUI() failed: " + e.Message); }
        }
        // Fallback: fire the event directly when UIMasterController is not available
        GameEvents.onHideUI.Fire();
    }

    // Calls UIMasterController.ShowUI() — same reasoning as InvokeHideUI.
    void InvokeShowUI()
    {
        var inst = GetUIMasterInstance();
        if (inst != null && _uiShowMethod != null)
        {
            try { _uiShowMethod.Invoke(inst, null); return; }
            catch (Exception e) { Debug.LogWarning("[FastVesselChanger] UIMasterController.ShowUI() failed: " + e.Message); }
        }
        GameEvents.onShowUI.Fire();
    }

    // Per-instance onShowUI handler — registered in Start(), removed in OnDestroy.
    void OnShowUI_Instance()
    {
        if (!HighLogic.LoadedSceneIsFlight)
            return;

        // Guard: skip if our own button/F2 handler triggered this event.
        // The handler updates userPreferredUIVisible directly.
        if (_uiToggleGuard)
            return;

        // During aggressive scene-load re-hide, suppress KSP's deferred UI init.
        // Don't update userPreferredUIVisible — keep the user's "hidden" preference.
        if (!userPreferredUIVisible && Time.realtimeSinceStartup < _uiHideDeadlineRealtime)
        {
            if (VERBOSE_DIAGNOSTICS) Debug.Log("[FastVesselChanger] onShowUI fired during re-hide window — suppressing");
            InvokeHideUI();
            return;
        }

        userPreferredUIVisible = true;
        _staticUserPreferredUIVisible = true;
    }

    // Per-instance onHideUI handler.
    void OnHideUI_Instance()
    {
        if (!HighLogic.LoadedSceneIsFlight)
            return;

        // Guard: skip if our own button/F2 handler triggered this event.
        if (_uiToggleGuard)
            return;

        userPreferredUIVisible = false;
        _staticUserPreferredUIVisible = false;
    }

    void OnVesselLoaded(Vessel _)
    {
        if (!userPreferredUIVisible)
            InvokeHideUI();
    }

    void OnFlightReady()
    {
        Debug.Log("[FastVesselChanger] onFlightReady ENTER — beginning deferred init");
        try
        {
            _flightReady = true;
            _switchWatchdogFlightReady = true;

            // --- Everything below was deferred from Start() to keep FVC completely ---
            // --- passive during the init phase.                                    ---

            // Step 1: Register event handlers
            // onShowUI/onHideUI are registered earlier in Start() for full scene-load coverage.
            Debug.Log("[FastVesselChanger] onFlightReady step 1: event handlers");
            try { GameEvents.onVesselLoaded.Add(OnVesselLoaded); } catch (Exception e) { Debug.LogError("[FastVesselChanger] Failed to register onVesselLoaded: " + e.Message); }

            // Step 2: Initialize timing and multiplayer
            Debug.Log("[FastVesselChanger] onFlightReady step 2: timing + multiplayer");
            lastSwitchTime = Planetarium.GetUniversalTime();
            if (FVCLunaHelper.IsLunaEnabled)
            {
                string playerName = FVCLunaHelper.GetCurrentPlayerName();
                Debug.Log("[FastVesselChanger] LunaMultiplayer player: " + playerName);
            }
            else
            {
                Debug.Log("[FastVesselChanger] Single-player mode");
            }

            // Step 3: Filter, vessel, and user prefs init
            Debug.Log("[FastVesselChanger] onFlightReady step 3: filters + prefs");
            InitializeVesselTypeFilter();
            RefreshSelectionsFromVessels();
            LoadUserPrefs();

            // Step 4: (removed — LoadUserPrefs in step 3 now loads all per-player simulation state)
            Debug.Log("[FastVesselChanger] onFlightReady step 4: (no-op, data loaded in step 3)");

            // Step 5: Cycle list + shuffle bag
            Debug.Log("[FastVesselChanger] onFlightReady step 5: cycle list");
            BuildCycleList(resetShuffleBag: false);
            RestoreShuffleBagFromLoadedIds();

            if (_pendingRandomRates)
            {
                cameraRotXRate = _pendingRandomX;
                cameraRotYRate = _pendingRandomY;
                cameraRotXText = cameraRotXRate.ToString("F2");
                cameraRotYText = cameraRotYRate.ToString("F2");
                _pendingRandomRates = false;
                SaveToScenario();
            }

            // Step 6: HullcamVDS detection + restore
            Debug.Log("[FastVesselChanger] onFlightReady step 6: hullcam");
            DetectHullcamVDS();
            var hullcamActiveVessel = FlightGlobals.ActiveVessel;
            _instanceVesselId = hullcamActiveVessel?.id ?? Guid.Empty;
            if (hullcamActiveVessel != null && _hullcamInstalled)
                ApplyVesselHullcamSettings(hullcamActiveVessel);

            // Activate hullcam immediately after setup — the scene is fully ready at
            // this point (onFlightReady) so parts are loaded.  Activating here instead
            // of step 11 eliminates the visible "flash" of the regular flight camera
            // before the hullcam takes over.
            bool hullcamActivatedEarly = false;
            if (_hullcamInstalled)
            {
                ActivateHullcamIfReady();
                hullcamActivatedEarly = _hullcamLastActivatedModule != null;
            }

            // Initialize per-vessel switch interval text for the current vessel
            if (hullcamActiveVessel != null)
            {
                int pvInterval;
                if (_vesselSwitchIntervals.TryGetValue(hullcamActiveVessel.id, out pvInterval))
                {
                    switchInterval = pvInterval;
                    switchIntervalText = pvInterval.ToString();
                }
            }

            // Restore "Control From Here" for the active vessel.
            // Also arm the frame-based re-application even for initial flight loads
            // (not just FVC switches) so deferred KSP/LMP resets are overridden.
            if (hullcamActiveVessel != null)
            {
                uint controlFlightId;
                if (_vesselControlFromHere.TryGetValue(hullcamActiveVessel.id, out controlFlightId) && controlFlightId != 0)
                {
                    Part controlPart = hullcamActiveVessel.parts?.FirstOrDefault(p => p.flightID == controlFlightId);
                    if (controlPart != null)
                    {
                        ApplyControlFromHere(hullcamActiveVessel, controlPart);
                        Debug.Log("[FastVesselChanger] Restored Control From Here: " + controlPart.partInfo.title + " (flightID=" + controlFlightId + ") post-set refTransformId=" + hullcamActiveVessel.referenceTransformId);

                        // Arm re-application if not already armed by SwitchToVessel
                        if (_pendingControlFromHereFrames <= 0)
                        {
                            _pendingControlFromHere = controlFlightId;
                            _pendingControlFromHereVesselId = hullcamActiveVessel.id;
                            _pendingControlFromHereFrames = CONTROL_RESTORE_FRAMES;
                            Debug.Log("[FastVesselChanger] Armed CFH re-application for initial load: " + CONTROL_RESTORE_FRAMES + " frames");
                        }
                    }
                }

                // Arm camera aim + zoom for initial load (SwitchToVessel sets
                // these for FVC-triggered switches, but a fresh server join /
                // scene load doesn't go through SwitchToVessel).
                if (!_pendingZoomRestore)
                {
                    float savedZoom;
                    if (_vesselZooms.TryGetValue(hullcamActiveVessel.id, out savedZoom))
                    {
                        _pendingZoomVesselId = hullcamActiveVessel.id;
                        _pendingZoom = savedZoom;
                        _pendingZoomRestore = true;
                        _pendingZoomFramesRemaining = ZOOM_RESTORE_FRAMES;
                    }
                }
                if (_pendingCameraTarget == 0)
                {
                    uint savedCamTarget;
                    if (_vesselCameraTargets.TryGetValue(hullcamActiveVessel.id, out savedCamTarget) && savedCamTarget != 0)
                    {
                        _pendingCameraTarget = savedCamTarget;
                        _pendingCameraTargetVesselId = hullcamActiveVessel.id;
                    }
                }
            }

            // Step 7: UI state sync
            Debug.Log("[FastVesselChanger] onFlightReady step 7: UI state");
            _staticUserPreferredUIVisible = userPreferredUIVisible;
            if (!userPreferredUIVisible)
            {
                InvokeHideUI();
                // Reset the realtime deadline: KSP's own deferred UI setup can
                // re-show the HUD after our initial hide.  LateUpdate will keep
                // calling InvokeHideUI() every frame until the deadline expires.
                _uiHideDeadlineRealtime = Time.realtimeSinceStartup + UI_HIDE_DURATION;
            }
            else
            {
                _uiHideDeadlineRealtime = 0f;
            }

            // Step 7b: Early camera aim + zoom — apply synchronously before any
            // LateUpdate runs.  Camera aim FIRST (since SetTargetPart resets distance),
            // then zoom.  The end-of-frame coroutine re-applies all three (aim, zoom,
            // control-from-here) every frame for ZOOM_RESTORE_FRAMES frames to fight
            // KSP's deferred scene init.
            // Skip when a hullcam was just activated — hullcam has its own camera,
            // and forcing flight camera zoom would interfere.
            if (!hullcamActivatedEarly)
            {
                var cam = FlightCamera.fetch;
                if (cam != null)
                {
                    if (!_camDistFieldSearched)
                    {
                        _camDistFieldSearched = true;
                        _camDistField = cam.GetType().GetField("distance",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    }

                    // Camera aim target first
                    if (_pendingCameraTarget != 0 && _pendingCameraTargetVesselId != Guid.Empty)
                    {
                        var aimVessel = FlightGlobals.ActiveVessel;
                        if (aimVessel != null)
                        {
                            Part targetPart = aimVessel.parts?.FirstOrDefault(p => p.flightID == _pendingCameraTarget);
                            if (targetPart != null)
                            {
                                cam.SetTargetPart(targetPart);
                                Debug.Log("[FastVesselChanger] Early camera aim applied in onFlightReady: " + targetPart.partName + " (flightID=" + _pendingCameraTarget + ")");
                            }
                        }
                    }

                    // Then zoom (SetTargetPart resets distance)
                    if (_pendingZoomRestore && _pendingZoomVesselId != Guid.Empty)
                    {
                        cam.SetDistanceImmediate(_pendingZoom);
                        _camDistField?.SetValue(cam, _pendingZoom);
                        Debug.Log("[FastVesselChanger] Early zoom applied in onFlightReady: " + _pendingZoom);
                    }
                }
            }

            // Step 8: Camera pitch limits
            Debug.Log("[FastVesselChanger] onFlightReady step 8: pitch limits");
            WidenPitchLimits();

            // Step 9: AppLauncher button
            Debug.Log("[FastVesselChanger] onFlightReady step 9: AppLauncher");
            GameEvents.onGUIApplicationLauncherReady.Add(OnGUIAppLauncherReady);
            GameEvents.onGUIApplicationLauncherUnreadifying.Add(OnGUIAppLauncherUnreadifying);
            _retryButtonCoroutine = StartCoroutine(RetryAppLauncherButton());

            // Step 10: Coroutines
            Debug.Log("[FastVesselChanger] onFlightReady step 10: coroutines");
            _cameraPitchOverrideCoroutine = StartCoroutine(ApplyPitchOverridesEndOfFrame());
            _twitchFileWriterCoroutine = StartCoroutine(TwitchFileWriterCoroutine());

            // Step 11: Camera log
            Debug.Log("[FastVesselChanger] onFlightReady step 11: camera log");
            WriteCameraLogFile();

            // Block phantom IMGUI clicks for 2 seconds after scene load.
            _sceneLoadGraceRealtime = Time.realtimeSinceStartup + 2f;

            // Safety fallback: if the blackout overlay is still armed but no hull cam
            // activated (e.g. the vessel has no matching cameras), clear it now so the
            // user doesn't stare at a black screen.
            _pendingHullcamBlackout = false;

            Debug.Log("[FastVesselChanger] onFlightReady COMPLETE — all deferred init done");
        }
        catch (Exception e)
        {
            Debug.LogError("[FastVesselChanger] onFlightReady FAILED: " + e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace);
        }
    }


    void InitializeVesselTypeFilter()
    {
        // Initialize with standard KSP vessel types.
        // Exclude Flag/Debris/SpaceObject/Unknown by default — these are rarely wanted in the cycle list.
        string[] enabledTypes = { "Ship", "Station", "Probe", "Lander", "Rover", "Plane", "Relay" };
        string[] disabledTypes = { "Flag", "Debris", "SpaceObject", "Unknown" };
        foreach (var t in enabledTypes)
            vesselTypeFilter[t] = true;
        foreach (var t in disabledTypes)
            vesselTypeFilter[t] = false;
    }

    void Update()
    {
        // Diagnostic heartbeat — runs even before _flightReady so we can detect main-thread freezes.
        float now = Time.realtimeSinceStartup;
        if (now - _lastHeartbeatRealtime >= HEARTBEAT_INTERVAL)
        {
            _lastHeartbeatRealtime = now;
            if (VERBOSE_DIAGNOSTICS) Debug.Log("[FastVesselChanger] HEARTBEAT frame=" + Time.frameCount
                + " t=" + now.ToString("F1") + "s flightReady=" + _flightReady
                + " scene=" + HighLogic.LoadedScene);
        }

        // Don't run any logic until the flight scene is fully initialized.
        // Running during init can interfere with other mods (Kopernicus, Scatterer,
        // Parallax) that read/write camera state during their setup phase.
        if (!_flightReady)
            return;

        // Enforce widened limits before stock FlightCamera input/clamp runs this frame.
        var camForLimits = FlightCamera.fetch;
        if (camForLimits != null)
        {
            if (!_pitchLimitsWidened)
                WidenPitchLimits();
            else
            {
                camForLimits.minPitch = EXPANDED_MIN_PITCH;
                camForLimits.maxPitch = EXPANDED_MAX_PITCH;
            }

        }

        // F2 is handled entirely by KSP's native UIMasterController.  Our
        // onShowUI / onHideUI callbacks update userPreferredUIVisible when KSP
        // fires them.  The _uiToggleGuard flag prevents the callbacks from
        // double-flipping userPreferredUIVisible when our *button* calls
        // InvokeHideUI/InvokeShowUI (which may fire those same events).

        // Changed hotkey from C to /
        if (Input.GetKeyDown(KeyCode.Slash))
        {
            showWindow = !showWindow;
            SyncAppButtonState(showWindow);  // keep toolbar button in sync with key toggle
            SaveToScenario();
        }

        if (autoEnabled && FlightGlobals.ready)
        {
            double ut = Planetarium.GetUniversalTime();
            // Use per-vessel interval if set, otherwise fall back to global
            int effectiveInterval = switchInterval;
            var av = FlightGlobals.ActiveVessel;
            if (av != null)
            {
                int perVessel;
                if (_vesselSwitchIntervals.TryGetValue(av.id, out perVessel))
                    effectiveInterval = perVessel;
            }
            double minimumAllowedInterval = Math.Max(effectiveInterval, MINIMUM_SWITCH_INTERVAL);
            if (ut - lastSwitchTime >= minimumAllowedInterval)
            {
                SwitchToNext();
                lastSwitchTime = ut;
            }
        }

        // ---- Post-switch health monitor ----
        // The watchdog is only cleared when BOTH conditions are met:
        //   1. The vessel passes basic health checks (loaded, rootPart.transform accessible)
        //   2. GameEvents.onFlightReady has fired (the "all systems started" milestone)
        // Without #2, the watchdog was clearing even when KSP was stuck in a permanent
        // NRE cascade (e.g. FlightGlobals.UpdateInformation) — the vessel looked healthy
        // but the scene never fully loaded, causing a permanent freeze.
        if (_switchWatchdogRealtime > 0f && _switchWatchdogTargetId != Guid.Empty)
        {
            bool vesselHealthy = false;
            try
            {
                var av = FlightGlobals.ActiveVessel;
                if (av != null && FlightGlobals.ready && av.loaded
                    && av.rootPart != null && av.rootPart.transform != null)
                {
                    vesselHealthy = true;
                }
            }
            catch { /* transform access threw — not healthy */ }

            if (vesselHealthy && _switchWatchdogFlightReady)
            {
                if (VERBOSE_DIAGNOSTICS) Debug.Log("[FastVesselChanger] Post-switch health check passed — watchdog cleared");
                _switchWatchdogRealtime = 0f;
                _switchWatchdogTargetId = Guid.Empty;
            }
            else
            {
                float elapsed = Time.realtimeSinceStartup - _switchWatchdogRealtime;

                if (elapsed >= SWITCH_WATCHDOG_TIMEOUT)
                {
                    // Informational only — log the failure but do NOT take recovery actions.
                    Debug.LogError("[FastVesselChanger] Stuck-switch watchdog timeout after "
                        + elapsed.ToString("F0") + "s. vesselHealthy="
                        + vesselHealthy + " flightReady=" + _switchWatchdogFlightReady
                        + ". No automatic recovery will be attempted.");
                    ScreenMessages.PostScreenMessage("FVC: Vessel switch may be stuck (see KSP.log)", 10f, ScreenMessageStyle.UPPER_CENTER);

                    _switchWatchdogRealtime = 0f;
                    _switchWatchdogTargetId = Guid.Empty;
                }
            }
        }

        // (UI re-assertion moved to end-of-frame coroutine for per-frame reliability)

        if (cameraRotEnabled)
        {
            var cam = FlightCamera.fetch;
            if (cam != null)
            {
                if (cameraRotYRate != 0f)
                    cam.camHdg += cameraRotYRate * Mathf.Deg2Rad * Time.deltaTime;
            }
        }

        UpdateHullcam();
    }

    void LateUpdate()
    {
        // Pre-render restore: override any FlightCamera.SetTarget() / reference
        // transform resets that fired during Update/coroutine phase this frame.
        // LateUpdate runs BEFORE the frame is rendered, so the user never sees a
        // frame at the wrong zoom, camera target, or control reference.
        // Frame countdown is managed by ApplyPitchOverridesEndOfFrame; this just applies.
        if (_pendingZoomRestore && _pendingZoomVesselId != Guid.Empty && _pendingZoomFramesRemaining > 0)
        {
            var cam = FlightCamera.fetch;
            if (cam != null)
            {
                var activeVessel = FlightGlobals.ActiveVessel;
                if (activeVessel != null && activeVessel.id == _pendingZoomVesselId)
                {
                    if (!_camDistFieldSearched)
                    {
                        _camDistFieldSearched = true;
                        _camDistField = cam.GetType().GetField("distance",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    }

                    // On the very first restore frame, clamp an unreasonably large saved zoom
                    // to cam.startDistance.  This prevents a vessel loading EXTREMELY zoomed out
                    // when the zoom was captured during scene init before the camera settled.
                    if (_pendingZoomFramesRemaining == ZOOM_RESTORE_FRAMES
                        && cam.startDistance > 0f && _pendingZoom > cam.startDistance * 10f)
                    {
                        Debug.Log(string.Format("[FastVesselChanger] Zoom restore clamped: {0:F0} → {1:F0} (startDist={2:F0})",
                            _pendingZoom, cam.startDistance, cam.startDistance));
                        _pendingZoom = cam.startDistance;
                        _vesselZooms[activeVessel.id] = _pendingZoom; // overwrite the bad saved value
                    }

                    // Camera aim target first (SetTargetPart resets distance)
                    if (_pendingCameraTarget != 0 && _pendingCameraTargetVesselId == activeVessel.id)
                    {
                        Part targetPart = activeVessel.parts?.FirstOrDefault(p => p.flightID == _pendingCameraTarget);
                        if (targetPart != null)
                            cam.SetTargetPart(targetPart);
                    }

                    // Zoom
                    cam.SetDistanceImmediate(_pendingZoom);
                    _camDistField?.SetValue(cam, _pendingZoom);
                }
            }
        }

        // Control From Here — uses its own longer countdown (CONTROL_RESTORE_FRAMES)
        // because LMP continuously syncs referenceTransformId from the server.
        if (_pendingControlFromHere != 0 && _pendingControlFromHereFrames > 0)
        {
            var activeVessel = FlightGlobals.ActiveVessel;
            if (activeVessel != null && activeVessel.id == _pendingControlFromHereVesselId)
            {
                Part controlPart = activeVessel.parts?.FirstOrDefault(p => p.flightID == _pendingControlFromHere);
                if (controlPart != null)
                    ApplyControlFromHere(activeVessel, controlPart);
            }
        }

        // Pre-render UI hide: prevents flicker by hiding the HUD before the frame
        // is drawn.  Uses a realtime deadline instead of frame count so slow/modded
        // systems that take longer to reach OnFlightReady are still covered.
        if (!userPreferredUIVisible && Time.realtimeSinceStartup < _uiHideDeadlineRealtime)
        {
            InvokeHideUI();
        }
    }

    IEnumerator ApplyPitchOverridesEndOfFrame()
    {
        while (true)
        {
            yield return new WaitForEndOfFrame();

            // Don't touch camera until flight scene is fully ready.
            if (!_flightReady) continue;

            var cam = FlightCamera.fetch;
            if (cam == null) continue;

            if (!_pitchLimitsWidened)
                WidenPitchLimits();
            else
            {
                cam.minPitch = EXPANDED_MIN_PITCH;
                cam.maxPitch = EXPANDED_MAX_PITCH;
            }

            // Down-only ground clamp bypass. Keep stock input speed/direction behavior.
            bool wantsPitchDownKey = Input.GetKey(KeyCode.DownArrow);
            bool wantsPitchUpKey = Input.GetKey(KeyCode.UpArrow);
            if (wantsPitchDownKey && !wantsPitchUpKey)
                ApplyDownGroundBypass(cam);
            else
                _downGroundBypassLatched = false;

            if (cameraRotEnabled && cameraRotXRate != 0f)
                cam.camPitch += cameraRotXRate * Mathf.Deg2Rad * Time.deltaTime;

            // Zoom + camera aim restore — applied every end-of-frame for
            // ZOOM_RESTORE_FRAMES frames.  KSP's deferred scene init (FlightCamera.
            // SetTarget()) fires several frames AFTER onFlightReady, resetting both.
            // Re-applying every frame guarantees our saved state overrides KSP's defaults.
            if (_pendingZoomRestore && _pendingZoomVesselId != Guid.Empty && _pendingZoomFramesRemaining > 0)
            {
                var activeVessel = FlightGlobals.ActiveVessel;
                if (activeVessel != null && activeVessel.id == _pendingZoomVesselId)
                {
                    // Cache the backing field once
                    if (!_camDistFieldSearched)
                    {
                        _camDistFieldSearched = true;
                        _camDistField = cam.GetType().GetField("distance",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    }

                    // First-frame clamp (belt-and-suspenders alongside the Update() check).
                    if (_pendingZoomFramesRemaining == ZOOM_RESTORE_FRAMES
                        && cam.startDistance > 0f && _pendingZoom > cam.startDistance * 10f)
                    {
                        _pendingZoom = cam.startDistance;
                        _vesselZooms[activeVessel.id] = _pendingZoom;
                    }

                    // Camera aim target (before zoom, since SetTargetPart resets distance)
                    if (_pendingCameraTarget != 0 && _pendingCameraTargetVesselId == activeVessel.id)
                    {
                        Part targetPart = activeVessel.parts?.FirstOrDefault(p => p.flightID == _pendingCameraTarget);
                        if (targetPart != null)
                            cam.SetTargetPart(targetPart);
                    }

                    // Zoom
                    cam.SetDistanceImmediate(_pendingZoom);
                    _camDistField?.SetValue(cam, _pendingZoom);

                    _pendingZoomFramesRemaining--;
                    if (_pendingZoomFramesRemaining <= 0)
                    {
                        _pendingZoomVesselId = Guid.Empty;
                        _pendingZoomRestore = false;
                        _pendingCameraTarget = 0;
                        _pendingCameraTargetVesselId = Guid.Empty;
                        VerboseLog("[FastVesselChanger] Zoom/aim restore complete after " + ZOOM_RESTORE_FRAMES + " frames: zoom=" + cam.Distance);
                    }
                }
            }

            // Control From Here restore — own countdown (CONTROL_RESTORE_FRAMES)
            // because LMP continuously syncs referenceTransformId from the server,
            // so the override must persist longer than the camera/zoom window.
            if (_pendingControlFromHere != 0 && _pendingControlFromHereFrames > 0)
            {
                var activeVessel = FlightGlobals.ActiveVessel;
                if (activeVessel != null && activeVessel.id == _pendingControlFromHereVesselId)
                {
                    Part controlPart = activeVessel.parts?.FirstOrDefault(p => p.flightID == _pendingControlFromHere);
                    if (controlPart != null)
                    {
                        ApplyControlFromHere(activeVessel, controlPart);
                        if (_pendingControlFromHereFrames == CONTROL_RESTORE_FRAMES)
                            Debug.Log("[FastVesselChanger] CFH coroutine: first-frame apply flightID=" + _pendingControlFromHere + " refTransformId=" + activeVessel.referenceTransformId);
                    }
                }

                _pendingControlFromHereFrames--;
                if (_pendingControlFromHereFrames <= 0)
                {
                    _pendingControlFromHere = 0;
                    _pendingControlFromHereVesselId = Guid.Empty;
                    if (activeVessel != null)
                        Debug.Log("[FastVesselChanger] CFH restore complete after " + CONTROL_RESTORE_FRAMES + " frames, final refTransformId=" + activeVessel.referenceTransformId);
                }
            }

            // (UI hide re-assertion handled by LateUpdate())
        }
    }

    // Reads just the UIVisible preference from the XML file.  Called in Awake() so
    // the hide logic is correct from the very first frame, even when entering flight
    // from the Space Center (where _staticUserPreferredUIVisible may be stale/true
    // while the on-disk preference is false).
    void LoadEarlyUIPreference()
    {
        try
        {
            string prefsPath = GetUserPrefsPath();
            if (!File.Exists(prefsPath)) return;

            var doc = new XmlDocument();
            doc.Load(prefsPath);
            XmlElement root = doc.DocumentElement;
            if (root == null) return;

            string playerKey;
            try { playerKey = FVCLunaHelper.GetCurrentPlayerName(); }
            catch { playerKey = "default"; }

            XmlElement playerSection = null;
            foreach (XmlElement el in root.GetElementsByTagName("Player"))
            {
                if (el.GetAttribute("name") == playerKey) { playerSection = el; break; }
            }
            if (playerSection == null) return;

            userPreferredUIVisible = ParseBool(playerSection["UIVisible"]?.InnerText, true);
            _staticUserPreferredUIVisible = userPreferredUIVisible;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[FastVesselChanger] LoadEarlyUIPreference failed: " + e.Message);
        }
    }

    void ApplyDownGroundBypass(FlightCamera cam)
    {
        if (cam == null)
            return;

        var active = FlightGlobals.ActiveVessel;
        bool isGrounded = active != null &&
            (active.situation == Vessel.Situations.LANDED
             || active.situation == Vessel.Situations.SPLASHED
             || active.situation == Vessel.Situations.PRELAUNCH);

        float handoffBoundary = isGrounded
            ? CAMERA_LOWER_HANDOFF_GROUNDED
            : (CAMERA_LOWER_POLE + CAMERA_POLE_WRAP_THRESHOLD);

        if (cam.camPitch <= handoffBoundary)
        {
            if (_downGroundBypassLatched)
                return;

            cam.camPitch += 2f * Mathf.PI;
            _downGroundBypassLatched = true;
            VerboseLog("[FastVesselChanger] Down bypass wrap applied at pitch=" + handoffBoundary + " current=" + cam.camPitch);
        }
        else if (cam.camPitch > handoffBoundary + 0.35f)
        {
            _downGroundBypassLatched = false;
        }
    }

    void OnGUI()
    {
        // Hullcam blackout overlay — covers the screen while waiting for the hull cam
        // to activate, preventing the jarring flash of the stock FlightCamera.
        // Drawn before the _flightReady check so it covers the loading phase.
        if (_pendingHullcamBlackout)
        {
            if (Time.realtimeSinceStartup > _hullcamBlackoutDeadline)
            {
                _pendingHullcamBlackout = false; // safety timeout
            }
            else
            {
                if (_blackTex == null)
                {
                    _blackTex = new Texture2D(1, 1);
                    _blackTex.SetPixel(0, 0, Color.black);
                    _blackTex.Apply();
                }
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _blackTex);
            }
        }

        if (!_flightReady || !showWindow) return;
        
        // Force window height recalculation when collapsible sections change
        if (showTypeFilter != lastShowTypeFilter || showCameraControls != lastShowCameraControls
            || _showHullcamSection != _lastShowHullcamSection || _showPresets != _lastShowPresets)
        {
            windowRect.height = 0; // Reset height to force GUILayout recalculation
            lastShowTypeFilter = showTypeFilter;
            lastShowCameraControls = showCameraControls;
            _lastShowHullcamSection = _showHullcamSection;
            _lastShowPresets = _showPresets;
        }
        
        var prevRect = windowRect;
        windowRect = GUILayout.Window(GetInstanceID(), windowRect, DrawWindow, "Fast Vessel Changer",
            GUILayout.Width(FIXED_WINDOW_WIDTH));
        if (windowRect.x != prevRect.x || windowRect.y != prevRect.y)
            SaveUserPrefs();
    }

    void DrawWindow(int id)
    {
        // Lazy-init styled label for current vessel display
        if (_currentVesselStyle == null)
        {
            _currentVesselStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
        }

        GUILayout.BeginVertical();

        // ---- Vessel List ----
        int selectedCount = 0;
        foreach (bool sel in selected.Values)
            if (sel) selectedCount++;
        GUILayout.BeginHorizontal();
        GUILayout.Label("Vessels (" + selectedCount + " selected):");
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Presets", GUILayout.Width(60)))
        {
            _showPresets = !_showPresets;
            if (_showPresets)
            {
                RefreshSelectionsFromVessels();
                RunAllPresetIntegrityChecks();
            }
        }
        if (GUILayout.Button("Refresh", GUILayout.Width(60)))
            RefreshSelectionsFromVessels();
        GUILayout.EndHorizontal();

        // Preset section (collapsible, below header)
        DrawPresetSection();

        // Search bar + filter toggle
        GUILayout.BeginHorizontal();
        GUILayout.Label("Search:", GUILayout.Width(50));
        vesselSearchText = GUILayout.TextField(vesselSearchText, GUILayout.ExpandWidth(true));
        if (!string.IsNullOrEmpty(vesselSearchText) && GUILayout.Button("X", GUILayout.Width(24)))
            vesselSearchText = "";
        if (GUILayout.Button((showTypeFilter ? "[-]" : "[+]") + "Filter", GUILayout.Width(62)))
        {
            showTypeFilter = !showTypeFilter;
            SaveUserPrefs();
        }
        GUILayout.EndHorizontal();

        if (showTypeFilter)
        {
            GUILayout.BeginVertical("box");
            var typeKeys = vesselTypeFilter.Keys.ToList();
            const int FILTER_COLS = 3;
            for (int i = 0; i < typeKeys.Count; i += FILTER_COLS)
            {
                GUILayout.BeginHorizontal();
                for (int j = i; j < Mathf.Min(i + FILTER_COLS, typeKeys.Count); j++)
                {
                    string typeKey = typeKeys[j];
                    bool prev = vesselTypeFilter[typeKey];
                    bool toggled = GUILayout.Toggle(prev, typeKey, GUILayout.Width(118));
                    if (toggled != prev)
                    {
                        vesselTypeFilter[typeKey] = toggled;
                        SaveUserPrefs();
                        SaveToScenario();
                        RefreshSelectionsFromVessels();
                    }
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();
        }

        scrollPos = GUILayout.BeginScrollView(scrollPos, GUILayout.Height(BASE_SCROLL_HEIGHT));

        // Rebuild cached sorted vessel list only when vessel count changes
        int currentVesselCount = FlightGlobals.Vessels.Count;
        if (currentVesselCount != _cachedVesselCount)
        {
            _cachedVesselCount = currentVesselCount;
            _cachedSortedVessels.Clear();
            foreach (var sv in FlightGlobals.Vessels)
                if (sv != null) _cachedSortedVessels.Add(sv);
            _cachedSortedVessels.Sort((a, b) => string.Compare(a.vesselName, b.vesselName, StringComparison.OrdinalIgnoreCase));
        }

        bool anyVisible = false;
        foreach (Vessel v in _cachedSortedVessels)
        {
            if (!IsVesselTypeEnabled(v.vesselType.ToString())) continue;
            if (!string.IsNullOrEmpty(vesselSearchText) && v.vesselName.IndexOf(vesselSearchText, StringComparison.OrdinalIgnoreCase) < 0) continue;

            anyVisible = true;
            bool prev = false;
            if (!selected.TryGetValue(v.id, out prev))
            {
                prev = false;
                selected[v.id] = prev;
            }

            GUILayout.BeginHorizontal();
            if (showCameraControls)
            {
                bool toggled = GUILayout.Toggle(prev, "");
                if (toggled != prev)
                {
                    selected[v.id] = toggled;
                    SaveToScenario();
                    BuildCycleList();
                }
            }
            GUILayout.Label(v.vesselName + "  [" + v.vesselType + "]");
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("GO", GUILayout.Width(30)) && FlightGlobals.ready)
            {
                var currentVessel = FlightGlobals.ActiveVessel;
                if (currentVessel != null)
                {
                    var cam = FlightCamera.fetch;
                    if (cam != null && _hullcamLastActivatedModule == null) _vesselZooms[currentVessel.id] = cam.Distance;
                }
                shuffleRemaining.RemoveAll(sv => sv != null && sv.id == v.id);
                SwitchToVessel(v);
                lastSwitchedVesselId = v.id;
                lastSwitchTime = Planetarium.GetUniversalTime();
            }
            GUILayout.EndHorizontal();
        }
        if (!anyVisible)
            GUILayout.Label(string.IsNullOrEmpty(vesselSearchText) ? "No vessels match the active type filter." : "No vessels match \"" + vesselSearchText + "\".");

        GUILayout.EndScrollView();

        // Divider between vessel list and current vessel.
        GUILayout.Space(4);
        GUILayout.Box("", GUILayout.Height(1), GUILayout.ExpandWidth(true));
        GUILayout.Space(4);

        // ---- Current Vessel ----
        {
            var cv = FlightGlobals.ActiveVessel;
            string cvName = cv != null ? cv.vesselName : "—";
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.Label("Now Viewing:  " + cvName, _currentVesselStyle ?? GUI.skin.label);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        // Divider between current vessel and controls.
        GUILayout.Space(4);
        GUILayout.Box("", GUILayout.Height(1), GUILayout.ExpandWidth(true));
        GUILayout.Space(6);

        // ---- Camera Controls ----
        if (GUILayout.Button((showCameraControls ? "[-] " : "[+] ") + "Camera Controls"))
        {
            showCameraControls = !showCameraControls;
            SaveUserPrefs();
        }
        if (showCameraControls)
        {
            GUILayout.BeginVertical("box");

            // ---- Vessel Selection ----
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Select Visible", GUILayout.ExpandWidth(true)))
            {
                foreach (Vessel sv in _cachedSortedVessels)
                {
                    if (!IsVesselTypeEnabled(sv.vesselType.ToString())) continue;
                    if (!string.IsNullOrEmpty(vesselSearchText) && sv.vesselName.IndexOf(vesselSearchText, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    selected[sv.id] = true;
                }
                SaveToScenario();
                BuildCycleList();
            }
            if (GUILayout.Button("Deselect All", GUILayout.ExpandWidth(true)))
            {
                foreach (var key in selected.Keys.ToList())
                    selected[key] = false;
                SaveToScenario();
                BuildCycleList();
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(4);

            // ---- Auto Switch Controls ----
            GUILayout.BeginHorizontal();
            GUILayout.Label("Auto Switch:", GUILayout.Width(80));
            if (GUILayout.Button(autoEnabled ? "ON" : "OFF", GUILayout.Width(40)))
            {
                ToggleAuto();
            }
            GUILayout.Label("Interval:", GUILayout.Width(52));
            switchIntervalText = GUILayout.TextField(switchIntervalText, GUILayout.Width(45));
            if (GUILayout.Button("Next Now", GUILayout.ExpandWidth(true)))
            {
                double ut = Planetarium.GetUniversalTime();
                double timeSinceLastSwitch = ut - lastSwitchTime;
                if (timeSinceLastSwitch >= MINIMUM_SWITCH_INTERVAL)
                {
                    SwitchToNext();
                    lastSwitchTime = ut;
                }
                else
                {
                    double timeRemaining = Math.Round(MINIMUM_SWITCH_INTERVAL - timeSinceLastSwitch, 1);
                    ScreenMessages.PostScreenMessage("Wait " + timeRemaining + " more seconds before switching", 3f, ScreenMessageStyle.UPPER_CENTER);
                }
            }
            int parsed;
            if (int.TryParse(switchIntervalText, out parsed) && parsed > 0)
            {
                double effectiveInterval = Math.Max(switchInterval, MINIMUM_SWITCH_INTERVAL);
                double remaining = Math.Max(0, effectiveInterval - (Planetarium.GetUniversalTime() - lastSwitchTime));
                if (!autoEnabled || parsed >= remaining)
                {
                    switchInterval = parsed;
                    // Store per-vessel override
                    var ivVessel = FlightGlobals.ActiveVessel;
                    if (ivVessel != null)
                        _vesselSwitchIntervals[ivVessel.id] = parsed;
                    pendingInterval = -1;
                }
                else
                {
                    pendingInterval = parsed;
                }
            }
            GUILayout.EndHorizontal();

            // Always render countdown label to maintain stable IMGUI layout;
            // dynamic show/hide causes control-ID shift that produces phantom clicks
            // on adjacent controls (same class of bug as the hullcam countdown).
            if (autoEnabled)
            {
                int displayInterval = switchInterval;
                var curV = FlightGlobals.ActiveVessel;
                if (curV != null)
                {
                    int pvI;
                    if (_vesselSwitchIntervals.TryGetValue(curV.id, out pvI))
                        displayInterval = pvI;
                }
                double effectiveInterval2 = Math.Max(displayInterval, MINIMUM_SWITCH_INTERVAL);
                double remaining2 = Math.Max(0, effectiveInterval2 - (Planetarium.GetUniversalTime() - lastSwitchTime));
                GUILayout.Label("Next switch in: " + remaining2.ToString("F0") + "s");
            }
            else
            {
                GUILayout.Label(" ");
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Flight UI: " + (userPreferredUIVisible ? "VISIBLE" : "HIDDEN"), GUILayout.Width(120));
            if (GUILayout.Button("Toggle Flight UI", GUILayout.Width(120)))
            {
                // Ignore clicks during the post-scene-load grace period to prevent
                // phantom IMGUI button hits from layout-shift during initialisation.
                if (Time.realtimeSinceStartup > _sceneLoadGraceRealtime)
                {
                    // Cancel any aggressive re-hide — user explicitly wants to toggle.
                    _uiHideDeadlineRealtime = 0f;
                    ToggleFlightUI();
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(6);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Auto Rotation:", GUILayout.Width(100));
            if (GUILayout.Button(cameraRotEnabled ? "ON" : "OFF", GUILayout.Width(40)))
            {
                cameraRotEnabled = !cameraRotEnabled;
                SaveToScenario();
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Random rates on switch:", GUILayout.Width(150));
            if (GUILayout.Button(cameraRotRandomEnabled ? "ON" : "OFF", GUILayout.Width(40)))
            {
                cameraRotRandomEnabled = !cameraRotRandomEnabled;
                SaveToScenario();
            }
            GUILayout.EndHorizontal();

            // Pitch (X) row
            GUILayout.BeginHorizontal();
            GUILayout.Label("Pitch (X) deg/s:", GUILayout.Width(105));
            if (GUILayout.Button("-", GUILayout.Width(22)))
            {
                cameraRotXRate = (float)Math.Round(cameraRotXRate - 1f, 1);
                cameraRotXText = cameraRotXRate.ToString("F1");
                SaveToScenario();
            }
            string newXText = GUILayout.TextField(cameraRotXText, GUILayout.Width(50));
            if (newXText != cameraRotXText)
            {
                cameraRotXText = newXText;
                float xParsed;
                if (float.TryParse(cameraRotXText, out xParsed))
                {
                    cameraRotXRate = xParsed;
                    SaveToScenario();
                }
            }
            if (GUILayout.Button("+", GUILayout.Width(22)))
            {
                cameraRotXRate = (float)Math.Round(cameraRotXRate + 1f, 1);
                cameraRotXText = cameraRotXRate.ToString("F1");
                SaveToScenario();
            }
            GUILayout.EndHorizontal();

            // Orbit (Y) row
            GUILayout.BeginHorizontal();
            GUILayout.Label("Orbit (Y) deg/s:", GUILayout.Width(105));
            if (GUILayout.Button("-", GUILayout.Width(22)))
            {
                cameraRotYRate = (float)Math.Round(cameraRotYRate - 1f, 1);
                cameraRotYText = cameraRotYRate.ToString("F1");
                SaveToScenario();
            }
            string newYText = GUILayout.TextField(cameraRotYText, GUILayout.Width(50));
            if (newYText != cameraRotYText)
            {
                cameraRotYText = newYText;
                float yParsed;
                if (float.TryParse(cameraRotYText, out yParsed))
                {
                    cameraRotYRate = yParsed;
                    SaveToScenario();
                }
            }
            if (GUILayout.Button("+", GUILayout.Width(22)))
            {
                cameraRotYRate = (float)Math.Round(cameraRotYRate + 1f, 1);
                cameraRotYText = cameraRotYRate.ToString("F1");
                SaveToScenario();
            }
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
        }

        DrawHullcamSection();

        GUILayout.Space(4);
        GUILayout.Label("Tip: Press '/' to toggle this window.");

        GUILayout.EndVertical();

        GUI.DragWindow(new Rect(0, 0, FIXED_WINDOW_WIDTH, 20));
    }

    bool IsVesselTypeEnabled(string vesselType)
    {
        if (vesselTypeFilter.ContainsKey(vesselType))
            return vesselTypeFilter[vesselType];
        return true; // Default to enabled if type not found
    }

    void ApplyEnabledTypeFilters(IEnumerable<string> enabledTypes)
    {
        foreach (var key in vesselTypeFilter.Keys.ToList())
            vesselTypeFilter[key] = false;

        foreach (var type in enabledTypes)
        {
            if (vesselTypeFilter.ContainsKey(type))
                vesselTypeFilter[type] = true;
        }
    }

    void RefreshSelectionsFromVessels()
    {
        var existing = new HashSet<Guid>();
        foreach (var v in FlightGlobals.Vessels)
        {
            if (v == null) continue;
            existing.Add(v.id);
            if (!selected.ContainsKey(v.id)) selected[v.id] = false;
        }

        var keys = new List<Guid>(selected.Keys);
        foreach (var k in keys)
        {
            if (!existing.Contains(k)) selected.Remove(k);
        }

        try
        {
            var scen = FastVesselChangerScenario.Instance;
            if (scen != null)
            {
                foreach (var id in scen.selectedVesselIds)
                {
                    Guid g;
                    if (Guid.TryParse(id, out g) && existing.Contains(g))
                        selected[g] = true;
                }
            }
        }
        catch { }
    }

    void ToggleAuto()
    {
        autoEnabled = !autoEnabled;
        if (autoEnabled)
        {
            BuildCycleList();
            // Initialize lastSwitchTime so the first switch happens after switchInterval
            lastSwitchTime = Planetarium.GetUniversalTime();
            lastSwitchedVesselId = Guid.Empty; // Reset to force next switch
            Debug.Log("[FastVesselChanger] Auto-switch enabled. Interval: " + switchInterval + "s (minimum: " + MINIMUM_SWITCH_INTERVAL + "s)");
        }
        else
        {
            // Apply any deferred interval change now that auto is off
            if (pendingInterval > 0)
            {
                switchInterval = pendingInterval;
                switchIntervalText = switchInterval.ToString();
                pendingInterval = -1;
            }
            Debug.Log("[FastVesselChanger] Auto-switch disabled.");
        }

        SaveToScenario();
    }

    void BuildCycleList(bool resetShuffleBag = true)
    {
        cycleList.Clear();
        foreach (var v in FlightGlobals.Vessels)
        {
            if (v == null) continue;
            bool included;
            if (selected.TryGetValue(v.id, out included) && included)
                cycleList.Add(v);
        }

        if (resetShuffleBag)
        {
            // Reset the shuffle bag so the next switch starts a fresh random round.
            shuffleRemaining.Clear();
            shuffleRemaining.AddRange(cycleList);
            VerboseLog("[FastVesselChanger] Shuffle bag reset: " + shuffleRemaining.Count + " vessels in round");
        }
    }

    void RestoreShuffleBagFromLoadedIds()
    {
        shuffleRemaining.Clear();

        if (cycleList.Count == 0)
        {
            VerboseLog("[FastVesselChanger] Shuffle bag restore skipped: cycle list is empty", warning: true);
            return;
        }

        if (_loadedShuffleBagIds.Count == 0)
        {
            shuffleRemaining.AddRange(cycleList);
            VerboseLog("[FastVesselChanger] Shuffle bag restore: no persisted remainder, starting fresh round (" + shuffleRemaining.Count + " vessels)");
            return;
        }

        var cycleById = cycleList.ToDictionary(v => v.id, v => v);
        foreach (var id in _loadedShuffleBagIds)
        {
            Guid vesselId;
            if (!Guid.TryParse(id, out vesselId))
                continue;

            Vessel vessel;
            if (cycleById.TryGetValue(vesselId, out vessel) && !shuffleRemaining.Any(existing => existing.id == vesselId))
                shuffleRemaining.Add(vessel);
        }

        if (shuffleRemaining.Count == 0)
        {
            VerboseLog("[FastVesselChanger] Shuffle bag restore: persisted remainder was empty or stale, next switch will refill from cycle list");
        }
        else
        {
            VerboseLog("[FastVesselChanger] Shuffle bag restored: remaining=" + shuffleRemaining.Count + " of " + cycleList.Count);
        }
    }

    void SwitchToNext()
    {
        // Apply any deferred interval change now that a switch is happening
        if (pendingInterval > 0)
        {
            switchInterval = pendingInterval;
            switchIntervalText = switchInterval.ToString();
            pendingInterval = -1;
        }

        // Prevent multiple switches in the same frame
        if (Time.frameCount == lastFrameCount)
        {
            Debug.LogWarning("[FastVesselChanger] Preventing double-switch in same frame");
            return;
        }
        lastFrameCount = Time.frameCount;

        if (cycleList.Count == 0)
        {
            BuildCycleList();
            if (cycleList.Count == 0)
            {
                Debug.Log("[FastVesselChanger] No selected vessels to cycle.");
                return;
            }
        }

        // Drop any vessels that have since been removed from the game
        shuffleRemaining.RemoveAll(v => v == null);

        // When the bag is empty every vessel has been seen — start a new round
        if (shuffleRemaining.Count == 0)
        {
            shuffleRemaining.AddRange(cycleList.Where(v => v != null));
            if (VERBOSE_DIAGNOSTICS) Debug.Log("[FastVesselChanger] Shuffle bag refilled for new round (" + shuffleRemaining.Count + " vessels)");
        }

        if (shuffleRemaining.Count == 0) return;

        // Prefer not to repeat the currently active vessel if alternatives exist
        var currentActive = FlightGlobals.ActiveVessel;
        List<Vessel> candidates = shuffleRemaining;
        if (shuffleRemaining.Count > 1 && currentActive != null)
        {
            var filtered = shuffleRemaining.Where(v => v.id != currentActive.id).ToList();
            if (filtered.Count > 0) candidates = filtered;
        }

        // Pick and remove a random entry from the bag
        int idx = UnityEngine.Random.Range(0, candidates.Count);
        var target = candidates[idx];
        shuffleRemaining.Remove(target);

        SwitchToVessel(target);
        lastSwitchedVesselId = target.id;
    }

    /// Unified toggle used by BOTH the F2 key handler and the control-panel button.
    /// Calls InvokeHideUI/ShowUI, flips userPreferredUIVisible, and syncs KSP's
    /// internal F2 toggle field so subsequent F2 presses go the right direction.
    void ToggleFlightUI()
    {
        _uiToggleGuard = true;
        try
        {
            if (userPreferredUIVisible)
                InvokeHideUI();
            else
                InvokeShowUI();

            userPreferredUIVisible = !userPreferredUIVisible;
            _staticUserPreferredUIVisible = userPreferredUIVisible;

            // Sync KSP's internal F2 toggle field so the next F2 press sees
            // the correct state.  HideUI()/ShowUI() do NOT update isUIShowing.
            // isUIShowing: true = visible, false = hidden.
            if (_uiQuickHideField != null)
            {
                var inst = GetUIMasterInstance();
                if (inst != null)
                {
                    try { _uiQuickHideField.SetValue(inst, userPreferredUIVisible); }
                    catch { }
                }
            }

            SaveToScenario();
        }
        finally
        {
            _uiToggleGuard = false;
        }
    }

    void SwitchToVessel(Vessel v)
    {
        if (v == null) return;

        if (!FlightGlobals.ready)
        {
            Debug.LogWarning("[FastVesselChanger] SwitchToVessel aborted: FlightGlobals not ready");
            return;
        }

        bool windowWasVisible = showWindow;

        // Save zoom level for the current vessel before switching away
        var currentVessel = FlightGlobals.ActiveVessel;
        if (currentVessel != null)
        {
            var cam = FlightCamera.fetch;
            // Only capture zoom when the stock FlightCamera is in control.
            // If a hull cam is active, cam.Distance is meaningless (hull cam mount, not orbit distance).
            if (cam != null && _hullcamLastActivatedModule == null)
            {
                float zoomToSave = cam.Distance;
                if (cam.startDistance > 0f && zoomToSave > cam.startDistance * 10f)
                {
                    Debug.Log(string.Format("[FastVesselChanger] Zoom capture clamped: {0:F0} → {1:F0} (startDist={2:F0})",
                        zoomToSave, cam.startDistance, cam.startDistance));
                    zoomToSave = cam.startDistance;
                }
                _vesselZooms[currentVessel.id] = zoomToSave;
                VerboseLog("[FastVesselChanger] Captured zoom before switch: vessel=" + currentVessel.vesselName + " id=" + currentVessel.id + " zoom=" + zoomToSave);
            }
            else
            {
                VerboseLog("[FastVesselChanger] Could not capture zoom before switch: FlightCamera.fetch is null or hull cam active", warning: true);
            }

            // Save camera aim target (which Part the camera orbits around)
            if (cam != null && cam.Target != null && _hullcamLastActivatedModule == null)
            {
                var aimPart = cam.Target.GetComponentInParent<Part>();
                if (aimPart != null && aimPart.flightID != 0)
                    _vesselCameraTargets[currentVessel.id] = aimPart.flightID;
            }

            // Save "Control From Here" — use referenceTransformId directly
            // (more reliable than the ReferenceTransform→Part→flightID chain).
            uint switchRefId = currentVessel.referenceTransformId;
            if (switchRefId != 0)
            {
                _vesselControlFromHere[currentVessel.id] = switchRefId;
                var refPart = currentVessel.parts?.FirstOrDefault(p => p.flightID == switchRefId);
                Debug.Log("[FastVesselChanger] CFH capture on switch-away: vessel=" + currentVessel.vesselName
                    + " refTransformId=" + switchRefId
                    + " partName=" + (refPart != null ? refPart.partInfo.title : "NOT_FOUND"));
            }
        }

        // Save hull cam state for the vessel we're leaving.
        // Do NOT call DeactivateCurrentHullCam() — the FLIGHT→FLIGHT scene reload will destroy
        // all PartModules and reset HullcamVDS state naturally. Calling deactivate here would
        // interfere with native hullcam key controls if we haven't personally activated anything.
        if (_hullcamInstalled)
        {
            SyncCurrentHullcamStateToDict();
        }

        // Persist the latest zoom map before triggering FLIGHT->FLIGHT reload.
        SaveToScenario();

        // Deactivate the hull cam BEFORE triggering the scene reload.  HullcamVDS
        // keeps a static `sCurrentCamera` reference to the active camera module.
        // If we call SetActiveVessel while a hull cam is active, that static ref
        // survives the FLIGHT→FLIGHT reload but the PartModule it points to is
        // destroyed, causing NullRefs in FlightGlobals.UpdateInformation and
        // Waterfall.  Calling LeaveCamera() here, while parts are still alive,
        // cleanly restores the FlightCamera and clears the static reference.
        if (_hullcamInstalled && _hullcamLastActivatedModule != null)
        {
            DeactivateCurrentHullCam();

            // LeaveCamera() → RestoreMainCamera() leaves the FlightCamera pointing at
            // whatever angle/distance it had when the hull cam took over.  KSP then
            // smoothly interpolates from that stale position to the "correct" one on
            // the new vessel, producing a visible rotation sweep.  Resetting mode to
            // Auto tells FlightCamera to snap-to rather than lerp, eliminating the
            // smooth transition artifact.
            var cam = FlightCamera.fetch;
            if (cam != null)
                cam.setModeImmediate(FlightCamera.Modes.AUTO);
        }

        // Re-verify game state after pre-switch operations.  The original
        // FlightGlobals.ready check at the top of SwitchToVessel can pass, but
        // DeactivateCurrentHullCam / SaveToScenario / packing events can corrupt
        // the game state in the window between the check and SetActiveVessel.
        if (!FlightGlobals.ready)
        {
            Debug.LogWarning("[FastVesselChanger] SwitchToVessel aborted: FlightGlobals became not-ready during pre-switch operations");
            return;
        }
        try
        {
            var av = FlightGlobals.ActiveVessel;
            if (av != null && av.rootPart != null)
            {
                var _ = av.rootPart.transform;  // throws if destroyed/corrupted
            }
        }
        catch
        {
            Debug.LogError("[FastVesselChanger] SwitchToVessel aborted: active vessel transform is corrupted — cannot safely switch");
            return;
        }

        // Assert hidden state before triggering the scene reload
        if (!userPreferredUIVisible)
            InvokeHideUI();

        // If the destination vessel has hull cam enabled, arm the blackout overlay
        // so OnGUI draws a full-screen black rect from scene-start until the hull cam
        // activates inside OnFlightReady, hiding the stock FlightCamera flash.
        VesselHullcamSettings hcSet;
        if (_hullcamInstalled
            && _vesselHullcamSettings.TryGetValue(v.id, out hcSet)
            && hcSet.hullcamEnabled)
        {
            _pendingHullcamBlackout = true;
            _hullcamBlackoutDeadline = Time.realtimeSinceStartup + 5f;
        }

        // Arm the stuck-switch watchdog (informational only).
        _switchWatchdogRealtime = Time.realtimeSinceStartup;
        _switchWatchdogTargetId = v.id;
        _switchWatchdogFlightReady = false;

        try
        {
            FlightGlobals.SetActiveVessel(v);
        }
        catch (Exception e)
        {
            Debug.LogError("[FastVesselChanger] SetActiveVessel failed: " + e.Message
                + ". Watchdog armed — recovery in " + SWITCH_WATCHDOG_TIMEOUT + "s if scene reload does not complete.");
            ScreenMessages.PostScreenMessage("FastVesselChanger: vessel switch failed — will retry in " + SWITCH_WATCHDOG_TIMEOUT + "s", 10f, ScreenMessageStyle.UPPER_CENTER);
            return;
        }

        // NOTE: Do NOT call cam.SetTarget() here.  SetActiveVessel() triggers a
        // full FLIGHT→FLIGHT scene reload that destroys the current FlightCamera and
        // creates a new one.  Calling SetTarget on the old camera with a transform
        // that's about to be destroyed is futile at best and can corrupt camera
        // statics that bleed into the new scene.  The new scene's FlightDriver.Start()
        // handles camera targeting automatically.

        // Restore window visibility after switch
        showWindow = windowWasVisible;

        // Queue zoom restore — end-of-frame coroutine will keep re-applying for
        // ZOOM_RESTORE_FRAMES frames to override deferred FlightCamera.SetTarget() calls.
        float savedZoom;
        if (_vesselZooms.TryGetValue(v.id, out savedZoom))
        {
            _pendingZoomVesselId = v.id;
            _pendingZoom = savedZoom;
            _pendingZoomRestore = true;
            _pendingZoomFramesRemaining = ZOOM_RESTORE_FRAMES;
            VerboseLog("[FastVesselChanger] RestoreZoom queued: vessel=" + v.vesselName + " id=" + v.id + " zoom=" + savedZoom);
        }

        // Queue camera aim target restore
        uint savedCamTarget;
        if (_vesselCameraTargets.TryGetValue(v.id, out savedCamTarget) && savedCamTarget != 0)
        {
            _pendingCameraTarget = savedCamTarget;
            _pendingCameraTargetVesselId = v.id;
        }
        else
        {
            _pendingCameraTarget = 0;
            _pendingCameraTargetVesselId = Guid.Empty;
        }

        // Queue control-from-here restore
        uint savedControl;
        if (_vesselControlFromHere.TryGetValue(v.id, out savedControl) && savedControl != 0)
        {
            _pendingControlFromHere = savedControl;
            _pendingControlFromHereVesselId = v.id;
            _pendingControlFromHereFrames = CONTROL_RESTORE_FRAMES;
        }
        else
        {
            _pendingControlFromHere = 0;
            _pendingControlFromHereVesselId = Guid.Empty;
            _pendingControlFromHereFrames = 0;
        }

        // Randomize camera rotation rates if enabled
        if (cameraRotRandomEnabled)
        {
            bool isGrounded = v.situation == Vessel.Situations.LANDED
                || v.situation == Vessel.Situations.PRELAUNCH
                || v.situation == Vessel.Situations.SPLASHED;

            // Each axis is uniform [-1.25, 1.25]. Resample until the sum of magnitudes is >= 0.70,
            // guaranteeing a noticeable combined rotation. When grounded, pitch (X) is forced
            // to 0 so the constraint effectively becomes |Y| >= 0.70.
            float x, y;
            do
            {
                x = isGrounded ? 0f : UnityEngine.Random.Range(-1.25f, 1.25f);
                y = UnityEngine.Random.Range(-1.25f, 1.25f);
            } while (Mathf.Abs(x) + Mathf.Abs(y) < 0.70f);

            cameraRotXRate = x;
            cameraRotYRate = y;
            cameraRotXText = cameraRotXRate.ToString("F2");
            cameraRotYText = cameraRotYRate.ToString("F2");
            // Store in statics so they survive addon destruction AND scenario reloads.
            // The scenario system restores on-disk values (last game save) during scene transitions,
            // which would overwrite any in-memory scenario changes. Statics are never touched by KSP.
            _pendingRandomRates = true;
            _pendingRandomX = cameraRotXRate;
            _pendingRandomY = cameraRotYRate;
        }

        Debug.Log("[FastVesselChanger] Switched to vessel: " + v.vesselName);
    }

    void LoadUserPrefs()
    {
        try
        {
            string prefsPath = GetUserPrefsPath();
            if (!File.Exists(prefsPath))
            {
                SaveUserPrefs();
                return;
            }

            var doc = new XmlDocument();
            doc.Load(prefsPath);
            XmlElement root = doc.DocumentElement;
            if (root == null)
                return;

            // --- All settings are per-player ---
            string playerKey = FVCLunaHelper.GetCurrentPlayerName();
            XmlElement playerSection = null;
            foreach (XmlElement el in root.GetElementsByTagName("Player"))
            {
                if (el.GetAttribute("name") == playerKey) { playerSection = el; break; }
            }
            if (playerSection == null)
            {
                Debug.Log("[FastVesselChanger] LoadUserPrefs: no player section for '" + playerKey + "', using defaults");
                return;
            }

            // UI settings (per-player)
            showCameraControls = ParseBool(playerSection["ShowCameraControls"]?.InnerText, true);
            lastShowCameraControls = showCameraControls;
            showTypeFilter = ParseBool(playerSection["ShowTypeFilter"]?.InnerText, false);
            lastShowTypeFilter = showTypeFilter;
            _showHullcamSection = ParseBool(playerSection["ShowHullcamSection"]?.InnerText, false);
            _lastShowHullcamSection = _showHullcamSection;
            _writeLMPPlayersLog = ParseBool(playerSection["WriteLMPPlayersLog"]?.InnerText, false);
            _writeVesselLog = ParseBool(playerSection["WriteVesselLog"]?.InnerText, false);
            _writeCameraLog = ParseBool(playerSection["WriteCameraLog"]?.InnerText, false);

            XmlElement filters = playerSection["TypeFilters"];
            if (filters != null)
            {
                var enabledTypes = filters.GetElementsByTagName("Filter")
                    .OfType<XmlElement>()
                    .Select(filter => filter.GetAttribute("name"))
                    .Where(name => !string.IsNullOrEmpty(name))
                    .ToList();
                if (enabledTypes.Count > 0)
                    ApplyEnabledTypeFilters(enabledTypes);
            }

            XmlElement window = playerSection["Window"];
            if (window != null)
            {
                float wx = ParseFloat(window.GetAttribute("x"), windowRect.x);
                float wy = ParseFloat(window.GetAttribute("y"), windowRect.y);
                float ww = ParseFloat(window.GetAttribute("width"), windowRect.width);
                float wh = ParseFloat(window.GetAttribute("height"), windowRect.height);
                windowRect = new Rect(wx, wy, Mathf.Max(FIXED_WINDOW_WIDTH, ww), Mathf.Max(MIN_WINDOW_HEIGHT, wh));
            }

            // Scalar settings
            int parsedInt;
            if (int.TryParse(playerSection["SwitchInterval"]?.InnerText, out parsedInt))
                switchInterval = parsedInt;
            switchIntervalText = switchInterval.ToString();
            autoEnabled = ParseBool(playerSection["AutoEnabled"]?.InnerText, false);
            showWindow = ParseBool(playerSection["ShowWindow"]?.InnerText, true);
            userPreferredUIVisible = ParseBool(playerSection["UIVisible"]?.InnerText, true);
            _staticUserPreferredUIVisible = userPreferredUIVisible;
            cameraRotEnabled = ParseBool(playerSection["CameraRotEnabled"]?.InnerText, false);
            cameraRotRandomEnabled = ParseBool(playerSection["CameraRotRandomEnabled"]?.InnerText, false);
            cameraRotXRate = ParseFloat(playerSection["CameraRotXRate"]?.InnerText, 0f);
            cameraRotYRate = ParseFloat(playerSection["CameraRotYRate"]?.InnerText, 0f);
            cameraRotXText = cameraRotXRate.ToString("F1");
            cameraRotYText = cameraRotYRate.ToString("F1");

            // Selected vessels
            XmlElement selVessels = playerSection["SelectedVessels"];
            if (selVessels != null)
            {
                selected.Clear();
                foreach (XmlElement v in selVessels.GetElementsByTagName("Vessel"))
                {
                    Guid g;
                    if (Guid.TryParse(v.GetAttribute("id"), out g))
                        selected[g] = true;
                }
            }

            // Per-vessel zoom levels
            XmlElement zoomsNode = playerSection["VesselZooms"];
            if (zoomsNode != null)
            {
                _vesselZooms.Clear();
                foreach (XmlElement z in zoomsNode.GetElementsByTagName("Zoom"))
                {
                    Guid vesselId;
                    float zoom;
                    if (!Guid.TryParse(z.GetAttribute("vessel"), out vesselId)) continue;
                    if (!float.TryParse(z.GetAttribute("distance"), NumberStyles.Float, CultureInfo.InvariantCulture, out zoom)) continue;
                    _vesselZooms[vesselId] = zoom;
                }
            }

            // Per-vessel camera aim targets
            XmlElement camTargetsNode = playerSection["VesselCameraTargets"];
            if (camTargetsNode != null)
            {
                _vesselCameraTargets.Clear();
                foreach (XmlElement t in camTargetsNode.GetElementsByTagName("Target"))
                {
                    Guid vesselId;
                    uint flightId;
                    if (!Guid.TryParse(t.GetAttribute("vessel"), out vesselId)) continue;
                    if (!uint.TryParse(t.GetAttribute("flightId"), out flightId)) continue;
                    _vesselCameraTargets[vesselId] = flightId;
                }
            }

            // Per-vessel control-from-here references
            XmlElement controlNode = playerSection["VesselControlFromHere"];
            if (controlNode != null)
            {
                _vesselControlFromHere.Clear();
                foreach (XmlElement c in controlNode.GetElementsByTagName("Control"))
                {
                    Guid vesselId;
                    uint flightId;
                    if (!Guid.TryParse(c.GetAttribute("vessel"), out vesselId)) continue;
                    if (!uint.TryParse(c.GetAttribute("flightId"), out flightId)) continue;
                    _vesselControlFromHere[vesselId] = flightId;
                }
            }

            // Per-vessel switch intervals
            XmlElement intervalsNode = playerSection["VesselSwitchIntervals"];
            if (intervalsNode != null)
            {
                _vesselSwitchIntervals.Clear();
                foreach (XmlElement intEl in intervalsNode.GetElementsByTagName("Interval"))
                {
                    Guid vesselId;
                    int seconds;
                    if (!Guid.TryParse(intEl.GetAttribute("vessel"), out vesselId)) continue;
                    if (!int.TryParse(intEl.GetAttribute("seconds"), out seconds)) continue;
                    _vesselSwitchIntervals[vesselId] = seconds;
                }
            }

            // Shuffle bag remainder — stored as vessel IDs, resolved against cycleList later
            XmlElement shuffleNode = playerSection["ShuffleBag"];
            if (shuffleNode != null)
            {
                _loadedShuffleBagIds.Clear();
                foreach (XmlElement v in shuffleNode.GetElementsByTagName("Vessel"))
                {
                    _loadedShuffleBagIds.Add(v.GetAttribute("id"));
                }
            }

            // Per-vessel hullcam settings
            XmlElement hullcamsNode = playerSection["HullcamSettings"];
            if (hullcamsNode != null)
            {
                _vesselHullcamSettings.Clear();
                foreach (XmlElement hc in hullcamsNode.GetElementsByTagName("VesselHullcam"))
                {
                    Guid vesselId;
                    if (!Guid.TryParse(hc.GetAttribute("vessel"), out vesselId)) continue;
                    var s = new VesselHullcamSettings();
                    s.hullcamEnabled = ParseBool(hc.GetAttribute("enabled"), false);
                    s.hullcamInterval = Mathf.Max(1f, ParseFloat(hc.GetAttribute("interval"), 10f));
                    s.includeExternal = ParseBool(hc.GetAttribute("includeExternal"), true);
                    foreach (XmlElement cam in hc.GetElementsByTagName("SelectedCam"))
                    {
                        uint fid;
                        if (uint.TryParse(cam.GetAttribute("flightId"), out fid))
                            s.selectedFlightIds.Add(fid);
                    }
                    foreach (XmlElement dis in hc.GetElementsByTagName("DisabledCam"))
                    {
                        uint fid;
                        if (uint.TryParse(dis.GetAttribute("flightId"), out fid))
                            s.disabledFlightIds.Add(fid);
                    }
                    _vesselHullcamSettings[vesselId] = s;
                }
                Debug.Log("[FastVesselChanger] LoadUserPrefs: loaded " + _vesselHullcamSettings.Count + " hullcam entries");
            }

            // Presets
            LoadPresetsFromXml(playerSection);

            Debug.Log("[FastVesselChanger] LoadUserPrefs: loaded player '" + playerKey + "' settings");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[FastVesselChanger] LoadUserPrefs error: " + e.Message);
        }
    }

    void SaveUserPrefs()
    {
        try
        {
            string prefsPath = GetUserPrefsPath();
            string prefsDir = Path.GetDirectoryName(prefsPath);
            if (!string.IsNullOrEmpty(prefsDir))
                Directory.CreateDirectory(prefsDir);

            string playerKey = FVCLunaHelper.GetCurrentPlayerName();

            // Read existing XML to preserve other players' sections
            var doc = new XmlDocument();
            if (File.Exists(prefsPath))
            {
                try { doc.Load(prefsPath); } catch { }
            }

            XmlElement root = doc.DocumentElement;
            if (root == null)
            {
                root = doc.CreateElement("FastVesselChangerUserPrefs");
                doc.AppendChild(root);
            }

            // --- All settings are per-player ---
            // Find or create the player section
            XmlElement playerSection = null;
            foreach (XmlElement el in root.GetElementsByTagName("Player"))
            {
                if (el.GetAttribute("name") == playerKey) { playerSection = el; break; }
            }
            if (playerSection == null)
            {
                playerSection = doc.CreateElement("Player");
                playerSection.SetAttribute("name", playerKey);
                root.AppendChild(playerSection);
            }
            // Clear and rebuild
            playerSection.RemoveAll();
            playerSection.SetAttribute("name", playerKey);

            // UI settings (per-player)
            SetXmlValue(doc, playerSection, "ShowCameraControls", showCameraControls.ToString());
            SetXmlValue(doc, playerSection, "ShowTypeFilter", showTypeFilter.ToString());
            SetXmlValue(doc, playerSection, "ShowHullcamSection", _showHullcamSection.ToString());
            SetXmlValue(doc, playerSection, "WriteLMPPlayersLog", _writeLMPPlayersLog.ToString());
            SetXmlValue(doc, playerSection, "WriteVesselLog", _writeVesselLog.ToString());
            SetXmlValue(doc, playerSection, "WriteCameraLog", _writeCameraLog.ToString());

            XmlElement windowNode = doc.CreateElement("Window");
            windowNode.SetAttribute("x", windowRect.x.ToString(CultureInfo.InvariantCulture));
            windowNode.SetAttribute("y", windowRect.y.ToString(CultureInfo.InvariantCulture));
            windowNode.SetAttribute("width", windowRect.width.ToString(CultureInfo.InvariantCulture));
            windowNode.SetAttribute("height", windowRect.height.ToString(CultureInfo.InvariantCulture));
            playerSection.AppendChild(windowNode);

            XmlElement filtersNode = doc.CreateElement("TypeFilters");
            foreach (var kv in vesselTypeFilter.Where(kv => kv.Value))
            {
                XmlElement filterNode = doc.CreateElement("Filter");
                filterNode.SetAttribute("name", kv.Key);
                filtersNode.AppendChild(filterNode);
            }
            playerSection.AppendChild(filtersNode);

            // Simulation settings
            SetXmlValue(doc, playerSection, "SwitchInterval", switchInterval.ToString());
            SetXmlValue(doc, playerSection, "AutoEnabled", autoEnabled.ToString());
            SetXmlValue(doc, playerSection, "ShowWindow", showWindow.ToString());
            SetXmlValue(doc, playerSection, "UIVisible", userPreferredUIVisible.ToString());
            SetXmlValue(doc, playerSection, "CameraRotEnabled", cameraRotEnabled.ToString());
            SetXmlValue(doc, playerSection, "CameraRotRandomEnabled", cameraRotRandomEnabled.ToString());
            SetXmlValue(doc, playerSection, "CameraRotXRate", cameraRotXRate.ToString(CultureInfo.InvariantCulture));
            SetXmlValue(doc, playerSection, "CameraRotYRate", cameraRotYRate.ToString(CultureInfo.InvariantCulture));

            // Selected vessels
            XmlElement selVessels = doc.CreateElement("SelectedVessels");
            foreach (var kv in selected)
            {
                if (kv.Value)
                {
                    XmlElement v = doc.CreateElement("Vessel");
                    v.SetAttribute("id", kv.Key.ToString());
                    selVessels.AppendChild(v);
                }
            }
            playerSection.AppendChild(selVessels);

            // Shuffle bag remainder
            XmlElement shuffleNode = doc.CreateElement("ShuffleBag");
            foreach (var remaining in shuffleRemaining)
            {
                if (remaining != null)
                {
                    XmlElement v = doc.CreateElement("Vessel");
                    v.SetAttribute("id", remaining.id.ToString());
                    shuffleNode.AppendChild(v);
                }
            }
            playerSection.AppendChild(shuffleNode);

            // Per-vessel switch intervals
            XmlElement intervalsNode = doc.CreateElement("VesselSwitchIntervals");
            foreach (var kvInt in _vesselSwitchIntervals)
            {
                XmlElement intEl = doc.CreateElement("Interval");
                intEl.SetAttribute("vessel", kvInt.Key.ToString());
                intEl.SetAttribute("seconds", kvInt.Value.ToString());
                intervalsNode.AppendChild(intEl);
            }
            playerSection.AppendChild(intervalsNode);

            // Per-vessel zoom levels
            XmlElement zoomsNode = doc.CreateElement("VesselZooms");
            foreach (var kvZoom in _vesselZooms)
            {
                if (float.IsNaN(kvZoom.Value) || float.IsInfinity(kvZoom.Value)) continue;
                XmlElement z = doc.CreateElement("Zoom");
                z.SetAttribute("vessel", kvZoom.Key.ToString());
                z.SetAttribute("distance", kvZoom.Value.ToString(CultureInfo.InvariantCulture));
                zoomsNode.AppendChild(z);
            }
            playerSection.AppendChild(zoomsNode);

            // Per-vessel camera aim targets
            XmlElement camTargetsNode = doc.CreateElement("VesselCameraTargets");
            foreach (var kvCam in _vesselCameraTargets)
            {
                if (kvCam.Value == 0) continue;
                XmlElement t = doc.CreateElement("Target");
                t.SetAttribute("vessel", kvCam.Key.ToString());
                t.SetAttribute("flightId", kvCam.Value.ToString());
                camTargetsNode.AppendChild(t);
            }
            playerSection.AppendChild(camTargetsNode);

            // Per-vessel control-from-here references
            XmlElement controlNode = doc.CreateElement("VesselControlFromHere");
            foreach (var kvCtrl in _vesselControlFromHere)
            {
                if (kvCtrl.Value == 0) continue;
                XmlElement c = doc.CreateElement("Control");
                c.SetAttribute("vessel", kvCtrl.Key.ToString());
                c.SetAttribute("flightId", kvCtrl.Value.ToString());
                controlNode.AppendChild(c);
            }
            playerSection.AppendChild(controlNode);

            // Per-vessel hullcam settings
            SyncCurrentHullcamStateToDict();
            XmlElement hullcamsNode = doc.CreateElement("HullcamSettings");
            foreach (var kvHc in _vesselHullcamSettings)
            {
                var hs = kvHc.Value;
                XmlElement hc = doc.CreateElement("VesselHullcam");
                hc.SetAttribute("vessel", kvHc.Key.ToString());
                hc.SetAttribute("enabled", hs.hullcamEnabled.ToString());
                hc.SetAttribute("interval", hs.hullcamInterval.ToString(CultureInfo.InvariantCulture));
                hc.SetAttribute("includeExternal", hs.includeExternal.ToString());
                foreach (var fid in hs.selectedFlightIds)
                {
                    XmlElement cam = doc.CreateElement("SelectedCam");
                    cam.SetAttribute("flightId", fid.ToString());
                    hc.AppendChild(cam);
                }
                foreach (var fid in hs.disabledFlightIds)
                {
                    XmlElement dis = doc.CreateElement("DisabledCam");
                    dis.SetAttribute("flightId", fid.ToString());
                    hc.AppendChild(dis);
                }
                hullcamsNode.AppendChild(hc);
            }
            playerSection.AppendChild(hullcamsNode);

            // Presets
            SavePresetsToXml(doc, playerSection);

            doc.Save(prefsPath);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[FastVesselChanger] SaveUserPrefs error: " + e.Message);
        }
    }

    static void SetXmlValue(XmlDocument doc, XmlElement parent, string name, string value)
    {
        XmlElement el = parent[name];
        if (el == null) { el = doc.CreateElement(name); parent.AppendChild(el); }
        el.InnerText = value;
    }

    string GetUserPrefsPath()
    {
        string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (string.IsNullOrEmpty(assemblyDir))
            return USER_PREFS_FILE_NAME;

        var pluginDir = new DirectoryInfo(assemblyDir);
        DirectoryInfo modDir = pluginDir.Parent;
        if (modDir == null)
            return Path.Combine(assemblyDir, USER_PREFS_FILE_NAME);

        return Path.Combine(modDir.FullName, "PluginData", USER_PREFS_FILE_NAME);
    }

    static float ParseFloat(string? value, float fallback)
    {
        float parsed;
        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            return parsed;
        return fallback;
    }

    static bool ParseBool(string? value, bool fallback)
    {
        bool parsed;
        if (bool.TryParse(value, out parsed))
            return parsed;
        return fallback;
    }

    void SaveToScenario()
    {
        try
        {
            // Snapshot current vessel's zoom before serializing so it isn't lost
            // when saving without having switched away from it first.
            // SKIP the snapshot when a zoom restore is in progress: during a
            // FLIGHT\u2192FLIGHT reload (including OnDestroy) cam.Distance is
            // stale/default, so snapshotting would overwrite the correct value
            // that SwitchToVessel already placed in the dict.
            if (_instanceVesselId != Guid.Empty && _pendingZoomFramesRemaining <= 0 && !_isDestroying)
            {
                var cam = FlightCamera.fetch;
                if (cam != null && _hullcamLastActivatedModule == null)
                {
                    float zoomToSave = cam.Distance;
                    if (cam.startDistance > 0f && zoomToSave > cam.startDistance * 10f)
                    {
                        Debug.Log(string.Format("[FastVesselChanger] Zoom snapshot clamped: {0:F0} → {1:F0} (startDist={2:F0})",
                            zoomToSave, cam.startDistance, cam.startDistance));
                        zoomToSave = cam.startDistance;
                    }
                    _vesselZooms[_instanceVesselId] = zoomToSave;
                }
            }

            // Snapshot camera aim target for current vessel.
            // SKIP during FLIGHT\u2192FLIGHT reload (pendingZoomFramesRemaining > 0):
            // ActiveVessel is already the NEW vessel at that point, so reading its
            // camera target would overwrite the OLD vessel's correct value that
            // SwitchToVessel already captured.
            if (_instanceVesselId != Guid.Empty && _pendingZoomFramesRemaining <= 0 && !_isDestroying)
            {
                var av = FlightGlobals.ActiveVessel;
                if (av != null)
                {
                    var cam = FlightCamera.fetch;
                    if (cam != null && cam.Target != null && _hullcamLastActivatedModule == null)
                    {
                        var aimPart = cam.Target.GetComponentInParent<Part>();
                        if (aimPart != null && aimPart.flightID != 0)
                            _vesselCameraTargets[_instanceVesselId] = aimPart.flightID;
                    }
                }
            }

            // Snapshot control-from-here reference for current vessel.
            // Uses referenceTransformId directly (more reliable than Part lookup).
            // Uses its own guard (_pendingControlFromHereFrames) since it has a
            // longer restore window than zoom/camera aim.
            if (_instanceVesselId != Guid.Empty && _pendingControlFromHereFrames <= 0)
            {
                var av = FlightGlobals.ActiveVessel;
                if (av != null)
                {
                    uint refId = av.referenceTransformId;
                    if (refId != 0)
                    {
                        _vesselControlFromHere[_instanceVesselId] = refId;
                        Debug.Log("[FastVesselChanger] CFH snapshot in SaveToScenario: vessel=" + av.vesselName
                            + " refTransformId=" + refId);
                    }
                }
            }
            else if (_instanceVesselId != Guid.Empty && _pendingControlFromHereFrames > 0)
            {
                Debug.Log("[FastVesselChanger] CFH snapshot SKIPPED (pendingFrames=" + _pendingControlFromHereFrames + ")");
            }

            // Sync active vessel's hull cam state to the dict before serializing
            SyncCurrentHullcamStateToDict();

            // All persistence now goes through the XML file
            SaveUserPrefs();

            if (VERBOSE_DIAGNOSTICS)
                Debug.Log("[FastVesselChanger] SaveToScenario: persisted to XML"
                    + " (vesselZooms=" + _vesselZooms.Count
                    + " hullcamEntries=" + _vesselHullcamSettings.Count + ")");
        }
        catch (Exception e)
        {
            Debug.LogError("[FastVesselChanger] SaveToScenario error: " + e.Message);
        }
    }

    void AddAppLauncherButton(string source)
    {
        if (_activeInstance != this)
        {
            return;
        }

        // If a previous instance left a valid button behind, adopt it instead of creating a second icon.
        if (_sharedAppButton != null)
        {
            _appButton = _sharedAppButton;
            return;
        }

        if (_appButton != null || _isAddingAppButton)
        {
            return;
        }

        _isAddingAppButton = true;
        try
        {
            Type alType = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                alType = a.GetType("ApplicationLauncher")
                       ?? a.GetType("KSP.UI.Screens.ApplicationLauncher");
                if (alType != null) break;
            }
            if (alType == null)
            {
                Debug.LogWarning("[FastVesselSwitcher] AppLauncher add failed (" + source + "): type not found");
                return;
            }

            var instanceProp = alType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            var instance = instanceProp?.GetValue(null, null);
            if (instance == null)
            {
                Debug.Log("[FastVesselSwitcher] AppLauncher add deferred (" + source + "): Instance not yet available");
                return;
            }

            // AppScenes.FLIGHT | AppScenes.MAPVIEW
            Type appScenesType = alType.GetNestedType("AppScenes", BindingFlags.Public);
            object scenes = appScenesType != null
                ? (object)((int)Enum.Parse(appScenesType, "FLIGHT") | (int)Enum.Parse(appScenesType, "MAPVIEW"))
                : (object)(8 | 16); // FLIGHT | MAPVIEW numeric fallback

            // Find the Callback delegate type
            Type callbackType = alType.Assembly.GetType("Callback");
            if (callbackType == null)
            {
                Debug.LogWarning("[FastVesselSwitcher] AppLauncher add failed (" + source + "): Callback type not found");
                return;
            }
            
            Delegate onTrue = Delegate.CreateDelegate(callbackType, this, "OnAppTrue");
            Delegate onFalse = Delegate.CreateDelegate(callbackType, this, "OnAppFalse");

            Texture2D icon = GameDatabase.Instance.GetTexture("FastVesselChanger/Textures/icon", false);
            if (icon == null)
            {
                Debug.LogWarning("[FastVesselChanger] Icon texture not found — using 1x1 fallback");
                icon = new Texture2D(1, 1);
                icon.SetPixel(0, 0, new Color(0.75f, 0.1f, 0.1f));
                icon.Apply();
            }

            var addMethod = alType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "AddModApplication" && m.GetParameters().Length == 8);
            if (addMethod == null)
            {
                Debug.LogWarning("[FastVesselSwitcher] AppLauncher add failed (" + source + "): AddModApplication(8) not found");
                return;
            }

            _appButton = addMethod.Invoke(instance, new object[]
                { onTrue, onFalse, null, null, null, null, scenes, icon });
            if (_appButton != null)
                _sharedAppButton = _appButton;
            Debug.Log("[FastVesselSwitcher] AppLauncher add " + (_appButton != null ? "succeeded" : "returned null") + " (" + source + ")");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[FastVesselSwitcher] AppLauncher add failed (" + source + "): " + e.GetType().Name + ": " + e.Message);
        }
        finally
        {
            _isAddingAppButton = false;
        }
    }

    void OnGUIAppLauncherReady()
    {
        AddAppLauncherButton("onGUIApplicationLauncherReady");
    }

    void OnGUIAppLauncherUnreadifying(GameScenes _)
    {
        // Mirror SCANsat's lifecycle pattern: remove our button before AppLauncher rebuild.
        RemoveAppLauncherButton("onGUIApplicationLauncherUnreadifying", forceClearShared: true);
    }

    IEnumerator RetryAppLauncherButton()
    {
        // Try immediately, then keep retrying every second for up to 30s.
        // This handles the case where onGUIApplicationLauncherReady already fired before Start().
        for (int i = 0; i < 30 && _appButton == null; i++)
        {
            AddAppLauncherButton("retry-" + i);
            if (_appButton != null)
            {
                _retryButtonCoroutine = null;
                yield break;
            }
            yield return new WaitForSeconds(1f);
        }

        _retryButtonCoroutine = null;
    }

    void OnAppTrue()
    {
        try
        {
            if (!showWindow)  // Only log/save if actually changing state
            {
                showWindow = true;
                SaveToScenario();
                Debug.Log("[FastVesselChanger] Window opened via toolbar");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[FastVesselChanger] OnAppTrue callback failed: " + e.GetType().Name + ": " + e.Message);
            try { showWindow = true; } catch { }
        }
    }

    void OnAppFalse()
    {
        try
        {
            if (showWindow)  // Only log/save if actually changing state
            {
                showWindow = false;
                SaveToScenario();
                Debug.Log("[FastVesselChanger] Window closed via toolbar");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[FastVesselChanger] OnAppFalse callback failed: " + e.GetType().Name + ": " + e.Message);
            try { showWindow = false; } catch { }
        }
    }

    // Sync the toolbar button's visual state to match showWindow.
    // Called when the slash key toggles the window so the button doesn't fall out of sync.
    // SetTrue(false)/SetFalse(false) change only the visual state; they do NOT re-fire our OnAppTrue/OnAppFalse callbacks.
    void SyncAppButtonState(bool windowOpen)
    {
        if (_appButton == null) return;
        if (!_appButtonMethodsSearched)
        {
            _appButtonMethodsSearched = true;
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var btnType = _appButton.GetType();
            _appButtonSetTrueMethod  = btnType.GetMethod("SetTrue",  flags);
            _appButtonSetFalseMethod = btnType.GetMethod("SetFalse", flags);
            Debug.Log("[FastVesselChanger] SyncAppButtonState: SetTrue=" + (_appButtonSetTrueMethod?.Name ?? "null")
                + " SetFalse=" + (_appButtonSetFalseMethod?.Name ?? "null"));
        }
        try
        {
            var method = windowOpen ? _appButtonSetTrueMethod : _appButtonSetFalseMethod;
            if (method == null) return;
            var parms = method.GetParameters();
            // SetTrue/SetFalse may take an optional bool makeCall (default false).
            // Pass false explicitly so our callbacks are NOT re-invoked.
            if (parms.Length == 0)
                method.Invoke(_appButton, null);
            else
                method.Invoke(_appButton, new object[] { false });
        }
        catch (Exception e)
        {
            Debug.LogWarning("[FastVesselChanger] SyncAppButtonState failed: " + e.Message);
        }
    }

    // Widen FlightCamera pitch limits so stock clamping does not block continuous rotation.
    // Original values are cached and restored when this addon unloads.
    void WidenPitchLimits()
    {
        var cam = FlightCamera.fetch;
        if (cam == null) return;
        if (!_pitchLimitsWidened)
        {
            _origMinPitch = cam.minPitch;
            _origMaxPitch = cam.maxPitch;
            _pitchLimitsWidened = true;
            Debug.Log("[FastVesselChanger] Cached pitch limits: min=" + _origMinPitch + " max=" + _origMaxPitch);
        }
        cam.minPitch = EXPANDED_MIN_PITCH;
        cam.maxPitch = EXPANDED_MAX_PITCH;
    }

    void RestorePitchLimits()
    {
        if (!_pitchLimitsWidened) return;
        var cam = FlightCamera.fetch;
        if (cam != null && !float.IsNaN(_origMinPitch))
        {
            cam.minPitch = _origMinPitch;
            cam.maxPitch = _origMaxPitch;
            Debug.Log("[FastVesselChanger] Restored pitch limits: min=" + _origMinPitch + " max=" + _origMaxPitch);
        }
        _pitchLimitsWidened = false;
        _origMinPitch = float.NaN;
        _origMaxPitch = float.NaN;
    }

    void OnDestroy()
    {
        Debug.Log("[FastVesselChanger] OnDestroy() enter");
        bool isActiveInstance = _activeInstance == this;

        if (isActiveInstance)
            _activeInstance = null;

        // Remove per-instance event handlers
        try { GameEvents.onShowUI.Remove(OnShowUI_Instance); } catch { }
        try { GameEvents.onHideUI.Remove(OnHideUI_Instance); } catch { }
        try { GameEvents.onVesselLoaded.Remove(OnVesselLoaded); } catch { }
        try { GameEvents.onFlightReady.Remove(OnFlightReady); } catch { }
        GameEvents.onGUIApplicationLauncherReady.Remove(OnGUIAppLauncherReady);
        GameEvents.onGUIApplicationLauncherUnreadifying.Remove(OnGUIAppLauncherUnreadifying);

        if (_retryButtonCoroutine != null)
        {
            StopCoroutine(_retryButtonCoroutine);
            _retryButtonCoroutine = null;
        }

        if (_cameraPitchOverrideCoroutine != null)
        {
            StopCoroutine(_cameraPitchOverrideCoroutine);
            _cameraPitchOverrideCoroutine = null;
        }

        if (_twitchFileWriterCoroutine != null)
        {
            StopCoroutine(_twitchFileWriterCoroutine);
            _twitchFileWriterCoroutine = null;
        }

        if (!isActiveInstance)
        {
            Debug.Log("[FastVesselChanger] Skipping persistence for duplicate destroyed flight addon instance.");
            RemoveAppLauncherButton("OnDestroy", forceClearShared: false);
            return;
        }

        // Sync hull cam state and persist BEFORE deactivating the hull cam.
        // SaveToScenario's zoom snapshot is guarded by _hullcamLastActivatedModule,
        // so keeping it non-null here prevents overwriting the saved zoom with a
        // stale cam.Distance value from the hull cam mount.
        if (_hullcamInstalled)
            SyncCurrentHullcamStateToDict();

        _isDestroying = true; // Signal SaveToScenario to skip camera snapshots
        SaveToScenario(); // Save state before destroying

        // Deactivate hull cam AFTER save so the zoom guard works, but before the
        // addon is fully destroyed.  This is a safety net for non-FVC vessel
        // switches (vessel destruction, LMP sync, etc.).  For FVC-initiated
        // switches, SwitchToVessel already deactivated before SetActiveVessel,
        // so _hullcamLastActivatedModule will be null and this is a no-op.
        if (_hullcamInstalled && _hullcamLastActivatedModule != null)
            DeactivateCurrentHullCam();
        SaveUserPrefs();

        // Restore stock pitch limits before leaving flight scene
        RestorePitchLimits();

        // Ensure UI is visible when truly leaving the flight scene (KSC, tracking
        // station, quit) so the player isn't stuck with a hidden HUD.  During a
        // FLIGHT→FLIGHT vessel switch HighLogic.LoadedScene is still FLIGHT (the
        // destination), so leavingFlight stays false and we skip the ShowUI — the
        // new FVC instance handles UI state via Awake().  Calling ShowUI here
        // during FLIGHT→FLIGHT would flash the HUD visibly for ~0.5 s until the
        // new instance hides it again (the old bug).
        bool leavingFlight = HighLogic.LoadedScene != GameScenes.FLIGHT;
        if (leavingFlight)
        {
            try
            {
                InvokeShowUI();
                Debug.Log("[FastVesselChanger] Restored UI visibility before leaving flight scene");
            }
            catch { }
        }
        
        RemoveAppLauncherButton("OnDestroy", forceClearShared: false);
        Debug.Log("[FastVesselChanger] OnDestroy() complete");
    }

    // -------------------------------------------------------------------------
    // Twitch overlay support
    // -------------------------------------------------------------------------

    /// <summary>
    /// Coroutine that writes two overlay text files to &lt;PluginData&gt; every 15 seconds:
    ///   players_online.txt  – LunaMultiplayer connected players ("Streamer" excluded)
    ///   current_vessel.txt  – active vessel name + situation line
    /// </summary>
    IEnumerator TwitchFileWriterCoroutine()
    {
        while (true)
        {
            try
            {
                string dir = Path.GetDirectoryName(GetUserPrefsPath());
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                // --- players_online.txt ---
                List<string> players = null;
                if (_writeLMPPlayersLog)
                {
                    players = GetLMPPlayerNames();
                    string playersPath = Path.Combine(dir, TWITCH_PLAYERS_FILE);
                    File.WriteAllText(
                        playersPath,
                        players.Count > 0 ? string.Join(Environment.NewLine, players) : string.Empty);
                }

                // --- current_vessel.txt ---
                if (_writeVesselLog)
                {
                    var v = FlightGlobals.ActiveVessel;
                    string vesselLine  = v != null ? v.vesselName : "";
                    string statusLine  = v != null ? GetVesselSituationText(v) : "";
                    string vesselPath = Path.Combine(dir, TWITCH_VESSEL_FILE);
                    File.WriteAllText(
                        vesselPath,
                        vesselLine + Environment.NewLine + statusLine);
                }

                // --- CURRENT_CAMERA.txt ---
                if (_writeCameraLog)
                {
                    WriteCameraLogFile(dir);
                }

                if (!_twitchWriterStartupLogged)
                {
                    _twitchWriterStartupLogged = true;
                    VerboseLog("[FastVesselChanger] Twitch overlay writer active. LunaEnabled=" + FVCLunaHelper.IsLunaEnabled
                        + ", playersLog=" + _writeLMPPlayersLog
                        + ", vesselLog=" + _writeVesselLog
                        + ", cameraLog=" + _writeCameraLog);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[FastVesselChanger] TwitchFileWriter error: " + e.Message);
            }

            yield return new WaitForSeconds(TWITCH_WRITE_INTERVAL);
        }
    }

    /// <summary>
    /// Writes the current camera name to CURRENT_CAMERA.txt.
    /// Called from the periodic coroutine and immediately on camera switches.
    /// </summary>
    void WriteCameraLogFile(string dir = null)
    {
        if (!_writeCameraLog) return;
        try
        {
            if (dir == null)
            {
                dir = Path.GetDirectoryName(GetUserPrefsPath());
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
            }
            string cameraPath = Path.Combine(dir, TWITCH_CAMERA_FILE);
            File.WriteAllText(cameraPath, GetCurrentCameraName());
        }
        catch (Exception e)
        {
            Debug.LogWarning("[FastVesselChanger] WriteCameraLogFile error: " + e.Message);
        }
    }

    /// <summary>
    /// Returns a human-readable name for the current camera view.
    /// Hull cam name (e.g. "KerbPro", "NavCam") when a hull cam is active,
    /// or the FlightCamera mode name (e.g. "Auto", "Free", "Orbital", "Chase") otherwise.
    /// </summary>
    string GetCurrentCameraName()
    {
        // If we activated a hull cam, report its name
        if (_hullcamLastActivatedModule != null && _hullcamCameraNameField != null)
        {
            var name = _hullcamCameraNameField.GetValue(_hullcamLastActivatedModule) as string;
            if (!string.IsNullOrEmpty(name))
                return name;
        }

        // Fall back to the stock FlightCamera mode
        var cam = FlightCamera.fetch;
        if (cam != null)
            return cam.mode.ToString();

        return "Unknown";
    }

    /// <summary>
    /// Returns the names of all players currently connected to the LunaMultiplayer server,
    /// excluding the reserved name "Streamer". Returns an empty list in single-player mode.
    /// Uses reflection so there is no hard dependency on LMP DLLs.
    /// </summary>
    List<string> GetLMPPlayerNames()
    {
        var players = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!FVCLunaHelper.IsLunaEnabled)
            return new List<string>();

        try
        {
            bool statusTypeAttempted = false;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type statusType = a.GetType("LunaMultiplayer.Client.Systems.Status.StatusSystem")
                               ?? a.GetType("LMP.Client.Systems.Status.StatusSystem");

                if (statusType == null)
                {
                    statusType = SafeGetTypes(a)
                        .FirstOrDefault(t => t != null
                                          && string.Equals(t.Name, "StatusSystem", StringComparison.Ordinal)
                                          && ((t.Namespace ?? string.Empty).IndexOf("Status", StringComparison.OrdinalIgnoreCase) >= 0
                                           || (t.FullName ?? string.Empty).IndexOf("LMP", StringComparison.OrdinalIgnoreCase) >= 0
                                           || (t.FullName ?? string.Empty).IndexOf("LunaMultiplayer", StringComparison.OrdinalIgnoreCase) >= 0));
                }

                if (statusType == null) continue;
                statusTypeAttempted = true;

                object statusInstance = TryGetStatusSystemInstance(statusType);
                int beforeCount = players.Count;

                // Read from instance if available; otherwise probe static members on the status type.
                if (statusInstance != null)
                    CollectPlayersFromStatusCarrier(statusInstance, players);
                else
                    CollectPlayersFromStatusCarrier(null, players, statusType);

                if (VERBOSE_DIAGNOSTICS)
                {
                    if (statusInstance == null)
                    {
                        string knownProps = string.Join(", ", statusType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance).Select(p => p.Name).ToArray());
                        string knownFields = string.Join(", ", statusType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance).Select(f => f.Name).ToArray());
                        VerboseLog("[FastVesselChanger] LMP StatusSystem instance not found; probing static members. Props: " + knownProps + " | Fields: " + knownFields);
                    }
                    else
                    {
                        VerboseLog("[FastVesselChanger] LMP StatusSystem carrier type: " + statusInstance.GetType().FullName + ", extractedPlayers=" + (players.Count - beforeCount));
                    }
                }

                break;
            }

            if (!statusTypeAttempted && VERBOSE_DIAGNOSTICS)
            {
                var candidateTypes = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a =>
                    {
                        string n = a.GetName().Name ?? string.Empty;
                        return n.IndexOf("Luna", StringComparison.OrdinalIgnoreCase) >= 0
                            || n.IndexOf("LMP", StringComparison.OrdinalIgnoreCase) >= 0;
                    })
                    .SelectMany(a => SafeGetTypes(a))
                    .Where(t => t != null && (t.Name.IndexOf("Status", StringComparison.OrdinalIgnoreCase) >= 0
                                           || t.FullName.IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0))
                    .Select(t => t.FullName)
                    .Where(name => !string.IsNullOrEmpty(name))
                    .Take(30)
                    .ToArray();

                VerboseLog("[FastVesselChanger] LMP status type not found via reflection. Candidate types: "
                    + (candidateTypes.Length > 0 ? string.Join(" | ", candidateTypes) : "<none>"));
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[FastVesselChanger] GetLMPPlayerNames error: " + e.Message);
        }

        return players.OrderBy(name => name).ToList();
    }

    object? TryGetStatusSystemInstance(Type statusType)
    {
        if (statusType == null)
            return null;

        const BindingFlags staticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        string[] singletonNames = new[] { "Singleton", "Instance", "fetch", "Fetch", "_instance", "instance" };

        for (Type? current = statusType; current != null; current = current.BaseType)
        {
            foreach (string memberName in singletonNames)
            {
                var prop = current.GetProperty(memberName, staticFlags);
                if (prop != null && prop.GetIndexParameters().Length == 0)
                {
                    try
                    {
                        var value = prop.GetValue(null, null);
                        if (value != null && statusType.IsAssignableFrom(value.GetType()))
                            return value;
                    }
                    catch { }
                }

                var field = current.GetField(memberName, staticFlags);
                if (field != null)
                {
                    try
                    {
                        var value = field.GetValue(null);
                        if (value != null && statusType.IsAssignableFrom(value.GetType()))
                            return value;
                    }
                    catch { }
                }
            }
        }

        try
        {
            if (typeof(UnityEngine.Object).IsAssignableFrom(statusType))
            {
                var objs = Resources.FindObjectsOfTypeAll(statusType);
                if (objs != null && objs.Length > 0)
                    return objs[0];
            }
        }
        catch { }

        // Fallback: find any static holder in the same assembly that exposes this system.
        try
        {
            foreach (var holderType in SafeGetTypes(statusType.Assembly))
            {
                if (holderType == null)
                    continue;

                const BindingFlags holderFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

                foreach (var prop in holderType.GetProperties(holderFlags))
                {
                    if (prop.GetIndexParameters().Length != 0)
                        continue;

                    object? value = null;
                    try { value = prop.GetValue(null, null); } catch { }
                    if (value == null)
                        continue;

                    if (statusType.IsAssignableFrom(value.GetType()))
                        return value;

                    if (value is IEnumerable enumerable)
                    {
                        foreach (var item in enumerable)
                        {
                            if (item != null && statusType.IsAssignableFrom(item.GetType()))
                                return item;
                        }
                    }
                }

                foreach (var field in holderType.GetFields(holderFlags))
                {
                    object? value = null;
                    try { value = field.GetValue(null); } catch { }
                    if (value == null)
                        continue;

                    if (statusType.IsAssignableFrom(value.GetType()))
                        return value;

                    if (value is IEnumerable enumerable)
                    {
                        foreach (var item in enumerable)
                        {
                            if (item != null && statusType.IsAssignableFrom(item.GetType()))
                                return item;
                        }
                    }
                }
            }
        }
        catch { }

        return null;
    }

    void CollectPlayersFromStatusCarrier(object? carrier, HashSet<string> players, Type? explicitType = null)
    {
        Type carrierType = explicitType ?? carrier?.GetType();
        if (carrierType == null)
            return;

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        foreach (var prop in carrierType.GetProperties(flags))
        {
            if (!IsLikelyPlayerContainerMemberName(prop.Name) || prop.GetIndexParameters().Length != 0)
                continue;

            var getter = prop.GetGetMethod(true);
            if (getter != null)
            {
                object? target = getter.IsStatic ? null : carrier;
                if (getter.IsStatic || target != null)
                {
                    object? container = null;
                    try { container = prop.GetValue(target, null); } catch { }
                    CollectPlayersFromContainer(container, players);
                }
            }
        }

        foreach (var field in carrierType.GetFields(flags))
        {
            if (!IsLikelyPlayerContainerMemberName(field.Name))
                continue;

            object? target = field.IsStatic ? null : carrier;
            if (field.IsStatic || target != null)
            {
                object? container = null;
                try { container = field.GetValue(target); } catch { }
                CollectPlayersFromContainer(container, players);
            }
        }
    }

    bool IsLikelyPlayerContainerMemberName(string memberName)
    {
        if (string.IsNullOrEmpty(memberName))
            return false;

        string normalized = memberName.Replace("<", string.Empty).Replace(">", string.Empty).Replace("k__BackingField", string.Empty);
        normalized = normalized.ToLowerInvariant();

        bool hasPlayer = normalized.Contains("player");
        bool hasContainer = normalized.Contains("list") || normalized.Contains("status") || normalized.Contains("collection") || normalized.Contains("dict") || normalized.Contains("map");
        return hasPlayer && hasContainer;
    }

    void CollectPlayersFromContainer(object? container, HashSet<string> players)
    {
        if (container == null || players == null)
            return;

        if (container is IDictionary dict)
        {
            if (VERBOSE_DIAGNOSTICS)
            {
                object firstKey = null;
                object firstValue = null;
                foreach (DictionaryEntry entry in dict)
                {
                    firstKey = entry.Key;
                    firstValue = entry.Value;
                    break;
                }
                string keyType = firstKey != null ? firstKey.GetType().FullName : "<none>";
                string valueType = firstValue != null ? firstValue.GetType().FullName : "<none>";
                VerboseLog("[FastVesselChanger] LMP player container IDictionary shape: count=" + dict.Count + ", keyType=" + keyType + ", valueType=" + valueType);
            }

            foreach (DictionaryEntry entry in dict)
            {
                AddPlayerName(entry.Key, players);
                AddPlayerName(entry.Value, players);
            }
            return;
        }

        if (container is IEnumerable enumerable)
        {
            int index = 0;
            foreach (var item in enumerable)
            {
                if (item == null)
                    continue;

                AddPlayerName(item, players);

                var itemType = item.GetType();
                var keyProp = itemType.GetProperty("Key", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var valueProp = itemType.GetProperty("Value", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (keyProp != null)
                {
                    object keyObj = null;
                    try { keyObj = keyProp.GetValue(item, null); } catch { }
                    AddPlayerName(keyObj, players);
                }
                if (valueProp != null)
                {
                    object valueObj = null;
                    try { valueObj = valueProp.GetValue(item, null); } catch { }
                    AddPlayerName(valueObj, players);
                }

                index++;
                if (index > 256)
                    break;
            }
        }
    }

    void AddPlayerName(object? source, HashSet<string> players)
    {
        string name = ExtractLikelyPlayerName(source);
        if (string.IsNullOrWhiteSpace(name))
            return;

        if (string.Equals(name, "Streamer", StringComparison.OrdinalIgnoreCase))
            return;

        players.Add(name);
    }

    string ExtractLikelyPlayerName(object? source)
    {
        if (source == null)
            return string.Empty;

        string text = source as string;
        if (!string.IsNullOrEmpty(text))
            return text!.Trim();

        Type type = source.GetType();

        string[] candidateNames = new string[]
        {
            "PlayerName", "Name", "NickName", "Nickname", "DisplayName", "Username", "UserName"
        };

        foreach (string memberName in candidateNames)
        {
            var prop = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.PropertyType == typeof(string))
            {
                string value = prop.GetValue(source, null) as string;
                if (!string.IsNullOrWhiteSpace(value))
                    return value!.Trim();
            }
        }

        foreach (string memberName in candidateNames)
        {
            var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null && field.FieldType == typeof(string))
            {
                string value = field.GetValue(source) as string;
                if (!string.IsNullOrWhiteSpace(value))
                    return value!.Trim();
            }
        }

        var nestedCandidates = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(p => p.PropertyType != typeof(string) && p.GetIndexParameters().Length == 0)
            .Take(6);

        foreach (var nested in nestedCandidates)
        {
            object nestedObj = null;
            try { nestedObj = nested.GetValue(source, null); } catch { }
            if (nestedObj == null)
                continue;

            foreach (string memberName in candidateNames)
            {
                var nestedProp = nestedObj.GetType().GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (nestedProp != null && nestedProp.PropertyType == typeof(string))
                {
                    string value = nestedProp.GetValue(nestedObj, null) as string;
                    if (!string.IsNullOrWhiteSpace(value))
                        return value!.Trim();
                }
            }
        }

        string fallback = source.ToString();
        if (string.IsNullOrWhiteSpace(fallback))
            return string.Empty;

        fallback = fallback.Trim();
        if (fallback.Length >= 28 && fallback.Count(ch => ch == '-') >= 3)
            return string.Empty;

        bool numericOnly = fallback.All(ch => char.IsDigit(ch));
        if (numericOnly)
            return string.Empty;

        return fallback;
    }

    IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        if (assembly == null)
            return Enumerable.Empty<Type>();

        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null);
        }
        catch
        {
            return Enumerable.Empty<Type>();
        }
    }

    /// <summary>
    /// Returns a human-readable vessel situation string suitable for Twitch overlays.
    /// Examples: "Orbiting Kerbin", "On escape trajectory from Kerbin", "Landed on Mun".
    /// </summary>
    string GetVesselSituationText(Vessel v)
    {
        if (v == null) return "";
        string body = v.mainBody?.bodyName ?? "Unknown";
        switch (v.situation)
        {
            case Vessel.Situations.LANDED:      return "Landed on " + body;
            case Vessel.Situations.SPLASHED:    return "Splashed down on " + body;
            case Vessel.Situations.PRELAUNCH:   return "Pre-launch at " + body;
            case Vessel.Situations.FLYING:      return "Flying over " + body;
            case Vessel.Situations.SUB_ORBITAL: return "Sub-orbital flight over " + body;
            case Vessel.Situations.ORBITING:    return "Orbiting " + body;
            case Vessel.Situations.ESCAPING:    return "On escape trajectory from " + body;
            case Vessel.Situations.DOCKED:      return "Docked";
            default:                            return v.situation.ToString();
        }
    }

    void RemoveAppLauncherButton(string source, bool forceClearShared)
    {
        // If this instance is tied to a shared button reference, clean it up once.
        var buttonToRemove = _appButton ?? _sharedAppButton;
        if (buttonToRemove == null)
            return;

        bool removed = false;
        try
        {
            Type alType = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                alType = a.GetType("ApplicationLauncher")
                       ?? a.GetType("KSP.UI.Screens.ApplicationLauncher");
                if (alType != null) break;
            }
            if (alType != null)
            {
                var instanceProp = alType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                var instance = instanceProp?.GetValue(null, null);
                var rem = alType.GetMethod("RemoveModApplication", BindingFlags.Public | BindingFlags.Instance);
                if (rem != null && instance != null)
                {
                    Debug.Log("[FastVesselSwitcher] AppLauncher remove attempt starting (" + source + ").");
                    rem.Invoke(instance, new object[] { buttonToRemove });
                    removed = true;
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[FastVesselSwitcher] AppLauncher remove failed (" + source + "): " + e.GetType().Name + ": " + e.Message);
        }

        Debug.Log("[FastVesselSwitcher] AppLauncher remove " + (removed ? "succeeded." : "could not run (instance/method missing).") + " (" + source + ")");
        _appButton = null;
        if (forceClearShared || removed)
            _sharedAppButton = null;
    }

    // -------------------------------------------------------------------------
    // Recovery Sentinel — survives scene transitions via DontDestroyOnLoad
}
