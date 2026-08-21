# Samsung Switch Watch 운영 판단·실패 처리 기준

이 문서는 Samsung Switch Watch가 **어떤 상태를 장비 문제로 판단하고, 어떤 실패는 재시도하며, 어떤 데이터는 보존하지 않는지**를 설명합니다.

목표는 단순한 Telnet 명령 실행기가 아니라 운영자가 결과의 의미와 한계를 설명할 수 있는 점검 도구를 만드는 것입니다.

## 1. 상태 소유권

### Viewer가 소유하는 것

- Agent 연결 설정
- 관리 대상 장비 목록
- Windows DPAPI로 보호한 장비 자격 증명
- Agent가 식별한 canonical model
- 감시 일정과 baseline
- 감시 gap과 event history
- 현재 Viewer 상태

### Agent가 소유하지 않는 것

- 장비 인벤토리
- 장비 계정·암호·enable 암호
- 명령 원문
- 장비 raw output
- 감시 일정
- baseline
- event history

Agent는 요청이 들어올 때만 짧은 Telnet 세션을 만들고 요청 처리가 끝나면 연결을 종료합니다.

## 2. 장비 모델 판정

로그인 확인에서는 인증과 필요한 enable 전환 이후 읽기 전용 명령을 한 번 수행합니다.

```text
show version
```

등록된 모델 token 중 정확히 하나만 확인되면 canonical model을 Viewer로 반환합니다.

| 결과 | 판정 |
|---|---|
| 등록 모델 1개 식별 | 정상 |
| 등록 모델 없음 | `MODEL_NOT_DETECTED` |
| 둘 이상 식별 | `MODEL_AMBIGUOUS` |

모델을 추정하지 않습니다. 판별에 사용한 raw output은 Viewer 응답·설정·로그에 남기지 않습니다.

## 3. 명령 안전 경계

수동 입력은 다음 조건을 모두 만족해야 합니다.

- 한 줄
- `show`로 시작
- 줄바꿈 없음
- command separator 없음
- configuration command 아님

감시 요청은 Agent API 하나에서 검증된 명령을 최대 8개까지만 허용합니다.

`show running-config`도 조회 명령으로 취급할 수 있지만, 결과는 Viewer 메모리에서만 다루며 자동 저장·export 대상이 아닙니다.

## 4. Telnet 재시도

재시도는 모든 실패에 적용하지 않습니다.

### 자동 재시도하지 않는 실패

- 로그인 실패
- enable 실패
- 명령 timeout
- 명령 validation 실패

이 경우 같은 인증이나 명령을 무조건 다시 실행하면 잠금, 중복 작업, 상태 왜곡을 만들 수 있으므로 즉시 실패로 반환합니다.

### 제한적으로 재연결하는 경우

장비가 **명령 실행 도중 연결 자체를 종료한 경우**에만 새 Telnet 세션을 최대 한 번 만듭니다.

재연결 후에는 이미 완료된 명령을 반복하지 않고 **아직 완료되지 않은 명령만** 실행합니다.

```text
명령 A 성공
명령 B 수행 중 연결 종료
명령 C 미수행
        ↓
1회 재연결
        ↓
B / C만 처리
```

## 5. 무한 대기 방지

Telnet 단계는 하나의 큰 timeout으로 처리하지 않습니다.

- 연결/로그인
- enable
- 명령 수집

각 단계에 제한된 시간과 바이트 예산을 둡니다. Samsung 장비의 Latin-1 출력과 Telnet IAC negotiation은 유지하되 끝없는 출력이나 세션을 기다리지 않습니다.

## 6. 현재 상태와 통신 실패 구분

Viewer의 자동 감시는 다음을 구분해야 합니다.

- 현재 수집 성공
- 현재 수집 불가
- 다음 수집 대기
- 이전 상태만 존재하는 stale 상태
- 설정/저장소 문제로 감시 자체가 중지된 상태

통신이 한 번 실패했다고 이전 정상 결과를 현재 정상 상태처럼 유지하지 않습니다. 반대로 수집 실패 자체를 장비의 특정 포트/프로토콜 장애로 단정하지도 않습니다.

## 7. stale result 방지

Agent 주소를 변경하거나 새 HTTP client generation을 만들었을 때 이전 연결의 늦은 응답이 새 상태를 덮으면 안 됩니다.

Viewer는 요청 generation을 구분하고 현재 generation이 아닌 결과는 버립니다.

이 원칙은 다음과 같은 상황을 방지합니다.

```text
구 Agent 응답 지연
        ↓
운영자가 새 Agent로 전환
        ↓
새 Agent 결과 수신
        ↓
뒤늦게 구 Agent 결과 도착
        ↓
구 결과는 폐기
```

## 8. 이벤트 폭주와 UI 보호

동일한 상태 변화가 짧은 시간에 반복될 수 있으므로 Viewer 이벤트 전달은 제한된 queue를 사용하고 동일 변경을 합칠 수 있습니다.

목적은 UI 편의보다 **감시 도구 자체가 이벤트 폭주 때문에 멈추지 않게 하는 것**입니다.

## 9. 설치 성공과 readiness 분리

Agent 설치 단계와 실제 연결 준비 상태는 구분합니다.

파일과 Windows Service 설치가 정상 commit된 뒤 로컬 HTTPS/API/version 또는 Windows Firewall readiness 확인이 실패할 수 있습니다.

이 경우 이미 정상 설치된 Agent를 무조건 rollback하지 않습니다.

예:

```text
AGENT_LOCAL_CONNECTION_UNCONFIRMED
```

이 상태는 "설치 실패"가 아니라 운영자가 Viewer와 관리망 경로를 추가 확인해야 하는 경고입니다.

## 10. transactional update

Agent/Viewer Setup은 검증되지 않은 파일을 현재 설치 위치에 바로 덮어쓰지 않습니다.

기본 원칙:

```text
package 검증
   ↓
staging
   ↓
현재 상태 snapshot / backup
   ↓
새 버전 activation
   ↓
검증
   ↓
commit
```

commit 전 실패하면 검증된 이전 설치를 복원합니다. 경로 소유권, reparse point, manifest, size/hash 등 안전성을 증명할 수 없는 경우에는 임의 삭제나 이동보다 fail-closed를 우선합니다.

## 11. 네트워크 보안 한계

### Viewer → Agent

- HTTPS TCP/18443
- 전송 암호화 목적
- 현재 Viewer가 Agent endpoint identity를 강한 trust pin으로 검증하는 구조는 아님
- 별도 application authentication 없음

### Agent → Switch

- Telnet TCP/23
- 평문 프로토콜
- RFC1918 기반 신뢰된 관리망 전제

따라서 Agent는 사설 관리 네트워크 안에서만 사용해야 하며 인터넷/공용망 서비스로 해석하면 안 됩니다.

## 12. 개인정보·운영정보 비저장

공개 저장소와 자동 테스트에는 다음 정보를 넣지 않습니다.

- 실제 장비 IP/hostname
- 계정과 암호
- 실제 MAC
- 실제 명령 결과
- 조직명·사이트명
- 인증서/토큰

수동 조회 raw output은 프로그램 내부에서도 Viewer 메모리에서만 사용하고 장기 저장을 하지 않는 것이 기본 경계입니다.

## 13. 운영자가 결과를 해석할 때

Samsung Switch Watch는 다음을 자동으로 보장하지 않습니다.

- 모든 Samsung iES firmware 명령 호환성
- Telnet 구간의 기밀성
- 장비 내부 장애 원인의 자동 확정
- EDR/GPO/Firewall 환경의 완전한 호환성

자동화 결과는 **현재 조회 근거를 빠르게 수집하고 변화 여부를 놓치지 않기 위한 운영 보조 증거**로 사용해야 합니다.
