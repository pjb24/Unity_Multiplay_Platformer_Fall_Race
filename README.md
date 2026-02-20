# Unity_Multiplay_Platformer_Fall_Race

멀티플레이 기반의 3D 플랫폼 레이스 게임입니다. 플레이어는 장애물을 넘고 체크포인트를 갱신하며 각 스테이지의 Goal에 도달해 기록을 남기고, 최종적으로 가장 빠른 누적 기록을 목표로 경쟁합니다.

## 게임 목표
- 총 3개 스테이지를 최대한 빠르게 완주하여 가장 낮은 **Total 기록(합산 시간)** 을 달성합니다.
- 각 스테이지 기록과 총합 기록을 기준으로 순위가 정해집니다.

## 게임 방법
1. 로비에서 세션 준비 후 게임이 시작됩니다.
2. 카운트다운 이후 Running 상태가 되면 레이스가 진행됩니다.
3. 스테이지 내 장애물을 피하고 이동/점프로 전진합니다.
4. 체크포인트를 통과하면 이후 낙하 시 해당 지점(또는 스테이지 시작 지점)으로 리스폰됩니다.
5. Goal 지점에 도달하면 해당 스테이지 기록이 확정됩니다.
6. 모든 스테이지 종료 후 결과 화면에서 최종 순위를 확인합니다.

## 게임 규칙
- 스테이지 수: **3개 스테이지**
- Goal Window: 스테이지 첫 도착자 발생 후 **60초** 동안 추가 완주를 인정합니다.
- Retire Penalty: 스테이지를 완주하지 못한 경우 **90초 페널티**가 적용될 수 있습니다.
- 낙하 처리: FallZone 진입 시 서버 권위로 리스폰 처리됩니다.
- 기록 처리: 미완주 스테이지는 기록 미입력 상태로 관리되며, 결과 정렬 시 완주 수/합산 기록 기준으로 비교됩니다.

## 조작 방법
### 키보드/마우스
- 이동: `WASD` 또는 `↑↓←→`
- 점프: `Space`

### 게임패드
- 이동: `Left Stick`
- 점프: `South Button (A/Cross)`

> 스테이지 시작 구간 월드 가이드에는 핵심 조작으로 `MOVE: WASD / ARROW KEYS`, `JUMP: SPACEBAR`가 안내됩니다.

## ThirdParty 의존성 안내 (빌드 필수)
이 프로젝트는 ThirdParty 에셋(오디오/캐릭터/VFX)을 사용합니다.

- **빌드를 위해 아래 ThirdParty 에셋이 반드시 프로젝트에 포함되어 있어야 합니다.**
- ThirdParty 누락 시 오디오, 캐릭터 프리팹, 이펙트 참조가 깨져 빌드/런타임 품질에 문제가 발생할 수 있습니다.

## 사용한 ThirdParty 목록

### 1) Footsteps Pack
- Asset Store: https://assetstore.unity.com/packages/audio/sound-fx/foley/footsteps-pack-330509
- 사용 리소스:
  - `EarthGround17Jump`
  - `EarthGround17Running_loop`

### 2) Free Casual Game SFX Pack
- Asset Store: https://assetstore.unity.com/packages/p/free-casual-game-sfx-pack-54116
- 사용 리소스:
  - `DM-CGS-12`
  - `DM-CGS-43`
  - `DM-CGS-48`
  - `DM-CGS-49`

### 3) Free Party Game Characters
- Asset Store: https://assetstore.unity.com/packages/p/free-party-game-characters-342650
- 사용 리소스:
  - 캐릭터 프리팹 6종

### 4) Bold VFX Pack Demo
- Asset Store: https://assetstore.unity.com/packages/vfx/particles/bold-vfx-pack-demo-346746
- 사용 리소스:
  - `FX_Stars_Yellow`