# 한계와 운영 전제

## Telnet

Agent→Switch는 Telnet/TCP 23 평문입니다. 장비 계정, 암호, 명령과 출력이 네트워크에서 암호화되지 않습니다. HTTPS·SPKI pin·API bearer는 Viewer→Agent만 보호합니다.

따라서 격리된 사설 관리망, VLAN/ACL, 최소 권한 계정과 제한된 패킷 캡처 권한이 필수입니다. SSH를 지원하는 장비는 암호화 채널 전환을 우선 검토하십시오.

## 제품 범위

- Windows x64 전용 POC
- 등록 모델 3개
- 설정 변경 자동화 없음
- 인터넷 공개형 API 아님
- Viewer가 실행 중일 때만 주기 감시
- 원문 결과 장기 저장·export 없음
- 다중 사용자 RBAC·중앙 IdP 연동 없음

## 검증 범위

공개 증거는 합성 Telnet, 비식별 fixture, Windows CI와 package smoke입니다. 실제 모델별 펌웨어, 회사망 latency, GPO, EDR, 방화벽과 24시간 운영은 확인되지 않았습니다.

## 배포

POC ZIP은 코드 서명되지 않을 수 있습니다. SHA-256과 provenance는 파일·빌드 출처 검증을 돕지만 조직의 실행 승인과 악성코드 검사를 대체하지 않습니다.

## 복구

인증서·token 손상 또는 pin 불일치는 자동 회전하지 않습니다. Agent 상태를 먼저 확인하고 안전한 절차로 인증 자료를 재생성한 뒤 Viewer를 모두 재페어링해야 합니다.
