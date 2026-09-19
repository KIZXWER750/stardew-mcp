# 1.11.3 실제 함수 계약

농사·자원 함수 11개, 상점 경로·구매 도구 5개, 영업 확인·판매·상자 도구 7개를 제공합니다. 기존 이동·상호작용 도구도 함께 유지합니다. 1.6.0 물 보충은 사용자 실증 완료, 1.7~1.8 구매·출입구·판매·상자는 게임 실증 전입니다.

| 함수 | 입력 | 역할 |
|---|---|---|
| find_plot_candidates | location, anchor_x/y, direction, width/height, search_radius, max_candidates | 보호 대상 없는 경작 후보 검색 |
| inspect_area | 공통 영역 | 각 칸 지형·작물·접근 정보 확인, 행동 없음 |
| clear_area | 공통 영역 | 지원 장애물만 정리, 기존 경작지·작물 보존 |
| till_plot | 공통 영역 | 빈 땅만 경작, 장애물은 제거하지 않음 |
| prepare_plot | 공통 영역 | 정리 단계 후 경작, 한 작업으로 실행 |
| plant_plot | 공통 영역 + seed_item_id, existing_crop_policy | 빈 경작지에 인벤토리 씨앗 심기 |
| water_plot | 공통 영역 + target_filter | 마른 경작지 물주기 |
| harvest_plot | 공통 영역 | 시작 시 성숙한 작물만 수확 |

## 공통 영역 입력

```json
{"location":"Farm","x":10,"y":20,"width":2,"height":2,"minimum_energy":20,"stop_time":2200}
```

좌표는 형식 예시입니다. 실제 좌표는 AI가 관측으로 선택합니다. x/y는 북서쪽, width는 동쪽, height는 남쪽으로 증가합니다. 면적 1~64칸. minimum_energy는 최소 20, stop_time은 유효한 HHMM 0600~2200입니다.

선택 `request_id`는 동일 입력 재전송을 구분하는 키입니다. 생략하면 Go가 생성합니다. 같은 세이브의 현재 로드 세션 안에서만 유효합니다. 동일 키로 다른 요청은 REQUEST_ID_CONFLICT입니다. 통신 실패 후 같은 일을 새 키로 무조건 재전송하면 안 됩니다.

## 파종·물주기 입력

- seed_item_id: 관측된 인벤토리 씨앗 ID. 예를 들어 `(O)472` 형식 또는 자격 접두어 없는 ID.
- existing_crop_policy: 기본 `PRESERVE_AND_REPORT`는 기존 작물 칸을 제외하고 보고. `REQUIRE_SAME_CROP`은 동일 씨앗 ID만 완료로 인정하며 다른 작물이 있으면 중단.
- target_filter: 물주기 기본 `ALL_HOED_SOIL`은 영역의 모든 칸이 경작지여야 함. `CROPS_ONLY`는 시작 시 작물 있는 칸만 포함.

## 결과

공통 작업 결과: taskId, requestId, operation, location, x/y/width/height, status, reason, phase, totalTargets, completedTargets, alreadySatisfiedTargets, remainingTargets, toolUses, placementUses, interactionUses, seedsConsumed, excludedTiles, harvestEvidence, issues, tiles, checkpointError.

- 완료 수는 초기 충족분 포함. remainingTargets = totalTargets - completedTargets.
- excludedTiles는 파종 기존 작물·수확 미성숙 작물·물주기 필터 제외 칸이며 총 대상 수에 포함되지 않음.
- 대상 0개일 수 있음. `COMPLETED 0/0`은 요청한 면적 전체에 새 작물을 심었다는 뜻이 아님.
- 정리 완료는 요청 가능한 장애물이 없는 상태이며, 기존 경작지/작물은 보존 조건상 충족으로 계산.
- prepare/till 완료는 모든 대상이 HoeDirt. 물주기 완료는 모든 대상이 젖은 HoeDirt.
- 수확 완료는 실행 직전 성숙한 작물의 수확 후 상태 변화 확인. 드롭 수집 완료와 다름.
- issues는 작업 중 보류 이력도 포함. 현재 미완료 판단은 최종 tiles와 remainingTargets를 함께 확인.
- PAUSED/BLOCKED/FAILED/CANCELLED는 전체 완료가 아님. 함수는 같은 조건으로 무한 재시도하지 않음.

inspect_area는 작업 ID 없이 location, observedAt, tiles, inventory, energy, time을 반환합니다. inventory에는 slot, itemId, qualifiedItemId, name, stack, isSeed가 있어 AI가 실제 씨앗 ID를 선택할 수 있습니다. tiles에 cropSeedId, readyForHarvest, dead, harvestMethod와 네 방향 접근 정보를 추가했습니다.

후보 탐색은 기존 기본값 search_radius=8(1~12), max_candidates=8(1~20)을 사용합니다. direction은 ANY/NORTH/SOUTH/EAST/WEST이며 영역 전체 위치를 기준으로 판단합니다. 경작 후보용이므로 작물이 있는 영역은 제외합니다. 기존 작물 물주기·수확에는 inspect_area를 사용하세요.


## 1.5.1 목표별 실행·복구

함수 종류·영역·씨앗·필터·기존 작물 정책이 같은 완료 작업은 한 사용자 목표에서 재전송하지 않습니다. 다른 지역의 별도 작업은 가능하되 전체 접수 횟수는 24회로 제한됩니다.

BLOCKED 결과에 recovery.candidate, requiresUserScope, nextSteps가 추가됩니다. candidate는 복구 검토 가능 표시이며 사용자 허용을 대체하지 않습니다. NOT_HOED, CLEARING_REQUIRED, 접근 문제를 구분합니다. 보호 대상 충돌·불명확한 도구 결과·시간초과는 자동 재시도하지 않습니다.

복구 호출은 별도 작업 ID를 사용합니다. 실패한 동작을 다시 실행하려면 그 사이 다른 선행 작업의 COMPLETED가 필요합니다. NO_WATER 일시 중단은 refill_watering_can의 검증된 완료가 있어야 재개됩니다. 동일 작업은 최초 포함 3회까지만 가능합니다. 성공한 동일 함수의 단순 재조회는 복구 진행으로 계산하지 않습니다.

일반 목표는 한 AI 응답 동안 여러 함수를 순차 호출하고 최종 답변 후 종료합니다. 명시적인 기존 HOME_AND_SLEEP/WATER_AND_SLEEP 검증 모드는 기존 별도 흐름을 유지합니다. 마지막 줄 GOAL COMPLETE는 완료 신호이며, 완료 여부를 증명하는 근거는 각 함수의 실제 결과입니다.


## 1.6.0 추가 함수

| 함수 | 입력 | 출력/용도 |
|---|---|---|
| analyze_farm_work | 공통 영역 + operation(clear/till/prepare/plant/water/harvest), 해당 씨앗·필터 | 칸별 READY/ALREADY_SATISFIED/EXCLUDED/선행 조건, 필요 도구와 누락 도구, 실행 가능한 칸의 씨앗 수, 마른 칸 수와 물, 다음 행동의 에너지 여유·시각 |
| find_water_sources | search_radius 1~64(기본32), max_sources 1~20(기본8) | 현재 플레이어 주변 Farm 물 공급원 후보 x/y, 접근 타일·경로 길이, 물뿌리개 잔량, 후보 판정 근거. 이동하지 않음 |
| refill_watering_can | location=Farm, 탐색 결과의 x/y, minimum_energy/stop_time | 한 물가로 접근·물뿌리개 1회 사용·잔량 증가 확인. 공통 TaskResult에 waterBefore/waterAfter 추가 |

analyze_farm_work는 실행 예약이나 완전한 성공 보증이 아닙니다. 계절/씨앗 배치 규칙, 총 에너지 비용, 인벤토리 수용량과 모든 도구 등급 호환성을 완전히 예측하지 않습니다. 실제 실행이 다시 검사하거나 실패를 보고합니다. 분석 결과의 limitations 필드를 반드시 고려합니다.

물 공급원 판정은 게임의 두 정수 타일 인수를 받는 CanRefillWateringCanOnTile 공개 메서드가 있으면 사용합니다. 없으면 자연 물 타일을 후보로만 반환합니다. 우물 등 특수 공급원은 해당 게임 API가 판정하는 경우에만 포함될 수 있습니다. 후보의 존재만으로 보충 성공을 보장하지 않습니다.

보충 함수는 물뿌리개의 정상 DoFunction을 사용하고 WaterLeft를 직접 대입하지 않습니다. 최대 용량을 읽을 수 있고 이미 가득 찼다면 도구를 사용하지 않고 완료합니다. 용량을 읽을 수 없고 잔량 증가도 없으면 확인 불가로 중단합니다. 애니메이션은 기존과 같이 보장하지 않습니다.

## 물 부족 복구 흐름

water_plot PAUSED(NO_WATER) → AI가 원래 영역/필터 기억 → find_water_sources → refill_watering_can COMPLETED → 같은 영역/필터 water_plot.

이는 AI가 도구 결과를 보고 조합하는 흐름이며 water_plot 내부가 임의로 다른 지역까지 이동하는 자동화는 아닙니다. 물주기 재호출은 새 작업 ID로 현재 토양을 읽고 이미 젖은 칸을 건너뜁니다. 에너지·시각·연결 문제에 의한 일시 중단은 물 보충만으로 재개할 수 없습니다.

물을 사용하기 전 동일 공급원 보충을 반복하면 이전 결과를 반환합니다. 물주기 작업에서 실제 도구 사용이 보고된 뒤에는 같은 공급원 재보충을 허용합니다. 한 목표의 전체 작업 24회, 동일 물주기 최초 포함 3회 제한은 유지합니다. 타 지역 이동, 씨앗 구매, 음식·수면 회복은 추가하지 않았습니다.


## 1.7.0 씨앗 구매 도구

| 함수 | 입력 | 출력·역할 |
|---|---|---|
| `find_shop_route` | `destination` (SeedShop 또는 Farm 등 관측된 지역명) | `status`, `location`, `destination`, `route[]`, `counters[]`, `accessVerified=false`. 로드된 맵의 warp/door/Action에서 경로를 읽음. 이동 없음. |
| `use_route_exit` | 경로에서 받은 `exit_id` | 실제 접근 칸에서 정상 방향키/상호작용으로 출입구 통과. 최대 5초 후 실제 지역·좌표와 완료/실패 반환. |
| `inspect_shop` | 없음 | 열린 상점의 `observationId`, `money`, `currency`, `seeds[]` (itemId/name/price/stock/tradeItem), 커서 보유 물품. 구매 없음. |
| `buy_shop_item` | `observation_id`, `item_id`, `quantity`, `max_total_cost`, `reserve_money` | 정상 구매 메뉴 클릭 → 인벤토리 배치 → 돈·씨앗 증감 검증. 완료 영수증 또는 부분 진행·중단 이유. |
| `close_shop` | 없음 | 커서 물품이 없고 메뉴가 닫혀도 되는 상태일 때 정상 종료. |

### 경로와 상점 열기

`find_shop_route`의 `route[]`는 from/to/x/y/action/approaches/exitId를 담습니다. AI가 접근 후보에 기존 `move_to`로 이동한 뒤 `use_route_exit`를 호출합니다. 지형 충돌은 기존 게임 이동 처리에 맡기며, 통과 전후 지역을 확인합니다. 벽·문을 강제로 통과하거나 순간이동하지 않습니다. 경로를 다시 조회하면 이전 출입구 토큰은 폐기됩니다.

`counters[]`는 현재 맵 Buildings 레이어의 Shop/SeedShop 액션 칸입니다. AI가 인접 칸에 접근하고 counter를 향해 기존 `interact`를 호출한 다음 `inspect_shop`으로 실제 메뉴 열림을 확인합니다. 문·카운터 좌표를 사용자가 입력하지 않습니다. **지도 연결은 영업 중·통행 가능 보장이 아닙니다.** 로드되지 않은 지역, 지원하지 않는 맵 액션, 대화창·휴점 등은 차단 사유를 보고합니다.

### 구매 제한과 검증

- 금화 상점의 씨앗 카테고리만 지원합니다. 비료·묘목·도구·물물교환은 구매 범위 밖입니다. 판매는 아래 별도 함수로 제공합니다.
- 수량은 1~99개이고 한 스택 이하여야 합니다. 인벤토리 빈칸 하나가 필요합니다.
- 총액은 max_total_cost 이하, 구매 후 잔액은 reserve_money 이상이어야 합니다. AI는 예산이 없으면 가격을 먼저 확인하고 사용자에게 예산을 물어봅니다.
- 구매 전 Shift/Ctrl을 놓아야 합니다. 배수 구매가 일어나지 않도록 확인합니다.
- 직접 돈을 빼거나 아이템을 생성하지 않습니다. 실제 ShopMenu 상품 행과 인벤토리 칸의 클릭 처리기를 사용합니다. 한 개마다 돈 감소·물품 증가가 맞는지 확인합니다.
- 동일 사용자 목표에서 같은 item/quantity/budget/reserve 구매는 같은 요청 ID를 사용합니다. 관측을 갱신해도 이미 처리된 영수증을 반환하며 재결제하지 않습니다. 새 사용자 목표에서는 별도 구매가 가능합니다.
- 영수증 중복 방지는 현재 게임 로드 세션 범위입니다. 게임을 재시작한 후 불확실한 구매를 자동 재시도하지 마세요.
- 완료 결과: status=COMPLETED, requestId, itemId, quantity, received, spent, moneyAfter.
- 실행 중 실패: status=BLOCKED, reason, confirmedClicks, received(인벤토리 증가분), held(커서의 해당 씨앗 수), spent, requiresInspection=true. **부분 구매는 되돌리지 않고 추가 구매도 하지 않습니다.** 커서 물품이 남으면 사용자가 인벤토리에 넣고 메뉴를 닫아야 할 수 있습니다.
- 게임/상점 모드마다 메뉴 내부 형식이 다르면 구매 전에 UNSUPPORTED 계열 오류로 중단합니다. 메뉴 호환성은 실제 게임 검증 대상입니다.

### 조합

기존 밭 관측 → 필요한 씨앗 수 결정 → 사용자 예산 확인 → 상점 경로 → 이동/출입 → 카운터 상호작용 → 상품 관측 → 구매 영수증 확인 → 상점 닫기 → 요청 시 Farm 복귀 → 같은 밭에 plant_plot → water_plot → 목표 종료.

구매 기능에 자동 귀가·수면·회복·씨앗 추천의 게임 규칙 전체를 포함한 것은 아닙니다. 작업 범위와 기존 보호 정책은 그대로 적용합니다.


## 1.8.0 영업·수확물 판매·상자

| 함수 | 입력 | 결과 및 용도 |
|---|---|---|
| `get_shop_status` | 없음 | 현재 날짜·시간·수요일 여부, 회관 완료/마을 열쇠, 축제 달력, 실제 상점 메뉴, 거래 시도 가능 여부 |
| `inspect_sellable_crops` | 없음 | 인벤토리의 채소·과일·꽃 수확물 스택: quoteId, slot, itemId, quantity, quality, estimatedUnitPrice |
| `sell_crop_stack` | quote_id, keep_quantity, minimum_total_price | 관측된 스택 전체 판매. 인벤토리 감소·금액 증가 검증 후 removed/earned/moneyAfter 반환 |
| `inspect_storage` | 없음 | 현재 지역 12칸 이내 일반 상자 위치. 실제 열려 있는 상자에서는 수확물 스택 quote 반환 |
| `open_storage` | chest_id | AI가 관측한 상자 옆으로 이동한 뒤 상호작용 1회. INPUT_SENT 후 inspect_storage로 열림 확인 필요 |
| `take_storage_crop` | quote_id, keep_in_chest | 실제 열린 상자에서 수확물 한 스택 전체를 인벤토리로 이동. 상자 감소·인벤토리 증가 검증 |
| `close_storage` | 없음 | 커서 보유 아이템이 없는 상자 메뉴 정상 종료 |

### 영업 조건

기본 거래 시간은 **09:00 이상 17:00 미만**, 건물은 21:00까지입니다. 수요일은 회관 복구 또는 마을 열쇠 예외를 확인합니다. 게임에서 로드한 축제 달력으로 휴업일을 확인하며 밤 행사인 여름 28일은 낮 거래를 막지 않습니다. 축제 달력을 읽을 수 없으면 `CLOSED_OR_UNKNOWN`으로 보고합니다.

`SCHEDULE_OPEN`은 영업 시간상 방문 후보라는 뜻이며 피에르가 실제 카운터에 있다는 보장은 아닙니다. 실제 상점 메뉴가 열렸다면 `SHOP_MENU_OPEN`을 최종 거래 근거로 사용합니다. 커스텀 상점 시간 모드는 사전 시간 판정과 다를 수 있습니다. 강제 메뉴 생성·시간 변경은 하지 않습니다.

`find_shop_route`도 영업 상태를 반환하고, 휴무/시간 밖에는 SeedShop 입장용 `use_route_exit`를 차단합니다. 단순한 건물 방문 목적까지 포괄하는 일반 이동 도구는 아닙니다. AI는 출발 전에 get_shop_status를 호출하고 휴점이면 구매·판매를 반복 시도하지 않습니다. 자동 수면·다음 날 대기는 이번 범위가 아닙니다.

근거: [Stardew Valley Wiki — Pierre's General Store](https://stardewvalleywiki.com/Pierre%27s_General_Store), 2026-09-18 확인.

### 판매 보호와 검증

판매는 사용자에게 허용된 경우에만 실행합니다. 이번 범위는 채소(-75)·과일(-79)·꽃(-80) 카테고리의 일반 수확물입니다. 씨앗·도구·자원·가공품·퀘스트/특수 아이템은 제외합니다. 음식용·선물용 보존 같은 사용자 의미는 AI가 대상에서 제외해야 합니다. 상점이 실제로 매입 가능한지 메뉴의 highlightMethod로 확인합니다.

keep_quantity는 **해당 아이템 ID의 인벤토리 전체 보존 수량**이며 품질을 합산합니다. keep_in_chest는 **해당 상자 안의 동일 아이템 ID 보존 수량**입니다. 이번 버전은 한 스택 전체만 판매/이동합니다. 스택 일부만 처리해야 보존 수량을 만족하는 경우 그 스택을 건너뛰고 알립니다. 예: 한 스택 10개 중 3개 보존 요청이면 7개를 임의로 분할해 팔지 않습니다.

판매는 실제 ShopMenu 인벤토리 클릭을 사용합니다. legacy sell_item의 돈 직접 가산 경로는 비활성화했습니다. 예상 가격·상점 배율로 최소 가격을 사전 검사하고 실제 earned와 removed를 확인합니다. 예기치 못한 결과는 BLOCKED로 반환하며 자동 재시도·환불·아이템 생성은 하지 않습니다. 판매 후 커서에 남은 물품은 사용자 확인이 필요할 수 있습니다.

### 상자 범위

기본 일반 플레이어 상자만 지원합니다. 냉장고·출하함·주니모/특수 상자는 제외하며 이번 구현에서는 특수 유형인 큰 상자도 제외될 수 있습니다. 개방은 정상 상호작용이므로 게임의 접근/잠금 처리를 따릅니다. 멀리 있는 상자를 원격으로 읽거나 아이템을 직접 대입하지 않습니다. 위치는 AI가 선택합니다.

상자 이동에는 인벤토리 빈칸 하나가 필요합니다. 꺼낸 수확물은 판매하기 전에 inspect_sellable_crops로 다시 관측합니다. 상자에서 꺼내기만 성공했다고 판매 완료로 보고하지 않습니다.

동일 사용자 목표 안에서 동일 스택·보존 조건의 완료 영수증은 재사용합니다. 새 관측으로 무작정 재판매하지 않습니다. 새 사용자 목표나 게임 재시작에 걸친 영속적 거래 보장은 아니므로 불확실한 결과를 재시도하지 않습니다.

## 1.10.0 생활·회복·귀가·수면

| 함수 | 입력 | 결과 및 용도 |
|---|---|---|
| `assess_daily_status` | 없음 | 현재 시각·날짜·위치·에너지·체력·02:00까지 남은 시간과 보수적 권고 |
| `find_food_options` | 없음 | 양수 회복 음식의 슬롯·정확한 ID·스택·예상 에너지/체력·판매가. 읽기 전용 |
| `find_recovery_options` | 없음 | 현재 음식, 귀가 후 수면 가능성, 목욕탕 자동화 지원 여부. 읽기 전용 |
| `consume_food` | `slot`, `item_id`, `reserve_quantity` | 정확히 한 개를 일반 입력으로 섭취하고 수량 감소·에너지/체력 전후를 검증 |
| `find_home_route` | 없음 | 현재 위치에서 FarmHouse까지 로드된 맵의 출구 연결. 읽기 전용 |
| `return_home` | 없음 | 반환된 접근점과 출구만 정상 이동으로 통과하고 FarmHouse 도착 검증 |
| `schedule_bedtime` | 선택: `target_bed_time`, `buffer_minutes` | 경로 홉 기반 보수적 출발 시각. 행동 없음 |
| `sleep_until_morning` | 없음 | FarmHouse 실제 침대 좌우 접근, 수면 질문 Yes, 다음 날 아침 검증 |

`consume_food`는 음식의 게임상 양수 회복 여부만 판정합니다. 퀘스트·선물·판매·번들·사용자 보관 목적은 AI가 사용자 지시와 관측에 따라 제외해야 하며, 보존 수량이 부족하면 실행기가 차단합니다. 이미 에너지와 체력이 모두 가득 찬 경우도 소비하지 않습니다.

`return_home`은 순간이동하지 않으며 각 지역 전환 직전에 경로를 다시 읽습니다. 접근점이나 목적지 전환을 확인하지 못하면 좌표를 추측하지 않고 중단합니다. `schedule_bedtime`의 시간은 경로 홉당 보수적 추정치이므로 장애물·대화·축제 등 실제 지연을 보장하지 않습니다.

표준 농가 현관은 건물 사각형에 포함되지만 실제로 통행할 수 있으므로 `x=59..66, y=15`와 `x=63..65, y=16`을 경로 계획의 통행 가능 예외로 사용합니다. 단, 해당 칸에 놓인 객체·지형·자원 장애물은 계속 차단됩니다. 다른 지역에서는 먼저 Farm까지 관측된 맵 경로를 사용하고, Farm에서는 `(64,15)`에 도착한 뒤 북쪽 정문 `(64,14)`을 정상 상호작용해 실제 `FarmHouse` 전환을 확인합니다.

외부에서 현관 영역으로 들어갈 수 있는 경로는 `(63,17)→(63,16)`, `(64,17)→(64,16)`, `(65,17)→(65,16)`의 북쪽 이동 세 개뿐입니다. 반대 방향은 정상적인 현관 이탈에만 사용하며, 다른 동·서·북 경계에서는 건물 벽을 통과하는 경로를 생성하지 않습니다.

목욕탕은 경로, 성별 탈의실, 수영 상태 및 회복 완료 조건의 인게임 검증이 남아 있어 실행 함수에서 제외했습니다. `find_recovery_options`는 지원되지 않는다는 사실을 명시하여 AI가 임의 조작하지 않도록 합니다.

## 1.11.0 생활 관리자

| 함수 | 주요 입력 | 역할 |
|---|---|---|
| `manage_daily_life` | `minimum_energy_percent`, `return_home_time`, `allow_food`, `allow_return_home`, `allow_sleep`, `maximum_food_sell_price`, `reserve_quantity`, `maximum_food_items`, `protected_item_ids` | 한 번의 생활 체크포인트에서 계속 작업·음식 회복·귀가·수면 중 허용된 행동을 선택하고 실제 결과를 검증 |

기본값은 에너지 20%, 귀가 시각 22:00, 음식 단가 상한 50골드, 동일 음식 예비 1개, 한 호출 최대 2개 섭취입니다. `allow_food`, `allow_return_home`, `allow_sleep`은 사용자 목표가 허용할 때만 true로 전달합니다. 시간이 귀가 시각 이상이면 에너지보다 귀가를 우선합니다. 귀가만 허용되면 FarmHouse 도착에서 끝나고, 수면까지 허용되면 다음 날 아침을 확인합니다.

자동 음식 선택은 양수 에너지 회복, 판매가 상한, 예비 수량, 보호 ID를 모두 만족해야 합니다. 후보 중 판매가가 낮은 것을 우선합니다. 안전한 후보가 없거나 정책 한도만큼 먹어도 에너지가 낮으면 `PAUSED`이며 작업을 재개하지 않습니다. 이 함수 자체는 계속 실행되는 감시기가 아니므로 AI가 장기 단계 전후에 호출합니다.

## 1.11.2 일반 야생 나무·밑동 제거와 드롭 회수

| 함수 | 주요 입력 | 역할 |
|---|---|---|
| `remove_wild_trees` | 공통 Farm 영역, `max_trees`(기본 3, 1~12), `preserve_young_trees`(기본 false), 에너지·시간 제한 | 일반 야생 나무를 밑동까지 제거하고 실제 loose debris를 탐지·접근·회수한 뒤 나무와 드롭이 모두 사라졌는지 검증 |

대상은 일반 `Tree` TerrainFeature만이며 기본적으로 씨앗·묘목·성장 중 나무·성목·밑동을 모두 포함합니다. 사용자가 어린 일반 나무 보존을 명시한 경우에만 `preserve_young_trees=true`를 전달합니다. `FruitTree`, 작물, 건물, 상자·기계·배치 시설은 제외합니다. 덤불과 큰 밑동·통나무 같은 resource clump도 일반 `Tree`가 아니므로 이 함수가 제거하지 않습니다. 기존 `clear_area`와 `prepare_plot`은 여전히 나무를 보호합니다. 나무 제거는 사용자 요청에 나무 제거가 포함된 경우에만 호출합니다.

`max_trees`보다 후보가 많으면 **성목 → 기존 밑동 → 어린나무** 순서로 선택하고, 같은 분류에서만 가까운 대상을 우선합니다. 따라서 가까운 어린나무 때문에 성목이 수량 제한 밖으로 밀리지 않습니다.

각 대상은 동서남북 한 칸에서만 작업합니다. 기본 도끼 기준 성목 단계는 최대 10회의 검증된 타격, 밑동 단계는 최대 5회, 어린나무 단계는 최대 4회로 제한합니다. 도끼 업그레이드나 어린 성장 단계 때문에 지형 변화가 더 일찍 확인되면 남은 예상 타격을 보내지 않습니다. 성목이 밑동으로 바뀔 때 타격 카운터를 새로 시작하고 낙하 상태가 정리되도록 기다린 뒤 밑동 작업을 이어갑니다.

나무가 모두 사라지면 최대 8타일 주변의 `location.debris` 중 실제 아이템 드롭을 검사합니다. 각 드롭과 같은 칸 또는 인접한 칸까지 이동해 자연스러운 자석·접촉 수집을 시도하고, 드롭 소멸과 인벤토리 증가를 함께 확인합니다. 여러 조각으로 움직이는 드롭은 일부 조각을 주운 뒤에도 남은 조각을 계속 추적합니다. 경로가 없다면 최대 24개의 지원되는 접근 장애물(일반 야생 나무, 잔디, 잡초, 작은 돌, 잔가지)을 정상 도구로 제거할 수 있습니다. 이 과정에서도 과일나무·작물·경작지·건물·상자·기계·가구·배치 시설은 보호합니다. 완료 결과에는 드롭 개체 수 `dropsDetected`/`dropsCollected`, 실제 획득 수량 `dropItemsCollected`, `obstaclesClearedForDrops`, `dropEvidence`가 포함됩니다.
