# 보안 및 민감정보 처리 정책

Samsung iES Switch Watch는 네트워크 장비 자격 증명과 관리망 정보를 다루는 운영 도구입니다.

## 공개하면 안 되는 정보

- 실제 스위치·Agent·Viewer IP, hostname, VLAN과 사내 subnet
- 장비 ID/PW/enable PW
- `SSW1` 페어링 코드와 API bearer token
- 인증서 private key 또는 DPAPI 보호 파일
- 실제 MAC, 사이트명, 조직명과 사용자·PC·도메인 이름
- 실제 `show` 출력, log, packet capture와 사내 보안정책 원문

재현에는 RFC 5737 문서용 IP, synthetic Telnet server와 비식별 fixture를 사용하십시오.

## 제품 보안 경계

- Viewer→Agent: HTTPS/TCP 18443 + 사전 페어링 SPKI pin + 32-byte bearer
- Agent→Switch: Telnet/TCP 23 평문
- Agent 경로: health를 포함해 모두 32-byte bearer 인증 필수
- 원격 기능: 인증 필수 API v5
- 수동 명령: 한 줄 `show`만 허용
- 장비 credential·inventory·결과 history: Agent 비저장

Telnet은 암호화되지 않습니다. Agent를 인터넷, 공용 Wi-Fi나 사용자 VLAN에 노출하지 말고 격리된 사설 관리망과 방화벽/ACL을 사용하십시오.

## 취약점 제보

공개 Issue나 PR에 실제 secret·운영정보·원문 출력을 첨부하지 마십시오. GitHub의 비공개 보안 제보 기능을 사용할 수 있으면 우선 사용하고, 불가능하면 저장소 소유자에게 노출을 최소화한 비공개 채널로 연락하십시오.

다음 정보만 비식별화해 전달합니다.

- 영향받는 버전
- 최소 재현 단계
- 예상 동작과 실제 동작
- 안정적인 오류 코드
- synthetic fixture로 재현 가능한지 여부

## 이미 노출된 경우

파일을 지우는 것만으로 충분하다고 가정하지 마십시오.

1. 장비 자격 증명·token을 먼저 폐기하거나 교체합니다.
2. 인증서 신원이 노출·변조된 경우 Agent 자료를 안전하게 재생성합니다.
3. 모든 Viewer를 새 코드로 재페어링합니다.
4. Git 기록, Actions artifact, Release와 cache의 노출 범위를 확인합니다.
5. 조직 보안 담당자에게 보고합니다.

## 보안 변경 원칙

- 인증서 자동 수락, TOFU, token 없는 API와 구형 API fallback을 추가하지 않습니다.
- command allowlist, 대상·시간·출력·동시 실행 상한을 약화시키지 않습니다.
- credential, pairing code, 명령과 raw output logging을 추가하지 않습니다.
- package manifest, lock, SBOM, hash, rollback의 fail-closed 경계를 유지합니다.

구현 상세는 [보안 설계](../docs/SECURITY.md)를 참고하십시오.
