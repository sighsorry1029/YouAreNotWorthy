# Valheim Vanilla global key와 Player based raids 개인 키

> 기준 게임: Valheim `0.221.12`, Steam public build `21981559`
> 조사일: 2026-08-25
> 범위: 현재 설치본의 코드와 SoftRef asset에 포함된 Vanilla 데이터

이 문서는 다음 두 목록을 구분해 정리한다.

1. Vanilla가 world에 저장하는 `global key`
2. Vanilla의 **Player based raids**가 캐릭터별 조건으로 실제 참조하는 player unique key

가장 중요한 전제는 두 저장소 모두 임의 문자열을 받을 수 있다는 점이다. 따라서
`GlobalKeys`나 `PlayerKeys` enum만 보고 모든 키를 얻을 수는 없다. 이 문서에서
"전체 목록"은 다음과 같이 범위를 고정한다.

- global key: 현재 코드의 `GlobalKeys` enum 전체와 현재 asset에서 정상 진행으로
  설정되는 고정 문자열 progression/quest key 전체
- personal key: 현재 Vanilla random-event asset의 Player based raids 조건이 참조하는
  player unique key 전체

## 1. 저장 위치와 이름 비교 방식

| 구분 | 실제 저장소 | 저장 범위 | 이름 비교 | 비고 |
|---|---|---|---|---|
| World global key | `ZoneSystem.m_globalKeys`, `m_globalKeysValues` | world runtime | base name을 lowercase로 정규화 | 값 없는 flag와 공백으로 구분된 `key value` payload를 모두 지원 |
| Player unique key | `Player.m_uniques` | character save | 대소문자를 구분하는 exact match | `Player.HaveUniqueKey(...)`가 검사 |
| Known item/material | `Player.m_knownMaterial` | character save | item의 `m_shared.m_name` | 현재 소지 여부가 아니라 한 번 발견한 이력 |
| `PlayerKeys` enum | 현재 `DamageTaken` 한 항목 | `Player.m_uniques` 안의 value-bearing entry | enum 기반 prefix 인식 | unique-key 전체 registry가 아닌 recognized value-key enum |

같은 문자열이 world global key와 character unique key 양쪽에 동시에 존재할 수 있지만,
두 상태는 서로 독립적이다. 예를 들어 `Character.m_defeatSetGlobalKey`가 지정된 적이
죽으면 Vanilla는 같은 문자열을 world global key로 설정하고 player unique-key queue에도
추가한다.

Global key는 사실상 case-insensitive이지만 player unique key는 그렇지 않다. 따라서
설정과 문서에서는 아래 asset 원문의 대소문자를 그대로 사용하는 것이 안전하다.

`PlayerEvents`는 개인 키가 아니라 **Player based raids를 켜는 world modifier/global
key**다.

Persistence 경로는 key 종류에 따라 갈린다. `GlobalKeys` enum에서
`NonServerOption`보다 앞에 있는 server modifier는 ZoneSystem의 일반 global-key save
block에서 제외되고 `World.m_startingGlobalKeys` 쪽에 지속된다. Progression과 그 밖의
non-server 문자열 key는 ZoneSystem world data에 저장된다. 위 표의
`m_globalKeys`/`m_globalKeysValues`는 실행 중 조회되는 runtime 저장소를 뜻한다.

## 2. `GlobalKeys` enum 전체

`GlobalKeys` enum은 world modifier를 파싱하기 위한 항목과 일부 진행 키, runtime 상태,
sentinel을 한 enum 안에 섞어 둔다. 이것은 모든 문자열 global key의 registry가 아니다.

### 2.1 값이 있는 shared world modifier

다음 14개는 일반적인 개인 진행 flag가 아니라 값을 가진 world/server 설정이다.

```text
PlayerDamage
EnemyDamage
WorldLevel
EventRate
ResourceRate
StaminaRate
AdrenalineRate
MoveStaminaRate
StaminaRegenRate
SkillGainRate
SkillReductionRate
EnemySpeedSize
EnemyLevelUpRate
Preset
```

### 2.2 Boolean shared world modifier

다음 18개도 캐릭터 진행 키가 아니라 world/server 전체 동작을 바꾸는 flag다.

```text
PlayerEvents
Fire
DeathKeepEquip
DeathDeleteItems
DeathDeleteUnequipped
DeathSkillsReset
NoBuildCost
NoCraftCost
AllPiecesUnlocked
NoWorkbench
AllRecipesUnlocked
WorldLevelLockedTools
PassiveMobs
NoMap
NoPortals
NoBossPortals
DungeonBuild
TeleportAll
```

### 2.3 enum에 직접 포함된 progression key

```text
defeated_eikthyr
defeated_dragon
defeated_goblinking
defeated_gdking
defeated_bonemass
KilledTroll
killed_surtling
KilledBat
```

### 2.4 Shared runtime world state

| 키 | 형태 | 용도 |
|---|---|---|
| `activeBosses` | 숫자 값 | alert되어 `bosscount`가 잡힌 뒤 아직 죽지 않은 boss 수를 추적하는 runtime 상태 |
| `AshlandsOcean` | shared 상태 | Ashlands ocean/tutorial 관련 world 상태 |

### 2.5 실제 key가 아닌 enum sentinel

| enum 항목 | 의미 |
|---|---|
| `NonServerOption` | server option 범위의 끝과 파싱 실패를 나타내는 경계값 |
| `Count` | enum 항목 수를 나타내는 끝 sentinel |

이 두 항목은 진행 조건으로 설정하거나 개인화할 key가 아니다.

## 3. 현재 정상 진행에서 설정되는 문자열 global key

다음 17개가 현재 asset에서 정상적인 처치 또는 quest 진행으로 설정되는 고정 문자열
progression/quest global key다. 이 표에는 enum에 있는 키와 enum 밖의 문자열 키를 모두
포함했다.

| Global key | 설정 원인 | 현재 raid와의 관계 |
|---|---|---|
| `defeated_eikthyr` | Eikthyr 처치 | raid global 조건 |
| `defeated_gdking` | The Elder (`gd_king`) 처치 | raid global 조건 |
| `defeated_bonemass` | Bonemass 처치 | raid global 조건 |
| `defeated_dragon` | Moder (`Dragon`) 처치 | raid global 조건; 실제 Moder 키는 `defeated_moder`가 아님 |
| `defeated_goblinking` | Yagluth (`GoblinKing`) 처치 | raid global 조건 |
| `defeated_queen` | The Queen (`SeekerQueen`) 처치 | raid global 조건 |
| `defeated_fader` | Fader 처치 | raid global 조건 |
| `defeated_serpent` | Serpent 처치 | raid에는 없고 Bog Witch 거래 조건에서 사용 |
| `KilledTroll` | Troll 또는 `Troll_Summoned` 처치 | raid global 조건 및 raid personal 조건 |
| `killed_surtling` | Surtling 처치 | raid global 조건 및 raid personal 조건 |
| `KilledBat` | Bat 처치 | raid global 조건 및 raid personal 조건 |
| `BossHildir1` | Hildir 1번 미니보스 처치 | raid personal 조건 |
| `BossHildir2` | Hildir 2번 미니보스 처치 | raid personal 조건 |
| `BossHildir3` | Hildir 3번 미니보스 처치 | raid personal 조건 |
| `Hildir1` | Hildir 1번 상자 반납 | `hildirboss1`의 global 조건 |
| `Hildir2` | Hildir 2번 상자 반납 | `hildirboss2`의 global 조건 |
| `Hildir3` | Hildir 3번 상자 반납 | `hildirboss3`의 global 조건 |

Hildir raid asset은 global 조건을 `hildir1`, `hildir2`, `hildir3`처럼 lowercase로
기록한다. Setter의 `Hildir1`~`Hildir3`와 같은 world key로 판정되는 이유는 world
global-key 조회가 case-insensitive이기 때문이다. 이것을 player unique key로 직접 쓸
때는 대소문자가 달라지면 다른 키가 된다.

### 3.1 현재 Vanilla raid가 참조하는 global key 합집합

20개 event 이름의 `requiredGlobalKeys`와 `forbiddenGlobalKeys`를 합치면 다음 13개다.
여기서 `forbiddenGlobalKeys`는 설명용 이름이며 Vanilla 원본 필드명은
`m_notRequiredGlobalKeys`다.

```text
KilledBat
KilledTroll
defeated_bonemass
defeated_dragon
defeated_eikthyr
defeated_fader
defeated_gdking
defeated_goblinking
defeated_queen
hildir1
hildir2
hildir3
killed_surtling
```

### 3.2 현재 데이터에 남아 있지만 정상 진행 목록에서 제외한 키

| 문자열 | 현재 상태 | 처리 권장 |
|---|---|---|
| `defeated_hive` | `Hive` boss prefab의 `m_defeatSetGlobalKey`이며 NetScene에는 등록되어 있지만, 현재 location/spawner와 소비 조건에서 정상 접근 경로를 찾을 수 없음 | dormant/dev/future 후보로만 취급 |
| `defeated_moder` | Dream text가 읽기만 하고 현재 setter가 없음 | legacy read-only 참조; Moder 진행에는 `defeated_dragon` 사용 |
| `elakingmole_defeated` | 오래된 추출물에는 있었지만 현재 manifest와 635개 실제 bundle에는 없음 | 현재 Vanilla 키 목록에서 제외 |

`HildirMap`은 현재 Vanilla에 존재하지만 Trader의 `m_keyType: Player`가 검사하는 character
unique key이므로 global key 표에는 포함하지 않았다. 또한 현재 번들에서 `season_*`
문자열은 발견되지 않았으므로 과거 버전이나 외부 문서의 seasonal key를 이 스냅샷에
섞지 않았다.

## 4. Player based raids가 참조하는 player unique key 전체

전체 Vanilla event baseline은 `boss_*` forced-event 7개를 포함해 29행, 27개 이름이다.
이 문서가 다루는 `boss_*`를 제외한 **random raid definition**은 22행, 20개 이름이다.
이 22행 전체의 `requiredPlayerKeysAny`, `requiredPlayerKeysAll`,
`forbiddenPlayerKeys`를 합치면 player unique key는 다음 **17개**다.

| Personal key literal | 조건 위치 | 사용하는 event |
|---|---|---|
| `$se_bonemass_name` | required any | raw `army_moder` 추가 variant |
| `$se_moder_name` | required any | `army_goblin` |
| `$se_yagluth_name` | required any | `army_gjall` |
| `BossHildir1` | required any | `hildirboss1` |
| `BossHildir2` | required any | `hildirboss2` |
| `BossHildir3` | required any | `hildirboss3` |
| `GP_Bonemass` | required any / forbidden | `army_moder`, `blobs`, `ghosts`, `skeletons`, `wolves` / `army_bonemass` |
| `GP_Eikthyr` | required any / forbidden | `army_theelder` / `army_eikthyr` |
| `GP_Moder` | forbidden | `army_moder`, `ghosts` |
| `GP_Queen` | forbidden | `army_gjall`, `army_seekers`, `gemgoblin` |
| `GP_TheElder` | required any / forbidden | `army_bonemass`, raw `army_theelder` 추가 variant / `army_theelder` |
| `GP_Yagluth` | forbidden | `army_goblin` |
| `KilledBat` | required all | `bats` |
| `KilledTroll` | required all | `foresttrolls` |
| `defeated_bonemass` | required all | `bats`, `surtlings` |
| `defeated_gdking` | required all | `foresttrolls` |
| `killed_surtling` | required all | `surtlings` |

`$se_bonemass_name`, `$se_moder_name`, `$se_yagluth_name`은 오타를 정리한 이름이 아니라
asset에 직렬화된 실제 literal이다. `GP_Moder`나 `GP_Yagluth`로 임의 치환하면 다른
unique key를 검사하게 된다.

Guardian power를 선택할 때 `Player.SetGuardianPower(name)`이 unique key로 저장하는
것은 status effect의 Unity Object `name`이다. 현재 asset에서 `GP_Bonemass`,
`GP_Moder`, `GP_Yagluth`의 Unity Object 이름은 각각 그대로 `GP_Bonemass`,
`GP_Moder`, `GP_Yagluth`이고, 표시용 `m_name`이 각각 `$se_bonemass_name`,
`$se_moder_name`, `$se_yagluth_name`이다. 따라서 `$se_*` 세 literal은 실제 event
condition이지만 정상적인 현재 guardian-power 선택으로 지급되는 unique key는 아니다.
Command, mod 또는 legacy character data 같은 다른 경로가 없다면 해당 required-any
branch는 충족되지 않는다.

처치 key는 앞서 설명한
`Character.m_defeatSetGlobalKey` 경로를 통해 같은 literal이 world와 character 양쪽에
생길 수 있다.

DropNSpawn의 생성된 20-name reference writer는 nonpreferred duplicate row를 출력에서
생략한다. 그래서 이 파일만 기준으로 하면 `$se_bonemass_name` variant가 보이지 않아
합집합이 16개로 보인다. Reference 생성이 Vanilla runtime의 22개 raid row를 수정하거나
합치는 것은 아니다. Vanilla 원본 전체를 설명하는 문서이므로 위 목록에는 이를 포함했다.

## 5. Player based raids의 known-item 조건

Known item도 캐릭터마다 다르므로 Player based raids의 개인화 조건에 참여하지만,
player unique key는 아니다. 현재 inventory에 아이템이 있어야 하는 조건도 아니다.
캐릭터가 해당 material을 한 번이라도 발견하여 `m_knownMaterial`에 기록했는지를 본다.

Asset/reference에 보이는 `TrophyTheElder` 같은 이름은 prefab ID다. 실제 runtime 검사는
해당 `ItemDrop`을 해석한 뒤 `m_itemData.m_shared.m_name`을
`Player.IsMaterialKnown(...)`에 전달한다.

### Required-known 합집합 17개

```text
CryptKey
Demister
DragonTear
HardAntler
PickaxeAntler
PickaxeBronze
PickaxeIron
TrophyBonemass
TrophyDragonQueen
TrophyEikthyr
TrophyTheElder
Wishbone
Wisp
YagluthDrop
chest_hildir1
chest_hildir2
chest_hildir3
```

### Forbidden-known 합집합 14개

```text
CryptKey
DragonTear
HardAntler
PickaxeAntler
PickaxeBronze
PickaxeIron
QueenDrop
TrophyBonemass
TrophyDragonQueen
TrophyEikthyr
TrophyGoblinKing
TrophyTheElder
Wishbone
YagluthDrop
```

두 목록의 중복을 제거한 전체 known-item literal은 19개다.

## 6. Vanilla random-event 조건표

다음 표는 DropNSpawn이 이름 기준으로 제공하는 20개 Vanilla event reference와 원본
asset을 대조한 결과다. `—`는 빈 목록이다.

`gemgoblin`은 조건 데이터에는 존재하지만 현재 `m_enabled=false`, `m_random=true`다.
따라서 Vanilla 자동 후보에는 들어가지 않는다. 아래 표는 enabled 여부와 별개로
serialized condition을 보존한다.

### 6.1 Global 조건

| Event | Required global: 모두 필요 | Forbidden global: 하나라도 있으면 차단 |
|---|---|---|
| `army_bonemass` | `defeated_gdking` | `defeated_bonemass` |
| `army_charred` | `defeated_queen` | `defeated_fader` |
| `army_charredspawners` | `defeated_queen` | `defeated_fader` |
| `army_eikthyr` | — | `defeated_eikthyr` |
| `army_gjall` | `defeated_goblinking` | `defeated_queen` |
| `army_goblin` | `defeated_dragon` | `defeated_goblinking` |
| `army_moder` | `defeated_bonemass` | `defeated_dragon` |
| `army_seekers` | `defeated_goblinking` | `defeated_queen` |
| `army_theelder` | `defeated_eikthyr` | `defeated_gdking` |
| `bats` | `KilledBat`<br>`defeated_bonemass` | — |
| `blobs` | `defeated_bonemass` | — |
| `foresttrolls` | `KilledTroll`<br>`defeated_gdking` | — |
| `gemgoblin` | `defeated_fader` | `defeated_queen` |
| `ghosts` | `defeated_bonemass` | — |
| `hildirboss1` | `hildir1` | — |
| `hildirboss2` | `hildir2` | — |
| `hildirboss3` | `hildir3` | — |
| `skeletons` | `defeated_bonemass` | — |
| `surtlings` | `killed_surtling`<br>`defeated_bonemass` | — |
| `wolves` | `defeated_bonemass` | — |

### 6.2 Player based raids 대체 조건

| Event | Required known: any | Forbidden known: any | Required player: any | Required player: all | Forbidden player: any |
|---|---|---|---|---|---|
| `army_bonemass` | `TrophyTheElder`<br>`CryptKey` | `TrophyBonemass`<br>`Wishbone` | `GP_TheElder` | — | `GP_Bonemass` |
| `army_charred` | — | — | — | — | — |
| `army_charredspawners` | — | — | — | — | — |
| `army_eikthyr` | — | `PickaxeAntler`<br>`PickaxeBronze`<br>`PickaxeIron`<br>`HardAntler`<br>`TrophyEikthyr` | — | — | `GP_Eikthyr` |
| `army_gjall` | `YagluthDrop`<br>`Demister`<br>`Wisp` | `QueenDrop` | `$se_yagluth_name` | — | `GP_Queen` |
| `army_goblin` | `DragonTear`<br>`TrophyDragonQueen` | `TrophyGoblinKing`<br>`YagluthDrop` | `$se_moder_name` | — | `GP_Yagluth` |
| `army_moder` | `TrophyBonemass`<br>`Wishbone` | `TrophyDragonQueen`<br>`DragonTear` | `GP_Bonemass` | — | `GP_Moder` |
| `army_seekers` | `YagluthDrop`<br>`Demister`<br>`Wisp` | `QueenDrop` | — | — | `GP_Queen` |
| `army_theelder` | `PickaxeAntler`<br>`PickaxeBronze`<br>`PickaxeIron`<br>`HardAntler`<br>`TrophyEikthyr` | `TrophyTheElder`<br>`CryptKey` | `GP_Eikthyr` | — | `GP_TheElder` |
| `bats` | — | — | — | `KilledBat`<br>`defeated_bonemass` | — |
| `blobs` | `TrophyBonemass`<br>`Wishbone` | — | `GP_Bonemass` | — | — |
| `foresttrolls` | `TrophyTheElder`<br>`CryptKey` | — | — | `KilledTroll`<br>`defeated_gdking` | — |
| `gemgoblin` | — | `QueenDrop` | — | — | `GP_Queen` |
| `ghosts` | `TrophyBonemass`<br>`Wishbone` | `TrophyDragonQueen`<br>`DragonTear` | `GP_Bonemass` | — | `GP_Moder` |
| `hildirboss1` | `chest_hildir1` | — | `BossHildir1` | — | — |
| `hildirboss2` | `chest_hildir2` | — | `BossHildir2` | — | — |
| `hildirboss3` | `chest_hildir3` | — | `BossHildir3` | — | — |
| `skeletons` | `TrophyBonemass`<br>`Wishbone` | — | `GP_Bonemass` | — | — |
| `surtlings` | `TrophyBonemass`<br>`Wishbone` | — | — | `killed_surtling`<br>`defeated_bonemass` | — |
| `wolves` | `Wishbone`<br>`TrophyBonemass` | — | `GP_Bonemass` | — | — |

### 6.3 원본 22행에 있는 중복 variant

원본 `RandEventSystem.m_events`와 `LocationList.m_events`를 합친 baseline에서
`boss_*` forced-event를 제외한 22개 random raid row에는 아래 두 이름이 각각 두 번
들어 있다. 나머지 조건과 raid 동작 데이터는 20-name 표의 같은 이름 행과 동일하고,
`requiredPlayerKeysAny`만 다르다.

| Event | 추가 raw variant의 required player: any | 20-name reference에 남은 값 |
|---|---|---|
| `army_moder` | `$se_bonemass_name` | `GP_Bonemass` |
| `army_theelder` | `GP_TheElder` | `GP_Eikthyr` |

이 중복은 단순한 문서 노이즈로만 끝나지 않는다.

- 자동 event 후보 수집은 행을 deduplicate하지 않는다. 두 row가 모두
  enabled/random이고 나머지 조건도 유효할 때, 이 두 이름은 단일 행 event보다 후보
  entry 수와 상대 선택 가중치가 2배다. 현재 두 duplicate 쌍은 이 조건을 만족한다.
- PlayerEvents가 켜지면 클라이언트는 22행을 각각 평가하지만 서버에는 event name만
  전송한다. 서버가 이를 `HashSet<string>`으로 만들기 때문에 같은 이름의 variant 중
  하나라도 준비되면 그 이름 전체가 준비된 것으로 보인다.
- 이름으로 event를 복원하는 RPC/save/forced-event 경로는 raw order의 첫 enabled
  match를 사용한다. 첫 `army_moder`는 `$se_bonemass_name`, 첫 `army_theelder`는
  `GP_TheElder` variant이고, 20-name reference에 남은 두 `GP_*` variant는 뒤쪽 row다.
- raw `army_theelder` variant의 `GP_TheElder`는 같은 event의 forbidden player key에도
  들어 있으므로 그 키 자체는 승인 경로가 되지 않는다. Known-item 경로는 별도로 남는다.

## 7. Player based raids의 실제 선택 규칙

### 7.1 `PlayerEvents`가 꺼져 있을 때

- 먼저 `m_enabled && m_random`인 row만 자동 raid 후보가 된다.
- `requiredGlobalKeys`: world에 모두 있어야 한다.
- `forbiddenGlobalKeys`: world에 하나라도 있으면 event가 차단된다.
- known-item과 player-key 대체 조건은 event 선택에 사용되지 않는다.

### 7.2 `PlayerEvents`가 켜져 있을 때

event에 아래 다섯 대체 목록 중 하나라도 값이 있으면 global 조건을 무시하고 character
조건을 사용한다.

```text
requiredKnownItems
forbiddenKnownItems
requiredPlayerKeysAny
requiredPlayerKeysAll
forbiddenPlayerKeys
```

다섯 목록이 모두 빈 `army_charred`, `army_charredspawners` 같은 event는
`PlayerEvents`가 켜져 있어도 `HaveGlobalKeys` 단계에서는 global 조건을 계속 사용한다.
뒤의 event-point 필터는 여전히 `possibleEvents.Contains(name)`을 확인하지만, 대체
목록이 모두 빈 event는 모든 player에게 ready이므로 결과상 추가 제한이 생기지 않는다.

Character readiness의 실제 논리는 다음과 같다.

```text
Ready = no forbidden-known hit
     && no forbidden-player-key hit
     && (
          any required-known hit
          || any required-player-key-any hit
          || (
               required-known 목록이 비어 있고
               required-player-key-any 목록도 비어 있고
               required-player-key-all을 모두 보유
             )
        )
```

검사 순서상 forbidden known/player key는 항상 먼저 차단한다. 그 뒤
`requiredKnownItems`와 `requiredPlayerKeysAny`는 하나의 OR 조건군처럼 동작한다.
둘 중 하나가 성공하면 `requiredPlayerKeysAll`을 검사하기 전에 즉시 성공한다.

따라서 `requiredPlayerKeysAll`은 required-known과 required-any가 둘 다 빈 event에서만
독립적인 승인 경로가 된다. 현재 데이터에서는 `bats`의 all 조건은 직접 의미가 있지만,
`foresttrolls`와 `surtlings`의 all 목록은 nonempty required-known 목록과 함께 있어 실제
승인 결과를 바꾸지 않는다. 이 동작은 필드 이름에서 예상하기 쉬운
`(known OR any) AND all`이 아니다.

Required-known과 required-any가 모두 비어 있고 required-all까지 비어 있으면 all 조건은
공집합에 대해 참이므로 forbidden 조건만 통과하면 ready다. 따라서 모든 대체 목록이 빈
event뿐 아니라 `army_eikthyr`, `gemgoblin`처럼 positive required 목록은 비고 forbidden
목록만 있는 event도 forbidden hit가 없는 player에게 ready가 된다.

PlayerEvents가 켜진 서버는 준비된 event 이름뿐 아니라 그 이름에 준비된 **해당 player의
위치**를 event point 후보로 사용한다. 실제 point가 되려면 해당 event의 biome,
`nearBaseOnly`일 때 동기화된 `baseValue >= 3`, 그리고 `position.y <= 3000` 조건도 모두
통과해야 한다. 서버는 먼저 준비된 player가 한 명이라도 있는지 확인하고, 이어 각 target
player를 이 조건들로 다시 필터한다. 시작된 raid instance 자체는 world에 공유된다.

## 8. 조사 출처와 갱신 방법

### 8.1 Authoritative source

- `assembly_valheim.dll`
  - version: `0.221.12`
  - SHA-256: `3B26C8512778F6E0664B5AF2A26F3C30993A00F584C1E76D9123A742B67E2004`
- SoftRef bundle `17245031`
  - `_GameMain.prefab`의 `RandEventSystem.m_events`
  - `LocationList.m_events`
  - SHA-256: `8A56EDABFA90BFA081D8CDA5D9372F3AC4E36D2A510CAF7FB3037A2102432DFF`
- `StreamingAssets/SoftRef/Bundles` 635개 전체의 global-key 관련 serialized field
  - typetree 역직렬화 오류: 0

현재 프로젝트가 compile reference로 사용하는 publicized assembly는 `0.221.4`로 실제
설치본보다 오래됐지만, 이 문서와 관련된 `GlobalKeys` enum과 raid 판정 로직은
`0.221.12` 설치본과 동일함을 별도로 대조했다.

### 8.2 Cross-check source

- `DNS_events.reference.yml`
  - SHA-256: `E6D1224816E69D30C08ADBB693E5AED23224D2131AAD114223318363623B9A96`
  - DropNSpawn이 random raid 20개를 이름 기준으로 출력하고, `Unknown / Untracked`
    섹션에 `boss_*` forced-event 7개도 포함한 총 27행의 생성 파일
  - 편집 대상이나 Vanilla 원본 asset이 아니며, 실제 설정은 `DNS_events.yml`

게임 업데이트 뒤에는 최소한 다음을 다시 확인해야 한다.

1. `Version.CurrentVersion`과 Steam build ID
2. `GlobalKeys` enum의 추가/삭제/순서 변경
3. 전체 bundle의 모든 `*GlobalKey*` 필드, `DreamTexts.m_trueKeys/m_falseKeys`,
   `GameKeyType.Global` generic key condition과 코드에 하드코딩된 `Get/SetGlobalKey` 호출
4. `_GameMain.prefab` 및 모든 `LocationList.m_events`
5. random raid raw 행 수, unique event 이름 수와 duplicate variant
6. personal-key와 known-item literal의 exact casing
7. setter prefab의 location/spawner/soft-reference 도달 경로와 NetScene 등록 여부

## 9. YNW 런타임 key reference와 이 문서의 관계

YNW를 설치한 source-of-truth 서버 또는 listen host는 world가 로드된 뒤 다음 파일을
자동 생성한다.

```text
BepInEx/config/YouAreNotWorthy/keys.reference.yml
```

이 문서는 특정 Valheim 버전의 Vanilla 코드와 전체 SoftRef asset을 조사한 고정 baseline이다.
반면 `keys.reference.yml`은 해당 실행에서 실제 로드된 Vanilla 및 upstream mod 데이터를
반영한다. 따라서 역할이 다르며 어느 한쪽이 다른 쪽을 대체하지 않는다.

- `globalKeys`: enum, world fallback, raid global 조건, creature defeat setter와 실행 중
  관찰된 `GetGlobalKey`/`SetGlobalKey`를 대소문자 무시로 합친다.
- `handling`: YNW가 같은 key를 `personalized` 또는 `sharedWorld` 중 어느 쪽으로 처리하는지
  보여준다.
- `aliases`: 같은 world-key identity의 다른 literal 표기를 보존한다.
- `defeatPrefabs`: root `Character.m_defeatSetGlobalKey`가 해당 key를 촉발하는 등록 prefab만
  기록한다. 처치 키가 아니면 이 필드를 생략한다.
- `playerBasedRaidKeys`: 로드된 raw event 행 전체의 required-any, required-all,
  forbidden-player 목록을 exact-case로 합친다. Known item은 포함하지 않는다.

`PlayerEvents`가 꺼져 있으면 raid는 global 조건을 쓰며, YNW의 personalizable 항목은 대상
캐릭터의 같은 이름 unique key로 재해석된다. `PlayerEvents`가 켜져 있고 다섯 alt 목록 중
하나라도 채워진 event는 Vanilla character alt 조건을 사용한다. 다섯 목록이 모두 비면
켜진 상태에서도 global 조건을 계속 쓴다.

Reference는 편집용 override가 아니다. YNW가 읽거나 watch하거나 ServerSync하지 않으며,
수정 내용은 다음 자동 갱신 때 덮어써진다. 목록은 발견된 key catalog이지 현재 world나
character의 진행 snapshot이 아니며, value와 실제 보유 여부는 출력하지 않는다. 아직
실행되지 않은 mod 전용 하드코딩 key는 그 `Get`/`Set` 경로가 최초로 실행된 뒤에야 발견될
수 있다.
