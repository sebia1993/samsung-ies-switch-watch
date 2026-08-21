# Samsung Switch Watch 검증 보고서

이 문서는 자동 테스트, Windows 패키지 검증, 현장 검증을 구분합니다.

**자동 테스트 성공을 실제 Samsung 스위치와 회사 네트워크에서의 검증 완료로 표현하지 않습니다.**

## 검증 기준 버전

- 공개 POC: `0.11.11-poc`
- 대상 OS: Windows x64
- 개발/빌드 SDK: `global.json` 기준 .NET SDK
- Release: Agent / Viewer self-contained ZIP

## 자동 검증 범위

### 1. 솔루션 복원·빌드·테스트

```powershell
dotnet restore SamsungSwitchWatch.sln --locked-mode
dotnet build SamsungSwitchWatch.sln -c Release --no-restore
dotnet test SamsungSwitchWatch.sln -c Release --no-build
```

NuGet lock file을 기준으로 복원하고 Core, Agent, Viewer, Setup과 배포 helper 계약을 검증합니다.

### 2. Telnet 프로토콜 경계

실제 회사 장비 대신 synthetic Telnet server와 fixture를 사용합니다.

검증 대상:

- Telnet IAC negotiation
- Latin-1 장비 출력
- 로그인 prompt 처리
- enable prompt 처리
- command prompt/완료 판단
- bounded timeout
- bounded output size
- 연결 종료와 제한된 재연결
- 인증·enable·timeout 실패 시 blind retry 금지

### 3. 명령 validation

- 한 줄 `show` 명령 허용
- newline 차단
- separator 차단
- configuration-changing command 차단
- 감시 요청 command count 상한

### 4. 모델 판정

`show version` 합성 출력에 대해 다음을 구분합니다.

- 등록 모델 정확히 1개
- 모델 미검출
- 여러 모델 token 동시 검출

raw detection output은 모델 식별 증거로 외부 저장하지 않습니다.

### 5. Viewer 상태 관리

- device/credential ownership
- stale client generation 결과 거부
- monitoring baseline/gap/event 상태
- bounded/coalesced event feed
- 저장소 손상·과대 파일 fail-closed
- Windows DPAPI 계약

### 6. Setup / rollback

- staging/backup/failed/journal 경로 제한
- manifest strict UTF-8
- declared size/SHA-256
- read-time mutation 감지
- Windows case-insensitive duplicate 감지
- 예상 top-level 파일 집합 검증
- rollback / quarantine 상태
- 설치 commit과 readiness warning 분리

### 7. Windows 패키지

`Windows CI`에서:

1. solution validation
2. unsigned POC package 생성
3. immutable candidate artifact 업로드
4. 별도 job에서 artifact 다시 다운로드
5. manifest / SHA-256 / package contract 검증
6. 추출된 실행 파일 smoke

를 수행합니다.

패키지를 **만든 workspace에서만 검사하는 것이 아니라 다운로드된 artifact를 다시 검증**하는 것이 핵심입니다.

## 검증 상태표

| 항목 | 자동 검증 | 현장 검증 필요 |
|---|---:|---:|
| 솔루션 restore/build/test | ✅ | - |
| synthetic Telnet IAC/Latin-1 | ✅ | - |
| 로그인/enable/prompt parsing | ✅ | ✅ firmware별 확인 |
| read-only command validation | ✅ | - |
| `show version` 모델 판정 | ✅ fixture | ✅ 실제 출력 |
| Viewer 상태·generation 처리 | ✅ | ✅ 장시간 운영 |
| Agent stateless 경계 | ✅ | ✅ 배포 환경 |
| Windows package contract | ✅ | - |
| 다운로드 후 SHA/manifest | ✅ | - |
| release executable smoke | ✅ | - |
| Windows Service 실제 설치/복구 | 일부 smoke | ✅ |
| EDR/백신 파일 잠금 | mock/회귀 | ✅ |
| Windows GPO/Firewall | 제한적 | ✅ |
| IES4224GP 실제 firmware | fixture 중심 | ✅ |
| IES4028XP 실제 firmware | fixture 중심 | ✅ |
| IES4226XP 실제 firmware | fixture 중심 | ✅ |

## 현장 검증 체크리스트

허가된 환경에서만 수행합니다.

### Agent

- [ ] Agent Setup 정상 설치
- [ ] Windows Service Running
- [ ] 서비스 재부팅 후 자동 시작
- [ ] Viewer→Agent HTTPS/18443 연결
- [ ] Firewall/GPO 적용 결과 확인
- [ ] uninstall/rollback 후 이전 상태 확인

### Switch

- [ ] 실제 모델 1개 정확히 식별
- [ ] 로그인 prompt 정상 처리
- [ ] enable 사용 장비 정상 처리
- [ ] `show` 결과 끝까지 수집
- [ ] 긴 출력에서 truncate/timeout 오판 없음
- [ ] 연결 단절 시 미완료 명령만 제한 재시도
- [ ] configuration command가 실행되지 않음

### Viewer

- [ ] 장비 등록/수정/삭제
- [ ] DPAPI 자격 증명 재시작 후 사용
- [ ] 수동 명령 결과 비저장 확인
- [ ] 주기 감시 상태 전이
- [ ] Agent 변경 후 stale 결과 미반영
- [ ] 장시간 이벤트에서 UI 응답성

## 공개 가능한 현장 검증 기록 형식

실제 결과를 저장소에 기록할 경우 운영정보를 제거합니다.

```text
검증일: YYYY-MM-DD
앱 버전: x.y.z
Windows: Windows 11 x64
장비 모델: IES4226XP
Firmware: 공개 가능한 범위만 기록
검증 대수: N

결과
- 모델 식별: PASS
- read-only 명령: PASS
- 결과 수집: PASS
- 주기 감시: PASS
- 설정 변경 명령: 0
```

실제 IP, hostname, MAC, username, 사이트명, raw output은 기록하지 않습니다.

## 검증의 의미

이 보고서의 자동 테스트는 **프로그램의 상태 머신·보안 경계·배포 계약이 코드 수준에서 재현 가능하게 검증됨**을 의미합니다.

반대로 실제 장비/사내 네트워크 검증은 firmware prompt, Telnet 구현 차이, EDR, GPO, Windows Firewall, 라우팅 등 코드 밖의 조건을 확인하기 위한 별도 단계입니다.
