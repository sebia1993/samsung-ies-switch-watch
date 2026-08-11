# Samsung Switch Watch 0.11.5-poc 릴리스 노트

릴리스 날짜: 2026-08-11

`0.11.5-poc`는 Agent Setup의 `이전 상태 복구`가 Windows 서비스 메타데이터 일부를
복원하지 못했을 때 전체 복구를 반복해서 실패시키던 문제를 보완한 현장 핫픽스입니다.
장비 관리, Viewer 소유 데이터, API v4와 읽기 전용 명령 정책은 변경하지 않습니다.

## 서비스 복구 핫픽스

- 기존 서비스의 실행 파일 경로, 시작 유형, 계정, 표시 이름, 서비스 SID와 이전 실행 상태는
  계속 핵심 복구 항목으로 취급합니다.
- 핵심 항목을 복원하거나 재확인하지 못하면 `ROLLBACK_SERVICE_RESTORE_FAILED`로 중단합니다.
  이때 Setup은 fail-closed로 동작하며 journal, 이전 Agent 파일과 진단 근거를 보존합니다.
- 일시적인 핵심 서비스 복원 실패 뒤에도 같은 journal로 `이전 상태 복구`를 다시 시도할 수
  있습니다. 핵심 실패가 계속되면 반복 시도 후에도 증거를 삭제하지 않습니다.
- 핵심 사후 검증은 서비스 설명, 자동 복구 정책과 DACL 조회에서 분리했습니다. 선택 항목을
  읽을 수 없는 사내 정책 환경에서도 핵심 구성과 실행 상태를 확인할 수 있으면 복구를
  완료합니다.
- Windows가 서비스를 `marked for delete` 상태로 보고하면 최대 20초만 삭제 완료를 기다립니다.
  완료되면 기존 서비스를 다시 만들거나 미설치 상태 복구를 계속하고, 제한 시간이 지나면
  무한 대기하지 않고 기존 실패 계약으로 중단합니다.

## 선택적 서비스 메타데이터 경고

서비스 실행 자체와 직접 관련되지 않은 다음 항목은 best effort로 복원합니다.

- 서비스 설명: `ROLLBACK_SERVICE_DESCRIPTION_RESTORE_WARNING`
- Windows 자동 복구 정책: `ROLLBACK_SERVICE_RECOVERY_POLICY_RESTORE_WARNING`
- 서비스 DACL: `ROLLBACK_SERVICE_DACL_RESTORE_WARNING`

이 중 하나만 복원하지 못해도 핵심 서비스 상태를 다시 읽어 확인할 수 있으면 복구는
완료됩니다. 경고는 진단에 남지만 완료된 트랜잭션의 journal과 제한된 임시 자료는 정리되며,
설치 또는 업데이트는 운영자가 상태를 확인한 뒤 별도로 다시 시작합니다. 경고에는 원시
Win32 오류, 절대 경로, 계정 또는 민감정보를 포함하지 않습니다.

## 0.11.4 작업 기록 호환성

- `0.11.4-poc`에서 남긴 현재 형식의 DeploymentJournal을 그대로 읽고 복구할 수 있습니다.
- 기존 journal 형식과 저장 위치를 깨는 스키마 변경은 없습니다.
- 복구 대기 또는 실패 중인 `Agent.__staging_*`, `Agent.__backup_*`, `Agent.__failed_*` 폴더와
  `%ProgramData%\SamsungSwitchWatch-Operations` 작업 기록은 수동으로 삭제하거나 이름을
  바꾸지 마십시오.

## 변경하지 않은 동작

- Agent API는 v4이며 호환되는 Viewer 연결 계약을 유지합니다.
- 장비, 자격 증명, 감시 일정과 결과 이력은 Viewer가 계속 소유합니다.
- Agent는 창 없는 Windows 서비스로 동작하고 Telnet 조회를 중계합니다.
- 수동 명령은 한 줄 `show` 조회 명령으로 제한되며 설정 변경 명령은 차단합니다.
- Agent의 임시 HTTPS 인증서는 Viewer가 자동 수락하며 지문이나 페어링 토큰을 요구하지
  않습니다.

## 배포 파일

GitHub Release Assets에는 다음 두 ZIP만 게시합니다.

```text
SamsungSwitchWatch-Agent-0.11.5-poc-win-x64.zip
SamsungSwitchWatch-Viewer-0.11.5-poc-win-x64.zip
```

두 패키지는 Windows x64 self-contained 시험판이며 Python 또는 별도 .NET 설치를 요구하지
않습니다. 코드 서명되지 않은 POC이므로 사내 반입 전에 SHA-256, SmartScreen, EDR,
AppLocker와 WDAC 정책을 확인해야 합니다.

## 검증과 현장 한계

- 자동화 테스트에서 세 선택적 메타데이터 경고가 복구 완료와 journal 정리를 막지 않는지
  검증했습니다.
- 핵심 서비스 복원이 한 번 실패한 뒤 다음 재시도에서 성공하는 경우와 계속 실패하여 journal,
  이전 파일과 증거를 보존하는 경우를 검증했습니다.
- 전체 서비스 메타데이터 대신 핵심 필드만 사용하는 복구 코드는 빌드와 회귀 테스트를
  통과했습니다. `marked for delete` 판별과 대기 시간은 정적 계약 테스트로 확인했습니다.
- `0.11.4-poc` 현재 형식 journal 호환 회귀를 포함해 Agent Setup 테스트 463개가 통과했습니다.
- 이 결과는 Fixture와 가짜 Windows 서비스 관리자를 이용한 로컬 검증입니다. 실제 Windows
  SCM, 서비스 DACL, EDR·백신, 사내 보안 정책 또는 파일 잠금 환경의 통과를 증명하지 않습니다.
- 실제 삼성 스위치 연결과 명령 출력은 현장 검증 범위이며, 운영 장비의 설정 변경 시험은 하지
  않습니다. 관리자 시험 PC에서 복구를 먼저 확인한 뒤 영향이 적은 Agent 한 대부터 단계적으로
  적용하십시오.
