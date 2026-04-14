// FVCVesselSettings.cs — Per-vessel settings class + master dictionary.
//
// Replaces the previous scattered per-vessel dicts:
//   _vesselZooms, _vesselCameraTargets, _vesselControlFromHere,
//   _vesselSwitchIntervals, _vesselHullcamSettings,
//   _vesselAutoLightsEnabled, _modManagedLightsActive
//
// All per-vessel state is now in one VesselSettings entry keyed by vessel GUID.
// XML is stored as a single <VesselSettings> block with one <Vessel> per entry.

using System;
using System.Collections.Generic;

#pragma warning disable CS8618, CS8600, CS8601, CS8625, CS8603, CS8604

public partial class FastVesselChanger
{
    // =========================================================================
    // VesselSettings — all per-vessel persistent + runtime state
    // =========================================================================

    private class VesselSettings
    {
        // --- Camera ---
        public float  cameraZoom           = float.NaN;  // NaN = never captured
        public uint   cameraTargetFlightId = 0;

        // --- Control ---
        public uint   controlRefFlightId   = 0;

        // --- Auto-switch ---
        public int    switchInterval       = 0;           // 0 = use global default

        // --- Auto-lights ---
        public bool   autoLightsEnabled    = true;        // default ON for all vessels
        public bool   lightsManagedByMod   = false;       // runtime-only, never persisted

        // --- SAS ---
        public bool   sasOn                = false;
        public int    sasMode              = 0;           // VesselAutopilot.AutopilotMode as int

        // --- RCS ---
        public bool   rcsOn                = false;

        // --- Navball speed reference ---
        public int    speedMode            = -1;           // FlightGlobals.SpeedDisplayModes as int; -1 = never captured

        // --- Hull cameras --- (null = never configured for this vessel)
        public VesselHullcamSettings hullcam = null;

        /// <summary>True if any field holds non-default data worth persisting.</summary>
        public bool HasAnyPersistableData()
        {
            return !float.IsNaN(cameraZoom)
                || cameraTargetFlightId != 0
                || controlRefFlightId   != 0
                || switchInterval       != 0
                || !autoLightsEnabled
                || sasOn
                || rcsOn
                || speedMode != -1
                || hullcam != null;
        }
    }

    // =========================================================================
    // Master dictionary — replaces all previous per-vessel dicts
    // =========================================================================

    private static Dictionary<Guid, VesselSettings> _vesselSettings =
        new Dictionary<Guid, VesselSettings>();

    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>Returns the VesselSettings for the given ID, creating a default entry if absent.</summary>
    private static VesselSettings GetOrCreateVesselSettings(Guid id)
    {
        VesselSettings s;
        if (!_vesselSettings.TryGetValue(id, out s))
        {
            s = new VesselSettings();
            _vesselSettings[id] = s;
        }
        return s;
    }

    /// <summary>
    /// Returns the VesselHullcamSettings nested inside the VesselSettings for the given ID,
    /// creating both entries if absent.
    /// </summary>
    private static VesselHullcamSettings GetOrCreateHullcam(Guid id)
    {
        var vs = GetOrCreateVesselSettings(id);
        if (vs.hullcam == null) vs.hullcam = new VesselHullcamSettings();
        return vs.hullcam;
    }
}
