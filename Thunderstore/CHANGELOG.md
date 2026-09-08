# Changelog

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
