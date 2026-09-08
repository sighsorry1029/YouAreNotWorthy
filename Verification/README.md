# Runtime verification

This standalone .NET 8 executable checks a built YouAreNotWorthy DLL against a
local Valheim installation. It does not launch Valheim, deploy the plugin, or
modify a world, character, configuration, or generated reference file. Build the
plugin separately before running it. The verifier is intentionally outside the
plugin project and release package.

## Build and run

Requirements: a .NET SDK that supports targeting .NET 8, the .NET 8 runtime,
the plugin DLL and its dependencies, and a Valheim
installation with `valheim_Data/Managed/publicized_assemblies/assembly_valheim_publicized.dll`.
The project reads `../../environment.props`. Command-line MSBuild properties
override its values, so a clean checkout does not need a machine-specific source
edit. `ValheimGamePath`, `CorlibPath`, and `PublicizedAssembliesPath` are supported
build overrides. The verifier references the publicized game assembly and
`UnityEngine.CoreModule.dll`; its standalone Harmony API is pinned to the
`Lib.Harmony` NuGet package, version 2.4.1.

From the repository root in PowerShell:

```powershell
$gamePath = 'D:\SteamLibrary\steamapps\common\Valheim'
dotnet build YouAreNotWorthy.csproj -c Debug -p:DeployToGame=true "-p:ValheimGamePath=$gamePath"
dotnet build Verification/RuntimeVerification/RuntimeVerification.csproj -c Release "-p:ValheimGamePath=$gamePath"
dotnet Verification/RuntimeVerification/bin/Release/net8.0/YNW.RuntimeVerification.dll --repo . --game "$gamePath" --configuration Debug
```

The verifier's `-c Release` controls its own compilation. `--configuration Debug`
selects the **plugin** at `<repo>/bin/Debug/YouAreNotWorthy.dll`; use
`--configuration Release` for the release plugin. The default plugin
configuration is Debug. Both `--repo` and `--game` are required. `--plugin <path>`
can select an explicitly supplied DLL instead of `<repo>/bin/<configuration>`.
`--help` prints the accepted arguments. Missing paths, failed assertions, or
unsupported IL fail with exit code 1; a completed run returns 0.

Build and run against the same game installation: the executable output includes
copies of its game compile references. Changing only `--game` does not replace
those copies. Runtime dependency lookup also searches the game's Managed,
publicized assembly and BepInEx/core directories, `<repo>/Libs`, and the selected
plugin DLL's directory. A custom build reference outside the standard game layout
must still be available in the verifier output or one of these runtime locations.

For before/after comparison, keep this verifier fixed and run it once with
`--repo <baseline checkout>` and once with `--repo <updated checkout>`. Build both
plugin DLLs first and use the same game installation for both runs.

## What it verifies

- The exact reflected overloads and expected original call patterns used by the
  SpawnSystem.UpdateSpawnList, CreatureSpawner.UpdateSpawner, and
  Character.OnDeath transpilers.
- The **actual transpiler methods from the selected plugin DLL**, including the
  expected replacement calls and disappearance of the replaced original calls.
- Re-emission and JIT compilation of the transformed IL, preserving local
  variables, branch labels and supported exception regions.
- The actual private `RestrictionEvaluator.ResolveEffectiveTier` method, reached
  through a small typed dynamic delegate. Cases cover a missing resolver,
  cached ranked and cached null results, direct requirement floors, repeated
  lookups after a cache miss, strongest ingredients in a production path,
  easiest alternative paths, an unclassified alternative in both orders,
  multiple production steps, and self cycles with/without a direct requirement.
  These cases construct graphs; they do not copy the resolution algorithm.

Tier tests instantiate uninitialized managed ItemData/SharedData objects as graph
identities and leave `m_dropPrefab` null. They exercise neither Unity object
construction nor prefab lookup. A broken private test seam causes an explicit
failure; the harness does not silently skip the affected checks.

Two informational lines report managed bytes allocated by 1,000 warmed cached
lookups, for a ranked result and a null result. The loop uses the same typed
delegate as the functional checks, with no reflection argument-array allocation
per call. Allocation totals are comparison data, not pass/fail thresholds and
not a Unity performance measurement.

## Limits and remaining game checks

The transformed spawn/death methods are **JIT-compiled but never invoked**.
Direct calls to Unity `InternalCall` methods in the verification IL are replaced
with signature-compatible stubs that return defaults. No gameplay behavior is
inferred from those stubs. The small decoder handles the installed targets; it
is not a general-purpose replacement for Harmony's patch pipeline and rejects
unsupported forms such as `calli`.

This run does not call plugin Awake/PatchAll, combine patches from other mods,
test Harmony priority/order/finalizers, or execute Unity's Mono/native runtime.
The verifier's NuGet Harmony version can differ from the installed BepInEx
Harmony version. Success therefore establishes a useful managed compatibility
check, not successful plugin initialization or real game execution.

Still verify in Valheim: startup and shutdown/event cleanup; remote client,
listen host, and dedicated server behavior; optional mod order and reflection;
network ownership and admin authorization; required/blocking spawn keys and
group spawns; snapshot readiness; UI lifecycle and frame costs; configuration,
save and public API compatibility; repeated requests and inventory quantity
conservation, including disconnects and owner changes. The tier cases do not
test runtime graph discovery, native prefab equality, cache invalidation,
concurrent changes, or every cyclic graph.
