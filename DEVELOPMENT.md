# Samsung Switch Watch 개발 가이드

이 문서는 저장소 유지보수와 검증에 필요한 개발 규칙을 정리합니다.

## 개발 환경

- Windows 중심 프로젝트
- .NET SDK: `global.json`에 고정된 버전
- Release target: `win-x64`
- Agent/Viewer package: self-contained, single-file, untrimmed

## 프로젝트 구조

- `src/SamsungSwitchWatch.Core`: Telnet negotiation, prompt, 명령 검증, 오류 코드
- `src/SamsungSwitchWatch.Agent`: stateless HTTPS→Telnet Windows Service
- `src/SamsungSwitchWatch.Viewer`: WPF 운영 UI, 장비/자격 증명/감시 상태
- `src/SamsungSwitchWatch.Agent.Setup`: Agent 설치·복구
- `src/SamsungSwitchWatch.Viewer.Setup`: Viewer 설치·복구
- `tests`: synthetic Telnet server와 비식별 fixture 기반 검증
- `scripts`: build / validate / package contract / deployment helper
- `tools/SamsungSwitchWatch.ManualCapture`: 비식별 WPF 문서 화면 생성
- `tools/SamsungSwitchWatch.StabilityHarness`: seeded synthetic 장시간 감시 workload

## 기본 검증

```powershell
dotnet restore SamsungSwitchWatch.sln --locked-mode
dotnet build SamsungSwitchWatch.sln -c Release --no-restore
dotnet test SamsungSwitchWatch.sln -c Release --no-build
.\scripts\validate.ps1 -Configuration Release
```

패키지:

```powershell
.\scripts\build-release.ps1 -Version 0.13.0-poc
```

Windows stability harness:

```powershell
dotnet run --project .\tools\SamsungSwitchWatch.StabilityHarness -c Release -- --profile smoke --devices 100 --seed 372811
```

기본 Windows CI는 5분 `smoke`만 실행합니다. 15분·1시간·8시간·24시간 profile은 목적과 실행 환경을 기록한 수동 검증에 사용합니다.

## 변경 원칙

- 공개 API 변경은 명시적인 버전 전환·마이그레이션 문서와 회귀 테스트를 함께 제공합니다.
- Telnet parser와 filesystem/deployment 경계는 deterministic test가 가능해야 합니다.
- 실제 회사 IP, 계정, MAC, 명령 결과, 인증서를 fixture에 넣지 않습니다.
- live switch test는 허가된 환경에서 사람이 명시적으로 수행합니다.
- mock/package/smoke 결과를 실제 장비 검증으로 표현하지 않습니다.
- pairing code와 bearer token을 인수·로그·fixture·진단 자료에 넣지 않습니다.
- 생성된 `bin`, `obj`, `artifacts`, release output, database, certificate를 커밋하지 않습니다.

## 장비 접근 계약

### 읽기 전용

- 수동 입력은 한 줄 `show` 명령만 허용합니다.
- line break와 command separator를 허용하지 않습니다.
- configuration-changing command를 추가하지 않습니다.
- 감시 API request는 검증된 명령 최대 8개입니다.

### 모델 판정

로그인 확인은 인증 후 `show version`을 실행합니다.

- 등록 모델 정확히 1개 → canonical model
- 미검출 → `MODEL_NOT_DETECTED`
- 여러 모델 → `MODEL_AMBIGUOUS`

raw model detection output을 반환·저장하지 않습니다.

### 재시도

- authentication 실패 재시도 금지
- enable 실패 재시도 금지
- command timeout blind retry 금지
- 장비가 명령 실행 중 연결을 닫은 경우에만 fresh session 1회
- 재연결 후 미완료 command만 실행

login/enable/command collection의 bounded time/byte budget과 Telnet IAC/Latin-1 지원을 유지합니다.

## Viewer / Agent 책임

### Viewer

Viewer가 다음을 소유합니다.

- Agent endpoint
- authority별 Agent SPKI pin과 DPAPI CurrentUser API token
- device inventory
- canonical model
- DPAPI CurrentUser credentials
- monitoring schedule
- baseline/gap/event

### Agent

Agent는 다음을 저장하지 않습니다.

- device inventory
- credential
- command/result
- monitoring state/history

Agent public runtime은 `--service` Windows Service만 허용합니다.

## 네트워크 보안 경계

- Production Agent: HTTPS TCP/18443
- Viewer source / switch target: private management network 범위
- Switch: Telnet TCP/23
- Agent 기능 API: bearer 인증 필수 API v5
- Agent TLS: 수동 페어링한 SPKI SHA-256 pin 검증
- 공개 health 외의 무인증 경로, TOFU와 v4 fallback 금지

Agent를 public Internet 또는 user access network에 노출하는 방향으로 변경하지 않습니다.

## Viewer 상태 안정성

- 오래된 HTTP client generation의 결과가 현재 상태를 덮지 못하게 합니다.
- 자동 상태는 awaiting/deferred/current unavailable/current result를 구분합니다.
- event feed는 bounded/coalesced 구조를 유지합니다.
- 새 HTTP client로 교체할 때 active request와 dispose race를 만들지 않습니다.
- 작은 work area에서도 dashboard가 사용 가능하도록 scroll/bounds 복원을 유지합니다.

## Setup / rollback 계약

- package를 protected staging에 복사하고 rehash한 뒤 activation합니다.
- 기존 설치 교체 전 rollback에 필요한 상태를 확보합니다.
- commit 전 실패는 검증된 이전 설치를 복원합니다.
- arbitrary extraction/download directory를 삭제하지 않습니다.
- unknown path ownership, marker, reparse 상태는 fail-closed입니다.
- install commit과 readiness check를 분리합니다.
- local HTTPS/API/firewall readiness failure만으로 정상 설치를 rollback하지 않습니다.
- Viewer user data와 DPAPI credentials는 program quarantine 대상이 아닙니다.

## Package contract

Agent/Viewer Setup은 다음을 검증합니다.

- bounded strict UTF-8 manifest
- read-time mutation
- declared size/hash
- Windows case-insensitive duplicate
- exact top-level file set
- unexpected subdirectory

CI의 candidate artifact는 업로드 후 다시 다운로드해 SHA/manifest/package contract와 executable smoke를 확인합니다.

## 사용자 매뉴얼

운영 흐름이 변경되면 사용자 매뉴얼 생성 입력도 함께 갱신합니다.

```powershell
python tools\build-user-manual.py --output docs\SamsungSwitchWatch_User_Manual_KO.docx --images docs\manual\images
```

Release ZIP에는 생성된 사용자용 PDF를 포함하며 editable source는 저장소 개발 자료로 관리합니다.

## 문서 화면

`tools/SamsungSwitchWatch.ManualCapture`는 실제 WPF 화면을 Demo 데이터로 렌더링합니다. 문서 화면에는 RFC 5737 주소와 합성 상태만 사용합니다.

스크린샷은 코드에서 실제 UI를 렌더링한 것임을 명확히 하며, 실제 회사 화면처럼 오해할 수 있는 데이터를 넣지 않습니다. 캡처 명령과 source SHA·Windows 실행 근거는 [화면 안내](docs/USAGE_SCREENSHOTS_KO.md)에 기록합니다.

## Release 원칙

- 문서/오탈자 변경만으로 새 Release를 만들지 않습니다.
- Release tag는 검증할 소스 commit을 명확히 가리켜야 합니다.
- 기존 immutable Release asset을 교체하지 않습니다.
- 사용자 Release에는 버전이 일치하는 Agent/Viewer ZIP만 게시합니다.
- 실제 운영 영향이 없는 내부 변경은 Release note의 주요 기능처럼 과장하지 않습니다.
