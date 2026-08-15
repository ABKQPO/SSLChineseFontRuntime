# SSLChineseFontRuntime

Runtime Chinese font fallback for ShellShock Live with BepInEx 6 IL2CPP.

The plugin installs a TextMeshPro fallback font, preloads the common Chinese
localization characters, and applies the fallback to newly created text. It is
used by `SSLWeaponNameTranslator` and should be installed alongside it.

## Installation

Copy both files into the game's plugin directory:

```text
ShellShock Live/
`-- BepInEx/plugins/SSLChineseFontRuntime/
    |-- SSLChineseFontRuntime.dll
    `-- 微软雅黑.ttf
```

The font file must remain beside the DLL. BepInEx 6 IL2CPP must already be
installed before starting the game.

## Build

From the repository root, build with the game installation as the reference
assembly source:

```powershell
dotnet build .\SSLChineseFontRuntime\SSLChineseFontRuntime.csproj -c Release
```
