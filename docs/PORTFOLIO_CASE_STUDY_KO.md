# 네트워크 엔지니어 포트폴리오: 읽기 전용 장비 점검과 실패 격리

[README로 돌아가기](../README.md)

이 프로젝트는 **관리망 접근 경계, 상태 조회의 안전한 실행, 프로토콜 실패 처리, 감시 부하 제어와 Windows 배포 복구**를 검토할 수 있는 POC입니다. 장비별 설정 변경 자동화나 현장 운영 성과는 이 공개 구현의 증거 범위에 포함하지 않습니다.

## 문제에서 구현까지

| 운영 상황 | 설계 판단과 비용 | 구현 근거 | 확인할 회귀 테스트 |
|---|---|---|---|
| 사용자가 입력한 조회 명령에 여러 명령이 섞임 | 한 줄 `show` 형식, 제어문자·연결 문법·길이 검사. CLI의 모든 의미를 해석하는 완전한 명령 allowlist는 아님 | [ReadOnlyQueryPolicy](../src/SamsungSwitchWatch.Core/Profiles/ReadOnlyQueryPolicy.cs) | [허용·차단·민감 조회 분류](../tests/SamsungSwitchWatch.Core.Tests/ReadOnlyQueryPolicyTests.cs) |
| 장비가 명령 도중 연결을 끊음 | 새 세션에서 미완료 명령만 제한적으로 재시도. 인증 실패·timeout에 무제한 재시도를 적용하지 않음 | [TelnetClient](../src/SamsungSwitchWatch.Core/Telnet/TelnetClient.cs) | [fault injection](../tests/SamsungSwitchWatch.Core.Tests/FaultInjectingTelnetTests.cs) |
| 감시 주기가 겹쳐 동일 장비에 동시 조회 | bounded queue·worker 2개·장비별 gate로 중복 억제. 과부하 시 오래된 queued work가 drop될 수 있으므로 drop도 관측 | [MonitoringCoordinator](../src/SamsungSwitchWatch.Viewer/Monitoring/MonitoringCoordinator.cs), [장비 gate](../src/SamsungSwitchWatch.Viewer/Monitoring/DeviceOperationGateRegistry.cs) | [중복·queue 상한·종료](../tests/SamsungSwitchWatch.Viewer.Tests/MonitoringCoordinatorTests.cs) |
| 연결 불가 장비를 매 주기 재시도 | 장비별 circuit breaker로 실패 누적 후 대기하고 half-open에서 복구 확인. 즉시 재검사보다 재시도 부하 감소를 우선 | [DeviceCircuitBreaker](../src/SamsungSwitchWatch.Viewer/Monitoring/DeviceCircuitBreaker.cs) | [circuit 상태 전이](../tests/SamsungSwitchWatch.Viewer.Tests/CircuitBreakerTests.cs) |
| Agent 주소·신원이 바뀌거나 token이 누락됨 | 사전 페어링한 인증서 pin과 bearer를 모두 검증. pin 변경 시 자동 수락하지 않아 재페어링이 필요 | [Agent 인증](../src/SamsungSwitchWatch.Agent/Security/AgentAuthentication.cs), [bearer middleware](../src/SamsungSwitchWatch.Agent/Api/BearerAuthenticationMiddleware.cs) | [Agent 인증](../tests/SamsungSwitchWatch.Agent.Tests/AgentAuthenticationTests.cs), [Viewer 연결](../tests/SamsungSwitchWatch.Viewer.Tests/ViewerConnectionTests.cs) |
| 업데이트 도중 파일 잠금·실패 | staging·journal·rollback으로 기존 설치 복구를 시도. 실제 EDR/GPO 파일 잠금은 현장 확인 필요 | [Agent 배포](../src/SamsungSwitchWatch.Agent.Setup/Deployment/AgentDeploymentOrchestrator.cs), [Viewer 배포](../src/SamsungSwitchWatch.Viewer.Setup/Deployment/ViewerDeploymentOrchestrator.cs) | [Agent 배포 상태](../tests/SamsungSwitchWatch.Agent.Setup.Tests/AgentDeploymentOrchestratorTests.cs), [Viewer 복구](../tests/SamsungSwitchWatch.Viewer.Setup.Tests/ViewerRecoveryTests.cs) |

## 합성 실패 사례로 설명하는 실행 정책

다음은 테스트로 검토할 수 있는 시나리오이며 실제 고객 장비 결과가 아닙니다.

```text
조회 A 완료 → 조회 B 중 세션 단절 → 새 세션 → B와 C 처리
```

[FaultInjectingTelnetTests](../tests/SamsungSwitchWatch.Core.Tests/FaultInjectingTelnetTests.cs)에서 완료한 A를 다시 실행하지 않는지, timeout·인증 실패를 재연결 대상으로 확대하지 않는지 확인합니다. 무한 출력은 시간·바이트 상한으로 중단해야 합니다. 단순 성공 케이스뿐 아니라 장비 응답이 늦거나 부분적으로 끊겼을 때 남기는 오류와 정리 상태가 검토 대상입니다.

감시 측면에서는 느린 장비가 있어도 전체 worker·queue 수가 상한 안에 있는지, 중지 후 gate가 남지 않는지 확인합니다. queue drop은 숨길 성공이 아니라 운영자가 확인할 과부하 증거입니다.

## 장비 없이 핵심 설계 재현

Windows x64 PowerShell과 [global.json](../global.json)에 고정된 .NET SDK가 필요합니다. NuGet 패키지 복원에는 네트워크 접근이 필요하지만 테스트와 harness는 합성 데이터를 사용하므로 실제 스위치·계정·Agent 서비스 설치가 필요하지 않습니다.

저장소 루트에서 핵심 테스트만 검토하려면:

```powershell
dotnet restore SamsungSwitchWatch.sln --locked-mode
dotnet build SamsungSwitchWatch.sln -c Release --no-restore
dotnet test tests/SamsungSwitchWatch.Core.Tests/SamsungSwitchWatch.Core.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~ReadOnlyQueryPolicyTests|FullyQualifiedName~FaultInjectingTelnetTests"
dotnet test tests/SamsungSwitchWatch.Viewer.Tests/SamsungSwitchWatch.Viewer.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~MonitoringCoordinatorTests|FullyQualifiedName~CircuitBreakerTests"
```

5분짜리 감시 안정성 smoke를 실행하려면:

```powershell
dotnet run --project tools/SamsungSwitchWatch.StabilityHarness/SamsungSwitchWatch.StabilityHarness.csproj -c Release --no-build --no-restore -- --profile smoke --devices 100 --seed 372811
```

위 solution에는 stability harness가 포함되어 있어 앞선 solution build 결과를 사용합니다. [harness 소스](../tools/SamsungSwitchWatch.StabilityHarness/Program.cs)에서 프로필·seed·판정 기준을 확인하고, 콘솔 JSON의 `Passed`, 최대 worker·queue, 종료 시 gate와 미처리 예외 값을 함께 검토합니다. 이 synthetic workload는 실제 Telnet 트래픽의 성능 측정이 아닙니다.

전체 Windows 자동 검증은 `./scripts/validate.ps1 -Configuration Release`와 [Windows CI 구성](../.github/workflows/windows-ci.yml)을 따릅니다. 이 로컬 안내는 명령·검증 범위를 설명하며 특정 실행의 통과를 미리 선언하지 않습니다.

## 검토자가 확인할 증거

[Windows CI](https://github.com/sebia1993/samsung-ies-switch-watch/actions/workflows/windows-ci.yml)에서 검토하는 commit SHA와 다음 job의 완료 여부를 확인합니다.

- `validate-and-package`: 솔루션, 배포 도우미, 의존성 검사와 POC ZIP 생성
- `stability-smoke`: 합성 100장비·5분 workload
- `verify-downloaded-artifact`: 업로드한 ZIP을 다시 받아 hash·package contract·추출 EXE smoke 검증

마지막 job만으로 장기 감시의 통과를 추정하지 않습니다. [Releases](https://github.com/sebia1993/samsung-ies-switch-watch/releases)의 Agent/Viewer 버전, tag 소스와 ZIP 무결성도 별도로 확인합니다. prerelease와 합성 모델 등록을 현장 지원 인증으로 해석하지 않습니다.

## 남는 제약과 다음 검증

Viewer→Agent의 HTTPS 보호가 Agent→Switch의 Telnet 평문까지 보호하지는 않습니다. 관리 VLAN·ACL과 장비 계정 권한은 별도 경계입니다. `show running-config`·`show startup-config`의 민감 분류가 모든 민감 출력 명령을 자동 탐지한다는 뜻도 아닙니다. 명령과 출력 취급은 [보안 설계](SECURITY.md)의 제한 안에서 검토해야 합니다.

다음 단계는 [현장 POC 체크리스트](FIELD_POC_CHECKLIST_KO.md)에 따라 허가된 환경에서 모델·펌웨어별 prompt/paging, 재부팅 후 서비스, 페어링, GPO/EDR와 장기 감시를 확인하는 것입니다. 실제 장비 접속·현장 통과·운영 시간 절감 수치를 이 문서에서 주장하지 않습니다.
