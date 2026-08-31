# Samsung iES Switch Watch 아키텍처

## 1. 설계 목표

이 프로젝트는 Samsung iES 스위치의 반복 조회를 중앙화하면서 장비 자격 증명, 원격 실행 권한과 운영 이력을 한 프로세스에 몰아넣지 않는 것을 목표로 합니다.

```text
운영자
  ↓
Viewer (장비·자격 증명·기준선·이력)
  ↓ HTTPS/18443 + SPKI pin + bearer
Agent Windows Service (요청 단위 실행)
  ↓ Telnet/23
Samsung iES Switch
```

Viewer는 업무 상태를 소유하고 Agent는 요청을 실행한 뒤 장비 정보와 결과를 보관하지 않습니다.

## 2. 컴포넌트

### Viewer

- WPF 데스크톱 UI
- Agent 주소, 인증서 pin과 보호된 API token 관리
- 장비 목록과 DPAPI CurrentUser 장비 자격 증명 관리
- 수동 조회, 주기 감시, baseline, gap, event 관리
- 늦게 도착한 이전 client generation 결과 거부
- `MonitoringCoordinator`: `PeriodicTimer` → bounded channel → worker 2개와 종료 lifecycle
- `AgentConnectionCoordinator`: HTTP/realtime 상태, client generation과 교체
- `ManualQueryService`: 조회 검증·취소·history와 실제 실행
- `DeviceLifecycleService`: 장비 revision·credential block과 stale operation 방어
- `EventFeedCoordinator`: bounded change buffer, sequence·overflow·coalescing

### Agent

- 창 없는 Windows Service
- 영구 HTTPS 신원과 API token 소유
- API v5 인증·인가, 요청 크기·빈도·동시 실행 제한
- configured CIDR 안의 사설 IPv4, 모델과 읽기 전용 명령 재검증
- 요청마다 Telnet 세션 생성·종료

### Setup

- Agent: 서비스, 방화벽, 보호 디렉터리와 페어링 코드 관리
- Viewer: 사용자 영역 설치와 안전한 업데이트
- 공통: manifest·크기·SHA-256 검증, staging, rollback, journal

## 3. 최초 페어링

### Agent 신원

`AgentIdentityStore.LoadOrCreate`는 최초 실행 때 RSA TLS 인증서와 instance ID를 만들고 이후 재사용합니다.

- 인증서 PFX: DPAPI LocalMachine
- 수명: 5년, 만료 30일 이내이면 fail-closed
- 공개키 pin: SubjectPublicKeyInfo의 SHA-256
- 인증서 확장: Digital Signature, Key Encipherment, TLS Server Authentication EKU

### API token

`AgentAuthenticationStore.LoadOrCreate`는 CSPRNG로 32-byte token을 생성합니다.

- 저장: `api-bearer-token.dpapi`
- 보호: DPAPI LocalMachine + 제품 전용 entropy
- 로드 상한: 4 KiB
- 손상·복호화 실패·길이 불일치: Agent 시작 차단

### 수동 코드

```text
SSW1.<base64url(SPKI SHA-256 32 bytes || bearer token 32 bytes)>
```

Setup은 관리자가 명시적으로 요청할 때 이 코드를 표시합니다. Viewer는 형식과 정확한 64-byte payload를 확인한 뒤:

- authority별 SPKI SHA-256 pin 저장
- bearer token을 DPAPI CurrentUser로 보호해 저장

합니다. 코드는 명령행 인수, 로그, 오류 응답이나 진단 파일에 기록하지 않습니다.

## 4. 연결 검증 순서

1. Viewer가 HTTPS 연결을 시작합니다.
2. 인증서 유효기간과 Server Authentication EKU를 확인합니다.
3. 인증서 SPKI SHA-256을 저장된 pin과 고정 시간 비교합니다.
4. 모든 `/api/v5/*` 요청에 bearer token을 넣습니다.
5. Agent가 32-byte token을 decode하고 고정 시간 비교합니다.
6. `/api/v5/identity` 응답의 SPKI도 같은 pin과 다시 비교합니다.
7. 모두 일치한 뒤에만 장비 요청을 허용합니다.

pin, token 또는 identity가 다르면 자동 갱신·TOFU·구형 API fallback 없이 연결을 차단합니다.

## 5. API v5

| 경로 | 인증 | 목적 |
|---|---:|---|
| `GET /health/live` | bearer | 최소 프로세스 생존 상태 |
| `GET /health/ready` | bearer | 최소 버전·프로토콜 준비 상태 |
| `GET /api/v5/identity` | bearer | Agent 신원과 실행 상한 |
| `POST /api/v5/telnet/test` | bearer | 로그인·모델 확인 |
| `POST /api/v5/telnet/execute` | bearer | 검증된 읽기 명령 실행 |
| `/api/v4/*` | bearer 후 426 | 새 Viewer와 재페어링 요구 |

Setup은 DPAPI LocalMachine으로 보호된 설치 token을 읽어 health 요청에 전달합니다. Viewer는 페어링 때 DPAPI CurrentUser로 보호한 token을 identity와 모든 API 요청에 전달합니다. health 응답에는 Agent ID, instance ID, 인증서 pin, token, IP와 장비 정보가 포함되지 않습니다.

## 6. Telnet 실행 상태 머신

```text
request validation
  → target IPv4/RFC1918/AllowedTargetCidrs/port/model validation
  → per-client rate limit / per-device gate
  → TCP connect
  → Telnet IAC negotiation
  → login
  → optional enable
  → show command collection
  → bounded result
  → session dispose
```

- 인증·enable 실패와 command timeout: 재시도 없음
- 명령 도중 즉시 연결 종료: 새 세션 최대 1회
- 재연결: 완료되지 않은 명령만 실행
- 출력: 명령당 최대 64 KiB
- 요청: 명령 최대 8개, 본문 최대 32 KiB
- Agent 전체 기본 동시 실행: 2개
- 장비 한 대: 동시 세션 1개

## 7. Viewer 자동 감시

```text
PeriodicTimer
  → MonitoringCoordinator scheduler
  → bounded Channel (capacity 256, 오래된 queued work drop)
  → workers ×2
  → 장비별 operation gate
```

같은 장비가 queued 또는 running이면 새 cycle은 별도 실행을 추가하지 않고 기존 작업의 completion을 공유합니다. 장비 revision과 Agent client generation이 달라진 late result는 저장·UI 반영 전에 폐기합니다. 연결 계층 실패 3회는 장비별 circuit을 30초 동안 Open으로 만들고, 시간이 지난 뒤 HalfOpen 수집 한 번만 허용합니다.

## 8. 데이터 소유권

| 데이터 | Viewer | Agent |
|---|---:|---:|
| Agent 주소·pin·보호 token | 저장 | token·신원만 저장 |
| 장비 목록·모델 | 저장 | 비저장 |
| 장비 ID/PW/enable PW | DPAPI CurrentUser | 요청 메모리만 |
| 점검 일정·baseline·event | 저장 | 비저장 |
| 수동 명령·원문 출력 | 메모리만 | 메모리만 |

## 9. 업데이트와 마이그레이션

v0.12는 무인증 API를 폐기합니다. 이전 Viewer 설정의 Agent 주소, 장비, 보호된 장비 자격 증명과 감시 이력은 유지하지만 연결 자격은 인정하지 않습니다. Agent와 Viewer를 함께 업데이트하고 새 `SSW1` 코드로 다시 페어링해야 합니다.

## 10. 배포 공급망

- exact .NET SDK와 locked NuGet restore
- 전이 의존성 취약점 검사
- Windows x64 self-contained publish
- strict package manifest와 파일 hash
- SPDX/CycloneDX SBOM
- 업로드 후 artifact 재다운로드 검증
- 추출 EXE mock smoke
- 공개 ZIP build provenance attestation
- annotated tag와 immutable prerelease

합성/CI 검증을 실제 장비·사내망 검증으로 표현하지 않습니다.
