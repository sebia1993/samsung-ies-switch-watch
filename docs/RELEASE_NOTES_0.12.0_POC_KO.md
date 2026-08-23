# Samsung iES Switch Watch 0.12.0-poc

이번 시험판은 Viewer→Agent 연결을 **수동 페어링, 영구 인증서 공개키 pin과 bearer 인증**으로 전환하는 보안 릴리스입니다.

## 주요 변경

- Agent가 기존 `AgentIdentityStore.LoadOrCreate`를 실제 서비스 TLS 신원에 사용합니다.
- CSPRNG 32-byte API token을 DPAPI LocalMachine으로 보호해 유지합니다.
- Setup이 `SSW1.<base64url(SPKI || token)>` 수동 페어링 코드를 표시합니다.
- Viewer는 SPKI pin과 DPAPI CurrentUser로 보호한 token을 authority별로 저장합니다.
- 모든 원격 기능은 인증 필수 API v5로 이동했습니다.
- 무인증 경로는 최소 `/health/live`, `/health/ready`만 남겼습니다.
- API v4는 인증되지 않은 요청을 401로 거부하고 인증된 요청도 426 upgrade로 차단합니다.
- token·SPKI는 고정 시간 비교하며 인증서 유효기간과 Server Authentication EKU를 확인합니다.
- pin 불일치, token 손상, 구형 API에 자동 fallback하지 않습니다.
- 인증·인증서·마이그레이션·비밀 비노출 회귀 테스트를 추가했습니다.

## 마이그레이션

기존 장비 목록, 장비 자격 증명과 감시 이력은 유지됩니다. 이전 무인증 Agent 연결은 호환되지 않습니다.

1. Agent와 Viewer를 같은 새 버전으로 업데이트합니다.
2. Agent Setup에서 새 페어링 코드를 확인합니다.
3. Viewer 연결 설정에 Agent 주소와 코드를 입력합니다.
4. 연결 시험을 통과한 뒤 저장합니다.

재페어링을 생략하는 insecure compatibility mode는 없습니다.

## 배포·공급망

- exact SDK와 일반/RID NuGet lock
- 전이 의존성 취약점 검사
- SPDX·CycloneDX SBOM
- manifest·SHA-256과 다운로드 후 package/EXE smoke
- SHA 고정 GitHub Actions와 build provenance attestation
- MIT License

## 보안 한계

Agent→Switch는 여전히 Telnet/TCP 23 평문입니다. 신뢰된 사설 관리망에서만 사용하고 가능하면 SSH 등 암호화 관리 채널로 전환하십시오.

POC ZIP은 코드 서명되지 않을 수 있습니다. 조직의 EDR·SmartScreen·WDAC 승인 절차를 따르십시오.

## 검증 증거

공개 검증은 합성 Telnet 서버, 비식별 fixture, Windows CI와 self-contained package smoke입니다. 실제 Samsung 장비나 사내망에서의 현장 성과를 주장하지 않습니다.
