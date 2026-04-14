using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml;
using UnityEngine;

#pragma warning disable CS8618, CS8600, CS8601, CS8625, CS8603, CS8604

public partial class FastVesselChanger
{
    // =========================================================================
    // Preset system — save/load/clear vessel selection + per-vessel settings
    // =========================================================================

    private const int PRESET_COUNT = 5;

    /// Snapshot of all per-vessel settings at save time.
    private class PresetVesselData
    {
        public Guid vesselId;
        public float zoom = -1f;               // -1 = not captured
        public int switchInterval = -1;         // -1 = not captured (use global)
        public bool hullcamEnabled = false;
        public float hullcamInterval = 10f;
        public bool hullcamIncludeExternal = true;
        public HashSet<uint> hullcamSelectedFlightIds = new HashSet<uint>();
    }

    // Static: survives FLIGHT→FLIGHT scene reloads (vessel switches).
    private static List<PresetVesselData>[] _presets = InitPresetSlots();
    // Per-preset integrity flags: true = all vessels still exist; false = some missing.
    private static bool[] _presetIntegrity = new bool[PRESET_COUNT];

    private bool _showPresets = false;

    // Lazy-initialized GUIStyle for red preset labels (missing vessels)
    private GUIStyle _presetRedStyle = null;

    static List<PresetVesselData>[] InitPresetSlots()
    {
        var slots = new List<PresetVesselData>[PRESET_COUNT];
        for (int i = 0; i < PRESET_COUNT; i++)
            slots[i] = new List<PresetVesselData>();
        return slots;
    }

    // ---- UI ----

    void DrawPresetSection()
    {
        if (!_showPresets) return;

        // Lazy-init red style for labels indicating missing vessels
        if (_presetRedStyle == null)
        {
            _presetRedStyle = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = Color.red },
                fontStyle = FontStyle.Bold
            };
        }

        GUILayout.BeginVertical("box");

        for (int i = 0; i < PRESET_COUNT; i++)
        {
            GUILayout.BeginHorizontal();

            bool hasData = _presets[i].Count > 0;
            bool intact = _presetIntegrity[i];
            string label = "Preset " + (i + 1);
            if (hasData)
                label += " (" + _presets[i].Count + ")";

            // Show label in red if integrity check failed
            GUIStyle labelStyle = (hasData && !intact) ? _presetRedStyle : GUI.skin.label;
            GUILayout.Label(label, labelStyle, GUILayout.Width(90));

            if (GUILayout.Button("Save", GUILayout.Width(42)))
            {
                RefreshSelectionsFromVessels();
                SavePreset(i);
                RunPresetIntegrityCheck(i);
                SaveUserPrefs();
            }
            if (GUILayout.Button("Load", GUILayout.Width(42)))
            {
                RefreshSelectionsFromVessels();
                RunPresetIntegrityCheck(i);
                if (_presetIntegrity[i])
                    LoadPreset(i);
                else
                    LoadPresetPartial(i);
            }
            if (GUILayout.Button("Clear", GUILayout.Width(42)))
            {
                ClearPreset(i);
                SaveUserPrefs();
            }

            GUILayout.EndHorizontal();
        }

        GUILayout.EndVertical();
    }

    // ---- Operations ----

    void SavePreset(int index)
    {
        // Sync current vessel's hullcam state into the dict before snapshotting
        SyncCurrentHullcamStateToDict();

        // Snapshot current zoom for the active vessel
        var cam = FlightCamera.fetch;
        var av = FlightGlobals.ActiveVessel;
        if (cam != null && av != null && _hullcamLastActivatedModule == null)
            GetOrCreateVesselSettings(av.id).cameraZoom = cam.Distance;

        _presets[index].Clear();
        foreach (var kv in selected)
        {
            if (!kv.Value) continue;
            var d = new PresetVesselData { vesselId = kv.Key };

            // Zoom
            VesselSettings pvs;
            _vesselSettings.TryGetValue(kv.Key, out pvs);
            if (pvs != null && !float.IsNaN(pvs.cameraZoom))
                d.zoom = pvs.cameraZoom;

            // Switch interval
            if (pvs != null && pvs.switchInterval != 0)
                d.switchInterval = pvs.switchInterval;

            // Hullcam settings
            if (pvs != null && pvs.hullcam != null)
            {
                var hcs = pvs.hullcam;
                d.hullcamEnabled = hcs.hullcamEnabled;
                d.hullcamInterval = hcs.hullcamInterval;
                d.hullcamIncludeExternal = hcs.includeExternal;
                d.hullcamSelectedFlightIds = new HashSet<uint>(hcs.selectedFlightIds);
            }

            _presets[index].Add(d);
        }
    }

    void LoadPreset(int index)
    {
        // Deselect all
        foreach (var key in selected.Keys.ToList())
            selected[key] = false;

        // Select vessels and restore per-vessel settings
        foreach (var d in _presets[index])
        {
            if (selected.ContainsKey(d.vesselId))
                selected[d.vesselId] = true;

            ApplyPresetVesselData(d);
        }

        SaveToScenario();
        BuildCycleList();
    }

    void LoadPresetPartial(int index)
    {
        var preset = _presets[index];
        var valid = preset.Where(d => selected.ContainsKey(d.vesselId)).ToList();

        if (valid.Count == 0)
        {
            ScreenMessages.PostScreenMessage("Preset " + (index + 1) + ": no valid vessels found", 3f, ScreenMessageStyle.UPPER_CENTER);
            return;
        }

        // Deselect all, then select & restore valid
        foreach (var key in selected.Keys.ToList())
            selected[key] = false;
        foreach (var d in valid)
        {
            selected[d.vesselId] = true;
            ApplyPresetVesselData(d);
        }

        int missing = preset.Count - valid.Count;
        ScreenMessages.PostScreenMessage("Preset " + (index + 1) + ": loaded " + valid.Count + " vessels (" + missing + " missing)", 3f, ScreenMessageStyle.UPPER_CENTER);

        SaveToScenario();
        BuildCycleList();
    }

    /// Writes a single PresetVesselData's settings into the live per-vessel dictionaries.
    void ApplyPresetVesselData(PresetVesselData d)
    {
        // Zoom
        if (d.zoom >= 0f)
            GetOrCreateVesselSettings(d.vesselId).cameraZoom = d.zoom;

        // Switch interval
        if (d.switchInterval >= 0)
            GetOrCreateVesselSettings(d.vesselId).switchInterval = d.switchInterval;

        // Hullcam settings
        var hcs = GetOrCreateHullcam(d.vesselId);
        hcs.hullcamEnabled = d.hullcamEnabled;
        hcs.hullcamInterval = d.hullcamInterval;
        hcs.includeExternal = d.hullcamIncludeExternal;
        hcs.selectedFlightIds = new HashSet<uint>(d.hullcamSelectedFlightIds);
    }

    void ClearPreset(int index)
    {
        _presets[index].Clear();
        _presetIntegrity[index] = true;
    }

    void RunPresetIntegrityCheck(int index)
    {
        var preset = _presets[index];
        if (preset.Count == 0)
        {
            _presetIntegrity[index] = true;
            return;
        }

        // A preset is "intact" if every stored vessel ID still exists in the game
        var existingIds = new HashSet<Guid>();
        foreach (var v in FlightGlobals.Vessels)
        {
            if (v != null)
                existingIds.Add(v.id);
        }

        _presetIntegrity[index] = preset.All(d => existingIds.Contains(d.vesselId));
    }

    void RunAllPresetIntegrityChecks()
    {
        for (int i = 0; i < PRESET_COUNT; i++)
            RunPresetIntegrityCheck(i);
    }

    // ---- Persistence ----

    void SavePresetsToXml(XmlDocument doc, XmlElement playerSection)
    {
        XmlElement presetsNode = doc.CreateElement("Presets");
        for (int i = 0; i < PRESET_COUNT; i++)
        {
            if (_presets[i].Count == 0) continue;
            XmlElement presetNode = doc.CreateElement("Preset");
            presetNode.SetAttribute("index", i.ToString());
            foreach (var d in _presets[i])
            {
                XmlElement v = doc.CreateElement("Vessel");
                v.SetAttribute("id", d.vesselId.ToString());
                if (d.zoom >= 0f)
                    v.SetAttribute("zoom", d.zoom.ToString(CultureInfo.InvariantCulture));
                if (d.switchInterval >= 0)
                    v.SetAttribute("switchInterval", d.switchInterval.ToString());
                v.SetAttribute("hullcamEnabled", d.hullcamEnabled.ToString());
                v.SetAttribute("hullcamInterval", d.hullcamInterval.ToString(CultureInfo.InvariantCulture));
                v.SetAttribute("includeExternal", d.hullcamIncludeExternal.ToString());
                foreach (var fid in d.hullcamSelectedFlightIds)
                {
                    XmlElement cam = doc.CreateElement("SelectedCam");
                    cam.SetAttribute("flightId", fid.ToString());
                    v.AppendChild(cam);
                }
                presetNode.AppendChild(v);
            }
            presetsNode.AppendChild(presetNode);
        }
        playerSection.AppendChild(presetsNode);
    }

    void LoadPresetsFromXml(XmlElement playerSection)
    {
        // Reset all slots
        for (int i = 0; i < PRESET_COUNT; i++)
            _presets[i].Clear();

        XmlElement presetsNode = playerSection["Presets"];
        if (presetsNode == null) return;

        foreach (XmlElement presetNode in presetsNode.GetElementsByTagName("Preset"))
        {
            int index;
            if (!int.TryParse(presetNode.GetAttribute("index"), out index)) continue;
            if (index < 0 || index >= PRESET_COUNT) continue;

            foreach (XmlElement v in presetNode.GetElementsByTagName("Vessel"))
            {
                Guid id;
                if (!Guid.TryParse(v.GetAttribute("id"), out id)) continue;

                var d = new PresetVesselData { vesselId = id };

                float zoom;
                string zoomAttr = v.GetAttribute("zoom");
                if (!string.IsNullOrEmpty(zoomAttr) && float.TryParse(zoomAttr, NumberStyles.Float, CultureInfo.InvariantCulture, out zoom))
                    d.zoom = zoom;

                int interval;
                string intervalAttr = v.GetAttribute("switchInterval");
                if (!string.IsNullOrEmpty(intervalAttr) && int.TryParse(intervalAttr, out interval))
                    d.switchInterval = interval;

                bool hcEnabled;
                if (bool.TryParse(v.GetAttribute("hullcamEnabled"), out hcEnabled))
                    d.hullcamEnabled = hcEnabled;

                float hcInterval;
                string hcIntervalAttr = v.GetAttribute("hullcamInterval");
                if (!string.IsNullOrEmpty(hcIntervalAttr) && float.TryParse(hcIntervalAttr, NumberStyles.Float, CultureInfo.InvariantCulture, out hcInterval))
                    d.hullcamInterval = hcInterval;

                bool incExt;
                if (bool.TryParse(v.GetAttribute("includeExternal"), out incExt))
                    d.hullcamIncludeExternal = incExt;

                foreach (XmlElement cam in v.GetElementsByTagName("SelectedCam"))
                {
                    uint fid;
                    if (uint.TryParse(cam.GetAttribute("flightId"), out fid))
                        d.hullcamSelectedFlightIds.Add(fid);
                }

                _presets[index].Add(d);
            }
        }

        RunAllPresetIntegrityChecks();
    }
}
