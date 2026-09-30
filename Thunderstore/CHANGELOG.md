# Changelog

## 1.0.9

- Fix Path of Valheiman 4.10.1 world records being blocked or converted into personal keys: monolith layout, placement, reset, completion and cooldowns, plus runestone discovery, dungeon completion and legacy place records.
- Preserve PoV's native character records and YNW's personal boss progression. Only the identified world-key families are shared; no PoV dependency is added. Update YNW on the host/server and clients.
- Keep public API v2 and its existing signatures and result values. The reserved PoV keys now return shared-key results and cannot be configured as personal requirements.
- Add regression checks for PoV key families, long layouts, cooldown/total queries and similarly named personal keys.
- Existing saves are not migrated or repaired. Back up affected worlds before loading: if PoV's reset marker is missing, its own initialization can remove existing world monolith completion records.

## 1.0.8

- Add read-only API v2 item-use checks for the local character and authenticated server peers, allowing integrations to waive item costs while preserving progression requirements. Unavailable item or character data does not authorize use; existing key-query contracts are preserved.
- Support BossRules 1.1.2 personal first-victory Queen and inner Frozen King summons. The external Frozen King gate remains paid, and final completion uses `defeated_frozenking_p3`. Install both updated mods on the host/server and clients.
- Reuse the existing item-tier cache and personal-key snapshots without changing saved keys or progression YAML. The new strict item-use API does not apply an administrator/debug bypass; existing native item restrictions are unchanged.
- Add API contract and result regression checks, and update the BepInEx package requirement to 5.4.2351.

## 1.0.7

- Increased the personal-key grant radius from 32 to 64 metres so nearby players can receive progression credit from large or airborne enemies more reliably.
- Simplified item-reference write states and centralized console-command registry ownership checks without changing commands, permissions, or network behavior.
- Added regression checks for console-command registration, replacement safety, repeated shutdown, and client/server game-assembly compatibility.
- Rewrote the package description with concise Valheim multiplayer, personal progression, raid, spawn, item-tier, and server-sync search terms.

## 1.0.6

- Added compatibility with Valheim 1.0.7, including updated spawn, tooltip, item-stand, location-icon, and private Harmony targets.
- Fixed Vanilla boss progression keys being misclassified after the `GlobalKeys` layout changed.
- Updated the embedded ServerSync implementation for the new `ZRoutedRpc.Everybody` constant contract.
- Kept YouAreNotWorthy active when an installed InventorySlots version fails before registering its own patches; only the unavailable optional hooks are skipped.
- Added 42 Valheim 1.0.7 resources to the default progression map, including Deep North materials under `defeated_fader`, while preserving production-path inheritance and existing tier assignments.
- Expanded compatibility checks to validate original client and dedicated-server assemblies, Harmony targets, transpilers, and both embedded and installed progression configuration.

## 1.0.5

- Reduced allocations during repeated item-tier checks and personal-key grants while preserving progression rules.
- Consolidated identical administrator RPC checks without changing authorization or network messages.
- Added a reproducible managed regression harness for item-tier resolution and spawn/death transpilers.
- Added automatic local game DLL updates after successful Debug builds with `DeployToGame=true`.
- Removed outdated resource counts from the progression documentation.

## 1.0.4

- Added per-character minimap visibility rules through the new ServerSynced `locations.yml`.
- Added Expand World Data location-prefab resolution while preserving custom icon, size, and animation tokens.
- Added independent validation, hot reload, and last-known-good handling for location rules.

## 1.0.3

- Fixed personalized GlobalKey queries for remote players on dedicated servers.
- Fixed boss and event personal keys not reaching nearby remote players on dedicated servers.

## 1.0.2

- Fixed dedicated-server admins being rejected by client-side ServerSync checks for `ynw:keys` and `ynw:items refresh`.
- Fixed runtime peer authentication to use Valheim's public connected-peer APIs.
- Fixed `ynw:keys players` excluding connected characters whose `Player` object was not instantiated on the dedicated server.

## 1.0.1

- Fixed personal progression restrictions for ItemStand-style boss altars such as Moder and Yagluth.
- Hardened personal-key distribution and fallback handling to avoid lost progression on failed requests.
- Improved item-tier resolution, generated item references, and InventorySlots compatibility diagnostics.
- Improved startup cleanup and compatibility with modded SpawnSystem patches.

## 1.0.0

- Initial release.
