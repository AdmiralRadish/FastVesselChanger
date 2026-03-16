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
    private bool _loadedTypeFiltersFromUserPrefs = false;
    private bool showCameraControls = false; // Toggle for camera controls section
    private bool lastShowCameraControls = false; // Track previous state to detect changes
    private string vesselSearchText = ""; // Live search filter for vessel list

    private Dictionary<Guid, bool> selected = new Dictionary<Guid, bool>();
    private Dictionary<string, bool> vesselTypeFilter = new Dictionary<string, bool>(); // Vessel type filtering
    private int switchInterval = 300; // default to 5 minutes
    private string switchIntervalText = "300"; // Text buffer for the input field to avoid getting stuck
    private int pendingInterval = -1; // interval typed by user but deferred (would fire immediately)
    private bool autoEnabled = false; // default to disabled
    private double lastSwitchTime = 0.0; // universal time of last switch (any switch)
    private Guid lastSwitchedVesselId = Guid.Empty; // Track the last switched vessel to avoid re-switching

    // Flight UI state tracking
    private bool userPreferredUIVisible = true; // Kept in sync with KSP's onShowUI/onHideUI events (F2)

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

    // Per-vessel zoom levels — keyed by vessel ID, survives vessel switches within a session
    private static Dictionary<Guid, float> _vesselZooms = new Dictionary<Guid, float>();
    // Pending zoom — applied every frame in Update() until the target vessel is active or timeout
    private static Guid _pendingZoomVesselId = Guid.Empty;
    private static float _pendingZoom = 0f;
    private static bool _pendingZoomRestore = false;
    private static bool _pendingZoomLoggedFirstFrame = false;
    private static float _pendingZoomDeadlineRealtime = 0f;
    private static Guid _zoomLockoutVesselId = Guid.Empty;
    private static float _zoomLockoutTarget = 0f;
    private static float _zoomLockoutUntilRealtime = 0f;
    private static FieldInfo _camDistField = null;  // cached reflection handle for FlightCamera.distance
    private static bool _camDistFieldSearched = false; // true once we've attempted the lookup
    private const float ZOOM_LOCKOUT_SECONDS = 1.0f;

    // Cached reflection handles for UIMasterController (the class that actually hides/shows KSP's HUD).
    // _uiMasterInstance is resolved lazily on first use because UIMasterController.Instance is not
    // assigned yet when Start() runs — KSP initialises it after addon startup.
    private PropertyInfo _uiInstanceProp = null;
    private object _uiMasterInstance = null;
    private MethodInfo _uiHideMethod = null;
    private MethodInfo _uiShowMethod = null;
    private Coroutine _uiHoldCoroutine = null;

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
    private const string TWITCH_PLAYERS_FILE = "players_online.txt";
    private const string TWITCH_VESSEL_FILE = "current_vessel.txt";
    private const string TWITCH_CAMERA_FILE = "CURRENT_CAMERA.txt";
    private const float TWITCH_WRITE_INTERVAL = 15f;

    // Guard against multiple switches in the same frame
    private int lastFrameCount = -1;

    // Cached sorted vessel list for OnGUI — rebuilt only when vessel count changes
    private List<Vessel> _cachedSortedVessels = new List<Vessel>();
    private int _cachedVesselCount = -1;

    // HullcamVDS fields are declared in FastVesselChanger.Hullcam.cs (partial class)

    void Awake()
    {
        if (_activeInstance != null && _activeInstance != this)
        {
            Debug.LogWarning("[FastVesselSwitcher] Duplicate Flight addon instance detected; destroying duplicate.");
            Destroy(this);
            return;
        }

        _activeInstance = this;
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
        // Without this guard both this instance and the surviving active instance would each
        // register their own event subscription and retry coroutine — two independent _appButton
        // instance fields both null — leading to two AddModApplication calls and two toolbar icons.
        if (_activeInstance != this)
        {
            Debug.LogWarning("[FastVesselSwitcher] Start() called on non-active duplicate instance; aborting.");
            return;
        }

        Debug.Log("[FastVesselChanger] started");
        
        // Initialize lastSwitchTime to prevent premature switching on startup
        lastSwitchTime = Planetarium.GetUniversalTime();
        
        // Log multiplayer status
        if (FVCLunaHelper.IsLunaEnabled)
        {
            Debug.Log("[FastVesselChanger] LunaMultiplayer detected - using per-player settings");
            string playerName = FVCLunaHelper.GetCurrentPlayerName();
            Debug.Log("[FastVesselChanger] Current player: " + playerName);
        }
        else
        {
            Debug.Log("[FastVesselChanger] Single-player mode");
        }

        InitializeVesselTypeFilter();
        RefreshSelectionsFromVessels();
        LoadUserPrefs();
        
        // Load persisted settings if scenario exists
        try
        {
            var scen = FastVesselChangerScenario.Instance;
            if (scen != null)
            {
                switchInterval = scen.switchInterval;
                switchIntervalText = switchInterval.ToString(); // Sync text buffer with loaded value
                autoEnabled = scen.autoEnabled;
                showWindow = scen.showWindow;
                userPreferredUIVisible = scen.uiVisible;
                cameraRotEnabled = scen.cameraRotEnabled;
                cameraRotRandomEnabled = scen.cameraRotRandomEnabled;
                cameraRotXRate = scen.cameraRotXRate;
                cameraRotYRate = scen.cameraRotYRate;
                cameraRotXText = cameraRotXRate.ToString("F1");
                cameraRotYText = cameraRotYRate.ToString("F1");
                // Window position and local filter prefs are loaded from the XML user prefs in LoadUserPrefs().
                selected.Clear();
                foreach (var id in scen.selectedVesselIds)
                {
                    Guid g;
                    if (Guid.TryParse(id, out g))
                        selected[g] = true;
                }

                _vesselZooms.Clear();
                foreach (var entry in scen.vesselZoomEntries)
                {
                    if (string.IsNullOrEmpty(entry)) continue;
                    var parts = entry.Split('|');
                    if (parts.Length != 2) continue;

                    Guid vesselId;
                    float zoom;
                    if (!Guid.TryParse(parts[0], out vesselId)) continue;
                    if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out zoom)) continue;
                    _vesselZooms[vesselId] = zoom;
                }

                if (!_loadedTypeFiltersFromUserPrefs && scen.selectedVesselTypes.Count > 0)
                {
                    ApplyEnabledTypeFilters(scen.selectedVesselTypes);
                    _loadedTypeFiltersFromUserPrefs = true;
                    SaveUserPrefs();
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[FastVesselChanger] Error loading scenario data: " + e.Message);
        }

        // Rebuild the selected vessel pool every time the flight addon is recreated,
        // then restore the remaining shuffle bag from scenario storage if present.
        BuildCycleList(resetShuffleBag: false);
        RestoreShuffleBagFromScenario(FastVesselChangerScenario.Instance);

        // If a vessel switch produced randomized rates, apply them now — after scenario load,
        // so they override whatever the scenario restored from the on-disk ConfigNode.
        if (_pendingRandomRates)
        {
            cameraRotXRate = _pendingRandomX;
            cameraRotYRate = _pendingRandomY;
            cameraRotXText = cameraRotXRate.ToString("F2");
            cameraRotYText = cameraRotYRate.ToString("F2");
            _pendingRandomRates = false;
            SaveToScenario();
        }

        // Detect HullcamVDS and restore hull cam state for the active vessel.
        // DetectHullcamVDS is idempotent (static flag) so it's safe to call on every addon recreation.
        DetectHullcamVDS();
        LoadHullcamSettingsFromScenario(FastVesselChangerScenario.Instance);
        var hullcamActiveVessel = FlightGlobals.ActiveVessel;
        // Pin the vessel this instance is responsible for. SyncCurrentHullcamStateToDict()
        // always writes to this ID, NOT FlightGlobals.ActiveVessel (which changes the moment
        // SetActiveVessel is called, before OnDestroy fires on the old instance).
        _instanceVesselId = hullcamActiveVessel?.id ?? Guid.Empty;
        if (hullcamActiveVessel != null && _hullcamInstalled)
            ApplyVesselHullcamSettings(hullcamActiveVessel);

        // Cache UIMasterController (the class that physically hides/shows KSP's flight HUD)
        CacheUIMasterController();

        // Reactively suppress any onShowUI KSP fires (vessel switch, vessel load, etc.)
        // by immediately re-hiding the UI one frame later if our preference is hidden.
        // This catches every onShowUI regardless of when KSP fires it.
        GameEvents.onShowUI.Add(OnShowUI);
        GameEvents.onVesselLoaded.Add(OnVesselLoaded);

        // If the addon was recreated mid-session (scene reload during vessel switch),
        // re-apply the hidden state immediately so it doesn't flash visible.
        if (!userPreferredUIVisible)
            RestartHoldUIHidden();

        // Stock AppLauncher button — subscribe to the event AND start a retry coroutine,
        // because the event may have already fired before we subscribed.
        GameEvents.onGUIApplicationLauncherReady.Add(OnGUIAppLauncherReady);
        GameEvents.onGUIApplicationLauncherUnreadifying.Add(OnGUIAppLauncherUnreadifying);
        _retryButtonCoroutine = StartCoroutine(RetryAppLauncherButton());

        // Apply camera pitch handling at end-of-frame so stock camera clamping runs first,
        // then we enforce widened limits and optional X auto-rotation without overriding
        // stock keyboard controls.
        _cameraPitchOverrideCoroutine = StartCoroutine(ApplyPitchOverridesEndOfFrame());
        _twitchFileWriterCoroutine = StartCoroutine(TwitchFileWriterCoroutine());

        // Write camera log immediately on vessel load so the file reflects the new camera state
        WriteCameraLogFile();
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

                Debug.Log("[FastVesselChanger] UIMasterController type found: HideUI=" + (_uiHideMethod != null) + ", ShowUI=" + (_uiShowMethod != null));
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
    // We ALSO always fire onHideUI directly because vessel transitions can call
    // GameEvents.onShowUI.Fire() directly (bypassing UIMasterController), which re-shows
    // UI elements without changing UIMasterController.showUI. In that state HideUI() may
    // return early due to an internal "already hidden" guard, so the direct event is needed
    // to reach the individual UI elements regardless.
    void InvokeHideUI()
    {
        var inst = GetUIMasterInstance();
        if (inst != null && _uiHideMethod != null)
        {
            try { _uiHideMethod.Invoke(inst, null); }
            catch (Exception e) { Debug.LogWarning("[FastVesselChanger] UIMasterController.HideUI() failed: " + e.Message); }
        }
        GameEvents.onHideUI.Fire();
    }

    // Calls UIMasterController.ShowUI() — same reasoning as InvokeHideUI.
    void InvokeShowUI()
    {
        var inst = GetUIMasterInstance();
        if (inst != null && _uiShowMethod != null)
        {
            try
            {
                _uiShowMethod.Invoke(inst, null);
                return;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[FastVesselChanger] UIMasterController.ShowUI() failed: " + e.Message);
            }
        }
        GameEvents.onShowUI.Fire();
    }

    void OnShowUI()
    {
        // KSP fires this at unpredictable times during vessel switches/loads.
        // RestartHoldUIHidden calls InvokeHideUI() synchronously on its first coroutine tick
        // (before the first yield), so there is zero frame delay — no visible flash.
        if (!userPreferredUIVisible)
            RestartHoldUIHidden();
    }

    void OnVesselLoaded(Vessel _)
    {
        if (!userPreferredUIVisible)
            RestartHoldUIHidden();
    }

    // Starts (or restarts) a coroutine that continuously re-asserts the hidden state for several
    // seconds, covering every stage of the vessel-switch/load pipeline that might re-show the UI.
    void RestartHoldUIHidden()
    {
        if (_uiHoldCoroutine != null)
            StopCoroutine(_uiHoldCoroutine);
        _uiHoldCoroutine = StartCoroutine(HoldUIHidden());
    }

    private static readonly WaitForSeconds _uiHoldWait = new WaitForSeconds(0.1f);

    IEnumerator HoldUIHidden()
    {
        float deadline = Time.time + 6f; // cover full vessel load pipeline
        float everyFrameUntil = Time.time + 2f; // every frame for first 2s (critical window)
        while (Time.time < deadline)
        {
            if (!userPreferredUIVisible)
                InvokeHideUI();
            else
                yield break; // user chose to show — stop fighting it
            if (Time.time < everyFrameUntil)
                yield return null; // every frame during vessel-load to prevent any flash
            else
                yield return _uiHoldWait; // 10 Hz for the remaining hold period
        }
        _uiHoldCoroutine = null;
    }


    void InitializeVesselTypeFilter()
    {
        // Initialize with standard KSP vessel types
        string[] types = { "Ship", "Station", "Probe", "Lander", "Rover", "Plane", "Relay", "Flag", "Debris", "SpaceObject", "Unknown" };
        foreach (var t in types)
        {
            vesselTypeFilter[t] = true; // Default: all types enabled
        }
    }

    void Update()
    {
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

        // Track F2 via Unity's input system. KSP's UIMasterController also handles F2 on its own,
        // but we sync our preference here so the control panel indicator stays accurate.
        // We don't call InvokeHideUI/ShowUI here because KSP's UIMasterController will do the
        // actual toggle — we just need our preference field to match what KSP is doing.
        if (Input.GetKeyDown(KeyCode.F2))
        {
            userPreferredUIVisible = !userPreferredUIVisible;
            // If KSP's UIMasterController ran before this Update() and fired onShowUI,
            // OnShowUI may have already suppressed the show. Re-assert the correct state.
            if (userPreferredUIVisible)
                InvokeShowUI();
            SaveToScenario();
        }

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
            // Enforce both the user-configured interval AND the minimum time
            double minimumAllowedInterval = Math.Max(switchInterval, MINIMUM_SWITCH_INTERVAL);
            if (ut - lastSwitchTime >= minimumAllowedInterval)
            {
                SwitchToNext();
                lastSwitchTime = ut;
            }
        }

        // Zoom restore — pre-seed as soon as target is active, then hard-lock briefly
        // to suppress transition-time camera systems from overriding the restore.
        if (_pendingZoomRestore && _pendingZoomVesselId != Guid.Empty)
        {
            if (Time.realtimeSinceStartup > _pendingZoomDeadlineRealtime)
            {
                VerboseLog("[FastVesselChanger] RestoreZoom timeout: target vessel did not become ready before deadline", warning: true);
                _pendingZoomVesselId = Guid.Empty;
                _pendingZoomRestore = false;
                _pendingZoomDeadlineRealtime = 0f;
            }

            var activeVessel = FlightGlobals.ActiveVessel;
            if (_pendingZoomVesselId != Guid.Empty && activeVessel != null && activeVessel.id == _pendingZoomVesselId)
            {
                var cam = FlightCamera.fetch;
                if (cam != null)
                {
                    // Cache the backing field once; guard with a sentinel so a failed lookup
                    // doesn't trigger an expensive GetFields scan on every restore call.
                    if (!_camDistFieldSearched)
                    {
                        _camDistFieldSearched = true;
                        // In this version of KSP RSS the backing field is "distance", not "camDistance".
                        _camDistField = cam.GetType().GetField("distance",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        VerboseLog("[FastVesselChanger] FlightCamera distance field lookup: "
                            + (_camDistField != null ? "found '" + _camDistField.Name + "'" : "NOT FOUND"));
                    }

                    // Log on the very first frame so we can see what's available
                    if (!_pendingZoomLoggedFirstFrame)
                    {
                        _pendingZoomLoggedFirstFrame = true;
                        VerboseLog("[FastVesselChanger] RestoreZoom start: target=" + _pendingZoom
                            + " current=" + cam.Distance
                            + " camDistField=" + (_camDistField != null ? _camDistField.Name : "NOT FOUND"));
                    }

                    // Pre-seed immediately when the target vessel becomes active.
                    cam.SetDistanceImmediate(_pendingZoom);
                    _camDistField?.SetValue(cam, _pendingZoom);
                    VerboseLog("[FastVesselChanger] RestoreZoom pre-seed applied: target=" + _pendingZoom + " final=" + cam.Distance);

                    // Aggressive lockout window — keep forcing zoom to block user scroll
                    // and other camera writers during the immediate post-load transition.
                    _zoomLockoutVesselId = _pendingZoomVesselId;
                    _zoomLockoutTarget = _pendingZoom;
                    _zoomLockoutUntilRealtime = Time.realtimeSinceStartup + ZOOM_LOCKOUT_SECONDS;
                    VerboseLog("[FastVesselChanger] RestoreZoom lockout started: seconds=" + ZOOM_LOCKOUT_SECONDS.ToString("F1") + " target=" + _zoomLockoutTarget);

                    // Pending restore is consumed; lockout enforces the value for a short window.
                    _pendingZoomVesselId = Guid.Empty;
                    _pendingZoomRestore = false;
                    _pendingZoomDeadlineRealtime = 0f;
                }
                else
                {
                    // Camera not ready yet — keep waiting until realtime deadline.
                    if (!_pendingZoomLoggedFirstFrame)
                    {
                        _pendingZoomLoggedFirstFrame = true;
                        VerboseLog("[FastVesselChanger] RestoreZoom: FlightCamera.fetch is null, waiting...", warning: true);
                    }
                }
            }
            else
            {
                // Vessel transition still in progress — FlightGlobals.ActiveVessel lags behind
                // SetActiveVessel by several frames (or longer on heavily modded installs).
                // Keep the restore request pending until the target vessel is actually active,
                // bounded by the realtime deadline set when the request is queued.
                VerboseLog("[FastVesselChanger] RestoreZoom waiting: active="
                    + (activeVessel != null ? activeVessel.id.ToString() : "null")
                    + " target=" + _pendingZoomVesselId.ToString());
            }
        }

        // During lockout we aggressively force zoom every frame for the target vessel.
        if (_zoomLockoutUntilRealtime > 0f && _zoomLockoutVesselId != Guid.Empty)
        {
            if (Time.realtimeSinceStartup > _zoomLockoutUntilRealtime)
            {
                VerboseLog("[FastVesselChanger] RestoreZoom lockout ended");
                _zoomLockoutVesselId = Guid.Empty;
                _zoomLockoutTarget = 0f;
                _zoomLockoutUntilRealtime = 0f;
            }
            else
            {
                var lockoutVessel = FlightGlobals.ActiveVessel;
                if (lockoutVessel != null && lockoutVessel.id == _zoomLockoutVesselId)
                {
                    var lockoutCam = FlightCamera.fetch;
                    if (lockoutCam != null)
                    {
                        lockoutCam.SetDistanceImmediate(_zoomLockoutTarget);
                        _camDistField?.SetValue(lockoutCam, _zoomLockoutTarget);
                    }
                }
            }
        }

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

    IEnumerator ApplyPitchOverridesEndOfFrame()
    {
        while (true)
        {
            yield return new WaitForEndOfFrame();

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
        if (!showWindow) return;
        
        // Force window height recalculation when collapsible sections change
        if (showTypeFilter != lastShowTypeFilter || showCameraControls != lastShowCameraControls
            || _showHullcamSection != _lastShowHullcamSection)
        {
            windowRect.height = 0; // Reset height to force GUILayout recalculation
            lastShowTypeFilter = showTypeFilter;
            lastShowCameraControls = showCameraControls;
            _lastShowHullcamSection = _showHullcamSection;
        }
        
        var prevRect = windowRect;
        windowRect = GUILayout.Window(GetInstanceID(), windowRect, DrawWindow, "Fast Vessel Changer",
            GUILayout.Width(FIXED_WINDOW_WIDTH));
        if (windowRect.x != prevRect.x || windowRect.y != prevRect.y)
            SaveUserPrefs();
    }

    void DrawWindow(int id)
    {
        GUILayout.BeginVertical();

        // ---- Vessel List ----
        int selectedCount = 0;
        foreach (bool sel in selected.Values)
            if (sel) selectedCount++;
        GUILayout.BeginHorizontal();
        GUILayout.Label("Vessels (" + selectedCount + " selected):");
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Refresh", GUILayout.Width(60)))
            RefreshSelectionsFromVessels();
        GUILayout.EndHorizontal();

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

        // Divider between vessel list and controls area.
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
                    pendingInterval = -1;
                }
                else
                {
                    pendingInterval = parsed;
                }
            }
            GUILayout.EndHorizontal();

            if (autoEnabled)
            {
                double effectiveInterval2 = Math.Max(switchInterval, MINIMUM_SWITCH_INTERVAL);
                double remaining2 = Math.Max(0, effectiveInterval2 - (Planetarium.GetUniversalTime() - lastSwitchTime));
                GUILayout.Label("Next switch in: " + remaining2.ToString("F0") + "s");
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Flight UI: " + (userPreferredUIVisible ? "VISIBLE" : "HIDDEN"), GUILayout.Width(120));
            if (GUILayout.Button("Toggle Flight UI", GUILayout.Width(120)))
            {
                userPreferredUIVisible = !userPreferredUIVisible;
                ToggleFlightHUD();
                SaveToScenario();
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

    void RestoreShuffleBagFromScenario(FastVesselChangerScenario? scen)
    {
        shuffleRemaining.Clear();

        if (cycleList.Count == 0)
        {
            VerboseLog("[FastVesselChanger] Shuffle bag restore skipped: cycle list is empty", warning: true);
            return;
        }

        if (scen == null || scen.shuffleRemainingVesselIds.Count == 0)
        {
            shuffleRemaining.AddRange(cycleList);
            VerboseLog("[FastVesselChanger] Shuffle bag restore: no persisted remainder, starting fresh round (" + shuffleRemaining.Count + " vessels)");
            return;
        }

        var cycleById = cycleList.ToDictionary(v => v.id, v => v);
        foreach (var id in scen.shuffleRemainingVesselIds)
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
            Debug.Log("[FastVesselChanger] Shuffle bag refilled for new round (" + shuffleRemaining.Count + " vessels)");
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

    void ToggleFlightHUD()
    {
        // Call UIMasterController directly — this is what KSP's F2 handler does internally.
        // It updates the internal UI state AND notifies all UI elements via the game events.
        if (userPreferredUIVisible)
            InvokeShowUI();
        else
            InvokeHideUI();

        Debug.Log("[FastVesselChanger] ToggleFlightHUD invoked " + (userPreferredUIVisible ? "ShowUI" : "HideUI"));
    }

    void SwitchToVessel(Vessel v)
    {
        if (v == null) return;

        if (!FlightGlobals.ready)
        {
            Debug.LogWarning("[FastVesselChanger] SwitchToVessel aborted: FlightGlobals not ready");
            return;
        }

        SaveUserPrefs();

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
                _vesselZooms[currentVessel.id] = cam.Distance;
                VerboseLog("[FastVesselChanger] Captured zoom before switch: vessel=" + currentVessel.vesselName + " id=" + currentVessel.id + " zoom=" + cam.Distance);
            }
            else
            {
                VerboseLog("[FastVesselChanger] Could not capture zoom before switch: FlightCamera.fetch is null or hull cam active", warning: true);
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

        // Start holding the UI hidden before the switch so no stage of the
        // vessel transition can sneak the HUD back in.
        if (!userPreferredUIVisible)
            RestartHoldUIHidden();

        try
        {
            FlightGlobals.SetActiveVessel(v);
        }
        catch (Exception e)
        {
            Debug.LogError("[FastVesselChanger] Warning: SetActiveVessel failed: " + e.Message);
            ScreenMessages.PostScreenMessage("FastVesselChanger: failed to activate vessel " + v.vesselName, 5f, ScreenMessageStyle.UPPER_CENTER);
            return;
        }

        try
        {
            var cam = FlightCamera.fetch;
            if (cam != null)
            {
                UnityEngine.Transform targetTransform = null;
                if (v.rootPart != null)
                {
                    targetTransform = v.rootPart.transform;
                }
                else if (v.parts != null && v.parts.Count > 0)
                {
                    targetTransform = v.parts[0].transform;
                }

                if (targetTransform != null)
                {
                    cam.SetTarget(targetTransform);
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[FastVesselChanger] Error focusing camera: " + e.Message);
        }

        // Restore window visibility after switch
        showWindow = windowWasVisible;

        // Queue zoom restore — Update() will apply it every frame until it sticks or times out
        float savedZoom;
        if (_vesselZooms.TryGetValue(v.id, out savedZoom))
        {
            _zoomLockoutVesselId = Guid.Empty;
            _zoomLockoutTarget = 0f;
            _zoomLockoutUntilRealtime = 0f;
            _pendingZoomVesselId = v.id;
            _pendingZoom = savedZoom;
            _pendingZoomRestore = true;
            _pendingZoomLoggedFirstFrame = false;
            _pendingZoomDeadlineRealtime = Time.realtimeSinceStartup + 30f;
            VerboseLog("[FastVesselChanger] RestoreZoom queued: vessel=" + v.vesselName + " id=" + v.id + " zoom=" + savedZoom);
        }
        else
        {
            _zoomLockoutVesselId = Guid.Empty;
            _zoomLockoutTarget = 0f;
            _zoomLockoutUntilRealtime = 0f;
            VerboseLog("[FastVesselChanger] RestoreZoom skipped: no saved zoom for vessel=" + v.vesselName + " id=" + v.id);
        }

        // Randomize camera rotation rates if enabled
        if (cameraRotRandomEnabled)
        {
            bool isGrounded = v.situation == Vessel.Situations.LANDED
                || v.situation == Vessel.Situations.PRELAUNCH
                || v.situation == Vessel.Situations.SPLASHED;

            // Each axis is uniform [-4, 4]. Resample until the sum of magnitudes is >= 1.10,
            // guaranteeing a noticeable combined rotation. When grounded, pitch (X) is forced
            // to 0 so the constraint effectively becomes |Y| >= 1.10.
            float x, y;
            do
            {
                x = isGrounded ? 0f : UnityEngine.Random.Range(-2.0f, 2.0f);
                y = UnityEngine.Random.Range(-2.0f, 2.0f);
            } while (Mathf.Abs(x) + Mathf.Abs(y) < 0.90f);

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
            _loadedTypeFiltersFromUserPrefs = false;
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

            showCameraControls = ParseBool(root["ShowCameraControls"]?.InnerText, false);
            lastShowCameraControls = showCameraControls;
            showTypeFilter = ParseBool(root["ShowTypeFilter"]?.InnerText, false);
            lastShowTypeFilter = showTypeFilter;
            _showHullcamSection = ParseBool(root["ShowHullcamSection"]?.InnerText, true);
            _lastShowHullcamSection = _showHullcamSection;
            _writeLMPPlayersLog = ParseBool(root["WriteLMPPlayersLog"]?.InnerText, false);
            _writeVesselLog = ParseBool(root["WriteVesselLog"]?.InnerText, false);
            _writeCameraLog = ParseBool(root["WriteCameraLog"]?.InnerText, false);

            XmlElement filters = root["TypeFilters"];
            if (filters != null)
            {
                var enabledTypes = filters.GetElementsByTagName("Filter")
                    .OfType<XmlElement>()
                    .Select(filter => filter.GetAttribute("name"))
                    .Where(name => !string.IsNullOrEmpty(name));
                ApplyEnabledTypeFilters(enabledTypes);
                _loadedTypeFiltersFromUserPrefs = true;
            }

            XmlElement window = root["Window"];
            if (window == null)
                return;

            float wx = ParseFloat(window.GetAttribute("x"), windowRect.x);
            float wy = ParseFloat(window.GetAttribute("y"), windowRect.y);
            float ww = ParseFloat(window.GetAttribute("width"), windowRect.width);
            float wh = ParseFloat(window.GetAttribute("height"), windowRect.height);
            windowRect = new Rect(wx, wy, Mathf.Max(FIXED_WINDOW_WIDTH, ww), Mathf.Max(MIN_WINDOW_HEIGHT, wh));
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

            var doc = new XmlDocument();
            XmlElement root = doc.CreateElement("FastVesselChangerUserPrefs");
            doc.AppendChild(root);

            XmlElement showCameraControlsNode = doc.CreateElement("ShowCameraControls");
            showCameraControlsNode.InnerText = showCameraControls.ToString();
            root.AppendChild(showCameraControlsNode);

            XmlElement showTypeFilterNode = doc.CreateElement("ShowTypeFilter");
            showTypeFilterNode.InnerText = showTypeFilter.ToString();
            root.AppendChild(showTypeFilterNode);

            XmlElement showHullcamSectionNode = doc.CreateElement("ShowHullcamSection");
            showHullcamSectionNode.InnerText = _showHullcamSection.ToString();
            root.AppendChild(showHullcamSectionNode);

            XmlElement filtersNode = doc.CreateElement("TypeFilters");
            foreach (var kv in vesselTypeFilter.Where(kv => kv.Value))
            {
                XmlElement filterNode = doc.CreateElement("Filter");
                filterNode.SetAttribute("name", kv.Key);
                filtersNode.AppendChild(filterNode);
            }
            root.AppendChild(filtersNode);

            XmlElement windowNode = doc.CreateElement("Window");
            windowNode.SetAttribute("x", windowRect.x.ToString(CultureInfo.InvariantCulture));
            windowNode.SetAttribute("y", windowRect.y.ToString(CultureInfo.InvariantCulture));
            windowNode.SetAttribute("width", windowRect.width.ToString(CultureInfo.InvariantCulture));
            windowNode.SetAttribute("height", windowRect.height.ToString(CultureInfo.InvariantCulture));
            root.AppendChild(windowNode);

            XmlElement writeLMPPlayersLogNode = doc.CreateElement("WriteLMPPlayersLog");
            writeLMPPlayersLogNode.InnerText = _writeLMPPlayersLog.ToString();
            root.AppendChild(writeLMPPlayersLogNode);

            XmlElement writeVesselLogNode = doc.CreateElement("WriteVesselLog");
            writeVesselLogNode.InnerText = _writeVesselLog.ToString();
            root.AppendChild(writeVesselLogNode);

            XmlElement writeCameraLogNode = doc.CreateElement("WriteCameraLog");
            writeCameraLogNode.InnerText = _writeCameraLog.ToString();
            root.AppendChild(writeCameraLogNode);

            doc.Save(prefsPath);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[FastVesselChanger] SaveUserPrefs error: " + e.Message);
        }
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
            // Use _instanceVesselId (pinned at Start) instead of FlightGlobals.ActiveVessel:
            // after SetActiveVessel, ActiveVessel points to the NEW vessel while the camera
            // still reflects the OLD vessel — writing cam.Distance against the new ID would
            // corrupt its zoom entry.  _instanceVesselId is always the vessel this instance
            // was created for.
            if (_instanceVesselId != Guid.Empty)
            {
                var cam = FlightCamera.fetch;
                // Only snapshot zoom when the stock FlightCamera is in control.
                // If a hull cam is active, cam.Distance reflects the hull cam mount
                // position, not the orbit view distance — don't overwrite the saved value.
                if (cam != null && _hullcamLastActivatedModule == null)
                    _vesselZooms[_instanceVesselId] = cam.Distance;
            }

            var scen = FastVesselChangerScenario.Instance;
            if (scen != null)
            {
                scen.switchInterval = switchInterval;
                scen.autoEnabled = autoEnabled;
                scen.showWindow = showWindow;
                scen.uiVisible = userPreferredUIVisible;
                scen.cameraRotEnabled = cameraRotEnabled;
                scen.cameraRotRandomEnabled = cameraRotRandomEnabled;
                scen.cameraRotXRate = cameraRotXRate;
                scen.cameraRotYRate = cameraRotYRate;
                scen.selectedVesselIds.Clear();
                scen.selectedVesselTypes.Clear();
                scen.shuffleRemainingVesselIds.Clear();
                scen.vesselZoomEntries.Clear();
                
                foreach (var kv in selected)
                {
                    if (kv.Value) scen.selectedVesselIds.Add(kv.Key.ToString());
                }
                
                foreach (var kvType in vesselTypeFilter)
                {
                    if (kvType.Value) scen.selectedVesselTypes.Add(kvType.Key);
                }

                foreach (var remaining in shuffleRemaining)
                {
                    if (remaining != null)
                        scen.shuffleRemainingVesselIds.Add(remaining.id.ToString());
                }

                foreach (var kvZoom in _vesselZooms)
                {
                    if (float.IsNaN(kvZoom.Value) || float.IsInfinity(kvZoom.Value)) continue;
                    scen.vesselZoomEntries.Add(kvZoom.Key + "|" + kvZoom.Value.ToString(CultureInfo.InvariantCulture));
                }

                // Sync active vessel's hull cam state to the dict before serializing
                SyncCurrentHullcamStateToDict();
                scen.vesselHullcamEntries.Clear();
                scen.vesselHullcamSelectedCams.Clear();
                foreach (var kvHc in _vesselHullcamSettings)
                {
                    var hs = kvHc.Value;
                    scen.vesselHullcamEntries.Add(kvHc.Key + "|" + hs.hullcamEnabled + "|" +
                        hs.hullcamInterval.ToString(CultureInfo.InvariantCulture) + "|" + hs.includeExternal);
                    foreach (var fid in hs.selectedFlightIds)
                        scen.vesselHullcamSelectedCams.Add(kvHc.Key + "|" + fid);
                }
                if (VERBOSE_DIAGNOSTICS)
                    Debug.Log("[FastVesselChanger] SaveToScenario: hullcamEntries=" + scen.vesselHullcamEntries.Count
                        + " hullcamCams=" + scen.vesselHullcamSelectedCams.Count
                        + " (activeVesselSelectedIds=" + _hullcamSelectedIds.Count + ")");
            }
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
        bool isActiveInstance = _activeInstance == this;

        if (isActiveInstance)
            _activeInstance = null;

        GameEvents.onShowUI.Remove(OnShowUI);
        GameEvents.onVesselLoaded.Remove(OnVesselLoaded);
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
        // FLIGHT→FLIGHT vessel switch the next scene is still FLIGHT and the new
        // FVC instance will re-apply the hidden state in Start().  Calling ShowUI
        // in that case causes a visible flicker — the UI pops in for several
        // frames until the new instance hides it again.
        bool leavingFlight = HighLogic.LoadedScene != GameScenes.FLIGHT;
        if (leavingFlight || userPreferredUIVisible)
        {
            try
            {
                InvokeShowUI();
                Debug.Log("[FastVesselChanger] Showing UI when leaving flight scene");
            }
            catch { }
        }
        
        RemoveAppLauncherButton("OnDestroy", forceClearShared: false);
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
}
