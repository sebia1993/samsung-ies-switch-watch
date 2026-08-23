# Samsung iES Switch Watch 프로젝트 상태

## 현재 릴리스

- 버전: `0.12.0-poc`
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
- 인증 필수 API v5, 최소 health 예외, v4 426
- 제한 시간·출력·동시 실행·요청 빈도 상한
- transactional Agent/Viewer Setup과 rollback
- locked dependencies, 취약점 검사, SBOM, SHA와 immutable release

## 증거 수준

코드·합성 fixture·Windows CI·package smoke 증거가 있습니다. 실제 Samsung 스위치, 사내 관리망, GPO/EDR와 장시간 운영 증거는 이 저장소에서 주장하지 않습니다.

## 알려진 한계

- Agent→Switch Telnet 평문
- Windows x64 전용
- 세 모델만 등록
- Viewer 종료 시 주기 감시 중단
- POC 패키지 코드서명 미적용 가능
- 실제 펌웨어·EDR·GPO·방화벽 검증 필요

## 다음 검증 우선순위

1. 허가된 장비에서 모델·펌웨어별 prompt와 출력
2. 실제 Windows Service update/rollback과 재페어링
3. EDR/GPO/방화벽 환경
4. 장시간 다수 장비 감시
5. 가능 장비의 SSH 등 암호화 관리 채널 전환성 검토
