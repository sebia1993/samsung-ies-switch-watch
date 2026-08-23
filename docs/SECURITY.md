# Samsung iES Switch Watch v0.12 보안 설계

## 보안 목표

- 원격 요청자가 페어링된 Viewer임을 확인합니다.
- Viewer가 연결한 HTTPS 종단이 페어링된 Agent인지 확인합니다.
- 장비 설정 변경 명령과 허용되지 않은 대상을 차단합니다.
- 자격 증명, API token, 명령과 원문 출력의 저장·로그 노출을 최소화합니다.
- 인증 자료가 손상되면 자동 우회하지 않고 fail-closed합니다.

## 위협 모델

대상은 신뢰된 Windows PC와 격리된 사설 관리망입니다. 다음을 고려합니다.

- 같은 사설망의 비인가 클라이언트가 Agent API를 호출
- DNS·라우팅 변경 또는 다른 TLS 종단으로의 연결
- 잘못된 인증서 자동 수락
- 저장 파일·요청·로그에서 token 또는 장비 암호 노출
- 변조된 API 요청으로 임의 대상·명령 실행
- 무한 대기·과도한 출력·동시 요청으로 인한 자원 고갈
- 패키지 또는 의존성 변조

로컬 Windows 관리자, 운영체제 자체의 완전한 침해와 Telnet 평문 구간의 도청을 애플리케이션만으로 막는 것은 범위 밖입니다.

## Agent 신원과 API token

- HTTPS 인증서와 instance ID는 `AgentIdentityStore.LoadOrCreate`로 생성·유지합니다.
- 인증서 PFX와 32-byte bearer token은 서로 다른 제품 entropy를 사용해 DPAPI LocalMachine으로 보호합니다.
- DataDirectory ACL은 SYSTEM, Administrators와 Agent 서비스 SID로 제한합니다.
- token 파일은 bounded read 후 정확히 32 bytes인지 검사합니다.
- 파일 누락 조합, 손상, DPAPI 실패, 인증서 만료 임박은 시작 실패입니다.
- token과 인증서가 바뀌면 기존 Viewer는 자동 신뢰하지 않으며 재페어링이 필요합니다.

## 페어링 코드

```text
SSW1.<base64url(SPKI 32 bytes || token 32 bytes)>
```

이 코드는 bearer token을 포함하는 비밀입니다.

- Setup이 로컬 DPAPI 자료를 읽어 화면에만 표시합니다.
- 사용자가 확인한 뒤에만 클립보드로 복사합니다.
- CLI 인수, 서비스 환경 설정, 앱 로그, 오류 응답, 지원 코드에 기록하지 않습니다.
- 메신저·메일·이슈·스크린샷으로 전달하지 않습니다.
- 노출이 의심되면 Agent 인증 자료를 안전한 절차로 재생성하고 모든 Viewer를 재페어링해야 합니다.

## Viewer 저장

- authority별 SPKI pin을 설정에 저장합니다.
- bearer token은 DPAPI CurrentUser로 보호한 ciphertext만 설정 파일에 저장합니다.
- 장비 자격 증명도 DPAPI CurrentUser로 보호합니다.
- 다른 PC·다른 Windows 사용자에게 복사한 자료는 복호화되지 않는 것이 정상입니다.
- pin과 token 중 하나라도 없거나 손상되면 연결 설정을 다시 요구합니다.

## TLS 검증

Viewer는 일반 CA 신뢰 대신 사전 페어링한 정확한 공개키를 사용합니다.

- 인증서 NotBefore/NotAfter 확인
- TLS Server Authentication EKU 확인
- RSA/ECDSA SubjectPublicKeyInfo SHA-256 계산
- 저장된 32-byte pin과 `CryptographicOperations.FixedTimeEquals` 비교
- API identity 본문의 SPKI와 같은 pin을 다시 비교
- 불일치 시 `AGENT_IDENTITY_CHANGED`로 차단

자체 서명이나 이름 불일치는 정확한 pin이 일치하는 경우에만 허용됩니다. TOFU, 무조건 수락, pin 자동 교체 또는 v4 fallback은 없습니다.

## API 인증·인가

- 무인증: `/health/live`, `/health/ready` 정확한 두 경로
- 인증 필수: identity, Telnet test/execute, v4 upgrade 응답을 포함한 나머지 모든 API
- 형식: 정확히 하나의 `Authorization: Bearer <43-char base64url>`
- decode 결과: 정확히 32 bytes
- 비교: 고정 시간
- 실패: 동일한 401 `AUTH_REQUIRED`, `Cache-Control: no-store`
- v4: 인증 후 426 `AGENT_API_UPGRADE_REQUIRED`

401 응답은 token 누락·형식 오류·불일치를 구분하지 않아 공격자에게 판별 정보를 주지 않습니다.

## 대상과 명령 인가

- 대상: canonical 사설 IPv4, TCP/23, loopback·link-local·multicast 제외
- 모델: 등록된 IES4224GP, IES4028XP, IES4226XP
- 명령: 한 줄 `show`, 128자 이하, 제어문자·separator·설정 흐름 차단
- Viewer 검증을 신뢰하지 않고 Agent가 다시 검사
- 요청당 최대 8개, 본문·출력·시간·동시 실행·빈도 상한

## 비밀과 로그

기록하지 않는 값:

- API token과 페어링 코드
- 장비 ID/PW/enable PW
- 장비 IP·hostname·MAC
- 명령 문자열과 Telnet 원문
- 인증서 개인 키

기록 가능한 값은 안정적인 오류 코드, 단계, 제한된 상태, 소요 시간과 출력 byte 수입니다. 예외 메시지나 요청 body를 그대로 기록하지 않습니다.

## Telnet 평문 위험

Agent→Switch는 Telnet/TCP 23이며 사용자 이름, 암호, 명령과 출력이 암호화되지 않습니다. 반드시 다음 조건을 적용해야 합니다.

- 인터넷·공용 Wi-Fi·사용자 VLAN에서 분리
- 허가된 사설 관리망과 방화벽/ACL 사용
- 관리망에서 패킷 캡처 권한 최소화
- 가능하면 장비의 SSH 등 암호화 관리 프로토콜로 전환

HTTPS와 API 인증은 Viewer→Agent만 보호하며 Telnet 구간을 암호화하지 않습니다.

## 공급망

- exact SDK, locked NuGet dependencies와 RID lock
- 전이 의존성 취약점 검사
- SPDX·CycloneDX SBOM
- package manifest, SHA-256, 다운로드 후 검증
- SHA로 고정한 GitHub Actions
- 공개 ZIP build provenance attestation
- 기존 release asset 덮어쓰기 금지

POC 산출물은 코드 서명되지 않을 수 있습니다. SHA-256은 파일 변경을 확인하지만 게시자 신원을 대신하지 않으므로 조직의 EDR·SmartScreen·WDAC 정책 승인이 필요합니다.

## 취약점 제보

실제 secret이나 운영정보를 공개 Issue에 첨부하지 마십시오. 비공개 제보 절차와 공개 금지 항목은 [저장소 보안 정책](../.github/SECURITY.md)을 따릅니다.
