# Runtime verification

This standalone .NET 8 executable checks a built YouAreNotWorthy DLL against a
local Valheim installation. It does not launch Valheim, deploy the plugin, or
modify a world, character, configuration, or generated reference file. Build the
plugin separately before running it. The verifier is intentionally outside the
plugin project and release package.

## Build and run

Requirements: a .NET SDK that supports targeting .NET 8, the .NET 8 runtime,
the plugin DLL and its dependencies, and a Valheim
installation with the original `valheim_Data/Managed/assembly_valheim.dll` (Valheim 1.0.7).
The project reads `../../environment.props`. Command-line MSBuild properties
override its values, so a clean checkout does not need a machine-specific source
edit. `CorlibPath` overrides the Managed compile-reference directory.
The verifier references the original game assembly and
`UnityEngine.CoreModule.dll`; its standalone Harmony API is pinned to the
`Lib.Harmony` NuGet package, version 2.4.1.

From the repository root in PowerShell:

```powershell
$gamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
dotnet build YouAreNotWorthy.csproj -c Debug -p:DeployToGame=true
dotnet build Verification/RuntimeVerification/RuntimeVerification.csproj -c Debug
dotnet Verification/RuntimeVerification/bin/Debug/net8.0/YNW.RuntimeVerification.dll --repo . --game "$gamePath" --configuration Debug
./Verification/Verify-GameReferences.ps1
```

The verifier's `-c Debug` controls its own compilation. `--configuration Debug`
selects the **plugin** at `<repo>/bin/Debug/YouAreNotWorthy.dll`; use
`--configuration Release` for the release plugin. The default plugin
configuration is Debug. Both `--repo` and `--game` are required. `--plugin <path>`
can select an explicitly supplied DLL instead of `<repo>/bin/<configuration>`.
`--help` prints the accepted arguments. Missing paths, failed assertions, or
unsupported IL fail with exit code 1; a completed run returns 0.

Game/Unity compile references are not copied into the verifier output. It loads
original game assemblies from `--game` and checks the actual loaded path. Use
`--managed <directory>` to check the dedicated server's `valheim_server_Data/Managed`
with the same plugin DLL and client BepInEx dependencies. Runtime lookup also
searches BepInEx/core, `<repo>/Libs`, and the selected plugin DLL directory.
`Verify-GameReferences.ps1 -ManagedPath <directory>` checks all final DLL game
type/member references and rejects obsolete literal-field load/store instructions.

For before/after comparison, keep this verifier fixed and run it once with
`--repo <baseline checkout>` and once with `--repo <updated checkout>`. Build both
plugin DLLs first and use the same game installation for both runs.

## What it verifies

- Personal/shared key classification, embedded default YAML validation/index compilation,
  the same read-only checks for the selected game's local progression.yml when present, and the
  1.0 spawn precheck followed by refusal when no eligible player exists.
- PoV 4.10.1 world-key classification and refusal of personal registration,
  including long layouts, bare cooldown/total queries, casing, similarly named
  unrelated personal keys, and rejection as a configured personal defeat key.
  These checks do not invoke the Get/Set Harmony prefixes: their diagnostic
  observer requires Unity's native clock. World storage, RPC synchronization,
  PoV rewards and existing-save recovery still require game verification.
- Static Harmony targets (including merged ServerSync), dynamic YNW target
  factories, named arguments and injected field existence. Two optional
  InventorySlots hooks are explicitly skipped by the standalone verifier.
- All eight vanilla methods transformed by YNW, including interaction key writes
  and location-icon transport/UI queries. Branches are widened during test IL
  emission; the production transpiler output itself is unchanged.
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

The transformed game methods are **JIT-compiled but never invoked**.
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

## Console command lifecycle checks

`ConsoleCommandVerification` invokes the built plugin's two command owners with
the original game's protected `Terminal.commands` field in an isolated process.
It checks registration identity, flags and tab options, repeated registration,
owned-entry removal, preservation of a foreign replacement, re-registration,
repeated shutdown, and independent cleanup of the two owners. The registry is
restored in `finally`. No console action or network request is executed.

Pre-existing name conflicts and an unavailable registry reach the plugin's
warning logger, which also initializes ServerSync. The baseline DLL cannot run
that initialization without BepInEx's Unity `ThreadingHelper.Instance`. These
warning paths are reviewed in the diff but are not executed by this standalone
test; check them in Valheim. The harness does not stub plugin initialization.
