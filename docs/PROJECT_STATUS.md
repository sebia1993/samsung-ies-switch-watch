# Samsung iES Switch Watch 프로젝트 상태

## 현재 릴리스

- 버전: `0.13.0-poc`
- 플랫폼: Windows x64
- 기술: C# / .NET / WPF / ASP.NET Core
- 목적: Samsung iES 스위치 읽기 전용 원격 점검·변화 감시 POC

## 구현 완료

- Viewer/Agent 책임 분리와 stateless Telnet 실행
- IES4224GP, IES4028XP, IES4226XP fixture 기반 모델 식별
- 한 줄 `show` 명령 정책과 이중 검증
- 영구 Agent TLS identity와 SPKI SHA-256 pin
- DPAPI LocalMachine 32-byte Agent bearer token
- `SSW1` 수동 페어링과 Viewer DPAPI CurrentUser token
- health 포함 전 경로 인증 필수 API v5, v4 426
- 제한 시간·출력·동시 실행·요청 빈도 상한
- `PeriodicTimer` 기반 감시 scheduler, 최대 256개 bounded queue와 worker 2개
- 장비별 중복 수집 억제, revision/client generation stale 결과 차단, idempotent 종료
- 장비별 Closed/Open/HalfOpen circuit breaker와 3회 실패·30초 half-open 정책
- Agent 연결, 수동 조회, 장비 revision, event feed 책임의 ViewModel 외부 분리
- `AllowedTargetCidrs` runtime authorization, canonical RFC1918 CIDR 검증과 Setup의 기존 범위 복원·최대 32개 입력
- `running-config`·`startup-config` 민감 조회 기본 차단과 명시적 opt-in
- deterministic Telnet fault transport와 연결·timeout·대용량 출력 회귀 테스트
- 비밀·IP tag 없는 Viewer/Agent 로컬 metrics와 seeded stability harness
- transactional Agent/Viewer Setup과 rollback
- locked dependencies, 취약점 검사, SBOM, SHA와 immutable release

## 증거 수준

코드·합성 fixture·deterministic fault transport·Windows CI·5분 stability smoke·package smoke 증거가 있습니다. 실제 Samsung 스위치, 사내 관리망, GPO/EDR와 8시간·24시간 장시간 운영 증거는 이 저장소에서 주장하지 않습니다.

## 알려진 한계

- Agent→Switch Telnet 평문
- Windows x64 전용
- 세 모델만 등록
- Viewer 종료 시 주기 감시 중단
- POC 패키지 코드서명 미적용 가능
- 실제 세 모델의 펌웨어별 prompt, enable, paging, 긴 출력과 단절 동작 미검증
- 실제 EDR·GPO·방화벽·Windows Service recovery 미검증
- 8시간 다수 장비와 24시간 장기 감시는 harness profile만 제공하며 실행 증거 미확보

## 다음 검증 우선순위

1. IES4224GP·IES4028XP·IES4226XP의 펌웨어별 prompt
2. 실제 enable mode, paging, 긴 출력과 Telnet disconnect
3. Windows Service recovery, update/rollback과 재페어링
4. EDR/GPO/방화벽 환경
5. 8시간 이상 다수 장비 감시와 24시간 장기 감시
6. 가능 장비의 SSH 등 암호화 관리 채널 전환성 검토
