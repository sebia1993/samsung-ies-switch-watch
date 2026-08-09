# Agent Setup Reliability 작업계획

## 목표

신규 기능보다 Agent 신규 설치·업데이트 성공률과 실패 후 복구 신뢰성을 우선한다. 이번 마일스톤은 설치기 파일시스템·ACL, Windows 서비스/SCM, commit·journal·복구, 현장 진단을 단계적으로 안정화한다.

## 진행 순서

```text
Phase 0 기준선·실패 매트릭스
  ↓
PR 1 파일시스템·ACL 안정화
  ↓ 실제 Windows 시험 PC 검증
PR 2 서비스·SCM 안정화
  ↓ 실제 Windows 시험 PC 검증
PR 3 commit·journal·복구 안정화
  ↓ 실제 Windows 시험 PC 검증
PR 4 현장 진단 trace·반복 검증
```

## PR 1 범위

- 설치·데이터·journal 경로의 실제 비파괴 write probe
- package → staging 복사·재해시와 install/staging/backup 디렉터리 이동의 bounded retry
- 공유 위반(`ERROR_SHARING_VIOLATION`)·잠금 위반(`ERROR_LOCK_VIOLATION`)만 재시도
- Access Denied, 보안 예외, 해시·경로·소유권·reparse point·journal 불일치는 재시도하지 않고 fail-closed
- ACL은 루트 상속을 기본으로 하고 계약 위반 하위 항목만 정규화
- 정상 설치와 rollback이 같은 파일 이동 재시도 정책을 사용
- 관련 단위·통합 테스트, native Setup PowerShell 소스 계약 테스트와 `git diff --check`

## PR 1 금지 범위

- Windows 서비스 권한·SCM 상태 머신 변경
- journal commit 의미 변경
- Viewer, Telnet, API, TLS, 네트워크 CIDR 변경
- 제품 버전·DOCX/PDF·대량 이미지 변경
- PR 2 이후 작업을 같은 브랜치에서 선행

## 완료 게이트

PR 1은 코드 테스트와 GitHub CI만으로 현장 성공을 주장하지 않는다. 승인된 Windows 시험 PC에서 신규 설치, 동일 버전 재설치, 업데이트, EDR 활성 상태, 파일 잠금, rollback을 확인한 뒤 다음 PR로 이동한다.

구체적인 실행·판정 기준은 `AGENT_SETUP_PR1_WINDOWS_VALIDATION_KO.md`를 따른다.

## 현재 기준선

- 기준 commit: `5439f44d90b1f82944788046b724c5b0ced628aa`
- 기준 제품 버전: `0.11.4-poc`
- PR 1 브랜치: `agent/setup-filesystem-reliability`
- Windows CI SDK: `.NET 10.0.302`
- 로컬 macOS 검증은 Windows Desktop testhost, 실제 NTFS ACL, SCM, EDR/GPO 결과를 대신하지 않는다.

## Phase 0 산출물

- `AGENT_SETUP_FAILURE_MATRIX_KO.md`: 파일·ACL·서비스·복구 실패 구간과 불변 조건
- 현재 설치 상태 머신의 mutation 전 경로 검증, staging, backup, activation, rollback 경계
- PR별 금지 범위와 실제 Windows 시험 PC 게이트
- 기준선 검증 결과와 실행하지 못한 Windows 전용 검증을 구분한 기록

## PR 1 구현 지도

| 관심사 | production 변경 지점 | 검증 지점 |
| --- | --- | --- |
| 비파괴 write probe | `SetupDiagnosticsService.ValidateDeploymentPathsForInstall`, `PhysicalSetupFileSystem.EnsureDirectoryWritable` | `ConfigurationAndInputTests`, `DeploymentSecurityTests` |
| package → staging 복사·재해시 | `AgentDeploymentOrchestrator.CopyFileWithRetryAsync`, `ComputeSha256WithRetryAsync`, `VerifyStagedRuntimeAsync` | `AgentDeploymentOrchestratorTests`, `test-agent-setup-retry-stability.ps1` |
| install/backup/staging 이동 | `AgentDeploymentOrchestrator.MoveDirectoryWithRetryAsync` | 정상 설치·activation·rollback 합성 실패 테스트 |
| transient 분류 | `PhysicalSetupFileSystem.IsTransientFileSystemException` | HResult 32/33, 일반 IO, Access Denied, SecurityException 계약 테스트 |
| 루트 상속·조건부 ACL 정규화 | `PhysicalSetupFileSystem.EnsureDirectoryAccess`, `NeedsAccessNormalization` | 합성 ACL 테스트, 실제 NTFS 통합 테스트, `test-agent-setup-filesystem-contract.ps1` |
| cleanup 제한 재시도 | `AgentDeploymentOrchestrator.TryDeleteEvidenceAsync` | journal·staging·backup·failed evidence cleanup 테스트 |

## PR 1 자동 검증 게이트

승인된 관리자 Windows PowerShell에서 다음 순서로 실행한다.

```powershell
dotnet restore SamsungSwitchWatch.sln --locked-mode
dotnet build SamsungSwitchWatch.sln -c Release --no-restore
dotnet test tests/SamsungSwitchWatch.Agent.Setup.Tests/SamsungSwitchWatch.Agent.Setup.Tests.csproj -c Release --no-build
.\scripts\test-agent-setup-retry-stability.ps1 -Configuration Release -Iterations 3 -NoBuild
.\scripts\test-agent-setup-filesystem-contract.ps1
.\scripts\validate.ps1 -Configuration Release
.\scripts\build-release.ps1 -Version 0.11.4-poc -SkipTests
$sourceCommit = (& git rev-parse HEAD).Trim()
.\scripts\test-package-contract.ps1 -ReleaseDirectory .\artifacts\release -Version 0.11.4-poc -ExpectedSourceCommit $sourceCommit
.\scripts\test-release-executable-smoke.ps1 -ReleaseDirectory .\artifacts\release -Version 0.11.4-poc
git diff --check
```

릴리스 패키지는 깨끗한 commit에서 생성한다. 로컬 진단을 위해 `-AllowDirty`로 만든 산출물은 제출 또는 릴리스 근거로 사용하지 않는다.

## PR 1 릴리스 차단 조건

- Agent Setup targeted test 또는 재시도 반복 테스트가 한 번이라도 실패
- 모든 `IOException` 또는 Access Denied를 재시도하는 코드가 존재
- source/destination 동시 존재처럼 모호한 이동 상태를 성공 처리
- package copy 후 해시 불일치를 재시도하거나 활성화 진행
- 기존 정상 설치·rollback이 서로 다른 이동 정책을 사용
- ACL 정규화가 reparse point를 따라가거나 예상 밖 SID를 보존
- Windows 시험 PC의 신규 설치·재설치·업데이트·지속 잠금·rollback 중 하나라도 실패
- package contract 또는 추출 실행 파일 smoke 실패
- 회사 GPO/EDR 결과가 없는데 현장 성공으로 보고

## PR 2 후속 작업 — 서비스·SCM 안정화

시작 조건은 PR 1 Windows 체크리스트 전체 통과와 PR 1 변경 고정이다. 브랜치는 `agent/setup-scm-reliability`를 사용하고 파일시스템 정책과 journal 의미를 다시 변경하지 않는다.

### 범위

- 서비스 기본 상태와 rollback 확장 정보를 분리 조회
- `SERVICE_CHANGE_CONFIG`, `READ_CONTROL`, `WRITE_DAC` 권한 요청 분리
- 신규 서비스 DACL 적용 실패는 설치 실패
- 기존 안전 DACL을 다시 쓰지 못하면 기존 DACL 유지 가능 여부를 명시적으로 판정
- 서비스 설명과 자동 복구 정책을 핵심 설치 단계에서 분리
- `CheckPoint`, `WaitHint`, Win32 종료 코드를 이용한 시작·정지 진행 판정
- 시작 정체와 timeout을 구분하는 안전한 세부 원인 코드

### PR 2 시작 프롬프트

```text
PR 1의 Windows 시험 PC 결과와 Agent Setup 테스트가 모두 통과한 commit에서
agent/setup-scm-reliability 브랜치를 만드세요.
WindowsServiceManager의 서비스 조회·구성·DACL 권한을 분리하고,
CheckPoint/WaitHint/Win32ExitCode 기반 상태 판정을 추가하세요.
파일시스템 재시도 정책, journal commit 의미, Viewer/Telnet/API/TLS/네트워크 정책은 변경하지 마세요.
신규 서비스와 기존 서비스의 DACL 실패 정책을 각각 테스트하고 실제 Windows 시험 PC에서 검증하세요.
```

## PR 3 후속 작업 — commit·journal·복구 안정화

시작 조건은 PR 2 Windows 검증 통과다. 브랜치는 `agent/setup-transaction-recovery`를 사용하고 PR 1 파일 정책과 PR 2 SCM 권한 구조를 다시 섞어 변경하지 않는다.

### 범위

- 설치 commit 최소 성공 조건을 실행 파일 해시, 서비스 존재·구성·RUNNING, 필수 SID/DACL, journal 기록으로 고정
- commit 이후 HTTPS·방화벽·정리 실패를 설치 rollback이 아닌 경고 또는 cleanup-pending으로 분리
- `committed` journal은 권위 상태를 확인한 뒤 cleanup-only 처리
- 모호하거나 변조된 journal은 계속 fail-closed
- cleanup 후 자동 재설치하지 않고 사용자 작업 가능 상태만 복구
- backup·staging·journal 정리 실패가 정상 Agent 설치를 되돌리지 않도록 상태 경계 검증

### PR 3 시작 프롬프트

```text
PR 2의 Windows 검증이 통과한 commit에서 agent/setup-transaction-recovery 브랜치를 만드세요.
commit의 최소 권위 상태와 committed journal의 cleanup-only 규칙을 먼저 문서화하고 테스트로 고정하세요.
모호한 journal, 경로, backup/staging 상태는 fail-closed로 유지하세요.
Viewer/Telnet/API/TLS/네트워크 정책과 PR 1 재시도 분류, PR 2 SCM 권한 구조는 변경하지 마세요.
```

## PR 4 후속 작업 — 현장 진단·반복 검증

시작 조건은 PR 3 Windows 검증 통과다. 브랜치는 `agent/setup-field-evidence`를 사용한다.

### 범위

- 관리자 전용 `setup-trace.jsonl`, 최대 1 MiB, 백업 1개
- 설치 단계, 안전 코드, 숫자 Win32 코드, 소요 시간만 기록
- 실패 빈도 요약 PowerShell과 설치 전·후 상태 canary
- SWD1 및 기존 익명 진단 형식 호환성 유지
- 실제 Windows 11/GPO/EDR 환경에서 신규 설치·동일 버전 재설치·업데이트·재부팅·자동 복구·제거 후 재설치 반복

### 기록 금지 정보

- Viewer 또는 스위치 IP
- PC명, 사용자명, 계정, 비밀번호
- 인증서 지문
- 절대 경로, 예외 원문, stack trace
- 방화벽 원문, 명령, 장비 출력

### PR 4 시작 프롬프트

```text
PR 3의 Windows 검증이 통과한 commit에서 agent/setup-field-evidence 브랜치를 만드세요.
setup trace의 허용 필드와 금지 필드를 먼저 테스트로 고정한 뒤 bounded JSONL 기록과 요약 도구를 구현하세요.
민감 정보, 절대 경로, 예외 원문, 장비 출력은 기록하지 마세요.
기존 SWD1과 익명 현장 진단 계약을 깨지 말고 실제 시험 PC 반복 결과를 제출하세요.
```

## 각 PR 제출 형식

1. 근본 원인과 이번 PR에서 다룬 실패 경로
2. 파일별 변경과 범위 밖 변경이 없다는 확인
3. targeted, 반복, 전체 validate, package, smoke 결과
4. 보안·fail-closed 계약 검토
5. 실제 Windows 시험 PC 결과와 보존한 안전 증거
6. 남은 위험과 다음 PR 시작 가능 여부

자동 merge하지 않는다. 각 PR은 해당 단계의 자동 검증과 실제 Windows 수용 검증이 모두 끝난 뒤에만 다음 브랜치의 기준 commit이 될 수 있다.
