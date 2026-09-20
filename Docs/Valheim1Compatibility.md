# Valheim 1.0.7 compatibility (2026-09-09)

Target: Windows x64 client Steam build 25185596 and dedicated server build
25185644, compared with 0.221.12 builds 21981559/21981590. This patch targets
1.0.7; it adds no old-game compatibility or custom save migration.

## Fixed

- The corrected Steam BepInEx log rejected both the local progression YAML and
  the embedded default at `defeated_eikthyr`. Old compiled `GlobalKeys.AshlandsOcean`
  was 42; the new game's `defeated_eikthyr` is 42. Recompiling against original
  1.0.7 DLLs fixes this collision without changing which policies are personal.
- Game references now use the original Managed DLLs. Existing nonpublic Harmony
  targets are named explicitly instead of using inaccessible `nameof` expressions.
  Cached AccessTools delegates and field injection remain; no access bypass is added.
- Static item tooltips target the new six-argument overload. Nested-tooltip depth,
  outer-call state/finalizers and FineDining order remain intact.
- Item stands expose attached prefab hashes; the offering gate resolves the hash
  through ObjectDB before the boss RPC and before item removal. The existing
  unresolved-item fallback and owner/queued-spawn checks are retained.
- Location identity lookup uses the game's new Vector2s zone key. The display
  token, metadata marker, trusted-sender check and receive/commit state are unchanged.
- `SpawnSystem.UpdateSpawnList` has a fourth `groupSalt` argument and checks keys
  **before** selecting a player. Shared keys keep the early vanilla check; personal
  keys defer to the existing eligible-player selection. With no eligible player,
  selection fails. The transpiler no longer reads an uninitialized player local.
  New alternate-biome groups, persistent-event checks and group salt remain vanilla.
- The existing merged ServerSync receives a three-instruction constant-field fix.
  See [library provenance and reproduction](../Libs/ServerSync.Compatibility.md).
- Installed InventorySlots 1.4.6 failed in YAML sync before its own PatchAll.
  Attempting to patch its methods then raised Mono BadImageFormatException and
  disabled YNW. Optional hooks now require that dependency's own Harmony patches
  to be present. Missing/inactive integration is reported; vanilla restrictions
  continue. This does not repair InventorySlots or certify a partly initialized
  dependency. Healthy InventorySlots integration still needs a compatible DLL test.
- The package manifest declares BepInExPack 5.4.2350 as required by the project
  instructions. These changes are released as mod version 1.0.6.

## Preserved boundaries

Configuration paths, keys and YAML; API v1 and result values; native unique-key
save ownership; `YNW_PersonalKeys` snapshot format and bounds; RPC identifiers,
sender/owner/admin checks and idempotent key additions are unchanged. Client-only
item gates, owner-client spawn execution, dedicated-server peer snapshots, raid
policy and group-spawner guards remain distinct. Unity messages, watcher/event
cleanup and patch ordering remain in place. No new per-frame scan, allocation,
UI recreation or runtime reflection search was introduced.

The official 1.0 additions do not establish equivalent personal-progression
semantics. No mod feature or protection was removed on that assumption.

## Validation

- Debug build, ILRepack merge and automatic copy to the client plugins directory:
  successful, zero compiler warnings/errors. Source and installed DLL SHA-256 match.
- Original client **and** dedicated-server DLLs: 271 final-plugin type/member
  references resolve; no obsolete literal-field load/store remains. The managed
  harness resolves 64 patch bindings and transforms/JIT-checks eight game methods;
  configuration and restriction-tier tests pass on both reference sets.
- Actual client Unity/Mono startup, `-batchmode -nographics`, existing mod set:
  The pre-release 1.0.5 build reached its loaded message after configuration, PatchAll and command
  registration. The failed InventorySlots dependency is explicitly skipped.
  No world was opened; the test process was stopped afterwards. This does not
  verify gameplay, graceful cleanup or rendered UI. Other mods still report errors.

Remaining: rendered tooltip nesting/order; eligible vs ineligible player spawns;
boss offering refusal/success with unchanged item counts; world save/reload;
client/host/dedicated sessions with config sync, disconnects, ownership changes,
admin requests and duplicate grants; map identity and optional-mod combinations.
The .NET harness uses Harmony 2.4.1 and native stubs for JIT checks, not the game's
patch pipeline. Actual startup used the installed BepInExPack 5.4.2333 loader.

See [verification commands and limits](../Verification/README.md). Detailed
snapshot paths, original-log provenance and local test outputs are recorded in
the global Valheim comparison's `youarenotworthy.md`.
