# Postman으로 API 테스트

## 가져오기

1. 최신 Postman Desktop에서 **Import**를 선택합니다.
2. `RTSPCaster.Backend.postman_collection.json`을 가져옵니다. Collection v2.1 형식이며 별도 Environment 파일은 필요하지 않습니다.
3. Collection의 **Variables**에서 `baseUrl`을 설정합니다. 기본값은 `http://localhost:5058`입니다. 다른 PC에서 테스트하려면 `http://서버IP:5058`로 바꾸세요. 마지막 `/`는 넣지 않습니다.
4. Backend를 실행합니다. 업로드 검사에는 ffprobe, 변환·송출에는 FFmpeg, 실제 송출에는 외부 MediaMTX가 필요합니다.

인증은 `No Auth`입니다. 변경 요청에 필요한 `X-RTSPCaster-Client: web` 헤더는 각 요청에 들어 있습니다. Origin 헤더는 별도로 넣지 않습니다.

## 권장 테스트 순서

1. **01. 상태 및 조회**에서 전체 상태, 설정, MediaMTX 연결 상태를 확인합니다.
2. **02. 업로드 및 채널 제어 → 동영상 업로드**의 **Body → form-data → files → Select Files**에서 실제 동영상을 선택합니다. 필요하면 여러 파일을 선택합니다. 파일은 컬렉션에 포함되지 않습니다.
3. 업로드 요청을 전송합니다. 첫 번째 성공 채널의 ID가 `channelId`에 자동 저장됩니다. 200이어도 파일별 오류가 있을 수 있으며 Tests가 이를 검사합니다. 실패 시 이전 채널을 잘못 제어하지 않도록 업로드 전에 저장된 channelId를 비웁니다.
4. `mediaMtxHost`, `mediaMtxPort`, `rtspPath` 변수를 맞춘 후 **채널 RTSP 대상 변경**을 실행합니다. 다른 채널과 같은 URL이면 409가 발생합니다.
5. **채널 송출 시작**을 실행합니다. 202는 작업 접수이며 성공 보장이 아닙니다. **채널 상세 및 송출 상태**를 다시 실행해 `Streaming` 또는 `Error`와 `statusMessage`를 확인합니다.
6. **RTSP 주소 반환**, **로그**를 확인합니다. **채널 송출 중지**로 테스트 송출을 종료합니다.

기존 채널을 테스트하려면 목록에서 ID를 확인해 `channelId`에 직접 입력합니다. `channelId`가 비어 있으면 개별 채널 요청은 건너뜁니다. 다른 PC에서 플레이어로 영상을 볼 때는 MediaMTX 호스트도 해당 PC에서 접근 가능한 서버 IP/DNS로 지정해야 합니다. `127.0.0.1`은 플레이어 PC 자체를 의미합니다.

## 변수

| 변수 | 용도 |
| --- | --- |
| `baseUrl` | Backend HTTP 주소 |
| `channelId` | 제어할 채널 ID. 업로드 성공 시 자동 설정 |
| `mediaMtxHost` / `mediaMtxPort` | 채널 대상 변경 및 전체 설정 교체 요청 값 |
| `rtspPath` | 선택 채널의 RTSP 경로 |
| `rtspTemplate` | 전체 적용할 RTSP 템플릿 |
| `lastLogId` | 로그 조회 후 자동 갱신되는 cursor |
| `allowGlobalChanges` | true일 때만 전체 설정·템플릿 변경, 전체 시작/중지 허용 |
| `allowDelete` | true일 때만 선택 채널 삭제 허용 |
| `enableSse` | true일 때만 SSE 지속 연결 허용 |

자동 저장은 Collection Variables에 수행합니다. 같은 이름의 Environment/Data/Local 변수를 정의하면 자동 저장 값을 가릴 수 있으므로 중복 정의를 피하세요.

## 안전 장치와 Runner

- **04. 전체 변경**은 기본적으로 건너뜁니다. `allowGlobalChanges=true`로 설정하면 기존 전체 채널/설정에도 영향을 줍니다. 설정 PUT은 부분 수정이 아니라 전체 교체이므로 Body를 확인하세요.
- **06. 채널 삭제**도 기본적으로 건너뜁니다. `allowDelete=true`로 설정한 뒤 실행합니다. 성공 시 channelId를 비웁니다. 삭제는 채널 등록만 제거하며 업로드 파일/캐시는 남습니다.
- 위 안전 장치는 Postman의 `pm.execution.skipRequest()`를 사용합니다. 이 기능을 지원하는 최신 Postman을 사용하세요.
- **02. 업로드 및 채널 제어**는 기본적으로 실행됩니다. 읽기 전용 테스트라면 해당 폴더를 Runner에서 제외하세요. 테스트용 서버/채널을 권장합니다.
- Runner에서 파일 업로드를 실행하려면 각 요청의 파일 선택과 Postman 로컬 파일 접근 설정을 확인하세요.
- 폴더를 순서대로 일괄 실행하면 시작 직후 중지가 호출될 수 있습니다. 실제 송출·자동 변환 성공은 수동으로 시작 → 상세 재조회 → 중지 순서로 확인하세요. 고정 대기 시간을 사용한 성공 판정은 넣지 않았습니다.
- **05. 오류 응답 검증**은 없는 채널(404), 잘못된 cursor(400), 필수 변경 헤더 누락(403)을 검사하며 상태를 변경하지 않습니다.

## SSE

`enableSse=true`로 설정하고 **07. SSE → 실시간 snapshot 이벤트**만 단독으로 실행합니다. 지속 연결이며 `snapshot` 이벤트와 JSON 데이터를 약 1초마다 받습니다. 수신 확인 후 **Cancel**을 누르고 enableSse를 false로 복원하세요. SSE는 Runner에서 제외해야 합니다. 스트림 종료에 의존하는 응답 테스트는 의도적으로 넣지 않았습니다.

요청에는 상태 코드·응답 계약 검증 스크립트가 포함되어 있습니다. 실제 서버 테스트는 Backend 실행 후 Postman에서 수행하세요. Collection의 정적 JSON/스크립트 검증만으로 실제 송출 성공을 보장하지 않습니다.
