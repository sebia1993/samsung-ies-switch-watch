# Samsung iES Switch Watch 설치·마이그레이션 가이드

## 1. 받을 파일

공식 GitHub `v0.12.0-poc` Release의 Assets에서 다음 두 파일을 받습니다.

- `SamsungSwitchWatch-Agent-0.12.0-poc-win-x64.zip`
- `SamsungSwitchWatch-Viewer-0.12.0-poc-win-x64.zip`

두 패키지는 Windows x64 self-contained 시험판입니다. 별도 .NET Runtime은 필요하지 않지만 코드 서명되지 않았을 수 있으므로 조직의 SmartScreen·EDR·AppLocker·WDAC 승인 절차를 먼저 따르십시오.

Release 설명에 표시된 SHA-256과 내려받은 ZIP의 hash를 비교합니다.

```powershell
Get-FileHash .\SamsungSwitchWatch-Agent-0.12.0-poc-win-x64.zip -Algorithm SHA256
Get-FileHash .\SamsungSwitchWatch-Viewer-0.12.0-poc-win-x64.zip -Algorithm SHA256
```

## 2. 설치 전 조건

- Agent PC: 스위치 관리망과 Telnet/TCP 23 연결 가능
- Viewer PC: Agent PC의 HTTPS/TCP 18443 연결 가능
- 두 PC: 신뢰된 사설망, 정확한 시스템 시간
- 설치 사용자: Agent PC 로컬 관리자
- 스위치: 별도로 발급된 조회 전용 계정 권장

Agent→Switch는 Telnet 평문입니다. 인터넷, 공용 Wi-Fi와 사용자 VLAN에 배치하지 마십시오.

## 3. Agent 설치

1. Agent ZIP을 새 로컬 폴더에 풉니다.
2. `SamsungSwitchWatch.Agent.Setup.exe`를 실행하고 UAC를 승인합니다.
3. `설치 / 업데이트`를 선택합니다.
4. 서비스·파일 설치 성공과 HTTPS/방화벽 확인 결과를 구분해 읽습니다.
5. `페어링 코드 보기`를 선택하고 경고를 확인합니다.
6. `SSW1.`로 시작하는 코드를 Viewer 연결 설정에 직접 전달합니다.

Agent는 `SamsungSwitchWatchAgent` Windows Service로 실행됩니다. Setup 창을 계속 열어 둘 필요는 없습니다.

페어링 코드는 API token을 포함합니다. 메신저·메일·이슈·진단 로그에 붙이지 마십시오. 나중에 다시 필요하면 Agent PC에서 같은 Setup을 관리자 권한으로 실행하고 `페어링 코드 보기`를 사용합니다.

## 4. Viewer 설치와 연결

1. Viewer ZIP을 새 로컬 폴더에 풉니다.
2. `SamsungSwitchWatch.Viewer.Setup.exe`를 실행합니다.
3. Viewer를 시작합니다.
4. 연결 설정에 `https://<Agent IPv4>:18443` 형식의 주소를 입력합니다.
5. Agent Setup의 페어링 코드를 입력합니다.
6. 연결 시험을 실행하고 저장합니다.

Viewer는 코드에서 인증서 공개키 pin과 token을 분리합니다. token은 현재 Windows 사용자 DPAPI로 보호해 저장하므로 다른 PC나 계정에 설정 파일만 복사해 사용할 수 없습니다.

## 5. 장비 등록

1. 장비의 사설 IPv4와 조회 전용 계정을 입력합니다.
2. TCP 포트는 23만 허용됩니다.
3. 로그인 시험을 실행합니다.
4. Agent가 `show version`으로 식별한 모델을 확인합니다.
5. 수동 조회 또는 주기 감시를 시작합니다.

지원 등록 모델은 IES4224GP, IES4028XP, IES4226XP입니다. 실제 펌웨어별 호환성은 허가된 환경에서 별도 확인해야 합니다.

## 6. 이전 버전에서 마이그레이션

v0.12는 인증 필수 API v5로 보안 경계를 바꿨습니다. 자동 무인증 호환 모드는 없습니다.

1. 진행 중인 Viewer 점검을 끝내고 Viewer를 닫습니다.
2. Agent와 Viewer를 모두 같은 새 버전으로 업데이트합니다.
3. Agent Setup에서 새 페어링 코드를 표시합니다.
4. Viewer 연결 설정에 Agent 주소와 새 코드를 입력합니다.
5. 연결 시험을 통과한 뒤 저장합니다.
6. 기존 장비 목록·자격 증명·감시 이력이 유지됐는지 확인합니다.

보존되는 데이터:

- Agent 주소
- 장비 목록과 canonical model
- DPAPI로 보호한 장비 자격 증명
- 감시 일정, baseline, gap, event 이력

다시 만들어야 하는 데이터:

- Agent 인증서 pin
- API bearer token

기존 Viewer는 pairing credential이 없으면 `연결 설정 필요` 상태로 전환합니다. 기존 Agent API v4 요청은 인증 없이 401, 인증된 경우에도 426을 반환합니다.

## 7. 오류별 조치

| 코드/상태 | 의미 | 조치 |
|---|---|---|
| `VIEWER_PAIRING_REQUIRED` | pin 또는 token 없음 | Agent Setup 코드로 재페어링 |
| `VIEWER_PAIRING_CORRUPT` | DPAPI token 손상·다른 사용자 | 현재 사용자로 재페어링 |
| `AGENT_PAIRING_REJECTED` | token 불일치 | 주소 확인 후 새 코드 입력 |
| `AGENT_IDENTITY_CHANGED` | 인증서 공개키 불일치 | 자동 수락 금지, Agent 교체 여부 확인 |
| `AGENT_VERSION_MISMATCH` | API/제품 버전 불일치 | Agent와 Viewer를 같은 Release로 업데이트 |
| `AGENT_API_UPGRADE_REQUIRED` | v4 호출 | Viewer 업데이트와 재페어링 |
| `AGENT_LOCAL_CONNECTION_UNCONFIRMED` | 설치 후 로컬 readiness 미확인 | 서비스·TCP/18443·진단 확인 |

인증 자료를 삭제하거나 pin 불일치를 무시하는 식으로 우회하지 마십시오.

## 8. 제거와 데이터

Setup의 제거 절차는 프로그램·서비스·제품 소유 방화벽 규칙을 대상으로 합니다. Viewer 사용자 데이터와 Agent의 보호된 운영 자료를 임의로 지우지 않습니다. 완전 삭제가 필요하면 먼저 필요한 이력을 백업하고 조직 절차에 따라 명시적으로 처리하십시오.

## 9. 현장 검증

공개 CI는 합성 Telnet 서버만 사용합니다. 실제 배포 전에 다음을 확인합니다.

- Windows Service 재부팅 후 자동 시작
- Viewer→Agent SPKI pin·bearer 연결
- GPO/방화벽/EDR 정책
- 모델·펌웨어별 prompt와 `show version`
- 로그인·enable·긴 출력·연결 단절
- Telnet 평문 관리망 분리
- update/rollback과 재페어링

실제 IP, 계정, MAC, 사이트명과 raw 출력은 공개 저장소에 기록하지 않습니다.
