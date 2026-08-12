# Samsung Switch Watch 0.11.8-poc 릴리스 노트

릴리스 날짜: 2026-08-12

`0.11.8-poc`는 Agent 설치의 서비스·파일 경합과 Viewer의 요청 취소·중복 실행을 줄이는
안정성 릴리스입니다. 기존 Agent API v4, Viewer 데이터 저장 형식, 장비 등록과 감시 흐름은
유지합니다.

## Agent Setup 안정성

- Windows 서비스가 이미 `START_PENDING`이면 중복 시작 요청을 보내지 않고 제한 시간 안에서
  기존 전환을 기다립니다.
- 서비스가 `STOP_PENDING`이면 중지가 완료된 뒤 한 번만 시작합니다.
- 서비스 상태 대기는 시스템 시각 변경의 영향을 받지 않는 단조 시계를 사용합니다. 제한을
  넘기면 기존 `SETUP_SERVICE_START_FAILED`로 fail-closed 중단합니다.
- 설치 journal은 같은 볼륨의 임시 파일을 완전히 기록한 뒤 원자적으로 교체합니다. 교체가
  완료된 뒤 백신·EDR이 임시 파일 정리만 막더라도 이미 저장된 journal을 실패로 되돌리지
  않습니다.
- 새 `%ProgramData%\SamsungSwitchWatch` 루트는 원자적 단일 생성에 성공한 경우에만 제품
  소유 폴더로 사용합니다. 사전 검사와 생성 사이에 다른 프로세스가 폴더를 만들면 그 폴더의
  ACL이나 파일을 변경하거나 rollback에서 삭제하지 않고 `SETUP_PATH_UNTRUSTED`로 중단합니다.

## Viewer 요청 수명과 중복 실행

- Agent 주소를 바꾸거나 Viewer를 종료하면서 이전 요청이 취소된 경우, 의도된 취소를 처리되지
  않은 UI 예외나 새 연결 실패로 표시하지 않습니다.
- `포트 상태/시스템 로그 수동 점검`과 직접 입력한 읽기 전용 장비 명령은 공통 단일 실행 규칙을 사용합니다. 하나가 실행
  중이면 다른 작업은 비활성화되고 대기열로 중복 실행되지 않습니다.
- 장비 관리 창을 닫으면 진행 중인 로그인 확인을 취소하고 입력된 로그인 PW와 enable PW를
  즉시 지웁니다. 닫힌 창에는 늦게 도착한 결과를 반영하지 않습니다.

## 수집 시간 초과 후보 전환

자동 수집에서 현재 후보 명령이 `COMMAND_TIMEOUT` 또는 `QUERY_TIMEOUT`으로 실패하면 같은
점검에서 즉시 새 세션을 열지 않습니다. 다음 감시 주기에 다음 후보를 한 번 시도하며, 마지막
후보까지 실패하면 그 후보를 유지한 채 해당 수집 항목을 `확인 불가`로 표시합니다. 포트 상태와
시스템 로그의 다른 수집 항목은 계속 처리합니다.

이 동작은 짧은 장비 세션 제한에서 연속 로그인과 불필요한 부하를 줄이기 위한 것입니다. 실제
삼성 스위치의 펌웨어별 후보 명령 지원 여부는 승인된 사내 시험 장비에서 읽기 전용으로 확인해야
합니다.

## 변경하지 않은 계약

- Agent API는 v4이며 요청·응답 형식을 변경하지 않습니다.
- Viewer 장비 목록, DPAPI 자격 증명, 설정, 기준값과 감시 이력의 저장 형식·위치는 변경하지
  않습니다.
- Agent는 장비 정보, 자격 증명, 명령 또는 결과를 저장하지 않습니다.
- 한 줄 `show` 조회만 허용하고 설정 변경 명령은 Viewer와 Agent 양쪽에서 차단합니다.
- Agent와 Viewer의 주요 화면·입력 순서·상태 분류를 전면 변경하지 않습니다.
- 공개 GitHub Release Assets는 Windows x64 self-contained Agent ZIP과 Viewer ZIP 두 개만
  유지합니다.

## 배포 파일

```text
SamsungSwitchWatch-Agent-0.11.8-poc-win-x64.zip
SamsungSwitchWatch-Viewer-0.11.8-poc-win-x64.zip
```

두 패키지는 코드 서명되지 않은 POC입니다. 공식 Release의 SHA-256을 확인하고 사내
SmartScreen, EDR, AppLocker와 WDAC 승인 절차를 따르십시오.

## 검증 범위와 현장 한계

- 가짜 Windows 서비스 관리자로 Running, Stopped, StartPending, StopPending과 제한 시간 경로를
  검증합니다.
- 가짜 파일 시스템으로 ProgramData 생성 경합, 외부 폴더 보존, journal 원자 교체와 임시 파일
  정리 실패를 검증합니다.
- Viewer의 Agent 교체 취소, 수동 작업 단일 실행, 장비 관리 창 닫기 취소·비밀번호 제거와 다음
  주기 후보 전환을 결정적 테스트로 검증합니다.
- 개발 환경의 Mock·fixture·패키지 검증은 실제 Windows SCM, 사내 EDR 파일 잠금, 원격 방화벽,
  Samsung 펌웨어 또는 운영 장비의 세션 동작을 증명하지 않습니다.
- 현장에서는 영향이 적은 Agent PC와 스위치 한 대부터 서비스 Running, Viewer 연결,
  `show port status`와 지원되는 syslog 후보를 순서대로 확인해야 합니다.
