# Samsung Switch Watch 0.11.7-poc 릴리스 노트

릴리스 날짜: 2026-08-12

`0.11.7-poc`는 Agent 설치 중 Windows 서비스 단계가 하나의 일반 오류로만 표시되던 문제를
개선한 설치 안정성 릴리스입니다. 서비스 실행에 필요한 핵심 실패와 서비스 설명·자동 복구
정책·DACL 같은 부가 설정 실패를 분리해, 실제 구동 가능 여부를 기준으로 설치 결과를
판단합니다. Agent와 Viewer의 업무 흐름 및 화면 배치는 변경하지 않습니다.

## 서비스 핵심 실패 세분화

Windows 서비스 단계의 실패를 다음과 같이 구분합니다.

- `SETUP_SERVICE_CAPTURE_FAILED`: 설치 전 서비스 상태 snapshot을 읽지 못했습니다.
- `SETUP_SERVICE_CONTRACT_FAILED`: 설치 전에 확인한 기존 서비스의 실행 경로·시작 유형·계정
  계약이 예상과 다릅니다.
- `SETUP_SERVICE_STOP_FAILED`: 기존 Agent 서비스를 제한 시간 안에 중지하지 못했습니다.
- `SETUP_SERVICE_CONFIG_FAILED`: 실행 경로·자동 시작 유형·가상 서비스 계정 같은 핵심 구성을
  적용하지 못했습니다.
- `SETUP_SERVICE_START_FAILED`: 새 Agent 서비스를 시작하지 못했습니다.

이 단계들은 Agent가 올바른 실행 파일과 계정으로 실제 구동되는 데 필요합니다. Setup은 해당
단계를 임의로 건너뛰거나 성공으로 바꾸지 않으며 fail-closed로 중단합니다. 실패 코드는 원시
서비스 계정, 실행 경로 또는 Windows 예외 원문을 노출하지 않고 익명 진단과 SWD1 지원 코드에
반영됩니다.

## 비필수 서비스 메타데이터 경고

핵심 서비스 구성과 Running 상태를 확인한 뒤 다음 부가 설정만 실패하면 설치를 되돌리지
않습니다.

- `SETUP_SERVICE_DESCRIPTION_WARNING`: 서비스 설명 설정 실패
- `SETUP_SERVICE_RECOVERY_POLICY_WARNING`: 자동 복구 정책 설정 실패
- `SETUP_SERVICE_DACL_WARNING`: 제한 서비스 DACL 적용 실패

경고는 무시되거나 정상으로 숨겨지지 않고 설치 결과와 익명 진단에 남습니다. 운영자는 Viewer
연결을 먼저 확인하고, 설치를 반복하거나 서비스 권한을 수동으로 확대하지 말아야 합니다. 조직의
SCM 정책, 백신·EDR 또는 GPO가 해당 부가 설정을 막는지는 Windows 관리자와 별도로 확인합니다.

## 변경하지 않은 계약

- 서비스 실행 파일 경로, 자동 시작 유형, 가상 계정과 실제 시작 확인은 계속 fail-closed입니다.
- `0.11.6-poc`의 제한된 파일 활성화 재시도와 rollback 계약을 유지합니다.
- Viewer 장비 목록, DPAPI 자격 증명, 설정과 감시 이력의 저장 형식·위치는 변경하지 않습니다.
- Agent API는 v4이며 Agent/Viewer 통신 계약과 Telnet 조회 제한을 변경하지 않습니다.
- Agent Setup, Viewer Setup과 Viewer의 버튼, 입력 순서, 상태 표시 및 운영 흐름은 변경하지
  않았습니다. UI 재설계나 신규 기능은 포함하지 않습니다.
- Agent/Viewer 공개 Asset은 Windows x64 self-contained ZIP 두 개만 유지합니다.

## 배포 파일

```text
SamsungSwitchWatch-Agent-0.11.7-poc-win-x64.zip
SamsungSwitchWatch-Viewer-0.11.7-poc-win-x64.zip
```

두 패키지는 코드 서명되지 않은 POC입니다. SHA-256과 공식 Release 조합을 확인하고 사내
SmartScreen, EDR, AppLocker 및 WDAC 승인 절차를 따르십시오.

## 검증 범위와 현장 한계

- Mock Windows 서비스 관리자를 사용해 서비스 상태 snapshot, 기존 계약 불일치, 중지, 핵심 구성과
  시작 실패가 각각 안정된 코드로 분류되는지 검증합니다.
- 서비스 설명, 자동 복구 정책과 제한 DACL 적용만 실패한 경우 경고를 남기고 핵심 설치를
  유지하는지 검증합니다.
- 기존 오류 코드 순서와 SWD1 해석 호환성, 익명 진단 허용 목록과 재현 스크립트를 검증합니다.
- 개발 환경 검증은 실제 Windows SCM 정책, 서비스 계정 권한, 백신·EDR 실시간 검사 또는 사내
  GPO 동작을 증명하지 않습니다. 실제 Agent PC에서는 영향이 적은 시험 대상 한 대부터 설치하고
  서비스 Running, TCP/18443과 Viewer 연결을 순서대로 확인해야 합니다.
- 실제 삼성 스위치 명령, Agent API v4, Viewer 데이터와 화면은 변경하지 않았습니다. 운영 장비
  설정을 변경하는 시험은 하지 않습니다.
