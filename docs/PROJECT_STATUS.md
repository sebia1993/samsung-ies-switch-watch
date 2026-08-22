# Samsung Switch Watch 프로젝트 상태

## 현재 단계

- 버전: `0.11.11-poc`
- 플랫폼: Windows x64
- 구현 언어: C# / WPF / .NET
- 용도: Samsung iES 스위치 읽기 전용 원격 조회·주기 감시 POC

## 현재 구현된 핵심 흐름

```text
Viewer
  ↓ HTTPS/18443
Agent Windows Service
  ↓ Telnet/23
Samsung iES Switch
```

### Viewer

- Agent 연결 관리
- 장비 목록 관리
- Windows DPAPI CurrentUser 기반 자격 증명 보호
- `show version` 기반 모델 자동 표시
- 수동 한 줄 `show` 조회
- 감시 일정 / baseline / gap / event 관리
- mini window / alert flow
- 상태 저장 파일의 bounded read / fail-closed 처리

### Agent

- Windows Service only
- stateless HTTPS→Telnet execution
- RFC1918 Viewer/target 경계
- 요청마다 새 Telnet session
- login / enable / command 수집 시간·바이트 상한
- Telnet IAC / Latin-1 처리
- 명령 수행 중 연결 종료 시 미완료 명령만 최대 1회 재연결

### Setup / 배포

- Agent Setup
- Viewer Setup
- transactional staging / backup / rollback
- manifest / size / SHA-256 검증
- immutable GitHub Release pipeline
- Windows self-contained package
- 다운로드 artifact 재검증과 executable smoke

## 등록된 지원 모델

- IES4224GP
- IES4028XP
- IES4226XP

등록 모델은 코드가 인식할 수 있는 범위를 의미하며 모든 firmware에 대한 현장 호환성을 의미하지 않습니다.

## 현재 보안 경계

### 보장하려는 것

- 장비 configuration command 차단
- 한 줄 `show` command allowlist
- Agent에 credential/inventory/result history 비저장
- Viewer credential DPAPI 보호
- 공개 test/fixture에서 실제 회사 데이터 배제
- 패키지 manifest/hash 검증
- 설치 경로·rollback fail-closed

### 현재 한계

- Agent→Switch는 Telnet 평문
- Viewer→Agent HTTPS는 transport encryption 중심
- Agent endpoint identity pinning 없음
- Agent API application authentication 없음
- trusted private management network 전제
- 코드서명 미적용 POC package

## 자동 검증 상태

`Windows CI`에서 다음을 수행합니다.

- locked restore
- Release build
- unit/integration tests
- deployment helper validation
- dependency advisory check
- Agent/Viewer POC package
- artifact upload/download
- package contract
- manifest / SHA-256
- extracted executable smoke

## 현장 검증이 별도로 필요한 영역

- Samsung 모델별 firmware prompt/output
- enable 동작 차이
- 실제 Windows Service install/update/rollback 전체 흐름
- 조직 GPO/Windows Firewall
- 백신/EDR 잠금
- 실제 관리망 Telnet latency/disconnect 특성
- 장시간 다수 장비 감시

## 문서 구조

README는 현재 동작과 운영 판단을 요약합니다.

세부 문서는 다음 역할로 분리합니다.

- `ARCHITECTURE.md`: 상세 컴포넌트와 배포 구조
- `OPERATING_LOGIC.md`: 재시도·모델판정·상태·보안 경계
- `VALIDATION_REPORT.md`: 자동/현장 검증 구분
- `INSTALL_KO.md`: 설치와 복구
- `FIELD_POC_CHECKLIST_KO.md`: 현장 체크
- `RELEASE_NOTES_*`: 과거 버전별 변경 기록

## 다음 기술 과제

우선순위는 기능 수 증가보다 실제 환경 증거와 운영 안정성입니다.

1. 등록 Samsung 모델별 firmware 현장 검증 증거 축적
2. 실제 EDR/GPO 환경의 install/update/rollback 검증
3. 장시간 다수 장비 monitoring에서 event/current-state 품질 확인
4. Telnet legacy 환경을 유지해야 하는 이유와 네트워크 분리 조건 지속 문서화
5. 향후 프로토콜 전환이 가능한 장비는 암호화된 관리 채널 검토

새 기능은 기존 읽기 전용 경계와 Viewer/Agent 책임 분리를 약화시키지 않는 범위에서만 추가합니다.
