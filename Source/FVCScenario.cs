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

// Persistence scenario module — DISABLED.
// The class NO LONGER extends ScenarioModule because even without [KSPScenario],
// extending ScenarioModule causes KSP's ScenarioTypes scanner to discover it
// via reflection, and LunaMultiplayer syncs any ScenarioModule subclass found
// in saved game data — triggering a timing-dependent freeze during vessel load.
// All persistence is now handled via the XML user prefs file
// (PluginData/FastVesselChanger.xml).
// The class is kept as a compile-compatible stub so existing references compile.
public class FastVesselChangerScenario
{
    public int switchInterval = 300;
    public bool autoEnabled = false;
    public bool showWindow = true;
    public bool uiVisible = true;
    public bool cameraRotEnabled = false;
    public bool cameraRotRandomEnabled = false;
    public float cameraRotXRate = 0f;
    public float cameraRotYRate = 0f;
    public List<string> selectedVesselIds = new List<string>();
    public List<string> selectedVesselTypes = new List<string>();
    public List<string> shuffleRemainingVesselIds = new List<string>();
    public List<string> vesselZoomEntries = new List<string>();
    public List<string> vesselHullcamEntries = new List<string>();
    public List<string> vesselHullcamSelectedCams = new List<string>();

    // Instance is always null — class is a stub, never instantiated.
    public static FastVesselChangerScenario Instance { get { return null; } }
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
