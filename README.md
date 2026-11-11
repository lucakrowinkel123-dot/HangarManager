# HangarManager client plugin

This branch contains the HangarManager source bundled with EMM-A 0.9.40 for Space Engineers 1. It provides toolbar and timer commands for storing and deploying ships and reading the hangar zone. The world needs the Hangar mod for the physical blocks.

The [0.6.59 tag](https://github.com/lucakrowinkel123-dot/HangarManager/tree/v0.6.59) preserves the earlier single-miner release. Both bundles use the same local HangarManager version label. The Pulsar submission identifies this revision by its Git commit.

## Source and build

The [plugin source](HangarManager) targets .NET Framework 4.8 and Pulsar Legacy. On Windows, use the installed Space Engineers 1 game assemblies and the Pulsar Legacy libraries. The project reads their locations from SpaceEngineersBin64 and PulsarLibraries or uses the defaults in Directory.Build.props.

~~~powershell
dotnet build HangarManager/HangarManager.csproj -c Release
~~~

[Companion project](https://github.com/lucakrowinkel123-dot/EMM-A)
