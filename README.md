# You Are Not Worthy

Per-character progression for Valheim.

## Showcase

### Personal progression

Characters sharing one world can see different trader stock based on their own
progression.

![Personalized trader stock](https://i.ibb.co/WNy7zLSB/merchantlocked.png)

### Item restrictions

Equipment, consumables, and feasts show their missing requirement and remain
unusable until the character earns the required key.

![Item progression restrictions](https://i.ibb.co/spD7LCyn/itemslocked.png)

### Doors and boss summons

Personal progression also applies when using key items on locked doors or
activating boss altars.

![Locked doors](https://i.ibb.co/Y7RPRSPq/doorlocked.png)

![Blocked boss summons](https://i.ibb.co/jKJKD99/summonlocked.png)

### Mod integrations

Other mods can reuse YNW's personal-key checks and localized blocked message
for their own interactions.

![Modded portal restriction](https://i.ibb.co/gLRRZ0Ww/adminportalrestriction.gif)

You Are Not Worthy (YNW) personalizes supported value-less, non-reserved
`globalkey` checks by using Valheim's native character unique keys. Players in
the same world can therefore have different trader stock, raid eligibility,
spawn eligibility, and item-use progression.

Value-bearing keys, reserved modifiers, seasonal keys, and other shared world
state keep their Vanilla behavior.

## Features

- Personalizes compatible Vanilla and modded global-key progression.
- Grants supported event keys to active players within 32 metres of a death,
  interaction, or requesting player.
- Adds simple creature-defeat keys through `defeatKeys`.
- Gates the final use of tiered items without blocking collection or crafting.
- Respects the server's actual **Player based raids** setting.
- Synchronizes the server's `progression.yml` to clients.
- Provides admin commands for connected Steam players.

## Configuration

`defeatKeys` is the only reserved root field. Every other root field is an item
tier, ordered from lowest to highest.

```yaml
defeatKeys:
  - prefabs: [Serpent]
    key: defeat_serpent

Meadows:
  - Wood
  - Stone

BlackForest:
  requiredKey: defeated_eikthyr
  resources:
    - HardAntler
    - Bronze
    - TrollHide

Swamp:
  requiredKey: defeated_gdking
  resources:
    - Iron
    - Entrails
    - CryptKey

SerpentItems:
  requiredKey: defeat_serpent
  resources:
    - SerpentMeat
```

- Tier names are labels, not biome checks.
- Creature and resource names are internal prefab/item names.
- `defeatKeys` grants its key around the defeated creature; it does not require
  the killing blow.
- Vanilla and modded defeat global keys normally do not need duplicate
  `defeatKeys` rules.
- Items can inherit tiers through recipes, cooking, fermentation, and smelting.
- A sequence-form tier has no `requiredKey` and classifies resources without
  restricting them.

## Item restrictions

When a character lacks an item's resolved `requiredKey`, YNW can block:

- equipment and ammunition use;
- food, potion, and other consumable use;
- direct boss offerings and final item-stand altar activation;
- key-item use on locked Vanilla doors;
- InventorySlots equipment routes when InventorySlots is installed.

Gathering, pickup, crafting, cooking, fermenting, building, repairing, combat,
and biome movement remain unrestricted. Players may prepare an item early, but
cannot perform its guarded final use until they earn the key.

Item-restriction bypass requires both server-admin status and Vanilla debug
mode. This bypass does not disable global-key, raid, or spawn personalization.

## Multiplayer behavior

Personalizable global-key writes are kept out of shared world progression.
Supported death and interaction paths grant the same literal as a native
character key to active players within 32 metres of the relevant source.

Trader and `ConditionalObject` checks use the local character. Raid and spawn
patches evaluate their supported key conditions against the relevant character
instead of treating every player as equally progressed. Raid instances, spawn
timers, opened doors, and spawned network objects remain shared world systems.

## Mod integration API

`YouAreNotWorthyApi.ApiVersion` identifies the public API contract. Version 1
provides `QueryLocal(key)` for the current character, `QueryPeer(peer, key)` for
an authenticated character on the authoritative server, and
`TryShowLocalMissingRequirement(key)` for integrations that have already
established a missing local requirement and want YNW to own the localized
center message and its shared one-second cooldown.

Both query methods return `KeyQueryResult`, distinguishing personal and shared
keys, present and missing keys, invalid input, and temporarily unavailable
character or snapshot data. Integrations should treat `Unavailable` as
fail-closed and must not read the internal `YNW_PersonalKeys` ZDO payload
directly. Calls must run on Unity's main thread, and transient join or respawn
results must not be cached permanently.

`TryShowLocalMissingRequirement` validates and canonicalizes the key but does
not evaluate whether it is missing. It returns `true` only when a message is
actually displayed; invalid or unavailable requests and requests suppressed by
the cooldown return `false`.

Supported value-less, non-reserved keys return a `Personal*` result. Reserved,
seasonal, and value-bearing world keys retain Vanilla shared state and return a
`Shared*` result.

Remote personal-key snapshots are owned by the client character. Peer and
character validation prevents accidental cross-player queries, but the API is
not an anti-cheat substitute for a server-owned progression ledger.

## Admin commands

Server administrators can manage the native personal keys of a connected
Steam player's current character:

| Command | Purpose |
| --- | --- |
| `ynw:keys players` | List connected players and SteamID64 values |
| `ynw:keys list <SteamID64>` | List the character's personal keys |
| `ynw:keys add <SteamID64> <key>` | Add a personal key |
| `ynw:keys remove <SteamID64> <key>` | Remove a personal key |
| `ynw:items refresh` | Re-scan and update the authoritative server `items.reference.yml` |

Personal-key command targets must be online through the Steam backend. Character
names, character IDs, crossplay identities, and offline characters are not supported.

`ynw:items refresh` may be requested by a connected server administrator, but
the scan and file write always run on the authoritative server. It uses the
server's currently applied effective `progression.yml` and loaded runtime; it
does not read the requesting client's local YAML or write a client reference.

## Generated references

After the authoritative world finishes loading, YNW generates these files in
the same configuration directory:

| File | Contents |
| --- | --- |
| `keys.reference.yml` | Discovered global keys, personal/shared handling, defeat prefabs, and Player based raid keys |
| `items.reference.yml` | Statically discovered player-facing item-use targets and their final required keys |

`items.reference.yml` is grouped by best-effort prefab owner and raw Valheim
`ItemType`. Within each type, keyless entries come first, followed by the order
in which each `requiredKey` first appears in `progression.yml`, then prefab name.

Both files are generated reference material. YNW does not read them as
configuration or send them through ServerSync, so editing them has no effect.
