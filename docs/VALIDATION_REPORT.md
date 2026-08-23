# Samsung iES Switch Watch 검증 보고서

이 문서는 합성 자동 검증과 실제 현장 검증을 분리합니다.

> 자동 테스트 성공을 실제 Samsung 스위치나 회사 네트워크 검증 완료로 표현하지 않습니다.

## 검증 기준

- 공개 POC: `0.12.0-poc`
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
- 연결 종료 시 미완료 명령만 제한 재연결
- fixture 기반 모델 단일/미검출/모호 판정

### Viewer 상태

- device·credential ownership
- client generation 전환 후 stale 결과 거부
- monitoring baseline/gap/event
- bounded/coalesced event feed
- 저장소 손상·과대 파일 fail-closed
- 마이그레이션 후 재페어링 요구

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
| 모델 식별 | fixture | 모델·펌웨어별 확인 필요 |
| Viewer 상태·stale 처리 | 자동 | 장시간 운영 확인 필요 |
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
- [ ] 로그인·enable·긴 출력·연결 단절
- [ ] 설정 변경 명령 0건
- [ ] 장시간 다수 장비 감시와 UI 응답성
- [ ] Telnet 관리망의 접근·캡처 권한 분리

## 공개 증거 원칙

실제 IP, hostname, MAC, username, 사이트명, raw output과 pairing code는 공개하지 않습니다. 현장 결과를 공개할 때는 비식별 모델·Windows 버전·검증 항목 PASS/FAIL만 기록합니다.
