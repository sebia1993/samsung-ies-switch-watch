# Samsung iES Switch Watch v0.12 현장 POC 체크리스트

이 체크리스트는 허가된 실제 환경에서만 사용합니다. 합성/CI 결과를 현장 PASS로 옮겨 적지 마십시오.

## 반입

- [ ] Agent/Viewer가 같은 Release
- [ ] 두 ZIP의 SHA-256이 Release 설명과 일치
- [ ] 조직의 코드 미서명·EDR·SmartScreen 승인
- [ ] 실제 IP·계정·MAC·raw output 공개 금지 확인

## 네트워크

- [ ] Viewer→Agent HTTPS/TCP 18443
- [ ] Agent→Switch Telnet/TCP 23
- [ ] Agent와 스위치가 격리된 사설 관리망에 있음
- [ ] 사용자 VLAN·공용 Wi-Fi·인터넷에서 Agent 접근 불가
- [ ] Telnet packet capture 권한과 관리망 ACL 최소화

## Agent

- [ ] Setup 설치와 UAC 정상
- [ ] `SamsungSwitchWatchAgent` 서비스 Running
- [ ] 재부팅 후 자동 시작
- [ ] 보호 디렉터리 ACL 확인
- [ ] Setup이 `SSW1` 페어링 코드를 명시적 조작 후에만 표시
- [ ] 코드·token이 로그·진단에 없음

## 페어링·API

- [ ] Viewer에 Agent 주소와 새 코드 입력
- [ ] API v5 identity 연결 성공
- [ ] token 누락·오류 시 401
- [ ] 다른 인증서/SPKI 시 연결 차단
- [ ] v4 요청 426
- [ ] health 응답에 Agent ID·pin·token·장비 정보 없음
- [ ] 재설치가 아닌 정상 재시작 후 기존 pin 연결 유지

## 장비

- [ ] 조회 전용 계정 사용
- [ ] 실제 모델·펌웨어 기록을 비식별화
- [ ] 로그인/선택적 enable
- [ ] `show version` 모델 단일 식별
- [ ] 한 줄 `show` 정상
- [ ] 줄바꿈·separator·설정 명령 차단
- [ ] 긴 출력·timeout·연결 단절 처리
- [ ] 설정 변경 0건

## Viewer

- [ ] 장비 등록·수정·삭제
- [ ] 재시작 후 DPAPI 장비 자격 증명 사용
- [ ] 수동 원문 결과 비저장
- [ ] baseline·gap·event 전이
- [ ] Agent 변경 후 stale 결과 미반영
- [ ] 장시간 다수 장비에서 UI 응답성

## 업데이트·복구

- [ ] 이전 장비·감시 데이터 보존
- [ ] 업데이트 후 재페어링 요구
- [ ] 새 코드로만 연결 성공
- [ ] 설치 중 실패 시 기존 프로그램 복구
- [ ] journal이 남으면 설치 차단과 복구 안내
- [ ] GPO/EDR 파일 잠금 시 안전한 실패

## 결과 기록

```text
검증일: YYYY-MM-DD
앱 버전: x.y.z
Windows: 공개 가능한 빌드
장비 모델/펌웨어: 공개 가능한 범위
검증 대수: N

페어링: PASS/FAIL
모델 식별: PASS/FAIL
읽기 전용 명령: PASS/FAIL
감시: PASS/FAIL
업데이트/복구: PASS/FAIL
설정 변경: 0
```

실제 주소, hostname, MAC, username, 사이트명, raw output과 pairing code는 기록하지 않습니다.
