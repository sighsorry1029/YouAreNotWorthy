# 구조 검토 및 구현 기록 — 2026-09-20

현재 구조는 **대체로 균형 잡혀 있으며, 일부 중복된 상태 표현과 호환성 처리만 정리하는 것이 유리**하다.
파일 크기를 근거로 분할하지 않았다. 구조 변경 두 건을 구현·검증하고 각각 커밋했다.
성능 향상을 측정한 작업은 아니며, 기대 효과는 변경 지점과 탐색·검증 비용의 감소다.

## 기준과 검토 범위

- 작업 브랜치: 기존 `main`. 시작 HEAD: `7a291283b9e633fe7b8efb73fd96077a12c4b7b2`.
- 시작 시 tracked 수정 19개, untracked 4개가 있었다. 기존 Valheim 대응, ResourceMap,
  버전 1.0.6, 개인 키 지급 거리 64m 변경 등을 이번 커밋에 포함하지 않았다.
  **빌드·검증 입력은 기존 변경을 포함한 작업 트리**다. HEAD 단독의 1.0.15 호환성 검증으로 해석하지 않는다.
- `C:\Users\blizz\.codex\AGENTS.md`, 저장소와 상위 경로의 프로젝트 지침 존재 여부,
  전역 Valheim `INDEX.md`, 프로젝트 설명서·스키마·검증 문서·과거 호환성 기록을 확인했다.
  별도의 프로젝트 `AGENTS.md`는 없었다.
- 기존 호환성 문서는 1.0.7 대응을 설명하고, 전역 2026-09-13 검토는 1.0.12의
  클라이언트/listen-host 실행 근거를 기록한다. 이번 설치본은 **1.0.15, Steam 25390630**이다.
  설치본의 게임 어셈블리 3개 해시를 보관된 1.0.15 원본과 대조했다.
  과거 실행 결과를 이번 변경의 실행 성공으로 재사용하지 않았다.
- 사용한 원본: 전역 snapshots 아래
  `client-b25390630-windows-x64-20260918T131715Z` 및
  `dedicated-server-b25390671-windows-x64-20260918T185703Z-depot-restored`.
  관련 원본/기존 ILSpy 추출 자료만 참고했으며 자료 재수집·publicize·게임 지원 범위 확대는 하지 않았다.

실제 모드 빌드 대상은 [YouAreNotWorthy.csproj](../YouAreNotWorthy.csproj)의 .NET Framework 4.8 클래스 라이브러리다.
[Plugin.cs](../Plugin.cs)의 `YouAreNotWorthyPlugin : BaseUnityPlugin`과 `Awake`가 진입점이고,
`Update`와 `OnDestroy → CleanupRuntime`이 런타임 갱신·정리를 담당한다. Harmony를 사용하는 일반 플러그인이며 프리로더 패처가 아니다.

컴파일은 `environment.props`의 원본 `valheim_Data/Managed`와 BepInEx를 참조한다.
남아 있는 `PublicizedAssembliesPath` 속성은 현재 게임 Reference의 입력이 아니다.
[ILRepack.targets](../ILRepack.targets)는 YNW + 고정된 `Libs/ServerSync.dll` + YamlDotNet 16.3.0을
최종 YNW DLL로 internalize/병합한다. Debug의 `DeployDebugToGame`은 병합 후 DLL만 plugins에 복사한다.
manifest의 BepInEx 의존성은 이미 `5.4.2350`이어서 변경하지 않았다.

검토 범위는 Plugin/API/Config/Core/Patches의 책임·상태·호출 관계, YAML 두 종류와 기본 리소스,
번역·생성 참조 파일, Harmony/리플렉션/Unity 메시지, 선택 연동 및 기존 검증 실행 파일이다.
`InventorySlots`는 SoftDependency와 동적 Harmony 대상이다. FineDining/expand_world_data 순서 표식,
PrefabOwnerResolver의 선택적 `Jotunn.Utils.ModQuery` 조회와 AssetBundle/SoftRef 소유자 추론도 경계를 확인했다.
Jotunn을 필수 의존성으로 바꾸지 않았고, 원격 키 관리자 명령의 Steam 전용 정책도 유지했다.

생성물 `bin/obj`, 배포 ZIP, 외부 라이브러리 전체 구현, 게임 전체 코드·리소스 재조사,
모든 외부 모드 조합, 네이티브 엔진과 Windows 외 플랫폼은 구조 변경 범위에서 제외했다.
병합 입력과 최종 DLL의 관련 참조/패치 계약은 검사했지만 외부 라이브러리 전체를 감사한 것은 아니다.
전체 ResourceMap 획득 경로 재조사도 하지 않았다.

## 영역별 판단과 유지 결정

| 영역 | 판단 | 책임·상태와 변경 사례에 따른 근거 |
| --- | --- | --- |
| Plugin / YAML 로더 | 균형 | Awake에서 초기화, CleanupRuntime에서 이벤트·watcher·coroutine·명령·패치를 정리한다. progression만 인덱스/캐시를 갱신하고 locations는 파일명 정책이 달라 generic loader로 합치지 않았다. |
| RestrictionEvaluator / ProgressionIndex | 큰 파일이지만 응집성 있음 | 제작 그래프·해석 캐시·참조 출력 후보는 같은 ResolverState 수명을 공유한다. `7a29128`도 같은 해석 경로의 할당과 검증을 함께 변경했다. 분할보다 현재 상태 소유권 유지가 낫다. |
| PlayerKeys / PersonalKeySnapshot / 관리자 RPC | 균형 | 로컬 native 키 변경과 owner 게시 ZDO 형식, requester 권한과 대상 character identity는 별도 책임이다. PlayerKeyCommands는 길지만 pending token/응답/timeout을 쪼개면 상태 전달이 늘어난다. |
| 두 콘솔 명령의 registry 처리 | 동일 호환성 처리 중복 | 비공개 필드 조회·충돌 거부·자기 인스턴스 해제가 동일하다. `4c7e242`에서 두 명령의 admin 판단이 함께 바뀌었고 `7a29128`에서 DirectPeerChecks로 그 경계를 모은 이력도 있다. 이번에는 registry 경계만 공동 배치했다. |
| 두 reference writer | 분리는 적절, item 결과 표현만 중복 | keys는 관찰/debounce, items는 캐시/owner 갱신·파일 존재 검사·강제 refresh·atomic write를 소유한다. 두 writer를 통합하지 않고 item 내부 결과 enum만 정리했다. |
| spawn / raid / location transport | 분리 유지 | spawn 대상 선택, native Player와 원격 ZDO fallback, raid 정책, 위치 전송과 표시 조건이 다르다. 유사한 조건문을 같은 정책으로 가정하지 않았다. |
| tooltip / 번역 / 선택 연동 | 유지 | 중첩 tooltip의 depth/최종 suffix, finalizer와 FineDining 순서, 번역 캐시 무효화, optional hook의 준비 상태가 각기 다른 수명주기를 가진다. |

`TryGetCanonicalPersonalKey`·`TryResolvePersonalKey`·`TryRegisterPersonalKey`는 각각 등록된 키 확인,
유효한 미등록 키 허용, 등록이라는 차이가 있다. 공개 API의 literal snapshot 조회도 그대로 유지했다.
관리 명령의 키 정규화와 지급 RPC의 키 검증은 허용 조건·등록 효과가 달라 합치지 않았다.
`PlayerKeys.FindNativeKeys`의 목록 복사는 native 임시 목록을 snapshot 재게시가 재사용하는 재진입 경계이므로 유지했다.
단순히 직접 참조가 적다는 이유로 타입이나 보호 분기를 삭제하지 않았다.

## 구현한 작은 단계

### 1. `657d5a1` — ItemReferenceWriter의 결과 표현 통일

- 문제/호출: `TryUpdate`와 `TryRefresh → TryWriteIfNeededWithResult`가 같은 네 결과를
  `ItemReferenceWriteResult`와 `ItemReferenceRefreshResult` 사이에서 1:1 변환했다.
- 최소 변경: private write 메서드부터 기존 Refresh 결과를 반환하고 중복 enum과 mapping switch를 삭제했다.
  `NotReady`는 기존 `RuntimeNotReady`로 표현한다. 새 파일·호출 단계는 없다.
- 기대 효과: 새 쓰기 상태를 추가하거나 읽을 때 확인할 표현/변환 위치가 하나 줄어든다.
- 보존/위험: Refresh enum 숫자, authority 검사, 1초/5초 retry, lock/revision, 생성 YAML과 atomic write를 유지했다.
  주요 회귀 위험은 not-ready 비교의 잘못된 치환이며 두 호출부를 diff로 대조했다.
- 검증: Debug 빌드·병합·배치, 기존 관리 코드 검사, 반환 지점과 retry 비교의 diff 검토.
  최종 메타데이터의 Refresh enum 값도 기준 DLL과 일치했다.
  실제 런타임 준비 전/후 refresh와 IO 실패·종료 경합은 실행하지 않았다.

### 2. `70a8618` — 명령 registry 호환성·소유권 검사 공동 배치

- 문제/호출: `PlayerKeyCommands`와 `ItemReferenceCommands`의 등록/Shutdown이 각각
  `Terminal.commands` reflection, 이름 충돌 검사, `ReferenceEquals` 제거 조건을 보유했다.
- 최소 변경: [ConsoleCommandRegistry.cs](../Core/ConsoleCommandRegistry.cs)의
  `CanRegister`/`Unregister`에 이 처리만 배치했다. 생성자·명령 이름/문구/flags/options,
  `_consoleCommand`, item `_shutdown`, keys pending 요청 상태는 기존 소유자에 남겼다.
- 비용 비교: 새 내부 파일 하나와 등록/해제 시 호출 한 단계가 생긴다. 대신 비공개 registry 변경과
  외부 명령 보존 조건을 두 파일에서 수정·검증하던 비용이 한 곳으로 모인다.
  factory/interface/명령 프레임워크는 추가하지 않았고 매 프레임 경로도 바뀌지 않았다.
- 보존/위험: 원본 1.0.15 `Terminal.commands`는 protected static 필드이며 공개 생성자가 즉시 등록한다.
  기존 cached reflection 접근을 유지했다. 등록 거부와 외부 교체 후 해제가 주된 회귀 위험이다.
- 검증: 실제 두 owner 메서드와 원본 registry를 쓰는 격리 검사 20개를
  변경 전/후 DLL 및 client/server 원본에 실행했다. identity/flags/options, 반복 등록,
  소유 항목 해제, 외부 교체본 보존, 재등록, 반복 종료, 독립 owner 정리가 통과했다.
  검사 코드는 [ConsoleCommandVerification.cs](../Verification/RuntimeVerification/ConsoleCommandVerification.cs)에 있다.
- 한계: 사전 이름 충돌/registry 부재의 경고는 plugin static 초기화까지 유발한다.
  기준 DLL도 게임 밖의 `ThreadingHelper.Instance` 부재로 이 검사에서 실패했다.
  제품 회귀로 분류하지 않았으며 초기화를 stub하거나 생산 코드를 바꾸지 않았다.
  해당 두 경로는 코드/diff로 검토했고 실제 Valheim 검증이 남는다.

이 순서대로 수정 → 검증 → diff 리뷰 → 커밋했다. 각 변경은 개별 revert 가능하다.
복잡한 resolver의 단일 호출 래퍼 인라인은 `EnsureResolver`/직접 티어 조회 순서까지 다시 입증할 비용에 비해
이익이 작아 보류했다. 새 캐시·정책 통합·다른 모드 변경은 수행하지 않았다.

## 기능 보존과 실행 비용 검토

- Harmony 대상/오버로드/priority, `__state`, 반환값과 예외/finalizer 처리는 수정하지 않았다.
  기준/최종 DLL의 최상위 Harmony 패치 타입에 속한 97개 메서드의 symbolic IL이 동일했다.
- API v1의 공개 메서드 시그니처와 결과 enum 숫자, 내장 progression/locations/Korean 리소스 바이트가 동일하다.
  YAML 키·기본값·live 변경, native 키 저장 형식, snapshot/RPC 식별자와 제한은 이번 변경 범위 밖이며 유지했다.
- owner와 관리자 권한은 같은 개념으로 합치지 않았다. 서버 connected-peer 인증, 현재 서버 연결 확인,
  요청 token/timeout, 대상 character 일치, 중복 키 추가 방지와 원격 snapshot bounds를 유지했다.
- 아이템 제거/보스 RPC 앞의 검사, 원격 요청/응답 및 native 저장은 변경하지 않았다.
  이것만으로 지연·중복·접속 해제·owner 교체 시 아이템 수량 보존을 실행 검증한 것은 아니다.
- Plugin.Update의 두 writer는 dirty/time gate를 사용한다. item 파일 존재 검사는 5초 주기이고,
  admin pending 검사는 요청이 없으면 즉시 반환한다. 전체 scan/serialization을 매 프레임 수행하지 않는다.
- 지도 필터는 원본 Minimap.UpdateLocationPins 내부의 5초 gate 뒤에서 호출된다.
  YNW가 매 프레임 UI를 재생성하지 않는다. tooltip은 문자열을 구성하지만 UI 객체 수명은 기존 UI에 있다.
- Door/OfferingBowl 접근자, 텍스트 source/localization 캐시는 기존 무효화 경계를 유지했다.
  텍스트 cache key 문자열, snapshot decode, 선택 InventorySlots의 PropertyInfo.GetValue 비용은 남지만
  실제 프레임 병목으로 측정하지 않았다. 새 캐시의 갱신·owner 변경·파괴 비용을 추가하지 않았다.
- warmed tier/null 조회는 각각 .NET 격리 실행의 1,000회에서 관리 할당 0바이트였다.
  기준도 동일하므로 이번 패치의 성능 향상 수치나 Unity 프로파일링 결과가 아니다.

## 검증 결과와 남은 범위

| 구분 | 수행 결과 | 미검증/필요한 실제 확인 |
| --- | --- | --- |
| 빌드 | 기준 및 두 단계에서 `dotnet build YouAreNotWorthy.csproj -c Debug -p:DeployToGame=true`, 경고/오류 0. ILRepack 및 plugins 복사 성공, 단계별 SHA-256 일치. 검증기 Debug 빌드도 통과. | Release/ZIP/버전 변경/게시/push는 요청 범위가 아니며 수행하지 않음. |
| 자동 검사 | 1.0.15 client/server 원본에 기준/최종 DLL 검사 통과: 64 Harmony bindings, 8 transpiled originals의 패턴/변환 IL/JIT, YAML·tier 검사, console 검사 20개. 최종 DLL의 게임 타입/멤버 참조 271개가 양 원본에서 resolve됨. 공개 계약·내장 리소스 비교 통과. | Unity native 호출은 기존 verifier에서 JIT용 stub으로 대체하며 실제 변환 메서드를 실행하지 않음. 전체 PatchAll/다른 모드 priority 조합/InventorySlots 2개 hook은 실행 검증 대상 밖. |
| 실제 게임 실행 | 이번 작업에서는 수행하지 않음. 로컬 DLL 배치까지만 완료. | 시작/종료·watcher/이벤트 정리, 콘솔 사전 충돌/경고, items refresh 준비/실패/재시도, client/host/dedicated와 Steam/PlayFab, 권한 거부·중복/지연·재접속, 아이템 수량·저장 왕복, 언어/live YAML/UI/선택 연동. |

최종 Debug DLL: `bin/Debug/YouAreNotWorthy.dll`.
설치 대상: `C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx\plugins\YouAreNotWorthy.dll`.
양쪽 SHA-256: `CFB2C49460200FC02AC99420AC870BA3075C6B73FB6785AFE8CEB105A2CC6C17`.
로컬 기준 DLL·단계별 로그는 Git 제외 경로 `obj/structure-review-20260920`에 보관했다.

별도로 남긴 확인된 문제는 `Docs/ProgressionSchema.md`의 번역 fallback 설명 불일치다.
일부 문장은 invalid/partial override가 항상 English로 보완된다고 설명하지만,
`RequirementTranslations.LoadSelectedLanguage`는 선택 언어의 내장값부터 사용한다.
해당 파일은 기존 수정 중이며 이번 구조 개선과 분리해 문서 정정 후보로 남겼다.
이번에 반드시 함께 고쳐야 하는 코드 결함이나 권한/동시 접근 정책 변경 근거는 확인하지 못했다.

기존 변경은 보존했다. 건드리지 않은 초기 파일 20개는 바이트 해시가 동일하고,
공유한 3개 파일(csproj·verifier Program·verification README)은 이번 추가 부분을 제외한 초기 내용을 대조했다.
그 세 파일은 이번 hunk만 stage했다. 작업 종료 시 기존 tracked 수정 19개와 untracked 4개가 남는 것이 의도된 상태다.
