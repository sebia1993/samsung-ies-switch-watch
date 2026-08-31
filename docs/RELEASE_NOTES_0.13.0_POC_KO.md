# Samsung iES Switch Watch 0.13.0-poc

이번 버전은 새 운영 기능 확대보다 Viewer 장시간 실행, 동시성 경계, 종료와 synthetic 장애 검증을 강화한 안정성 리팩터링입니다. API v5, bearer 인증, SPKI SHA-256 pin, DPAPI 범위, stateless Agent와 기존 Telnet 재시도 정책은 유지합니다.

## 주요 변경

- Dashboard의 감시 scheduler를 `MonitoringCoordinator`로 분리했습니다.
- `PeriodicTimer`, 최대 256개 bounded channel과 worker 2개로 자동 수집을 실행합니다.
- 같은 장비의 queued/running 수집을 중복 등록하지 않고, 가득 찬 queue에서는 오래된 작업을 명시적으로 drop하여 무제한 증가를 막습니다.
- 장비 operation gate는 참조-counted lease로 관리해 대기자와 실행자가 없으면 `SemaphoreSlim`을 제거합니다.
- Agent 연결 generation, 수동 조회, 장비 revision·credential block, event buffer 책임을 별도 coordinator/service로 분리했습니다.
- 장비별 연결 계층 실패가 3회 연속 발생하면 30초 동안 circuit을 열고, 이후 HalfOpen probe 한 번으로 복구 여부를 확인합니다.
- shutdown 시 새 작업 차단, lifetime 취소, monitoring worker 종료 대기, client/service 정리 순서를 명시하고 반복 dispose를 안전하게 처리합니다.

## 보안 경계

- Agent가 `AllowedTargetCidrs`를 실제 target authorization에 적용합니다.
- target은 IPv4, RFC1918, TCP/23, configured CIDR 포함 조건을 모두 만족해야 합니다.
- CIDR은 canonical network boundary, 최대 32개, 중복 제거 규칙을 적용하며 malformed·IPv6·공인망 범위를 거부합니다.
- 빈 CIDR 목록은 기존 설치 업그레이드 호환을 위해 RFC1918 기본 세 범위로 정규화합니다. Setup은 검증된 기존 범위를 다시 표시하고, 운영자가 최대 32개의 승인된 관리 VLAN CIDR로 더 좁힐 수 있게 합니다.
- `show running-config`와 `show startup-config`를 민감 조회로 분류하고 기본 차단합니다. Viewer 연결 설정에서 명시적으로 허용한 요청만 Viewer와 Agent 검증을 모두 통과할 수 있습니다.
- 기존 한 줄 `show` 검증, 제어문자·separator 차단, Latin-1 제한과 출력 상한은 유지합니다.

## 장애 검증·진단

- 외부 서비스 없이 동작하는 deterministic `FaultInjectingByteTransport`와 17개 fault scenario를 추가했습니다.
- 인증 실패, enable 실패와 명령 timeout은 재시도하지 않고, 명령 실행 중 세션 종료만 최대 한 번 재연결하는 기존 계약을 회귀 테스트합니다.
- 명령 A 완료 후 B에서 단절되면 B와 C만 다시 실행하고 A를 재실행하지 않는지 검증합니다.
- 최대 output/wire byte, hard/idle timeout, 잘못된 Telnet negotiation과 paging loop를 검증합니다.
- Viewer와 Agent에 `System.Diagnostics.Metrics` 기반 로컬 지표를 추가했습니다. 자격 증명, pairing code, token, 명령 원문·출력과 실제 장비 IP는 tag로 기록하지 않습니다.

## Stability harness

`tools/SamsungSwitchWatch.StabilityHarness`는 실제 스위치 없이 10·50·100·250 장비 workload를 재현합니다. normal/slow/failure 수집, seeded disconnect, Agent 재연결, 장비 수정·삭제, 설정 변경과 monitor stop/start를 포함합니다.

지원 profile:

- `smoke`: 5분, GitHub Windows CI용
- `quick`: 15분
- `standard`: 1시간
- `extended`: 8시간
- `manual`: 24시간

summary에는 seed, 수집·실패·재연결·drop 수, peak queue/workers/operation gates, 종료 시 남은 gate, GC, managed heap, working set/private memory, thread/handle와 미처리 예외를 포함합니다.

## 검증 범위

자동 검증은 합성 Telnet transport, 비식별 fixture, Windows GitHub Actions, package contract와 self-contained EXE smoke를 대상으로 합니다. 이를 실제 Samsung 장비나 사내 관리망 검증 완료로 표현하지 않습니다.

## 아직 실제 환경에서 확인할 항목

- IES4224GP 펌웨어별 prompt
- IES4028XP 펌웨어별 prompt
- IES4226XP 펌웨어별 prompt
- enable mode behavior
- paging behavior
- long command output
- Telnet disconnect behavior
- EDR, GPO와 Firewall 정책
- Windows Service recovery
- 8시간 이상 다수 장비 감시
- 24시간 장기 감시

Agent에서 스위치까지는 여전히 Telnet/TCP 23 평문입니다. 승인된 사설 관리망과 최소 CIDR 범위에서만 사용하십시오.
