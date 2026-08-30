# YouAreNotWorthy 기능 및 `progression.yml` 스키마

> 현재 스키마는 단일 `progression.yml` 문서다. 이전 versioned
> `progression.yml`과 별도 `ResourceMap.yml`은 지원·변환·삭제하지 않는다.

## 1. 기능 개요

YNW는 값이 없고 예약되지 않은 boolean global key를 캐릭터별 native unique key로
개인화한다. 같은 world의 플레이어라도 각자 보유한 key에 따라 trader, raid, spawn 및
아이템 사용 결과가 달라질 수 있다. 값이 있는 global key, world modifier, 계절과 같은
공유 상태는 Vanilla world key로 유지한다.

핵심 설정 파일은 다음 경로에 생성된다. Korean 번역은 DLL에 내장되며 별도 번역
폴더나 파일을 생성하지 않는다.

```text
BepInEx/config/YouAreNotWorthy/progression.yml
```

source-of-truth인 서버/listen host는 world가 로드된 뒤 다음 정보 파일도 생성한다.

```text
BepInEx/config/YouAreNotWorthy/keys.reference.yml
BepInEx/config/YouAreNotWorthy/items.reference.yml
```

`keys.reference.yml`은 발견된 Vanilla/mod global key와 Player based raid용 unique-key
literal을 기록한다. `items.reference.yml`은 YNW가 정적으로 식별할 수 있는 item-use 제한
후보와 최종 요구 personal key를 prefab owner 및 원본 `ItemType`별로 기록한다. YNW는 두
파일을 설정으로 읽거나 ServerSync로 보내지 않으므로 편집해도 동작이 바뀌지 않는다.

YNW는 BepInEx `.cfg` option을 노출하지 않는다. ServerSync lock은 항상 켜져 있고,
item restriction은 로컬 캐릭터가 server admin이면서 Vanilla debug mode일 때만 우회한다.
차단 중앙 문구의 재표시 간격은 1초로 고정한다. 이전
`sighsorry.YouAreNotWorthy.cfg`는 동작에 사용하지 않으며 삭제·migration하지 않는다.

## 2. `progression.yml` 전체 구조

```yaml
defeatKeys:
  - prefabs: [Serpent]
    key: defeat_serpent

Meadows:
  - Wood
  - Stone
  - Resin
  - Flint

BlackForest:
  requiredKey: defeated_eikthyr
  resources:
    - HardAntler
    - Bronze
    - Copper

Swamp:
  requiredKey: defeated_gdking
  resources:
    - Iron
    - Ooze
    - CryptKey

CustomBiome:
  requiredKey: some_custom_key

Ocean:
  requiredKey: defeated_gdking
  resources:
    - Chitin
    - SerpentScale

SerpentItems:
  requiredKey: defeat_serpent
  resources:
    - SerpentMeat
```

루트의 `defeatKeys`만 예약 필드다. 그 밖의 모든 루트 key는 사용자 정의 tier ID로
해석한다. `version`, `itemTiers`, `requirementText`, `Globalkey` 같은 wrapper는 없다.

tier 선언 순서는 낮은 rank에서 높은 rank 순서다. `defeatKeys`가 문서 중간에 있어도 tier
rank에는 포함하지 않지만, 읽기 쉬움을 위해 맨 위에 두는 것을 권장한다. Tier ID는
`Heightmap.Biome`을 검사하는 값이 아니라 데이터 label이므로 `CustomBiome`도 가능하다.

### 2.1 `defeatKeys`

각 rule은 정확히 다음 두 필드를 가진다.

| 필드 | 형식 | 의미 |
|---|---|---|
| `prefabs` | 비어 있지 않은 string 배열 | 죽은 creature의 내부 prefab 이름 |
| `key` | 비어 있지 않은 string | 사망 위치 주변에 지급할 personal key |

동작 규칙:

- 비플레이어 `Character`의 죽음만 대상으로 한다.
- 공격자 또는 막타를 판정하지 않는다.
- 죽은 prefab의 위치에서 32m 안에 있는 활성 플레이어 모두에게 key를 지급한다.
- 한 rule의 여러 prefab은 같은 key를 지급한다.
- counter, biome, 설정 가능한 radius, notification 조건은 없다.
- 비교 전에 공백과 끝의 `(Clone)`을 제거하며 대소문자는 무시한다.

Vanilla/mod prefab이 이미 `m_defeatSetGlobalKey`로 같은 진행 key를 쓰면 별도
`defeatKeys` rule은 일반적으로 필요 없다. YNW가 그 global-key write를 같은 사망 위치와
32m 규칙으로 자동 개인화한다.

### 2.2 제한 없는 tier 축약형

```yaml
Meadows:
  - Wood
  - Stone
```

배열형 tier는 resource만 분류하며 요구 key가 없다. 이 tier로 계산된 아이템은 item
restriction에서 허용된다. 배열은 비어 있으면 안 된다.

### 2.3 제한 tier 객체형

```yaml
BlackForest:
  requiredKey: defeated_eikthyr
  resources:
    - HardAntler
    - Bronze
```

| 필드 | 필수 | 의미 |
|---|---:|---|
| `requiredKey` | 예 | guarded use에 필요한 캐릭터 personal key |
| `resources` | 아니오 | 이 tier에 직접 분류할 resource 배열 |

`requiredKey`만 있는 객체형 tier도 유효하다. 다만 `resources`가 없으므로 그 항목 자체는
어떤 아이템도 분류하지 않고 실제 item gate 효과도 만들지 않는다. 빈 객체 또는
`resources`만 가진 객체형은 허용하지 않는다. 제한 없는 tier는 객체형이 아니라 배열
축약형으로 쓴다.

`requiredKey`는 world key를 직접 읽지 않는다. 동일 이름으로 캐릭터에 저장된 native
unique key를 검사한다. YNW가 개인화한 `defeated_eikthyr`, Vanilla native player key,
또는 `defeatKeys`가 만든 사용자 key를 모두 사용할 수 있다.

## 3. 자동 요구 문구

`requirementText`는 YAML에 저장하지 않는다. 아이템을 실제로 검사하거나 tooltip을 만들
때 `requiredKey`의 출처와 현재 Valheim 언어를 사용하여 두 번째 줄을 계산한다.

```text
You are not worthy!
Defeat Eikthyr!
```

처리 순서는 다음과 같다.

1. `Hildir1`, `Hildir2`, `Hildir3`이면 Hildir 상자 문구를 만든다.
2. 그 밖의 key는 설정된 `defeatKeys`와 runtime root
   `Character.m_defeatSetGlobalKey`에서 해당 key를 지급하는 prefab을 합친다.
3. prefab이 하나면 `Defeat <name>!`을 표시한다.
4. prefab이 여러 개면 `Defeat either <name1>, <name2>!`을 표시한다.
5. 처치 출처를 찾지 못하면 `Obtain <requiredKey>!`을 표시한다.

Creature/item 이름의 fallback 순서는 다음과 같다.

1. 현재 선택된 언어의 localized 이름
2. English localized 이름
3. creature/item prefab 이름
4. 출처 자체가 없으면 required-key 문자열

YNW는 English와 Korean 문장 틀을 DLL에 내장한다. 번역팩은 다음과 같이 정확한 이름의
클라이언트 로컬 파일 하나를 BepInEx 아래 어느 폴더에나 배치하여 선택 언어를 override할
수 있다.

```text
YouAreNotWorthy.<Valheim language name>.yml
YouAreNotWorthy.Turkish.yml
```

내장 Korean 스키마는 다음과 같다.

```yaml
blocked: "당신은 자격이 없습니다!"
defeat: "무찌르기: $1"
defeatEither: "다음 중 하나를 무찌르기: $1"
find: "찾아주기: $1"
obtain: "필요 조건: $1"
```

YNW는 시작 시 BepInEx 전체를 한 번만 검색하며 런타임 `translations` 폴더나
`Korean.yml`을 생성하지 않는다. 외부 파일 하나가 해당 언어의 내장값을 override하고,
생략한 필드는 해당 내장 언어 값 또는 내장 파일이 없는 언어에서는 English로 보완된다.
같은 언어의 외부 파일이 둘 이상이면 어느 것도 임의 선택하지 않고 내장 언어 또는
English로 fallback한다. `.json`, `.yaml`, `YouAreNotWorthy.Turkish.backup.yml`처럼
확장자 또는 점 구성이 다른 이름은 인식하지 않는다.
이전 `BepInEx/config/YouAreNotWorthy/translations/<Language>.yml` 경로는 읽거나
migration하지 않는다.

지원하는 key는 `blocked`, `defeat`, `defeatEither`, `find`, `obtain` 다섯 개뿐이다.
`blocked`에는 숫자 placeholder를 쓸 수 없고, 나머지 값에는 이름 또는 key가 들어갈
`$1` placeholder가 반드시 있어야 한다. 일부 key만 작성한 유효한 파일은 빠진 문장 틀을
DLL 내장 English 기본값으로 보완한다. 반면 YAML이 잘못되었거나 작성된 값 하나라도
유효하지 않으면 파일 전체를 무시하고 다섯 문장 모두 English 기본값을 사용한다.

번역 파일은 클라이언트 로컬 설정이며 ServerSync하지 않는다. 이미 발견된 파일의 내용을
바꾼 뒤에는 게임을 재시작하거나 다른 Valheim 언어로 전환했다가 돌아오면 다시 읽는다.
외부 파일 경로 목록은 시작 시 한 번만 만들기 때문에 외부 파일을 추가, 이동, 이름 변경,
삭제한 경우에는 재시작해야 한다. Creature/item 이름은 계속 Valheim 또는 해당 upstream
mod의 localization을 사용한다. 첫 줄은 `blocked`의 현재 언어 값이며 내장 English
fallback은 `You are not worthy!`로 고정된다.

Hildir 진행 key는 다음처럼 처리한다.

| key | 상자 prefab | 예시 문구 |
|---|---|---|
| `Hildir1` | `chest_hildir1` | `Find Hildir's Brass Chest!` |
| `Hildir2` | `chest_hildir2` | `Find Hildir's Silver Chest!` |
| `Hildir3` | `chest_hildir3` | `Find Hildir's Bronze Chest!` |

실제 출력의 상자 부분은 `$item_chest_hildir1/2/3` name token을 현재 언어로 변환한
값이다. `BossHildir1/2/3`은 상자 반환 key와 다른 처치 진행 key이므로 runtime defeat
prefab이 발견되면 일반 `Defeat ...!` 규칙을 따른다.

표시 문구는 YAML 또는 scene의 출처가 바뀌면 다시 만들고, 언어가 바뀐 뒤에는 localization
cache를 무효화하고 해당 언어의 번역 파일을 다시 읽는다. `keys.reference.yml`을 읽어
문구를 만들지는 않는다.

## 4. Resource 이름과 tier 계산

Resource에는 prefab 또는 `$item_*` 같은 내부 item token을 쓴다. 현재 언어로 번역된
표시 이름을 설정에 쓰면 안 된다.

Resource identity는 다음 순서로 정규화한다.

1. 앞뒤 공백 제거
2. 끝의 `(Clone)` 제거
3. `$item_` 또는 선두 `$` 제거
4. 문자와 숫자만 남김
5. 소문자 변환

따라서 `Black_Core`, `black-core`, `$item_BlackCore`는 같은 identity다. 같은 identity가
두 tier에 등장하면 먼저 나온 값을 쓰지 않고 문서 전체를 거부한다.

아이템은 prefab/internal item 이름으로 직접 tier를 얻거나 다음 생산 경로의 입력에서
tier를 상속한다.

- 일반 `Recipe`
- `CookingStation.ItemConversion`
- `Fermenter.ItemConversion`
- `Smelter.ItemConversion`

한 생산 경로에서는 가장 높은 입력 rank가 출력에 상속된다. 하나의 출력에 여러 제작
경로가 있으면 가장 낮은 요구 rank로 만들 수 있는 경로를 선택한다. 출력에 직접 지정된
tier가 더 높으면 직접 tier가 우선한다. 순환 경로는 중단한다.

선언 순서가 뒤인 제한 없는 tier가 결과의 최종 tier가 되면 낮은 tier의 요구 key는 함께
누적되지 않는다. 예를 들어 `Ocean`을 가장 높은 제한 없는 tier로 두고 어떤 recipe의
최고 입력이 Ocean resource라면 그 결과는 허용된다. 누적 gate가 필요하면 해당 상위
tier에도 `requiredKey`를 명시해야 한다.

## 5. 실제 item restriction

플레이어가 계산된 tier의 `requiredKey`를 가지고 있지 않을 때 다음 최종 사용 경로를
차단한다.

| 경로 | 대상 |
|---|---|
| `Humanoid.EquipItem` | 무기, 방어구, 도구, 일반 장착 아이템 |
| equipment 판정 | `AmmoNonEquipable` 포함 |
| `Player.CanConsumeItem` | 음식, potion, mead 등 Consumable |
| `OfferingBowl.UseItem` | item stand를 사용하지 않는 직접 boss-item offering |
| `OfferingBowl.InitiateSpawnBoss` | item stand 공물 배치 뒤 최종 boss 소환 |
| `Door.Interact` | 인벤토리의 물리적 key item을 자동 사용하는 Vanilla 잠긴 문 |
| `Door.UseItem` | key item을 문에 직접 사용하는 Vanilla 잠긴 문 |
| `ItemDrop.ItemData.GetTooltip` | 실제 guarded item의 동적 경고 |

같은 두 줄을 중앙 문구와 tooltip 끝의 빨간 경고에 표시한다. 캐릭터가 key를 얻거나
admin+debug 우회를 만족하면 이후 생성되는 경고는 사라진다.

InventorySlots는 soft dependency다. 설치되어 있으면 일반 equip 판정을 먼저 수행하고
non-quick 전용 장비 slot 경로도 보호한다. Quick slot에 넣는 행위 자체는 막지 않지만
실제 장착·소비 단계에서 다시 검사한다.

### 5.1 `items.reference.yml`

source-of-truth 서버/listen host는 effective item resolver가 준비되면 다음 형식의 읽기 전용
reference를 생성한다.

```yaml
# ===== Valheim =====
# ----- Chest -----
- ArmorBronzeChest, defeated_eikthyr

# ----- Consumable -----
- UnknownFood
- CarrotSoup, defeated_eikthyr

# ===== ExampleMod =====
# ----- Consumable -----
- ExamplePotion, defeated_gdking
```

- `- Prefab`은 제한 후보이지만 계산된 최종 `requiredKey`가 없다는 뜻이다.
- `- Prefab, key`는 캐릭터가 해당 personal key를 갖지 않았을 때 관련 최종 사용 경로가
  차단된다는 뜻이다.
- 출력 key는 직접 resource 지정과 recipe/cooking/fermenting/smelting 상속을 모두 반영한
  최종 결과다.
- owner 순서는 `Valheim`, mod 이름순, `Unknown / Untracked`이고 owner 추론 결과는 표시와
  정렬에만 사용한다.
- `ItemType`은 번역하거나 이름으로 추정한 분류가 아니라 loaded Valheim enum 이름이다.
  각 type 안에서는 key 없는 항목을 먼저 두고, key가 있는 항목은 해당 `requiredKey`가
  `progression.yml` top-level tier에 처음 등장한 순서와 prefab 이름순으로 정렬한다. 같은 key가
  여러 tier에 반복되어도 하나의 그룹으로 모인다.
- 일반 장비, consumable, ammo는 유효한 inventory icon이 있을 때만 기록한다. 이는 VNEI가
  Humanoid의 내부 공격 item과 player-facing item을 가를 때 사용하는 가벼운 신호를 차용한
  것이다. Door key와 명시적인 OfferingBowl/ItemStand item은 icon이 없어도 유지한다.
- VNEI 0.17.5의 내장 기본 blacklist에 속한 prefab은 reference에서 제외한다. VNEI DLL,
  실행 중 설정, 사용자 blacklist에는 의존하지 않는 YNW 내부 고정 목록이다.
- blacklist item의 prefab 이름 또는 내부 item token을 tier의 `resources`에 직접 명시하면
  reference 제외를 해제한다. 생산 경로로 tier를 상속한 것만으로는 직접 명시로 보지 않는다.
  이 예외는 원래 YNW의 최종 사용 제한 후보인 item에만 적용하므로 일반 Material을 새로 싣지 않는다.
- tier 전파에만 쓰이고 직접 제한 행동이 없는 일반 재료는 싣지 않는다. ItemStand의 명시적
  boss-item과 supported-item은 icon 없이도 포함하고, supported-type은 icon이 있는 item만 포함한다.
  broad allow-all stand나 runtime에서만 바뀐 공물은 reference에 없을 수 있다. 실제 소환 제한은
  소환 순간 현재 부착 item을 다시 검사한다.

이 파일은 runtime/source가 바뀔 때 다시 쓰며 내용이 같으면 timestamp를 유지한다. 삭제하면
authoritative runtime에서 다시 생성하지만, 클라이언트는 로컬 사본을 만들거나 덮어쓰지 않는다.

문 제한은 문이나 플레이어가 위치한 biome 또는 tier ID의 이름을 검사하지 않는다.
`Door`가 요구하는 물리적 key item의 계산된 tier를 검사하므로, 그 item을 인벤토리에
가지고 있으면서 동시에 tier의 `requiredKey`를 개인 key로 보유해야 문을 열 수 있다.
개인 key만으로 물리적 key item을 대체하지 않으며, 물리적 item만 있고 개인 key가 없으면
같은 중앙 문구와 key-item tooltip 경고를 표시한다. key item이 없는 일반 문은 영향을
받지 않는다.

key item도 `resources`에 직접 명시되거나 생산 경로를 통해 tier를 상속해야 이 추가 제한을
받는다. 내장 기본값은 `CryptKey`를 `Swamp`, `DvergrKey`를 `Mistlands`에 직접 명시한다.
다만 열린 상태는 Vanilla의 공유 ZDO world state다. 조건을 충족한 플레이어가 문을 열면
다른 플레이어도 열린 문을 통과할 수 있으며, YNW는 문 상태나 충돌까지 개인화하지 않는다.

다음 동작은 의도적으로 허용한다.

- pickup, 보관, 거래
- crafting과 제작 재료 사용
- 공격, 방어, 일반 interact
- build, repair, remove
- cook, ferment, biome 이동
- item stand에 공물을 놓는 행위 자체

즉 progression을 건너뛰어 아이템을 얻거나 제작해도 최종 장착·소비·소환 시점에 제한한다.

## 6. Global-key 개인화

### 6.1 분류

YNW는 처음 만난 값 없는 비예약 boolean global key를 personal key로 등록한다. 다음은
world state로 유지한다.

- `key=value` 형태의 value-bearing global key
- server/world modifier와 예약 enum 영역
- `PlayerEvents`, `activeBosses`, `AshlandsOcean` 같은 공유 옵션·상태
- `season_winter`, `season_fall`, `season_summer`, `season_spring`

### 6.2 읽기

`ZoneSystem.GetGlobalKey(string|GlobalKeys)`가 personal boolean key를 조회하면 world set
대신 해당 플레이어의 native unique key를 검사한다. Trader, `ConditionalObject` 및 같은
Vanilla global-key 조회 경로를 쓰는 mod가 이 동작을 공유한다.

### 6.3 쓰기

Personal boolean key write는 world에 기록하지 않고 사건 위치 32m 안의 활성 플레이어에게
같은 이름의 native unique key를 지급한다.

- `Character.m_defeatSetGlobalKey`와 `defeatKeys`: 죽은 creature 위치
- `Trader.UseItem`: Trader 위치; Hildir 상자 반환의 `Hildir1/2/3` 포함
- `OfferingBowl.UseItem`: OfferingBowl 위치
- `Vegvisir.Interact`: Vegvisir 위치
- 별도 위치가 없는 일반 mod/console write: 요청 플레이어 위치 fallback

요청 플레이어도 반경 조건을 만족할 때만 지급되며, 32m 밖의 플레이어나 막타자를 강제로
포함하지 않는다. Fallback 요청 플레이어를 찾지 못하면 world write는 차단하지만 위치를
추측해 personal key를 지급하지 않는다. 공유/value key write는 Vanilla대로 world에
저장한다.

## 7. 관리자 명령

### 7.1 Personal key

서버 관리자는 Steam backend로 접속 중인 계정이 현재 사용하는 캐릭터의 native unique
key를 조회·추가·삭제할 수 있다.

```text
ynw:keys players
ynw:keys list <SteamID64>
ynw:keys add <SteamID64> <key>
ynw:keys remove <SteamID64> <key>
```

- `players`가 출력한 17자리 SteamID64를 다른 명령의 대상 인수로 그대로 사용한다.
- 캐릭터 이름은 중복될 수 있고 character ID는 복제된 캐릭터 파일 사이에서 충돌할 수
  있으므로 대상 인수로 받지 않는다.
- 서버는 원격 요청자를 실제 접속 socket과 admin list로 다시 검증하고, 일치하는 Steam
  peer가 현재 사용하는 캐릭터에만 명령을 적용한다.
- 대상은 처리 시점에 접속 중이고 캐릭터 로딩을 마친 상태여야 한다. crossplay와 offline
  character는 지원하지 않는다.
- 변경 대상은 별도 YNW 저장소가 아니라 `Player.AddUniqueKey`/`RemoveUniqueKey`가 쓰는
  native character unique-key set이다. 따라서 YNW gate뿐 아니라 Vanilla Player based
  raids 및 같은 key를 읽는 mod에도 영향을 줄 수 있다.
- 변경이 발생하면 활성 캐릭터의 정상 profile 저장을 요청한다. 저장에 실패하면 메모리
  변경은 유지하되 명령 결과와 로그에 경고한다.
- 빈 key, 공백을 포함한 key, value-bearing key, 예약된 shared-world key는 추가·삭제
  대상으로 받지 않는다.
- native unique key spelling은 대소문자를 구분한다. `list`, `keys.reference.yml`, Vanilla
  또는 해당 mod가 정의한 정확한 casing을 사용해야 한다. 이미 runtime에 알려진 key는 그
  canonical spelling으로 해석하지만, 아직 알려지지 않은 임의 mod key의 입력 오타를
  자동 교정하지 않는다.
- `remove` 뒤 정상 진행 사건이 다시 발생하면 같은 key를 다시 받을 수 있다.
- player별 YAML, offline pending queue, `.fch` 직접 편집은 제공하지 않는다.

ServerCharacters의 Steam peer 식별과 서버측 admin 재검증 구조를 참고했지만 YNW는 해당
mod에 의존하지 않는다. ServerCharacters가 설치되어 있어도 YNW는 그 mod가 보관하는
offline profile을 열거나 수정하지 않고, 접속 중인 `Player`와 현재 save path만 사용한다.
명령에는 server-admin 권한만 필요하다. admin+debug는 item restriction 우회 조건이며 이
명령의 추가 조건이 아니다.

### 7.2 Item reference 갱신

```text
ynw:items refresh
```

연결된 서버 관리자 또는 서버/listen-host 콘솔에서 `items.reference.yml`의 즉시 재스캔을
요청할 수 있다. 원격 클라이언트는 요청만 보내며, 서버가 실제 peer와 admin list를 다시
검증한 뒤 source-of-truth 서버에서 resolver와 prefab-owner cache를 무효화하고 파일을
생성한다.

기준은 서버에 현재 적용된 effective `progression.yml`과 서버의 `ObjectDB`/`ZNetScene`이다.
클라이언트의 로컬 YAML이나 item 목록은 사용하지 않고 클라이언트 reference도 쓰지 않는다.
이 명령은 디스크 YAML을 다시 읽지 않으므로, 서버 파일을 막 편집했다면 reload 완료 로그를
확인한 뒤 실행해야 한다. 내용이 동일하면 파일 timestamp를 유지하며, world runtime이 아직
준비되지 않았거나 쓰기에 실패하면 완료로 보고하지 않는다.

## 8. Raid와 spawn

YNW는 `PlayerEvents`를 강제로 켜거나 끄지 않고 서버의 실제 **Player based raids** 값을
따른다.

- `PlayerEvents`가 켜져 있고 event에 alternate known-item/player-key 조건이 있으면 Vanilla
  alternate 조건을 사용한다.
- 그 외에는 `requiredGlobalKeys`와 `forbiddenGlobalKeys`를 사용한다. Personal boolean
  key는 대상 캐릭터 key로, 공유/value key는 world 조건으로 평가한다.
- `PlayerEvents`가 꺼져 있으면 personal global-key 조건을 만족하는 플레이어 위치로 event
  point를 제한한다.

`SpawnSystem.SpawnData.m_requiredGlobalKey`가 personal이면 해당 key를 가진 플레이어만 base
spawn 후보가 된다. `CreatureSpawner`의 required/blocking personal key도 trigger 범위 안의
같은 플레이어 snapshot으로 평가한다. Timer, roll, cap, group, raid instance와 생성된
network object는 공유 world system으로 남는다.

## 9. ServerSync, hot reload, LKG

`progression.yml` 원문 하나가 검증·적용·동기화의 단위다.

1. 시작 시 로컬 `progression.yml`을 읽고 전체 schema를 검증한다.
2. source-of-truth 서버/listen host의 유효한 원문을 하나의 ServerSync 값으로 전송한다.
3. 클라이언트는 받은 원문 전체를 다시 검증한 뒤 런타임 상태를 한 번에 교체한다.
4. 실패하면 현재 last-known-good 문서 전체를 유지한다.

클라이언트는 server YAML을 메모리에만 적용하고 로컬 `progression.yml`을 덮어쓰지 않는다.
연결 중 로컬 편집은 현재 서버 설정을 바꾸지 않으며 연결 밖에서 쓸 fallback으로 남는다.
최초 실행에 유효한 로컬 파일이 없으면 embedded default를 생성해 적용한다.

별도의 BepInEx `.cfg` 동기화 channel은 없다. `ConfigSync`는 authoritative YAML 전송과
server/admin 및 version 상태 확인에만 내부적으로 사용한다. YNW는 모든 peer에 같은
version을 요구하며 `keys.reference.yml`과 `items.reference.yml`은 동기화하지 않는다.

## 10. 검증 규칙

다음은 `progression.yml` 전체를 거부한다.

- YAML root가 mapping이 아님
- `defeatKeys`의 대소문자가 정확하지 않거나 sequence가 아님
- `defeatKeys` rule에 `prefabs`, `key` 외 필드가 있음
- `defeatKeys.key`가 비었거나 `prefabs`가 비었음
- 한 defeat rule에 정규화 후 중복 prefab이 있음
- tier ID가 비었거나 대소문자를 무시했을 때 중복됨
- tier 값이 resource 배열 또는 허용된 객체형이 아님
- 배열 축약형 resource가 비었음
- 객체형에 `requiredKey`, `resources` 외 필드가 있음
- 객체형 `requiredKey`가 비었거나 누락됨
- resource identity가 문서 내에서 중복됨
- personal-key canonical casing이 충돌함
- value-bearing/shared world key를 personal key로 등록하려 함

사용자 정의 tier ID 때문에 알 수 없는 루트 이름 자체는 오류로 판정할 수 없다. 예를 들어
`BlackForrest` 오타도 유효한 custom tier가 될 수 있다. 대신 그 값의 형태와 하위 필드는
엄격하게 검증한다.

## 11. 내장 기본 tier

기본 `defeatKeys`에는 `Serpent` 사망 위치 32m 안의 활성 플레이어에게
`defeat_serpent`를 지급하는 rule이 하나 있다. 기본 tier 순서와 gate는 다음과 같다.

| rank | tier | resource 수 | requiredKey |
|---:|---|---:|---|
| 0 | `Meadows` | 15 | 없음 |
| 1 | `BlackForest` | 16 | `defeated_eikthyr` |
| 2 | `Swamp` | 13 | `defeated_gdking` |
| 3 | `Ocean` | 2 | `defeated_gdking` |
| 4 | `SerpentItems` | 1 | `defeat_serpent` |
| 5 | `Mountain` | 10 | `defeated_bonemass` |
| 6 | `Plains` | 14 | `defeated_dragon` |
| 7 | `Mistlands` | 19 | `defeated_goblinking` |
| 8 | `AshLands` | 22 | `defeated_queen` |

`Meadows`는 분류 기준이지만 제한하지 않는다. `Ocean`은 Swamp와 같은 Elder 진행 key를
요구한다. `SerpentItems`는 실제 biome 판정이 아닌 별도 tier label이며 `SerpentMeat`와
그 tier를 상속한 아이템에 `defeat_serpent`를 요구한다. 기본 resource 112개는 정규화 후
모두 고유하다.

## 12. 레거시와 제한

- 제거된 `killKeys` 이름은 alias로 읽지 않으며, 기본 custom key `killed_serpent`도
  `defeat_serpent`로 migration하지 않는다.
- 이전 `version: 4` 및 v1-v3 형식은 현재 스키마로 변환하지 않는다.
- 기존 별도 `ResourceMap.yml`은 읽거나 자동 병합하거나 삭제하지 않는다.
- 제거된 `itemTiers`, `requirementText`, trigger/action, counter 필드를 지원하지 않는다.
- 이전 번역 필드명이나 파일 형식의 alias·migration은 지원하지 않고 현재 다섯 필드만 읽는다.
- 기존 counter custom data를 읽거나 삭제하지 않는다.
- 기존 world global key를 제거하거나 캐릭터로 이관하지 않는다.
- 기존 character unique key의 spelling을 migration하지 않는다.
- clean/new world에서 world-write-blocking 모델을 시작하는 것을 권장한다.
- base `Character.OnDeath`를 호출하지 않는 독자적인 mod death 구현은 자동 처치 출처와
  `defeatKeys` 대상이 아니다.
- Vanilla equip/consume/offering/raid/spawn 경로를 우회하는 mod는 별도 compatibility가
  필요하다.
- 로컬 Harmony restriction과 event RPC를 server-authoritative anti-cheat 경계로 간주하면
  안 된다.

기존 파일은 복구를 위해 사용자가 직접 보관·삭제할 수 있도록 YNW가 손대지 않는다.

## 13. 검증 체크리스트

1. 서버와 모든 클라이언트의 YNW version을 맞춘다.
2. 기존 YAML, world와 character를 백업한다.
3. 로그에서 `progression.yml` 검증과 ServerSync 적용 성공을 확인한다.
4. 서로 다른 key를 가진 두 플레이어로 trader, raid, spawn 및 item 결과를 비교한다.
5. 직접 분류 item과 recipe/cooking/fermenting/smelting 상속 item을 각각 시험한다.
6. 장착, `AmmoNonEquipable`, 음식/potion, 직접 OfferingBowl 공물과 item-stand 소환을
   시험한다.
7. `CryptKey`와 `DvergrKey` 각각에 대해 물리적 item만 보유, 개인 key만 보유, 둘 다 보유한
   경우의 `Door.Interact`/`Door.UseItem` 결과와 key-item tooltip을 확인한다.
8. 한 key가 한 prefab/여러 prefab/출처 없는 custom key일 때 자동 두 번째 줄을 확인한다.
9. `Hildir1/2/3`에서 현재 언어의 상자 이름과 `Find ...!` 문구를 확인한다.
10. 게임을 재시작하거나 다른 언어로 전환했다가 돌아온 뒤 해당 로컬 번역 파일이 적용되고
   기존 언어 이름이 cache에 남지 않는지 확인한다.
11. admin 단독, debug 단독, admin+debug를 구분해 시험한다.
12. YAML을 일부러 잘못 편집해 이전 LKG가 유지되는지 확인한다.
13. 사망·Hildir 반납·OfferingBowl·Vegvisir 사건의 31.9m/32.1m 경계를 확인한다.
14. 비관리자/관리자, 잘못된 SteamID64, 로그아웃한 대상, `add`/`list`/`remove`, 제거 뒤
    정상 사건에 의한 재지급을 확인한다.
15. 같은 character ID를 복제한 서로 다른 Steam 계정이 접속해도 SteamID64가 일치하는
    peer의 현재 캐릭터만 변경되는지 확인한다.
16. 전용 서버/listen host에서는 `items.reference.yml`이 생성되고 remote client에서는
    생성되지 않는지 확인한 뒤, 서버에서 파일을 삭제하면 자동으로 다시 생성되는지 확인한다.
17. `items.reference.yml`이 `Valheim` -> mod -> `Unknown / Untracked` 순서이고, 각 owner 안에서
    raw `ItemType`, key 없는 항목, `progression.yml`의 requiredKey 첫 등장 순서, prefab 순으로
    정렬되는지 확인한다.
18. monster attack ItemDrop은 빠지고 icon이 있는 player item 및 icon 없는 명시적
    Door/OfferingBowl/ItemStand item은 유지되는지 확인한다. VNEI 기본 blacklist item은 빠지되
    `resources`에 직접 지정한 최종 사용 제한 후보만 다시 포함되는지 확인한다.
19. `progression.yml`을 reload했을 때 `items.reference.yml`의 최종 key와 정렬이 갱신되는지 확인하고,
    Jotunn 등록 prefab과 추적 불가능한 prefab의 owner fallback을 각각 확인한다.
20. 연결된 비관리자의 `ynw:items refresh`는 서버에서 거부되고, 관리자의 요청은 서버 파일만
    갱신하며 결과가 요청자 콘솔에 돌아오는지 확인한다. 같은 내용을 다시 갱신했을 때 timestamp가
    유지되는지도 확인한다.
