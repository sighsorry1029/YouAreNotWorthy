# Changelog

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
