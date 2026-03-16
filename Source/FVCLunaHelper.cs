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

/// <summary>
/// Helper class to detect and interact with LunaMultiplayer server
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
                var assemblyName = a.GetName().Name ?? string.Empty;
                if (assemblyName.IndexOf("LunaMultiplayer", StringComparison.OrdinalIgnoreCase) >= 0
                    || assemblyName.Equals("LMP.Client", StringComparison.OrdinalIgnoreCase)
                    || assemblyName.Equals("LmpClient", StringComparison.OrdinalIgnoreCase))
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
        {
            if (!string.Equals(_cachedPlayerName, "SinglePlayer", StringComparison.OrdinalIgnoreCase) || !IsLunaEnabled)
                return _cachedPlayerName;

            // If Luna is enabled and we previously fell back to SinglePlayer,
            // retry resolution because the client may not have been fully initialized yet.
            _cachedPlayerName = null;
        }

        try
        {
            if (IsLunaEnabled)
            {
                // Strategy 1: Try Main.MyPlayer.PlayerName
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
                            string? resolvedName = ExtractStringMember(myPlayer, "PlayerName")
                                                ?? ExtractStringMember(myPlayer, "Name")
                                                ?? ExtractStringMember(myPlayer, "UserName")
                                                ?? ExtractStringMember(myPlayer, "Username");
                            if (!string.IsNullOrWhiteSpace(resolvedName))
                            {
                                _cachedPlayerName = SanitizePlayerName(resolvedName!);
                                Debug.Log("[FastVesselChanger] Detected Luna player: " + _cachedPlayerName);
                                return _cachedPlayerName;
                            }
                        }
                    }
                }

                // Strategy 2: Try SettingsSystem.CurrentSettings.PlayerName (LMP client source of truth)
                foreach (var a in assemblies)
                {
                    Type settingsType = a.GetType("LmpClient.Systems.SettingsSys.SettingsSystem")
                                      ?? a.GetType("LMP.Client.Systems.Settings.SettingsSystem")
                                      ?? a.GetType("LunaMultiplayer.Client.Systems.SettingsSys.SettingsSystem");
                    if (settingsType == null) continue;

                    object? currentSettings = null;
                    var currentSettingsProp = settingsType.GetProperty("CurrentSettings", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (currentSettingsProp != null)
                    {
                        try { currentSettings = currentSettingsProp.GetValue(null, null); } catch { }
                    }

                    if (currentSettings == null)
                    {
                        var singletonProp = settingsType.GetProperty("Singleton", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                                        ?? settingsType.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        if (singletonProp != null)
                        {
                            object? singleton = null;
                            try { singleton = singletonProp.GetValue(null, null); } catch { }
                            if (singleton != null)
                            {
                                var instanceCurrentSettings = singleton.GetType().GetProperty("CurrentSettings", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                                if (instanceCurrentSettings != null)
                                {
                                    try { currentSettings = instanceCurrentSettings.GetValue(singleton, null); } catch { }
                                }
                            }
                        }
                    }

                    if (currentSettings != null)
                    {
                        string? resolvedName = ExtractStringMember(currentSettings, "PlayerName")
                                            ?? ExtractStringMember(currentSettings, "Name")
                                            ?? ExtractStringMember(currentSettings, "UserName")
                                            ?? ExtractStringMember(currentSettings, "Username");
                        if (!string.IsNullOrWhiteSpace(resolvedName))
                        {
                            _cachedPlayerName = SanitizePlayerName(resolvedName!);
                            Debug.Log("[FastVesselChanger] Detected Luna player (settings): " + _cachedPlayerName);
                            return _cachedPlayerName;
                        }
                    }
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[FastVesselChanger] Error detecting Luna player name: " + e.Message);
        }

        // Fallback to single-player mode. Only cache this when Luna is not enabled,
        // so we keep retrying player detection while Luna initializes.
        if (!IsLunaEnabled)
            _cachedPlayerName = "SinglePlayer";

        return "SinglePlayer";
    }

    private static string? ExtractStringMember(object source, string memberName)
    {
        if (source == null || string.IsNullOrEmpty(memberName))
            return null;

        var type = source.GetType();
        var prop = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (prop != null && prop.PropertyType == typeof(string))
        {
            try { return prop.GetValue(source, null) as string; } catch { }
        }

        var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (field != null && field.FieldType == typeof(string))
        {
            try { return field.GetValue(source) as string; } catch { }
        }

        return null;
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
