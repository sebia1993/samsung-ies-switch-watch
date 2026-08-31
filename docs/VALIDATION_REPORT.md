# Samsung iES Switch Watch 검증 보고서

이 문서는 합성 자동 검증과 실제 현장 검증을 분리합니다.

> 자동 테스트 성공을 실제 Samsung 스위치나 회사 네트워크 검증 완료로 표현하지 않습니다.

## 검증 기준

- 공개 POC: `0.13.0-poc`
- 대상: Windows x64
- SDK: `global.json`의 exact .NET SDK
- 산출물: Agent/Viewer self-contained ZIP

## 자동 검증

### 빌드·테스트

```powershell
dotnet restore SamsungSwitchWatch.sln --locked-mode
dotnet build SamsungSwitchWatch.sln -c Release --no-restore
dotnet test SamsungSwitchWatch.sln -c Release --no-build
dotnet format SamsungSwitchWatch.sln --verify-no-changes --no-restore
```

### 인증·TLS

- 정확한 `SSW1` 64-byte pairing payload
- DPAPI LocalMachine token persistence와 CurrentUser Viewer 보호
- malformed/wrong/missing bearer의 동일한 401
- health를 포함한 모든 Agent 경로 인증과 최소 health 응답
- API v4의 426 upgrade 계약
- 인증서 유효기간, Server Authentication EKU와 SPKI SHA-256 pin
- identity body pin 불일치 fail-closed
- token·pairing code 로그/CLI 비노출 계약

### Telnet·명령

- synthetic Telnet IAC negotiation과 Latin-1
- 로그인/enable/prompt 처리
- 단계별 timeout과 output 상한
- 한 줄 `show` 정책과 설정 명령 차단
- `running-config`·`startup-config`의 민감 조회 분류, 기본 차단과 명시적 opt-in
- 연결 종료 시 미완료 명령만 제한 재연결
- 17개 deterministic fault scenario로 slow/timeout/reset/partial/paging/대용량·무한 출력 검증
- 인증·enable·명령 timeout 재시도 0회, 명령 중 세션 종료만 최대 1회 재연결
- A 성공 후 B 단절 시 재연결 뒤 B·C만 실행하고 A는 재실행하지 않는 계약
- fixture 기반 모델 단일/미검출/모호 판정

### Viewer 상태

- device·credential ownership
- client generation 전환 후 stale 결과 거부
- `PeriodicTimer` scheduler, 최대 256개 bounded monitoring queue와 worker 2개
- 같은 장비 중복 수집 억제, queue drop accounting, cancellation과 idempotent dispose
- 장비 revision 변경·삭제·감시 해제 중 late completion 차단
- connection-level 3회 실패 후 30초 Open, HalfOpen probe를 거치는 circuit breaker
- monitoring baseline/gap/event
- bounded/coalesced event feed
- 저장소 손상·과대 파일 fail-closed
- 잠긴 파일·남은 partial temp·쓰기 실패를 포함한 저장 fault fail-closed
- 마이그레이션 후 재페어링 요구

### 관리망 authorization

- target IPv4·RFC1918·TCP/23·금지 주소 검증
- `AllowedTargetCidrs` 최대 32개, canonical network boundary, 중복 제거
- malformed·IPv6·공인망·범위 밖 target 거부
- 빈 목록은 기존 설치 호환을 위해 RFC1918 기본 세 범위로 정규화
- Agent Setup의 기존 제한 범위 복원, 다중 입력 정규화와 최대 32개 검증

### Diagnostics·stability

- `System.Diagnostics.Metrics` 기반 Viewer queue/worker/event/connection 지표
- Agent Telnet session/request/reconnect/timeout/admission 지표
- 사용자 이름·암호·token·pairing code·명령 원문/출력·실제 IP를 metric tag로 기록하지 않음
- seed를 출력하는 10/50/100/250 장비 stability harness
- 5분 smoke, 15분 quick, 1시간 standard, 8시간 extended, 24시간 manual profile
- 수집·실패·재연결·drop·peak queue/workers/operation gates·종료 시 gate·GC·메모리·thread/handle·미처리 예외 summary

### 설치·패키지

- protected staging, backup, journal과 rollback
- strict UTF-8 manifest, size·SHA-256, read-time mutation
- Windows 대소문자 중복과 reparse 경계
- locked 일반/RID 복원
- 전이 의존성 취약점 검사
- SPDX·CycloneDX SBOM
- candidate artifact 업로드 후 재다운로드
- manifest/hash/package contract와 추출 EXE mock smoke
- GitHub build provenance attestation 계약

## 검증 상태

| 항목 | 합성/CI | 실제 현장 |
|---|---:|---:|
| restore/build/unit/integration | 자동 | - |
| API v5 bearer·SPKI pin | 자동 | 배포 경로 확인 필요 |
| DPAPI scope | Windows CI | 계정·도메인 정책 확인 필요 |
| Telnet IAC/Latin-1/prompt | synthetic | 펌웨어별 확인 필요 |
| 읽기 전용 명령 validator | 자동 | 운영 계정 권한 확인 필요 |
| 민감 조회 opt-in | 자동 | 조직 승인·출력 취급 확인 필요 |
| target CIDR authorization | 자동 | 실제 관리 VLAN 범위 확인 필요 |
| 모델 식별 | fixture | 모델·펌웨어별 확인 필요 |
| Viewer bounded 감시·stale 처리 | 자동 | 장시간 운영 확인 필요 |
| Telnet fault injection | deterministic synthetic | 실제 단절·paging 확인 필요 |
| stability harness | Windows CI 5분 smoke | 8시간·24시간 실행 필요 |
| local metrics 비밀 비노출 | 자동 | 현장 수집·운영 절차 확인 필요 |
| package/SBOM/SHA | 자동 | 반입 정책 확인 필요 |
| Windows Service/rollback | 단위·smoke | 실제 EDR/GPO 확인 필요 |
| Telnet 평문 분리 | 문서·정책 | VLAN/ACL 현장 확인 필요 |

## 현장 검증 체크

허가된 환경에서만 수행합니다.

- [ ] Agent Setup 설치·업데이트·복구
- [ ] 서비스 재부팅 후 자동 시작
- [ ] 새 코드로 Viewer 재페어링
- [ ] 잘못된 token·변경된 인증서 차단
- [ ] Firewall/GPO/EDR 정책
- [ ] 실제 모델과 펌웨어 식별
- [ ] IES4224GP 펌웨어별 prompt
- [ ] IES4028XP 펌웨어별 prompt
- [ ] IES4226XP 펌웨어별 prompt
- [ ] 로그인·enable mode behavior
- [ ] paging behavior
- [ ] long command output
- [ ] Telnet disconnect behavior
- [ ] 설정 변경 명령 0건
- [ ] Windows Service recovery
- [ ] 8시간 이상 다수 장비 감시와 UI 응답성
- [ ] 24시간 장기 감시
- [ ] Telnet 관리망의 접근·캡처 권한 분리

## 공개 증거 원칙

실제 IP, hostname, MAC, username, 사이트명, raw output과 pairing code는 공개하지 않습니다. 현장 결과를 공개할 때는 비식별 모델·Windows 버전·검증 항목 PASS/FAIL만 기록합니다.
