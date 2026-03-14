using System;
using System.Collections.Generic;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Xml;
using UnityEngine;

#pragma warning disable CS8618, CS8600, CS8601, CS8625

/// <summary>
/// Helper class to detect and interact with LunaMultiplayer server
/// Uses reflection to avoid hard dependency on Luna DLLs
/// Prefixed FVC to avoid type-name collision with identically-named helpers
/// in other KSP mods loaded into the same AppDomain.
/// </summary>
public static class FVCLunaHelper
{
    private static bool? _isLunaAvailable = null;
    private static string _cachedPlayerName = null;

    /// <summary>
    /// Check if LunaMultiplayer is installed and active
    /// </summary>
    public static bool IsLunaEnabled
    {
        get
        {
            if (_isLunaAvailable == null)
            {
                _isLunaAvailable = DetectLunaMultiplayer();
            }
            return _isLunaAvailable.Value;
        }
    }

    private static bool DetectLunaMultiplayer()
    {
        try
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var a in assemblies)
            {
                // Look for LunaMultiplayer assemblies
                if (a.GetName().Name.Contains("LunaMultiplayer") || a.GetName().Name == "LMP.Client")
                {
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Get the current player's username from LunaMultiplayer
    /// Falls back to "SinglePlayer" if Luna is not available
    /// </summary>
    public static string GetCurrentPlayerName()
    {
        if (_cachedPlayerName != null)
            return _cachedPlayerName;

        try
        {
            if (IsLunaEnabled)
            {
                // Try to get from LunaMultiplayer.Client.Main.MyPlayer.PlayerName
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                Type mainType = null;

                foreach (var a in assemblies)
                {
                    mainType = a.GetType("LunaMultiplayer.Client.Main");
                    if (mainType == null) mainType = a.GetType("LMP.Client.Main");
                    if (mainType != null) break;
                }

                if (mainType != null)
                {
                    // Get MyPlayer property
                    var myPlayerProp = mainType.GetProperty("MyPlayer", BindingFlags.Public | BindingFlags.Static);
                    if (myPlayerProp != null)
                    {
                        var myPlayer = myPlayerProp.GetValue(null);
                        if (myPlayer != null)
                        {
                            // Get PlayerName property
                            var playerNameProp = myPlayer.GetType().GetProperty("PlayerName", BindingFlags.Public | BindingFlags.Instance);
                            if (playerNameProp != null)
                            {
                                var playerName = playerNameProp.GetValue(myPlayer) as string;
                                if (!string.IsNullOrEmpty(playerName))
                                {
                                    _cachedPlayerName = SanitizePlayerName(playerName!);
                                    Debug.Log("[FastVesselChanger] Detected Luna player: " + _cachedPlayerName);
                                    return _cachedPlayerName;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[FastVesselChanger] Error detecting Luna player name: " + e.Message);
        }

        // Fallback to single-player mode
        _cachedPlayerName = "SinglePlayer";
        return _cachedPlayerName;
    }

    /// <summary>
    /// Sanitize player name for use as a config key (remove special characters)
    /// </summary>
    private static string SanitizePlayerName(string playerName)
    {
        // Remove/replace characters that might break config parsing
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        foreach (char c in playerName)
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-')
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('_'); // Replace invalid chars with underscore
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Clear cached player name when switching servers/players
    /// Call this in relevant game events or on player switch
    /// </summary>
    public static void ClearCache()
    {
        _cachedPlayerName = null;
    }
}

// Persistence scenario module stores settings in the KSP save under SCENARIO
// Supports both single-player and LunaMultiplayer servers with per-player settings
[KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT)]
public class FastVesselChangerScenario : ScenarioModule
{
    public int switchInterval = 300;
    public bool autoEnabled = false;
    public bool showWindow = true;
    public bool uiVisible = true;
    public bool cameraRotEnabled = false;
    public bool cameraRotRandomEnabled = false;
    public float cameraRotXRate = 0f;   // pitch deg/s (positive = up)
    public float cameraRotYRate = 10f;  // orbit deg/s (positive = right)
    public bool zoomTrackingEnabled = true;
    public List<string> selectedVesselIds = new List<string>();
    public List<string> selectedVesselTypes = new List<string>();

    public static FastVesselChangerScenario Instance { get; private set; }

    /// <summary>
    /// Get the current player identifier for scenario storage
    /// Returns player name for multiplayer, "SinglePlayer" for single player
    /// </summary>
    private string GetPlayerKey()
    {
        return FVCLunaHelper.GetCurrentPlayerName();
    }

    /// <summary>
    /// Create a config key with player prefix for multiplayer compatibility
    /// Example: "Player1_selectedVesselId" or "SinglePlayer_switchInterval"
    /// </summary>
    private string MakePlayerKey(string baseKey)
    {
        return GetPlayerKey() + "_" + baseKey;
    }

    public override void OnSave(ConfigNode node)
    {
        base.OnSave(node);
        try
        {
            string playerPrefix = GetPlayerKey();

            // Save settings with player prefix
            string switchIntervalKey = MakePlayerKey("switchInterval");
            string autoEnabledKey = MakePlayerKey("autoEnabled");
            string showWindowKey = MakePlayerKey("showWindow");
            
            node.SetValue(switchIntervalKey, switchInterval.ToString(), true);
            node.SetValue(autoEnabledKey, autoEnabled.ToString(), true);
            node.SetValue(showWindowKey, showWindow.ToString(), true);
            node.SetValue(MakePlayerKey("uiVisible"), uiVisible.ToString(), true);
            node.SetValue(MakePlayerKey("cameraRotEnabled"), cameraRotEnabled.ToString(), true);
            node.SetValue(MakePlayerKey("cameraRotRandomEnabled"), cameraRotRandomEnabled.ToString(), true);
            node.SetValue(MakePlayerKey("cameraRotXRate"), cameraRotXRate.ToString(), true);
            node.SetValue(MakePlayerKey("cameraRotYRate"), cameraRotYRate.ToString(), true);
            node.SetValue(MakePlayerKey("zoomTrackingEnabled"), zoomTrackingEnabled.ToString(), true);

            // Save vessel selections (each as a separate value)
            string vesselIdPrefix = MakePlayerKey("selectedVesselId");
            foreach (var id in selectedVesselIds)
            {
                node.AddValue(vesselIdPrefix, id);
            }

            // Save vessel type filters
            string vesselTypePrefix = MakePlayerKey("selectedVesselType");
            foreach (var type in selectedVesselTypes)
            {
                node.AddValue(vesselTypePrefix, type);
            }

            Debug.Log("[FastVesselChanger] Saved settings for player: " + playerPrefix);
        }
        catch (Exception e)
        {
            Debug.LogError("[FastVesselChanger] Scenario OnSave error: " + e.Message);
        }
    }

    public override void OnLoad(ConfigNode node)
    {
        base.OnLoad(node);
        try
        {
            selectedVesselIds.Clear();
            selectedVesselTypes.Clear();
            
            string playerPrefix = GetPlayerKey();

            // Load settings with player prefix
            string switchIntervalKey = MakePlayerKey("switchInterval");
            string autoEnabledKey = MakePlayerKey("autoEnabled");
            string showWindowKey = MakePlayerKey("showWindow");

            if (node.HasValue(switchIntervalKey))
            {
                int.TryParse(node.GetValue(switchIntervalKey), out switchInterval);
            }
            if (node.HasValue(autoEnabledKey))
            {
                bool.TryParse(node.GetValue(autoEnabledKey), out autoEnabled);
            }
            if (node.HasValue(showWindowKey))
            {
                bool.TryParse(node.GetValue(showWindowKey), out showWindow);
            }
            string uiVisibleKey = MakePlayerKey("uiVisible");
            if (node.HasValue(uiVisibleKey))
                bool.TryParse(node.GetValue(uiVisibleKey), out uiVisible);
            string camRotEnabledKey = MakePlayerKey("cameraRotEnabled");
            if (node.HasValue(camRotEnabledKey))
                bool.TryParse(node.GetValue(camRotEnabledKey), out cameraRotEnabled);
            string camRotRandomKey = MakePlayerKey("cameraRotRandomEnabled");
            if (node.HasValue(camRotRandomKey))
                bool.TryParse(node.GetValue(camRotRandomKey), out cameraRotRandomEnabled);
            string camRotXKey = MakePlayerKey("cameraRotXRate");
            if (node.HasValue(camRotXKey))
                float.TryParse(node.GetValue(camRotXKey), out cameraRotXRate);
            string camRotYKey = MakePlayerKey("cameraRotYRate");
            if (node.HasValue(camRotYKey))
                float.TryParse(node.GetValue(camRotYKey), out cameraRotYRate);
            string zoomTrackKey = MakePlayerKey("zoomTrackingEnabled");
            if (node.HasValue(zoomTrackKey))
                bool.TryParse(node.GetValue(zoomTrackKey), out zoomTrackingEnabled);

            // Load vessel selections
            string vesselIdPrefix = MakePlayerKey("selectedVesselId");
            foreach (var v in node.GetValues(vesselIdPrefix))
            {
                if (!string.IsNullOrEmpty(v)) selectedVesselIds.Add(v);
            }

            // Load vessel type filters
            string vesselTypePrefix = MakePlayerKey("selectedVesselType");
            foreach (var t in node.GetValues(vesselTypePrefix))
            {
                if (!string.IsNullOrEmpty(t)) selectedVesselTypes.Add(t);
            }

            Debug.Log("[FastVesselChanger] Loaded settings for player: " + playerPrefix + 
                     " (vessels: " + selectedVesselIds.Count + ", types: " + selectedVesselTypes.Count + ")");
        }
        catch (Exception e)
        {
            Debug.LogError("[FastVesselChanger] Scenario OnLoad error: " + e.Message);
        }
    }

    public override void OnAwake()
    {
        base.OnAwake();
        Instance = this;
    }
}

// Prefixed FVC to avoid type-name collision with identically-named helpers in other mods.
public static class FVCPersistenceHelpers
{
    // Serialize a list of Guids to a list of strings
    public static List<string> SerializeGuidList(List<Guid> guids)
    {
        var outList = new List<string>();
        if (guids == null) return outList;
        foreach (var g in guids)
        {
            outList.Add(g.ToString());
        }
        return outList;
    }

    // Parse list of GUID strings to Guid list (invalid entries are skipped)
    public static List<Guid> ParseGuidList(List<string> strs)
    {
        var outList = new List<Guid>();
        if (strs == null) return outList;
        foreach (var s in strs)
        {
            try
            {
                var g = new Guid(s);
                outList.Add(g);
            }
            catch { }
        }
        return outList;
    }
}

[KSPAddon(KSPAddon.Startup.Flight, false)]
public class FastVesselChanger : MonoBehaviour
{
    private static FastVesselChanger _activeInstance;
    private const string USER_PREFS_FILE_NAME = "FastVesselChanger.xml";

    // Minimum time (seconds) between any two switches to prevent rapid switching
    private const double MINIMUM_SWITCH_INTERVAL = 10.0;

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
    private bool zoomTrackingEnabled = false;
    private float cameraRotXRate = 0f;   // pitch deg/s (positive = up)
    private float cameraRotYRate = 10f;  // orbit deg/s (positive = right)
    private string cameraRotXText = "0";
    private string cameraRotYText = "10";

    // Widened pitch limits — cached originals restored when auto-rotation is disabled
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
    // Pending zoom — applied every frame in Update() for up to N frames after a vessel switch
    private static Guid _pendingZoomVesselId = Guid.Empty;
    private static float _pendingZoom = 0f;
    private static int _zoomRestoreFrames = 0;
    private static FieldInfo _camDistField = null; // cached reflection handle

    // Cached reflection handles for UIMasterController (the class that actually hides/shows KSP's HUD)
    private object _uiMasterInstance = null;
    private MethodInfo _uiHideMethod = null;
    private MethodInfo _uiShowMethod = null;
    private Coroutine _uiHoldCoroutine = null;

    private List<Vessel> cycleList = new List<Vessel>();      // full selected vessel list
    private List<Vessel> shuffleRemaining = new List<Vessel>(); // vessels not yet visited this round
    private object _appButton = null;
    private static object _sharedAppButton = null;
    private bool _isAddingAppButton = false;
    private Coroutine _retryButtonCoroutine = null;
    
    // Guard against multiple switches in the same frame
    private int lastFrameCount = -1;

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
                zoomTrackingEnabled = scen.zoomTrackingEnabled;
                // Window position and local filter prefs are loaded from the XML user prefs in LoadUserPrefs().
                selected.Clear();
                foreach (var id in scen.selectedVesselIds)
                {
                    try
                    {
                        var g = new Guid(id);
                        selected[g] = true;
                    }
                    catch { }
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
    }

    void CacheUIMasterController()
    {
        try
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = a.GetType("UIMasterController");
                if (t == null) continue;

                var instanceProp = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                if (instanceProp != null)
                    _uiMasterInstance = instanceProp.GetValue(null);

                _uiHideMethod = t.GetMethod("HideUI", BindingFlags.Public | BindingFlags.Instance);
                _uiShowMethod = t.GetMethod("ShowUI", BindingFlags.Public | BindingFlags.Instance);

                Debug.Log("[FastVesselChanger] UIMasterController found: HideUI=" + (_uiHideMethod != null) + ", ShowUI=" + (_uiShowMethod != null));
                break;
            }
            if (_uiMasterInstance == null)
                Debug.LogWarning("[FastVesselChanger] UIMasterController.Instance not found — falling back to event firing");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[FastVesselChanger] CacheUIMasterController failed: " + e.Message);
        }
    }

    // Calls UIMasterController.HideUI() — this is what KSP's F2 handler uses internally.
    // We ALSO always fire onHideUI directly because vessel transitions can call
    // GameEvents.onShowUI.Fire() directly (bypassing UIMasterController), which re-shows
    // UI elements without changing UIMasterController.showUI. In that state HideUI() may
    // return early due to an internal "already hidden" guard, so the direct event is needed
    // to reach the individual UI elements regardless.
    void InvokeHideUI()
    {
        if (_uiMasterInstance != null && _uiHideMethod != null)
        {
            try { _uiHideMethod.Invoke(_uiMasterInstance, null); }
            catch (Exception e) { Debug.LogWarning("[FastVesselChanger] UIMasterController.HideUI() failed: " + e.Message); }
        }
        GameEvents.onHideUI.Fire();
    }

    // Calls UIMasterController.ShowUI() — same reasoning as InvokeHideUI.
    void InvokeShowUI()
    {
        if (_uiMasterInstance != null && _uiShowMethod != null)
        {
            try
            {
                _uiShowMethod.Invoke(_uiMasterInstance, null);
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

    IEnumerator HoldUIHidden()
    {
        float deadline = Time.time + 6f; // cover full vessel load pipeline
        while (Time.time < deadline)
        {
            if (!userPreferredUIVisible)
                InvokeHideUI();
            else
                yield break; // user chose to show — stop fighting it
            yield return null; // suppress every frame — eliminates frame gaps where UI can flash
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
            SaveToScenario();
        }

        if (autoEnabled)
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

        // Zoom restore — runs every frame until the distance sticks or the counter expires
        if (_zoomRestoreFrames > 0 && _pendingZoomVesselId != Guid.Empty)
        {
            var activeVessel = FlightGlobals.ActiveVessel;
            if (activeVessel != null && activeVessel.id == _pendingZoomVesselId)
            {
                var cam = FlightCamera.fetch;
                if (cam != null)
                {
                    // Cache the backing field once
                    if (_camDistField == null)
                    {
                        _camDistField = cam.GetType().GetField("camDistance",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        // First time: log all distance-related float fields so we can verify the right name
                        foreach (var f in cam.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                            if (f.FieldType == typeof(float) && f.Name.ToLower().Contains("dist"))
                                Debug.Log("[FastVesselChanger] FlightCamera float field: " + f.Name + " = " + f.GetValue(cam));
                    }

                    // Log on the very first frame so we can see what's available
                    if (_zoomRestoreFrames == 240)
                        Debug.Log("[FastVesselChanger] RestoreZoom start: target=" + _pendingZoom
                            + " current=" + cam.Distance
                            + " camDistField=" + (_camDistField != null ? _camDistField.Name : "NOT FOUND"));

                    cam.SetDistanceImmediate(_pendingZoom);
                    _camDistField?.SetValue(cam, _pendingZoom);
                    _zoomRestoreFrames--;
                    if (_zoomRestoreFrames == 0)
                    {
                        Debug.Log("[FastVesselChanger] RestoreZoom done: final Distance=" + cam.Distance);
                        _pendingZoomVesselId = Guid.Empty;
                    }
                }
                else
                {
                    // cam null — decrement so we don't stall forever, log once
                    if (_zoomRestoreFrames == 240)
                        Debug.LogWarning("[FastVesselChanger] RestoreZoom: FlightCamera.fetch is null on frame 240");
                    _zoomRestoreFrames--;
                }
            }
            else
            {
                // Vessel transition still in progress — FlightGlobals.ActiveVessel lags behind
                // SetActiveVessel by several frames. Don't cancel; let the counter expire naturally
                // so the restore fires as soon as the new vessel becomes active.
                _zoomRestoreFrames--;
                if (_zoomRestoreFrames == 0)
                    _pendingZoomVesselId = Guid.Empty;
            }
        }

        if (cameraRotEnabled)
        {
            var cam = FlightCamera.fetch;
            if (cam != null)
            {
                // Ensure limits are widened even if cam was null when the button was pressed
                if (!_pitchLimitsWidened)
                    WidenPitchLimits();

                if (cameraRotYRate != 0f)
                    cam.camHdg += cameraRotYRate * Mathf.Deg2Rad * Time.deltaTime;
                if (cameraRotXRate != 0f)
                {
                    float newPitch = cam.camPitch + cameraRotXRate * Mathf.Deg2Rad * Time.deltaTime;
                    if (newPitch < cam.minPitch || newPitch > cam.maxPitch)
                    {
                        cameraRotXRate = -cameraRotXRate;
                        cameraRotXText = cameraRotXRate.ToString("F1");
                        newPitch = Mathf.Clamp(newPitch, cam.minPitch, cam.maxPitch);
                        SaveToScenario();
                    }
                    cam.camPitch = newPitch;
                }
            }
        }
    }

    void OnGUI()
    {
        if (!showWindow) return;
        
        // Force window height recalculation when collapsible sections change
        if (showTypeFilter != lastShowTypeFilter || showCameraControls != lastShowCameraControls)
        {
            windowRect.height = 0; // Reset height to force GUILayout recalculation
            lastShowTypeFilter = showTypeFilter;
            lastShowCameraControls = showCameraControls;
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
        int selectedCount = selected.Values.Count(v => v);
        GUILayout.Label("Vessels (" + selectedCount + " selected):");

        // Search bar
        GUILayout.BeginHorizontal();
        GUILayout.Label("Search:", GUILayout.Width(50));
        vesselSearchText = GUILayout.TextField(vesselSearchText, GUILayout.ExpandWidth(true));
        if (!string.IsNullOrEmpty(vesselSearchText) && GUILayout.Button("X", GUILayout.Width(24)))
            vesselSearchText = "";
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Deselect All", GUILayout.Width(100)))
        {
            foreach (var key in selected.Keys.ToList())
                selected[key] = false;
            SaveToScenario();
            BuildCycleList();
        }
        if (GUILayout.Button((showTypeFilter ? "[-] " : "[+] ") + "Filter", GUILayout.ExpandWidth(true)))
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

        string searchLower = vesselSearchText.ToLowerInvariant();
        bool anyVisible = false;
        foreach (Vessel v in FlightGlobals.Vessels.Where(v => v != null).OrderBy(v => v.vesselName))
        {
            if (!IsVesselTypeEnabled(v.vesselType.ToString())) continue;
            if (!string.IsNullOrEmpty(vesselSearchText) && !v.vesselName.ToLowerInvariant().Contains(searchLower)) continue;

            anyVisible = true;
            bool prev = false;
            if (!selected.TryGetValue(v.id, out prev))
            {
                prev = false;
                selected[v.id] = prev;
            }

            GUILayout.BeginHorizontal();
            bool toggled = GUILayout.Toggle(prev, "");
            if (toggled != prev)
            {
                selected[v.id] = toggled;
                SaveToScenario();
                BuildCycleList();
            }
            GUILayout.Label(v.vesselName + "  [" + v.vesselType + "]");
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("GO", GUILayout.Width(30)))
            {
                if (zoomTrackingEnabled)
                {
                    var currentVessel = FlightGlobals.ActiveVessel;
                    if (currentVessel != null)
                    {
                        var cam = FlightCamera.fetch;
                        if (cam != null) _vesselZooms[currentVessel.id] = cam.Distance;
                    }
                }
                SwitchToVessel(v);
                lastSwitchedVesselId = v.id;
                lastSwitchTime = Planetarium.GetUniversalTime();
                shuffleRemaining.RemoveAll(sv => sv != null && sv.id == v.id);
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

            // ---- Auto Switch Controls ----
            GUILayout.BeginHorizontal();
            GUILayout.Label("Interval (s):", GUILayout.Width(80));
            switchIntervalText = GUILayout.TextField(switchIntervalText, GUILayout.Width(55));
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
            if (pendingInterval > 0)
                GUILayout.Label("(pending)", GUILayout.ExpandWidth(false));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(autoEnabled ? "Stop Auto" : "Start Auto", GUILayout.Width(100)))
            {
                ToggleAuto();
            }
            if (GUILayout.Button("Next Now", GUILayout.Width(90)))
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
            if (GUILayout.Button("Refresh List", GUILayout.Width(90)))
            {
                RefreshSelectionsFromVessels();
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
            GUILayout.Label("Auto Rotation: " + (cameraRotEnabled ? "ON" : "OFF"), GUILayout.Width(150));
            if (GUILayout.Button(cameraRotEnabled ? "Disable" : "Enable", GUILayout.Width(70)))
            {
                cameraRotEnabled = !cameraRotEnabled;
                if (cameraRotEnabled)
                    WidenPitchLimits();
                else
                    RestorePitchLimits();
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

            GUILayout.BeginHorizontal();
            GUILayout.Label("Zoom tracking:", GUILayout.Width(150));
            if (GUILayout.Button(zoomTrackingEnabled ? "ON" : "OFF", GUILayout.Width(40)))
            {
                zoomTrackingEnabled = !zoomTrackingEnabled;
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
                    try
                    {
                        var g = new Guid(id);
                        if (existing.Contains(g)) selected[g] = true;
                    }
                    catch { }
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

    void BuildCycleList()
    {
        cycleList.Clear();
        foreach (var v in FlightGlobals.Vessels)
        {
            if (v == null) continue;
            bool included;
            if (selected.TryGetValue(v.id, out included) && included)
                cycleList.Add(v);
        }
        // Reset the shuffle bag so the next switch starts a fresh random round
        shuffleRemaining.Clear();
        shuffleRemaining.AddRange(cycleList);
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

        SaveToScenario();
        SaveUserPrefs();

        bool windowWasVisible = showWindow;

        // Save zoom level for the current vessel before switching away
        if (zoomTrackingEnabled)
        {
            var currentVessel = FlightGlobals.ActiveVessel;
            if (currentVessel != null)
            {
                var cam = FlightCamera.fetch;
                if (cam != null)
                    _vesselZooms[currentVessel.id] = cam.Distance;
            }
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
        if (zoomTrackingEnabled)
        {
            float savedZoom;
            if (_vesselZooms.TryGetValue(v.id, out savedZoom))
            {
                _pendingZoomVesselId = v.id;
                _pendingZoom = savedZoom;
                _zoomRestoreFrames = 240; // ~4s at 60fps — covers full vessel load pipeline
            }
        }

        // Randomize camera rotation rates if enabled
        if (cameraRotRandomEnabled)
        {
            cameraRotXRate = UnityEngine.Random.Range(-1.5f, 1.5f);
            cameraRotYRate = UnityEngine.Random.Range(-1.5f, 1.5f);
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
                if (TryLoadLegacyUserPrefs())
                {
                    SaveUserPrefs();
                }
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

    bool TryLoadLegacyUserPrefs()
    {
        try
        {
            var cfg = KSP.IO.PluginConfiguration.CreateForType<FastVesselChanger>();
            cfg.load();
            showCameraControls = cfg.GetValue<bool>("showCameraControls", false);
            lastShowCameraControls = showCameraControls;
            float wx = cfg.GetValue<float>("windowX", windowRect.x);
            float wy = cfg.GetValue<float>("windowY", windowRect.y);
            float ww = cfg.GetValue<float>("windowW", windowRect.width);
            float wh = cfg.GetValue<float>("windowH", windowRect.height);
            windowRect = new Rect(wx, wy, Mathf.Max(FIXED_WINDOW_WIDTH, ww), Mathf.Max(MIN_WINDOW_HEIGHT, wh));
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[FastVesselChanger] Legacy user prefs migration failed: " + e.Message);
            return false;
        }
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
                scen.zoomTrackingEnabled = zoomTrackingEnabled;
                scen.selectedVesselIds.Clear();
                scen.selectedVesselTypes.Clear();
                
                foreach (var kv in selected)
                {
                    if (kv.Value) scen.selectedVesselIds.Add(kv.Key.ToString());
                }
                
                foreach (var kvType in vesselTypeFilter)
                {
                    if (kvType.Value) scen.selectedVesselTypes.Add(kvType.Key);
                }
                
                try
                {
                    var saveNode = new ConfigNode("CAMERASWITCHER_SAVE");
                    scen.OnSave(saveNode);
                }
                catch { }
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

    // Widen FlightCamera pitch limits to the full sphere so stock clamping does not block
    // auto-rotation. Original values are cached and restored when rotation is disabled.
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
        cam.minPitch = -Mathf.PI;
        cam.maxPitch =  Mathf.PI;
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

        if (!isActiveInstance)
        {
            Debug.Log("[FastVesselChanger] Skipping persistence for duplicate destroyed flight addon instance.");
            RemoveAppLauncherButton("OnDestroy", forceClearShared: false);
            return;
        }

        SaveToScenario(); // Save state before destroying
        SaveUserPrefs();

        // Restore stock pitch limits before leaving flight scene
        RestorePitchLimits();

        // Ensure UI is visible when leaving flight scene so player isn't stuck with hidden UI
        try
        {
            InvokeShowUI();
            Debug.Log("[FastVesselChanger] Showing UI when leaving flight scene");
        }
        catch { }
        
        RemoveAppLauncherButton("OnDestroy", forceClearShared: false);
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

// Minimal addon that registers the stock AppLauncher button in Space Center and Tracking Station.
// The main FastVesselChanger addon (Flight) handles Flight and Map View.
[KSPAddon(KSPAddon.Startup.SpaceCentre | KSPAddon.Startup.TrackingStation, false)]
public class FastVesselChangerNonFlight : MonoBehaviour
{
    private object _appButton = null;

    void Start()
    {
        GameEvents.onGUIApplicationLauncherReady.Add(OnAppLauncherReady);
        GameEvents.onGUIApplicationLauncherUnreadifying.Add(OnAppLauncherUnreadifying);
        StartCoroutine(RetryButton());
    }

    void OnAppLauncherReady() { AddButton(); }

    void OnAppLauncherUnreadifying(GameScenes _) { RemoveButton("onGUIApplicationLauncherUnreadifying"); }

    System.Collections.IEnumerator RetryButton()
    {
        for (int i = 0; i < 15 && _appButton == null; i++)
        {
            AddButton();
            if (_appButton != null) yield break;
            yield return new UnityEngine.WaitForSeconds(1f);
        }
    }

    void AddButton()
    {
        if (_appButton != null) return;
        try
        {
            Type alType = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                alType = a.GetType("ApplicationLauncher") ?? a.GetType("KSP.UI.Screens.ApplicationLauncher");
                if (alType != null) break;
            }
            if (alType == null) return;

            var readyProp = alType.GetProperty("Ready", BindingFlags.Public | BindingFlags.Static);
            if (readyProp == null || !(bool)readyProp.GetValue(null, null)) return;

            var instanceProp = alType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            var instance = instanceProp?.GetValue(null, null);
            if (instance == null) return;

            Type appScenesType = alType.GetNestedType("AppScenes", BindingFlags.Public);
            object scenes;
            if (appScenesType != null)
            {
                int flags = 0;
                foreach (var n in new[] { "SPACECENTER", "TRACKINGSTATION" })
                    try { flags |= (int)Enum.Parse(appScenesType, n); } catch { }
                if (flags == 0) flags = 1; // SPACECENTER numeric fallback
                scenes = Enum.ToObject(appScenesType, flags);
            }
            else
            {
                scenes = 1 | 32; // SPACECENTER | TRACKINGSTATION numeric fallback
            }

            Type ruitType = alType.Assembly.GetType("RUIToggleButton");
            Type onTrueType = ruitType?.GetNestedType("OnTrue");
            Type onFalseType = ruitType?.GetNestedType("OnFalse");
            Delegate onTrue = onTrueType != null
                ? Delegate.CreateDelegate(onTrueType, this, "OnTrue")
                : (Delegate)(UnityEngine.Events.UnityAction)OnTrue;
            Delegate onFalse = onFalseType != null
                ? Delegate.CreateDelegate(onFalseType, this, "OnFalse")
                : (Delegate)(UnityEngine.Events.UnityAction)OnFalse;

            Texture2D icon = GameDatabase.Instance.GetTexture("FastVesselChanger/Textures/icon", false);
            if (icon == null) icon = new Texture2D(1, 1);

            var addMethod = alType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "AddModApplication" && m.GetParameters().Length == 8);
            if (addMethod != null)
                _appButton = addMethod.Invoke(instance, new object[] { onTrue, onFalse, null, null, null, null, scenes, icon });
        }
        catch (Exception e)
        {
            Debug.LogWarning("[FastVesselChanger] NonFlight button failed: " + e.Message);
        }
    }

    void OnTrue() { }
    void OnFalse() { }

    void OnDestroy()
    {
        GameEvents.onGUIApplicationLauncherReady.Remove(OnAppLauncherReady);
        GameEvents.onGUIApplicationLauncherUnreadifying.Remove(OnAppLauncherUnreadifying);
        RemoveButton("OnDestroy");
    }

    void RemoveButton(string source)
    {
        if (_appButton == null) return;
        try
        {
            Type alType = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                alType = a.GetType("ApplicationLauncher") ?? a.GetType("KSP.UI.Screens.ApplicationLauncher");
                if (alType != null) break;
            }
            if (alType != null)
            {
                var instanceProp = alType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                var instance = instanceProp?.GetValue(null, null);
                var rem = alType.GetMethod("RemoveModApplication", BindingFlags.Public | BindingFlags.Instance);
                if (rem != null && instance != null)
                    rem.Invoke(instance, new object[] { _appButton });
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[FastVesselChanger] NonFlight button remove failed (" + source + "): " + e.Message);
        }
        _appButton = null;
    }
}
