# HangarManager 0.6.59

Adds toolbar actions and numbered timer commands for the physical Hangar blocks used by the single-miner EMM-A release. It reads the storage zone and confirms store and deploy operations.

This is the exact source bundled with EMM-A 0.8.56. The original local plugin descriptor labels it v0.6.59 and credits EVY. The game world needs the Hangar mod for the physical blocks.

## Source and build

The [plugin source](HangarManager) targets .NET Framework 4.8 and Pulsar Legacy. On Windows, use the installed Space Engineers 1 game assemblies and the Pulsar Legacy libraries. The project reads their locations from SpaceEngineersBin64 and PulsarLibraries or uses the defaults in Directory.Build.props.

~~~powershell
dotnet build HangarManager/HangarManager.csproj -c Release
~~~

[Companion project](https://github.com/lucakrowinkel123-dot/EMM-A)
