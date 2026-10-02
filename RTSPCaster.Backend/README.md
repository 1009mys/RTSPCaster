# RTSPCaster.Backend

.NET 10 ASP.NET Core API. `RTSPCaster.Lib`의 검사·변환·송출 기능을 사용하며 Win UI에는 의존하지 않습니다. 웹 화면과 브라우저 영상 재생은 포함하지 않습니다.

## 실행

1. .NET 10 SDK와 FFmpeg/ffprobe를 설치합니다. 실행 파일을 PATH 또는 Backend 출력 폴더의 `tools`에 두거나 `Backend:FfmpegPath`, `Backend:FfprobePath`를 설정합니다.
2. 외부 MediaMTX를 별도로 실행합니다. Backend가 MediaMTX 프로세스를 시작하거나 종료하지는 않습니다.
3. 솔루션 디렉터리에서 `dotnet run --project RTSPCaster.Backend --launch-profile http`를 실행합니다.
4. 다른 PC에서는 `http://서버IP:5058/api/status`로 접근합니다. 서버 방화벽의 TCP 5058 허용 여부를 확인하세요. 기본 바인딩은 `appsettings.json`의 `Urls`에 설정된 `http://0.0.0.0:5058`입니다.
5. `GET /openapi/v1.json`에서 OpenAPI 명세를 확인합니다. Swagger UI는 포함하지 않습니다.

**인증·권한 검사는 없습니다.** API에 연결할 수 있는 사용자는 파일 업로드, 설정 변경, 송출 제어와 채널 삭제를 할 수 있습니다. 파일 업로드는 디스크를 사용하며, 연결 검사와 송출은 Backend 서버에서 수행됩니다. 방화벽·네트워크 노출 범위를 운영자가 결정하세요. HTTPS 프로필 사용 시 원격 클라이언트가 신뢰할 수 있고 서버 이름에 맞는 인증서가 필요합니다.

### 브라우저 요청

- 같은 Origin의 웹 화면에서는 추가 CORS 설정 없이 사용할 수 있습니다.
- 별도 웹 서버/개발 서버에서는 `Backend:AllowedOrigins`에 **웹 화면의 Origin**을 정확히 등록합니다. 예: `http://192.168.0.20:5173`. 마지막 `/` 없이 지정합니다. 원격 주소도 지원합니다.
- 변경 요청(`POST`, `PUT`, `DELETE`)에는 `X-RTSPCaster-Client: web` 헤더가 필요합니다. 공개 고정값이며 인증키가 아닙니다. 의도치 않은 HTML 폼 요청을 구분하기 위한 정책입니다.
- 조회와 SSE는 위 헤더 없이 사용할 수 있습니다. 허용하지 않은 Origin의 브라우저 요청은 403입니다. Origin이 없는 일반 API 클라이언트의 원격 접속은 허용합니다.
- 브라우저의 `FormData`로 업로드할 때 `Content-Type`을 직접 지정하지 마세요. 브라우저가 multipart boundary를 설정합니다.

## Win과 완전히 분리된 저장소

기본 저장소는 `appsettings.json`의 `Backend:DataDirectory` 값 `.`을 기준으로 한 Backend 실행 폴더입니다. 운영 환경에서는 유지할 절대 경로를 지정하세요.

| 경로 | 내용 |
| --- | --- |
| `rtspcaster.db` | Backend 전용 채널, 메타데이터, 설정, 변환 캐시 인덱스, 송출 이력 |
| `media/` | 서버에서 생성한 고유 파일명으로 보관하는 업로드 원본 |
| `converted/` | 변환 결과 |
| `converted/copy-timeline-v1/` | Lib의 타임스탬프 복구 캐시 |
| `logs/` | Lib의 채널별 FFmpeg 송출 로그 |
| `backend.lock` | 같은 저장소를 사용하는 Backend 중복 실행 방지 |

`Backend:DataDirectory`로 전용 저장소를 변경할 수 있습니다. **Win의 데이터 폴더를 지정하지 마세요.** Win 데이터의 가져오기·공유·변경 기능은 제공하지 않습니다. Win과 Backend를 동시에 실행할 수 있지만, 동일 MediaMTX의 동일 RTSP 경로로 동시에 송출하지 않아야 합니다.

채널 삭제는 송출/준비를 중지하고 채널 등록만 삭제합니다. Win과 마찬가지로 미디어 원본·변환 캐시·이력은 삭제하지 않습니다. 장기간 운영 시 별도의 디스크 관리가 필요합니다. 재실행하면 저장된 채널과 설정을 복원하지만 자동 송출은 하지 않습니다.

## 서버 설정

`appsettings.json`의 `Urls`와 `Backend` 항목을 사용합니다. 리눅스를 포함한 모든 환경에서 실행 전에 배포 폴더의 이 파일을 수정해 설정합니다. 환경 변수는 Backend 설정을 재정의하지 않습니다.

| 설정 | 기본값 / 용도 |
| --- | --- |
| `Urls` | `http://0.0.0.0:5058`: API 수신 주소 |
| `DataDirectory` | `.`: Backend 실행 폴더 기준 전용 저장소 |
| `FfmpegPath` | null: 출력 폴더/도구 폴더/PATH에서 탐색 |
| `FfprobePath` | null: 출력 폴더/도구 폴더/PATH에서 탐색 |
| `MaxUploadBytes` | 2147483648 (요청 내 파일 합계 최대 2 GiB) |
| `AllowedOrigins` | 빈 배열: 같은 Origin의 브라우저만 허용 |

업로드는 ASP.NET Core multipart 버퍼링을 사용합니다. 큰 요청은 임시 디스크 공간도 필요하며, 앞단 프록시가 있다면 프록시의 업로드 크기/시간 제한도 맞춰야 합니다. 요청당 최대 50개 파일을 순서대로 검사합니다. 파일 하나의 ffprobe 검사는 최대 60초입니다. 등록 응답이 끝나기 전에 요청을 취소하면 아직 등록하지 않은 파일은 정리되며, 이미 등록된 파일/채널은 유지됩니다.

웹 Frontend는 `/api/uploads/limits`에서 크기 제한을 확인한 후 파일당 요청 한 개씩 순차 전송합니다. 따라서 선택 파일 합계가 제한을 넘더라도 각 파일이 제한 이내라면 업로드할 수 있습니다. API를 직접 호출하여 여러 파일을 한 요청에 넣으면 기존 요청 합계 제한이 그대로 적용됩니다.

## API

JSON 속성명은 camelCase이며 상태 enum은 문자열입니다. `RTSPCaster.Backend.http`에 요청 예제가 있습니다.

| 메서드 | 경로 | 기능 |
| --- | --- | --- |
| GET | `/api/status` | 설정, MediaMTX 상태, 모든 채널, 최근 로그 스냅샷 |
| GET | `/api/channels` | 채널 목록과 메타데이터·진행률·품질 |
| GET | `/api/channels/{id}` | 채널 상세 |
| GET | `/api/uploads/limits` | `{ "maxUploadBytes": 2147483648 }`: 현재 요청 내 파일 합계 제한 |
| POST | `/api/channels/upload` | multipart `files` 필드로 한 개 이상 업로드·검사·채널 등록 |
| PUT | `/api/channels/{id}/endpoint` | `rtspPath`, `mediaMtxHost`, `mediaMtxPort` 변경 |
| GET | `/api/channels/{id}/url` | `{ "rtspUrl": "rtsp://..." }` 반환 |
| POST | `/api/channels/{id}/start` | 검사·필요 시 변환·송출 준비를 시작하고 202 반환 |
| POST | `/api/channels/{id}/stop` | 준비·변환 취소 및 송출 중지; 완료 상태 반환 |
| DELETE | `/api/channels/{id}` | 준비·송출 중지 후 채널 삭제; 204 반환 |
| POST | `/api/channels/start-all` | 채널별 접수 여부/오류 배열과 202 반환 |
| POST | `/api/channels/stop-all` | 모든 준비·송출 중지; 204 반환 |
| GET / PUT | `/api/settings` | MediaMTX 기본 대상, 템플릿, 재시작·파일 기록 정책 조회/전체 교체 |
| GET | `/api/rtsp-template/help` | 템플릿 변수와 예제 |
| POST | `/api/rtsp-template/apply` | `{ "template": "stream_{index:D3}" }` 일괄 적용 |
| GET | `/api/mediamtx` | 최근 연결 검사 결과 |
| POST | `/api/mediamtx/check` | 즉시 TCP 연결 검사 |
| GET | `/api/logs?after=0` | 로그 ID 이후의 메모리 로그; 최근 최대 500개 |
| GET | `/api/log-files?skip=0` | 백엔드 로그 파일 목록, 최대 100개 및 hasMore |
| GET | `/api/log-files/content?name=ch1_20260320.log&offset=0` | 최대 64KiB UTF-8 파일 내용; offset 생략 시 최신 구간 |
| GET | `/api/events` | SSE 스냅샷 스트림 |

### 업로드와 송출

서버 로컬 파일 경로 등록은 제공하지 않습니다. 업로드 파일은 항상 Backend 전용 저장소에 보관합니다. 업로드 응답은 파일별 `fileName`, `channel`, `error` 배열입니다. 요청 형식이 유효하면 HTTP 200을 반환하므로 **각 항목의 error를 확인**해야 합니다. 한 파일의 형식 오류는 다른 파일 등록을 막지 않습니다. 영상 스트림이 없는 파일은 거부합니다. 같은 표시 파일명도 별도 저장하며 RTSP 경로에 순번을 붙여 충돌을 피합니다.

시작 API의 202는 **작업 접수**이며 송출 성공을 의미하지 않습니다. 이후 채널 상태 또는 SSE를 확인하세요. HTTP 연결을 닫아도 접수한 시작 작업은 계속됩니다. 중지·삭제·Backend 종료 시 검사/변환 작업을 취소하고 FFmpeg를 중지합니다. 변환은 캐시 충돌과 자원 경합을 피하도록 한 번에 하나씩 수행하며 서로 다른 채널의 송출은 병렬로 유지됩니다.

상태: `Idle`, `Probing`, `Converting`, `Ready`, `Streaming`, `Stopping`, `Error`. 중복 시작, 준비/송출/중지 중 URL 수정은 409입니다. 실패 원인은 `statusMessage`와 로그에 나타납니다. 존재하지 않는 채널은 404, 잘못된 요청은 400, 서비스/도구 실행 불가는 503 등 ProblemDetails로 반환합니다. multipart 파서 단계의 크기 오류는 400 또는 413입니다.

### 설정과 RTSP URL

`PUT /api/settings`는 부분 수정이 아닌 전체 설정 교체입니다. MediaMTX 기본 주소 변경은 **신규 채널과 연결 모니터**에 적용됩니다. 기존 채널은 endpoint API 또는 전체 템플릿 적용으로 변경합니다.

- `mediaMtxHost`: IPv4 또는 DNS 호스트명. 기본값 `127.0.0.1`.
- `mediaMtxPort`: 1~65535, 기본값 8554.
- `bulkRtspTemplate`: 기본값 `rtsp://{host}:{port}/stream_{index}`.
- `autoRestartEnabled`: 기본값 true.
- `fileLoggingEnabled`: 기본값 true. .log 파일 생성 및 추가 기록 여부. 저장 즉시 적용하며 SQLite에 저장되어 재시작 후에도 유지됩니다. 끄더라도 기존 파일과 실시간 로그·헬스 수집은 유지합니다.
- `maxAutoRestartAttempts`: 0~20, 기본값 3.
- `autoRestartBaseDelaySeconds`: 1~120, 기본값 2.
- `autoRestartResetThresholdSeconds`: 5~3600, 기본값 30.

**다른 PC에서 영상을 보려면 MediaMTX 호스트를 클라이언트에서도 접근 가능한 서버 IP/DNS 이름으로 설정하세요.** `127.0.0.1`이 포함된 RTSP URL은 다른 PC에서 서버를 가리키지 않습니다. API 포트와 MediaMTX RTSP 포트는 별개입니다. 현재 Lib의 URL 형식에 맞춰 IPv6 RTSP 대상은 거부합니다.

템플릿은 `{host}`, `{port}`, `{index}`, `{index:D3}`, `{name}`을 지원합니다. 경로만 지정하면 각 채널의 기존 대상 호스트/포트를 유지합니다. 경로의 허용 문자 외 문자는 `_`로 바꿉니다. 준비·변환·송출·중지 중 채널은 건너뛰며 결과에 `updated`, `skipped` 채널 ID 배열을 반환합니다. 입력이 잘못되었거나 결과 URL이 중복되면 적용 전에 요청을 거부합니다.

### 실시간 상태와 로그

파일 기록은 `logs/ch{채널ID}_{yyyyMMdd}.log`에 채널별 FFmpeg 상세 출력을 저장합니다. 메모리 로그와 달리 재시작 후에도 남으며, 설정을 다시 켜면 같은 채널/날짜 파일에 이어 기록합니다. 삭제·보존 기한 관리는 별도로 수행해야 합니다.

파일 목록 API는 `{ files: [{ name, size, lastWriteTimeUtc }], hasMore }`를 반환합니다. 수정 시각 내림차순으로 100개씩 읽으며 `skip=100`처럼 다음 목록을 요청합니다. 기록 중에는 목록 순서가 변경될 수 있습니다.

파일 내용 API는 `{ name, size, lastWriteTimeUtc, offset, nextOffset, hasMore, content }`를 반환합니다. 크기와 위치는 바이트 단위입니다. `offset=0`은 처음, 생략은 최신 구간이고 `nextOffset`을 사용하면 UTF-8 문자를 나누지 않고 다음 구간을 읽습니다. 파일이 축소되어 위치가 유효하지 않으면 409, 파일이 없으면 404입니다. 로그 행 자체는 구간 경계에서 나뉠 수 있습니다.

표준 파일명만 허용하고 경로 탐색·심볼릭 링크/리파스 포인트는 거부합니다. 백엔드 전용 `logs` 폴더 밖의 파일은 조회하지 않습니다. 응답은 캐시하지 않습니다. 로그는 민감한 운영 정보를 포함할 수 있으므로 인증 프록시/신뢰 네트워크에서만 접근을 허용하고 저장소 자체의 OS 쓰기 권한도 제한하세요. Windows 앱은 별도 SQLite 설정과 기본 `log` 폴더를 사용하며 웹에서는 조회하지 않습니다.

`EventSource('/api/events')`의 `snapshot` 이벤트를 구독합니다. 연결 직후와 이후 약 1초마다 `/api/status`와 같은 전체 스냅샷을 받습니다. 이벤트의 `data`는 JSON이며 JSON 문자열로 프레이밍하므로 로그 개행이 SSE 이벤트 경계를 깨지 않습니다.

증분 이벤트나 Last-Event-ID 재생은 제공하지 않습니다. 연결 복구 시 최신 전체 상태를 다시 받습니다. 느린 클라이언트 때문에 서버에 이벤트 큐가 계속 쌓이지 않습니다. SSE 클라이언트 연결 종료는 송출 중지와 무관합니다. 프록시 사용 시 SSE 버퍼링을 끄고 연결 제한 시간을 충분히 설정하세요.

로그는 `id`, UTC `timestamp`, 선택적 `channelId`, `message`를 제공합니다. `/api/logs`의 `lastId`를 다음 조회의 `after`로 사용하세요. Backend 재시작 시 ID가 초기화되므로 반환된 lastId가 이전 cursor보다 작으면 cursor를 초기화하세요. 이 API는 최근 500개 메모리 로그만 제공하며 오래된 로그 재생 API가 아닙니다.

채널의 `healthSamples`는 최근 최대 30개이며 FPS, bitrateKbps, speed, 추정 latencyMs를 포함합니다. 추정 지연은 송출 경과 시간과 FFmpeg 미디어 시간 차이로 계산하며 실제 네트워크 종단 간 지연 측정치는 아닙니다. 웹 클라이언트가 이 값을 이용해 그래프와 표시 문자열을 구성합니다.

VLC 실행·실행 파일 경로 등록·클립보드 조작은 Backend에서 수행하지 않습니다. URL과 로그를 반환하고 클라이언트가 복사/외부 플레이어 열기를 처리합니다. 브라우저 재생(HLS/WebRTC 등)은 아직 구현하지 않습니다.

## 검증

- `dotnet build RTSPCaster.slnx`
- `dotnet run --project RTSPCaster.Backend.SmokeTests`
- `dotnet run --project RTSPCaster.Lib.SmokeTests`

Backend 테스트는 임시 전용 저장소와 실제 Kestrel HTTP 서버를 사용합니다. 테스트 실행 파일이 FFmpeg/ffprobe 대역으로 동작하므로 외부 도구 설치 없이 API·수명·취소·자동 재시작을 검증합니다. 실제 코덱 변환, MediaMTX RTSP 수신, 다른 PC의 방화벽/플레이어 연결은 설치 환경에서 추가 확인해야 합니다. Win 프로젝트 코드는 변경하지 않으며 Lib의 기존 생성자와 기본 경로는 유지합니다.
