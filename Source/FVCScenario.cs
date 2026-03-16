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
    public float cameraRotYRate = 0f;   // orbit deg/s (positive = right)
    public List<string> selectedVesselIds = new List<string>();
    public List<string> selectedVesselTypes = new List<string>();
    public List<string> shuffleRemainingVesselIds = new List<string>();
    public List<string> vesselZoomEntries = new List<string>();
    public List<string> vesselHullcamEntries = new List<string>();         // "guid|enabled|interval|includeExternal"
    public List<string> vesselHullcamSelectedCams = new List<string>();    // "guid|flightId" per selected camera

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

            // Save shuffle-bag remainder so vessel-switch reloads keep the current round intact
            string shuffleRemainingPrefix = MakePlayerKey("shuffleRemainingVesselId");
            foreach (var id in shuffleRemainingVesselIds)
            {
                node.AddValue(shuffleRemainingPrefix, id);
            }

            // Save per-vessel zoom levels
            string vesselZoomPrefix = MakePlayerKey("vesselZoom");
            foreach (var entry in vesselZoomEntries)
            {
                node.AddValue(vesselZoomPrefix, entry);
            }

            // Save per-vessel hull camera settings
            string vesselHullcamEntryPrefix = MakePlayerKey("vesselHullcamEntry");
            foreach (var entry in vesselHullcamEntries)
                node.AddValue(vesselHullcamEntryPrefix, entry);
            string vesselHullcamCamPrefix = MakePlayerKey("vesselHullcamCam");
            foreach (var entry in vesselHullcamSelectedCams)
                node.AddValue(vesselHullcamCamPrefix, entry);

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
            shuffleRemainingVesselIds.Clear();
            vesselZoomEntries.Clear();
            vesselHullcamEntries.Clear();
            vesselHullcamSelectedCams.Clear();
            
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

            // Load shuffle-bag remainder
            string shuffleRemainingPrefix = MakePlayerKey("shuffleRemainingVesselId");
            foreach (var id in node.GetValues(shuffleRemainingPrefix))
            {
                if (!string.IsNullOrEmpty(id)) shuffleRemainingVesselIds.Add(id);
            }

            // Load per-vessel zoom levels
            string vesselZoomPrefix = MakePlayerKey("vesselZoom");
            foreach (var z in node.GetValues(vesselZoomPrefix))
            {
                if (!string.IsNullOrEmpty(z)) vesselZoomEntries.Add(z);
            }

            // Load per-vessel hull camera settings
            foreach (var e in node.GetValues(MakePlayerKey("vesselHullcamEntry")))
                if (!string.IsNullOrEmpty(e)) vesselHullcamEntries.Add(e);
            foreach (var e in node.GetValues(MakePlayerKey("vesselHullcamCam")))
                if (!string.IsNullOrEmpty(e)) vesselHullcamSelectedCams.Add(e);

            Debug.Log("[FastVesselChanger] Loaded settings for player: " + playerPrefix + 
                     " (vessels: " + selectedVesselIds.Count + ", types: " + selectedVesselTypes.Count + ", remaining: " + shuffleRemainingVesselIds.Count + ", zooms: " + vesselZoomEntries.Count + ")");
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
