# Samsung Switch Watch 0.11.10-poc 릴리스 노트

릴리스 날짜: 2026-08-13

`0.11.10-poc`는 Agent–Viewer–스위치의 기존 운영 흐름과 API 계약을 유지하면서 Telnet,
로컬 저장, 이벤트 전달, 작은 화면, 패키지 검증과 Viewer 복구의 실패 경계를 강화한 안정성
릴리스입니다. 새로운 장비 제어 기능이나 설정 변경 명령은 추가하지 않았습니다.

## Telnet 세션과 수집 경계

- 로그인, 선택적 enable 전환과 명령 수집에 각각 제한된 시간·바이트 예산을 적용합니다.
- Samsung 장비의 Latin-1 출력과 Telnet IAC 협상 바이트를 처리하면서도 끝없는 배너·프롬프트·
  명령 출력에는 무한 대기하지 않습니다.
- 포트 상태와 시스템 로그 수집은 bounded 결과만 Viewer에 전달하며 기존 읽기 전용 명령 정책과
  세션 종료 규칙을 유지합니다.

## Agent와 Viewer 로컬 상태

- Agent의 임시 신원 입력 파일은 크기 상한과 형식 검사를 통과해야 합니다. 손상되거나 과대한
  입력을 정상 신원으로 사용하지 않습니다.
- Viewer 설정, 장비와 감시 JSON은 파일별 상한 안에서 읽고 씁니다. 손상·과대 파일을 이전 정상
  상태처럼 표시하지 않습니다.
- 원자적 파일 교체가 완료된 뒤 백신·EDR이 destination 재확인이나 임시 파일 정리만 잠시
  막더라도 이미 완료된 저장을 실패로 오판하지 않습니다.

## 이벤트와 작은 화면

- Viewer 이벤트 feed는 제한된 용량을 사용하고 같은 변경을 합쳐 느린 UI 소비자가 메모리를
  무제한 사용하지 않게 합니다.
- Agent 연결이 교체된 뒤 이전 client의 늦은 결과가 현재 연결 상태를 덮어쓰지 못합니다.
- 작은 Windows 작업 영역에서는 대시보드가 스크롤되며, 저장된 창 위치와 크기는 현재 사용 가능한
  화면 경계 안으로 복원됩니다.

## Agent·Viewer 패키지 검증

- `BUILD-MANIFEST.json`은 엄격한 UTF-8과 2 MiB 바이트 상한으로 읽습니다.
- 읽기 전후 hash·길이 변화, 각 entry의 선언 크기와 SHA-256을 검증합니다.
- Windows 대소문자 비구분 이름 충돌을 거부하고 manifest에 선언된 정확한 최상위 파일 집합만
  허용하며 하위 디렉터리를 거부합니다.
- Agent는 검증 뒤 staging으로 복사된 manifest도 다시 확인하여 검증과 활성화 사이 변경을
  차단합니다.

## Viewer Setup 설치와 복구

- 활성화·복구의 디렉터리 Move/Delete는 제한된 횟수, 짧은 간격과 취소 인식을 사용해 일시적인
  EDR 잠금을 흡수합니다. 지속 잠금과 모호한 토폴로지는 journal과 증거를 보존하고 중단합니다.
- commit 전 새 Viewer 설치가 손상된 경우 새 세대의 manifest 완전성을 rollback 전제로 삼지
  않습니다. 해당 세대를 managed failed 경로로 격리하고, 먼저 검증한 이전 backup을 복구한 뒤
  복구된 설치를 다시 검증합니다.
- 첫 설치 실패도 같은 원칙으로 손상된 active 세대를 격리한 뒤 미설치 상태로 정리합니다.
- 이미 commit됐거나 정상 실행이 관측된 설치의 기존 cleanup 계약은 변경하지 않습니다.

## 호환성

- Agent API는 v4를 유지합니다.
- Viewer 설정·장비·감시 저장 형식은 변경하지 않습니다.
- 임시 TLS, RFC1918 Viewer/장비 제한, TCP/18443과 Telnet/23, DPAPI 자격 증명, 읽기 전용
  명령과 민감 출력 비저장이라는 기존 보안 계약을 유지합니다.
- `0.11.9-poc`의 모델 자동 판별과 `detectedModel` 호환 동작을 그대로 유지합니다.

## 배포 파일

GitHub Release의 사용자 정의 Assets는 다음 두 파일만 허용합니다.

```text
SamsungSwitchWatch-Agent-0.11.10-poc-win-x64.zip
SamsungSwitchWatch-Viewer-0.11.10-poc-win-x64.zip
```

두 ZIP은 Windows x64 self-contained 패키지이며 Python 또는 온라인 .NET 설치가 필요하지
않습니다. Source code ZIP/tar.gz와 내부 `BUILD-MANIFEST.json`, SBOM, `SHA256SUMS.txt`는 별도
사용자 정의 Asset이 아닙니다.

## 검증 범위와 현장 확인

개발 환경에서는 Mock Telnet, fixture, 저장·동시성·Setup 회귀와 패키지/워크플로 계약을
검증합니다. 이 근거는 다음 현장 검증을 대신하지 않습니다.

- 실제 Windows SCM 서비스 설치·시작·중지·복구
- 사내 백신·EDR·SmartScreen·AppLocker·WDAC의 잠금과 허용 정책
- Viewer PC와 Agent PC 사이 방화벽·GPO·라우팅
- IES4224GP, IES4028XP, IES4226XP 실제 펌웨어의 로그인·enable·명령·Latin-1 출력

현장에는 같은 Release의 Agent와 Viewer ZIP을 함께 반입하고, 단일 시험 장비의 읽기 전용
명령부터 단계적으로 검증하십시오.
