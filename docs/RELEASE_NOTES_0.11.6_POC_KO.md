# Samsung Switch Watch 0.11.6-poc 릴리스 노트

릴리스 날짜: 2026-08-11

`0.11.6-poc`는 Agent 업데이트 중 Windows 파일 잠금이나 백신·EDR 검사 때문에 프로그램
폴더 이동이 일시적으로 실패하는 경우를 제한적으로 재시도하고, 계속되는 실패 원인을 안정된
코드로 구분하는 설치 안정성 핫픽스입니다. Agent와 Viewer의 업무 흐름 및 화면 배치는
변경하지 않습니다.

## 제한된 Agent 파일 활성화 재시도

Agent Setup은 다음 두 디렉터리 이동을 각각 최대 5회만 시도합니다.

- 기존 Agent 설치 폴더 → 검증된 backup 폴더
- 검증된 staging 폴더 → 실제 Agent 설치 폴더

`IOException` 또는 접근 거부가 발생하고 원본·대상이 이동 전 상태로 확인된 경우에만 다음
시도를 진행하며, 실패한 시도 사이에만 250ms 대기합니다. 이동 API가 오류를 반환했더라도
원본이 사라지고 대상이 존재하는 정확한 완료 상태이면 성공으로 조정합니다. 원본과 대상이
모두 존재하거나 둘 다 확인되지 않는 등 상태가 모호하면 반복하지 않고 fail-closed로
중단합니다. 무한 재시도는 하지 않습니다.

## 안정된 오류 코드와 운영 조치

- `SETUP_BACKUP_MOVE_FAILED`: 기존 Agent 설치 폴더를 backup으로 옮기지 못했습니다.
- `SETUP_FILE_ACTIVATION_FAILED`: staging을 실제 설치 위치로 활성화하지 못했습니다.
- `SETUP_BACKUP_ACCESS_WARNING`: backup 이동은 완료됐지만 관리자 전용 ACL 강화를 확인하지
  못했습니다. 이 항목만 실패하면 경고를 남기고 설치를 계속합니다.

두 이동 실패 코드는 원시 경로 또는 Windows 예외를 노출하지 않고 익명 진단과 SWD1 지원
코드에 안정적으로 반영됩니다. 실패 시 Setup은 기존 rollback 절차로 이전 Agent 파일과 서비스
상태를 복원합니다. 운영자는 제품 폴더를 수동으로 이동·삭제하거나 ACL을 넓히지 말고 화면의
rollback 결과를 확인해야 합니다. `이전 상태 복구`가 표시되면 이를 먼저 완료한 뒤
`설치/업데이트`를 별도로 한 번 실행합니다.

## 변경하지 않은 계약

- `0.11.5-poc`에서 보강한 rollback 및 서비스 복구 정책을 유지합니다.
- Viewer 장비 목록, DPAPI 자격 증명, 설정과 감시 이력의 저장 형식·위치는 변경하지 않습니다.
- Agent API는 v4이며 Agent/Viewer 통신 계약과 Telnet 조회 제한을 변경하지 않습니다.
- Agent Setup, Viewer Setup과 Viewer의 버튼, 입력 순서, 상태 표시 및 운영 흐름은 변경하지
  않았습니다. UI 재설계나 신규 기능은 포함하지 않습니다.
- Agent/Viewer 공개 Asset은 Windows x64 self-contained ZIP 두 개만 유지합니다.

## 배포 파일

```text
SamsungSwitchWatch-Agent-0.11.6-poc-win-x64.zip
SamsungSwitchWatch-Viewer-0.11.6-poc-win-x64.zip
```

두 패키지는 코드 서명되지 않은 POC입니다. SHA-256과 공식 Release 조합을 확인하고 사내
SmartScreen, EDR, AppLocker 및 WDAC 승인 절차를 따르십시오.

## 검증 범위와 현장 한계

- Fixture 기반 테스트는 첫 이동 실패 후 성공, 지속적인 backup 이동 실패, 지속적인 staging
  활성화 실패, 이동 완료 뒤 늦은 I/O 오류, 재시도 중 취소와 모호한 원본·대상 상태를 다룹니다.
- backup ACL 강화 실패가 경고로 남으면서 정상 설치를 유지하는 경우를 Mock 파일 시스템으로
  검증합니다.
- 이 검증은 실제 Windows 파일 잠금, SCM, 백신·EDR 실시간 검사, 사내 ACL·GPO 또는 운영 PC의
  성능을 증명하지 않습니다. 인터넷이 없는 개발 환경과 비식별 Fixture만으로 재현 가능한
  범위입니다.
- 실제 삼성 스위치 명령과 장비 접속은 변경하지 않았습니다. 현장에서는 관리자 시험 PC에
  먼저 적용하고, 영향이 적은 Agent 한 대에서 설치·rollback·Viewer 연결을 순서대로 확인해야
  합니다. 운영 장비 설정을 변경하는 시험은 하지 않습니다.
