# Samsung Switch Watch 0.11.11-poc 릴리스 노트

## Viewer 설치 자동 격리와 복구

- Viewer Setup은 기존 Viewer 프로그램 폴더가 완전한 package 검증을 통과하지 못해도 바로
  삭제하지 않습니다. 새 package를 먼저 검증한 뒤 기존 폴더를 해당 transaction의 backup으로
  이동합니다.
- 새 Viewer가 commit되기 전 설치·자체점검·정상 실행 확인이 실패하거나 작업이 취소되면 기존
  폴더를 canonical Viewer 설치 경로로 되돌립니다.
- 새 Viewer가 정상 실행되어 commit된 뒤에만 기존 폴더를 제품 소유 최근 격리본으로 확정합니다.
  이전 격리본은 marker, exact path와 non-reparse 상태를 모두 확인한 경우에만 정리하여 최근
  1개를 유지합니다.
- 격리 회전이나 정리가 끝나지 않으면 새 Viewer는 유지하되 journal을 남겨 다음 복구에서
  계속합니다. 모호한 경로·marker·reparse 상태에서는 자료를 삭제하지 않고 fail-closed로
  중단합니다.
- 임의의 다운로드·압축 해제 폴더는 격리하거나 삭제하지 않습니다.

## Viewer journal 호환성

- 새 Viewer 설치 transaction은 journal format 3으로 기록합니다. format 3은 기존 설치가
  없음, 검증됨 또는 검증되지 않아 격리됨을 구분합니다.
- v0.11.11 Viewer Setup은 기존 format 2 journal을 원래 의미대로 읽고 복구합니다. format 2
  복구 중에는 기록을 format 3으로 바꾸지 않습니다.
- format 3 복구가 남아 있으면 v0.11.11 또는 더 최신 Viewer Setup으로 복구를 완료한 뒤에만
  이전 Viewer 버전으로 내리십시오. 구형 Setup은 이해하지 못하는 format 3을 변경하지 않고
  복구 필요 상태로 중단합니다.

## Viewer Setup 전용 SWS1 지원 코드

- 취소·중복 실행을 제외한 조치 가능한 Viewer Setup 설치·복구 실패와 복구 불가 검사 화면에
  `SWS1-XXXX-XXXX-XXXX-XXXX` 형식의 24자 지원 코드를 표시합니다. 코드는 읽기 전용으로
  선택하거나 복사할 수 있고 새 작업 시작, 성공 또는 취소 시 이전 값을 지웁니다.
- SWS1에는 제품 버전, 설치·복구 작업, 최초 실패·실패 단계, 기존 설치 분류와 package·journal·
  격리·원복·commit의 제한된 상태만 들어갑니다.
- 경로, 사용자명, 파일명·해시, transaction ID, 자격 증명, 장비 IP·정보, 명령·출력과 예외
  원문은 SWS1에 포함하지 않습니다.
- 끝의 CRC-8은 전화·메신저 전달 과정의 입력 오타를 확인하기 위한 값입니다. SWS1은 비밀값,
  인증 수단, 페어링 토큰, 인증서 지문 또는 접근 승인 값이 아닙니다.
- Agent Setup과 Viewer Agent 연결 실패에 사용하는 기존 SWD1의 비트 배치, 고정 코드와 해석은
  변경하지 않았습니다.

## 사용자 데이터와 외부 계약

- `%LOCALAPPDATA%\SamsungSwitchWatch`의 Agent 주소, 화면 설정, 장비 목록, DPAPI CurrentUser
  자격 증명, 감시 설정·기준선·이력은 격리·회전·삭제하지 않습니다.
- Agent API v4, Viewer 설정·장비·감시 저장 형식, Agent–Viewer 역할과 읽기 전용 `show` 명령
  정책은 변경하지 않았습니다.
- Agent와 Viewer는 같은 `0.11.11-poc` Release 조합으로 업데이트하는 것을 권장합니다.
- 공개 패키지는 Windows x64 self-contained이며 Python, 온라인 package 설치 또는 개발 도구가
  필요하지 않습니다.

## 검증 범위와 현장 확인

- 릴리스 전 회귀 테스트의 필수 대상은 SWS1 고정 벡터·CRC·민감정보 제외, format 2/3 journal
  복구, 격리·원복·회전의 중단 단계, 제한된 Move/Delete 재시도와 사용자 데이터 불변 계약입니다.
- 릴리스 게이트에서는 package manifest와 ZIP 파일 집합, packaged executable smoke, 사용자
  매뉴얼 render 및 정확한 두 ZIP Release 계약이 모두 통과해야 합니다.
- Mock·fixture·로컬 Windows 검증은 실제 사내 EDR·AppLocker·WDAC, 사용자별 ACL, 전원 중단,
  원격 방화벽·라우팅과 삼성 스위치 펌웨어를 증명하지 않습니다. 현장에서는 시험 PC 한 대에서
  설치·실패 복구·SWS1 표시를 확인한 뒤 단계적으로 확대하십시오.
- 이 POC는 Authenticode로 서명되지 않아 SmartScreen이나 사내 보안 제품이 경고할 수 있습니다.
  보안 정책을 우회하지 말고 조직의 반입 승인 절차를 따르십시오.

## 배포 파일

```text
SamsungSwitchWatch-Agent-0.11.11-poc-win-x64.zip
SamsungSwitchWatch-Viewer-0.11.11-poc-win-x64.zip
```

GitHub Release 사용자 정의 Assets에는 위 두 ZIP만 게시합니다. BUILD-MANIFEST, SBOM과
SHA256SUMS는 내부 Actions 검증 자료이며 별도 공개 Asset으로 추가하지 않습니다.
