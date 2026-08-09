# Agent Setup 실패 매트릭스

이 문서는 `main` 기준 Agent 설치 실패를 파일·서비스·복구 단계별로 분류하고, PR 1에서 검증할 불변 조건을 기록한다. 실제 Windows 시험 PC의 결과가 추가되기 전까지 현장 성공으로 해석하지 않는다.

| 단계 | 관찰 증상 | 우선 확인 | PR 1 불변 조건 |
| --- | --- | --- | --- |
| 경로 사전 확인 | `SETUP_PATH_NOT_WRITABLE` | Program Files/ProgramData ACL, EDR, GPO | 설치·데이터·journal 경로를 상위 폴더 존재만으로 통과하지 않고 실제 임시 파일 write/flush/delete로 확인하며 probe가 만든 빈 경로 계층은 정리한다. |
| 패키지 staging | 파일 복사·재해시 중 `IOException` | EDR 파일 잠금, 대상 파일 상태 | 복사와 source/destination/staging 해시 읽기의 공유·잠금 오류만 최대 5회 재시도하고, 대상이 생겼으나 해시가 다르면 즉시 실패한다. |
| 설치 백업 이동 | 기존 Agent 폴더 이동 실패 | Agent 프로세스 잔존, 잠금 | 정상 설치도 rollback과 동일한 bounded move 정책을 사용한다. |
| 새 버전 활성화 | staging → install 실패 | 대상 폴더 존재, reparse point | source/destination topology가 모호하면 복구하지 않고 fail-closed 한다. |
| ACL 적용 | `Access Denied`, `PathUntrusted` | 소유자, 명시적 쓰기 ACE, 상속 플래그, reparse point | Access Denied·보안 예외·소유권 불일치는 재시도하지 않는다. 루트 ACL 상속을 먼저 설정하고 권한 또는 하위 상속 계약을 위반한 항목만 정규화한다. |
| rollback 파일 복구 | `ROLLBACK_FILE_RESTORE_FAILED` | backup/staging/failed topology | 백업·설치 폴더가 동시에 존재하는 모호한 상태를 성공으로 취급하지 않는다. |
| 정리 | cleanup failure | EDR 잠금, ACL, 잔존 파일 | 공유·잠금 오류만 제한 재시도하고, 정리 실패 시 journal/evidence를 보존한다. |

## 검증 명령

Windows 개발 환경에서 다음을 순서대로 실행한다.

```powershell
dotnet restore SamsungSwitchWatch.sln --locked-mode
dotnet test tests/SamsungSwitchWatch.Agent.Setup.Tests/SamsungSwitchWatch.Agent.Setup.Tests.csproj -c Release
.\scripts\test-agent-setup-retry-stability.ps1 -Configuration Release -Iterations 3 -NoBuild
.\scripts\test-agent-setup-filesystem-contract.ps1
.\scripts\validate.ps1 -Configuration Release
git diff --check
```

현재 PR 1의 자동 테스트는 합성 파일시스템과 예외를 사용한다. 실제 Windows 서비스 ACL, 회사 GPO/EDR, Program Files 권한, 현장 스위치 네트워크는 별도 시험 PC에서 확인해야 한다.
