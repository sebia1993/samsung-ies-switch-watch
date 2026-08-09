# Agent Setup PR 1 Windows 검증 체크리스트

## 목적

이 체크리스트는 파일시스템·ACL 안정화 변경을 실제 Windows 환경에서 승인하기 위한 수용 기준이다. 자동 테스트 통과만으로 회사 GPO, EDR, Program Files ACL 또는 장시간 파일 잠금에서의 성공을 주장하지 않는다.

## 사전 조건

- 복원 가능한 Windows 11 시험 PC 또는 VM 스냅샷
- 관리자 PowerShell과 .NET SDK `10.0.302`
- 시험할 정확한 Git commit SHA 기록
- 회사 정책을 재현해야 하는 시나리오에서는 EDR/GPO 활성 상태 기록
- 실제 스위치, 회사 계정, 운영 IP 또는 자격 증명은 사용하지 않음

## 자동 검증

관리자 PowerShell에서 다음 명령이 모두 성공해야 한다.

```powershell
dotnet restore SamsungSwitchWatch.sln --locked-mode
dotnet test tests/SamsungSwitchWatch.Agent.Setup.Tests/SamsungSwitchWatch.Agent.Setup.Tests.csproj -c Release
.\scripts\test-agent-setup-retry-stability.ps1 -Configuration Release -Iterations 3 -NoBuild
.\scripts\validate.ps1 -Configuration Release
git diff --check
```

결과에는 실행한 commit SHA, Windows 빌드, EDR/GPO 상태, 전체 테스트 수와 실패 수를 함께 기록한다.

## 후보 패키지 검증

자동 검증이 끝난 깨끗한 commit에서 현재 개발 버전 패키지를 만들고, 생성 직후와 별도 디렉터리로 전달한 뒤의 계약을 모두 확인한다.

```powershell
.\scripts\build-release.ps1 -Version 0.11.4-poc -SkipTests
$sourceCommit = (& git rev-parse HEAD).Trim()
.\scripts\test-package-contract.ps1 `
    -ReleaseDirectory .\artifacts\release `
    -Version 0.11.4-poc `
    -ExpectedSourceCommit $sourceCommit
.\scripts\test-release-executable-smoke.ps1 `
    -ReleaseDirectory .\artifacts\release `
    -Version 0.11.4-poc
```

`-AllowDirty`로 만든 로컬 진단 패키지는 수용 증거로 사용하지 않는다. Agent ZIP의 Setup EXE smoke는 관리자 Windows 환경에서 수행하고, 실제 신규 설치·업데이트 시나리오는 아래 현장 시험으로 별도 판정한다.

## 현장 시나리오

| ID | 조건과 실행 | 합격 기준 |
| --- | --- | --- |
| FS-01 | 제품 폴더가 없는 상태에서 사전 점검 실행 | 설치·데이터·Operations 경로가 `PATHS_READY`로 통과하고 `.samsung-switch-watch-write-*.tmp` 또는 probe가 만든 빈 중간 폴더가 남지 않는다. |
| FS-02 | 신규 설치 | Agent 파일·데이터·journal이 고정 제품 경로에 생성되고 서비스가 정상 상태가 된다. |
| FS-03 | 동일 버전 재설치 | staging/backup/failed 잔여물이 없고 기존 설정·identity가 보존된다. |
| FS-04 | 이전 정상 버전에서 후보 버전으로 업데이트 | 기존 설치가 backup으로 이동한 뒤 후보 버전이 활성화되며, 성공 후 backup과 journal이 정리된다. |
| FS-05 | EDR 활성 상태에서 신규 설치·재설치·업데이트를 각각 3회 실행 | 공유·잠금 경합이 일시적이면 제한 재시도로 완료되고, 무한 대기나 임의 Access Denied 재시도가 없다. |
| FS-06 | 외부 프로세스로 기존 설치 파일을 잠근 뒤 업데이트하고 제한 시간 안에 잠금을 해제 | 최대 5회 범위 안에서 이동이 재시도되고 이후 정상 완료된다. |
| FS-07 | 잠금을 계속 유지한 채 업데이트 | 제한 재시도 후 실패하며 기존 설치, rollback 의존 파일과 journal 증거가 보존된다. |
| FS-08 | Access Denied 또는 명시적 deny ACL을 만든 상태에서 사전 점검 | `SETUP_PATH_NOT_WRITABLE` 또는 `SETUP_PATH_UNTRUSTED`로 즉시 중단되고 서비스·방화벽·설치 파일을 변경하지 않는다. |
| ACL-01 | 하위 파일에 Built-in Users 읽기 ACE와 서비스 과잉 권한을 추가한 뒤 업데이트 | 루트 상속 계약에 맞게 하위 ACL이 정규화되고 SYSTEM·Administrators·필요한 서비스 SID 외 ACE가 남지 않는다. |
| ACL-02 | 데이터 폴더의 `install-receipt.json`에 서비스 ACE를 추가한 뒤 업데이트 | receipt는 SYSTEM·Administrators 전용으로 복구되고 서비스 ACE가 제거된다. |
| ACL-03 | 제품 경로 또는 하위 항목을 junction/symlink로 구성 | `SETUP_PATH_UNTRUSTED`로 mutation 전에 거부되고 연결 대상의 내용과 ACL이 바뀌지 않는다. |
| ACL-04 | 하위 디렉터리의 SYSTEM·Administrators·서비스 권한은 유지하되 하위 상속 플래그만 제거한 뒤 업데이트 | 해당 디렉터리만 상속 가능한 정규 ACL로 복구되고, 이후 만든 하위 파일에도 계약 권한이 상속된다. |
| RB-01 | 활성화 직후 실패를 유도해 rollback 실행 | 신규 설치는 제거되고, 업데이트는 이전 파일·서비스 상태가 복원되며 모호한 source/destination 동시 존재를 성공으로 처리하지 않는다. |

## 증거 보존

각 실패 시나리오에서 다음 항목을 보존한다.

- Setup 화면의 안정된 오류 코드와 단계
- `agent-native-setup-transaction.json` 존재 여부와 stage
- install/staging/backup/failed 경로의 존재 여부만 기록한 목록
- Windows 이벤트 시간과 EDR 탐지 시간
- 민감한 경로, 계정, IP, 명령 또는 원시 장비 출력은 첨부하지 않음

모든 시나리오가 합격하기 전에는 PR 2 서비스·SCM 변경을 시작하지 않는다.
