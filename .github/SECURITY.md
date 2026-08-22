# 보안 및 민감정보 처리

Samsung Switch Watch는 네트워크 장비 자격 증명과 관리망 정보를 다룰 수 있는 운영 도구입니다.

공개 저장소에는 실제 운영정보를 올리지 않습니다.

## 공개하면 안 되는 정보

- 실제 스위치/Agent/Viewer IP와 hostname
- 사내 subnet, VLAN, 관리망 구조
- 장비 username/password/enable password
- 실제 MAC 주소
- 실제 `show` 출력
- 인증서 private key, token, secret
- 실제 Windows 사용자/PC/도메인 이름
- 사이트명, 조직명, 고객명
- 사내 배포 경로 또는 보안 제품 정책 원문

문제 재현에는 RFC 5737 문서용 IP, mock Telnet server, 합성 fixture를 사용하십시오.

## 장비 안전 경계

- 수동 명령은 한 줄 `show` 명령만 허용합니다.
- configuration-changing command를 추가하지 않습니다.
- Agent는 장비 credential/inventory/result history를 저장하지 않습니다.
- raw command output은 Viewer 메모리에서만 사용하고 저장·export하지 않습니다.
- 자동 테스트와 CI는 실제 스위치에 접속하지 않습니다.

## 네트워크 경계

Agent는 trusted private management network용입니다.

- Viewer→Agent: HTTPS TCP/18443
- Agent→Switch: Telnet TCP/23
- Telnet은 암호화되지 않습니다.
- Agent API에는 별도 application authentication이 없습니다.
- 현재 TLS는 transport encryption 목적이며 Agent endpoint identity를 강하게 인증하는 trust pin 구조가 아닙니다.

Agent를 인터넷, 공용 Wi-Fi, 사용자 VLAN에 노출하지 마십시오.

## 취약점 또는 민감정보를 발견한 경우

실제 credential, 내부 주소, raw device output이 포함된 내용을 공개 Issue나 PR에 붙이지 마십시오.

저장소 소유자에게 비공개 채널로 다음 정보만 최소화해 전달합니다.

- 영향받는 버전
- 재현에 필요한 비식별 단계
- 예상 동작과 실제 동작
- secret이나 운영정보를 제거한 오류 코드

이미 secret이나 실제 운영정보가 공개 commit에 들어갔다면 단순 파일 삭제만으로 충분하다고 가정하지 말고, 해당 credential/token을 먼저 폐기·교체한 뒤 Git 기록과 공개 artifact 노출 범위를 별도로 확인해야 합니다.

## 보안 관련 변경 원칙

- command allowlist를 느슨하게 만드는 변경은 안전 변경으로 취급하지 않습니다.
- timeout/output limit을 제거하지 않습니다.
- credential/raw output logging을 추가하지 않습니다.
- Agent statefulness를 확대하는 변경은 명시적 threat review 없이 진행하지 않습니다.
- package manifest/hash/rollback fail-closed 경계를 약화시키지 않습니다.
