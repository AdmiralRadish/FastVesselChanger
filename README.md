# CameraSwitcherAuto

A minimal Kerbal Space Program camera toggle plugin in C#.

## Structure
- `CameraSwitcherAuto/` - Visual Studio project (SDK-style, .NET Framework 4.7.2)
- `Source/CameraSwitcherAuto.cs` - plugin source
- `Build/build.bat` - build helper script
- `GameData/CameraSwitcherAuto/Plugins/` - auto-generated output directory

## Prerequisites

This project uses **KSPBuildTools** to automatically manage KSP DLLs and plugin packaging.

### Setup KSP Installation Path

Before building, you must configure your KSP installation path:

1. **Option A: Using csproj.user file (Recommended)**
   - Copy `CameraSwitcherAuto/CameraSwitcherAuto.csproj.user.example` to `CameraSwitcherAuto/CameraSwitcherAuto.csproj.user`
   - Edit the `.csproj.user` file and set `KSPBT_GameRoot` to your KSP installation directory (the one containing `GameData`)
   - Example: `C:\Games\KSP_1.12.5` or `D:\SteamLibrary\steamapps\common\Kerbal Space Program`

2. **Option B: Environment Variable**
   - Set environment variable: `KSPBT_GameRoot=C:\Path\To\KSP`

3. **Option C: Command Line**
   ```bat
   Build\build.bat Release x64
   ```

## Build

Build using the .NET CLI or Visual Studio:

```bat
# Using batch script
Build\build.bat Release x64

# Using dotnet CLI
dotnet build CameraSwitcherAuto.sln -c Release -f net472

# Open in Visual Studio 2026 and build normally
```

The compiled DLL will be automatically placed in: `GameData/CameraSwitcherAuto/Plugins/`

## Install

Copy the entire `GameData/CameraSwitcherAuto` folder into your KSP installation's `GameData` directory.

```
Your KSP Installation/
├── GameData/
│   ├── CameraSwitcherAuto/     ← Copy here
│   │   └── Plugins/
│   │       └── CameraSwitcherAuto.dll
│   └── ... other mods ...
```

## Usage
- **Toggle Key**: `/` (forward slash) - press in-flight to toggle the UI window
- **Auto-Switch**: Enable automatic cycling through selected vessels at a configurable interval
- **Vessel Selection**: Check/uncheck vessels in the UI to include them in the cycle
- **Type Filter**: Filter vessels by type (Ship, Station, Probe, Lander, Rover, Plane, Debris, etc.)
- **Window State**: The UI remembers whether it was open or closed when you close/open your save

## Features

### Multi-Player Support (LunaMultiplayer)
This mod is **fully compatible with LunaMultiplayer servers**. Each player has completely separate settings:
- Each player's vessel selections are independent
- Window state is remembered per player
- Auto-switch intervals are per-player
- Type filters are per-player

See [LUNAMULTIPLAYER.md](LUNAMULTIPLAYER.md) for detailed information about multiplayer support.

### Single-Player Mode
Works seamlessly in single-player KSP with no configuration needed for multiplayer features.

## Development

This project uses:
- **KSPBuildTools 1.1.1** - Automatic KSP DLL management
- **.NET Framework 4.7.2** - Target framework
- **Visual Studio 2026 (v18+)** - Recommended IDE
- **Modern SDK-style project format** - No hardcoded references

For more information:
- [KSPBuildTools Documentation](https://kspbuildtools.readthedocs.io/)
- [KSP Modding Guide](https://kerbal-space-program-master.readthedocs.io/)

## License
MIT

