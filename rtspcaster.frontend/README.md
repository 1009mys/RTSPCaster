# RTSPCaster Frontend

Vue 3 기반 RTSP Caster 웹 제어판입니다. Win처럼 상단 설정, 중앙 채널 목록, 하단 로그로 구성됩니다.

## 기능
- 다중 파일 업로드·드래그 앤 드롭·취소·파일별 결과 표시
- MediaMTX 연결 확인, RTSP 템플릿, 자동 재시작 정책
- 채널별/전체 시작·중지, 채널 삭제, 주소 편집·복사
- 코덱·해상도·호환성·변환 진행률·FPS·비트레이트·속도 그래프
- SSE 자동 재연결, 채널 검색, 로그 필터·복사·자동 스크롤

브라우저에서 VLC 실행 파일을 실행하거나 RTSP 영상을 직접 재생하지 않습니다. URL을 복사해 외부 플레이어에서 여세요. 클립보드가 제한되면 직접 복사 대화상자가 열립니다.

## 개발 실행
Node.js 버전은 package.json의 engines를 따릅니다. Backend에는 .NET 10과 FFmpeg/ffprobe가 필요합니다. MediaMTX는 별도로 실행합니다.

저장소 루트의 첫 번째 터미널:
```sh
dotnet run --project RTSPCaster.Backend --launch-profile http
```
두 번째 터미널:
```sh
cd rtspcaster.frontend
npm ci
npm run dev
```
`http://127.0.0.1:4848`에 접속합니다. Vite가 `/api`와 SSE를 `http://127.0.0.1:5058`로 전달합니다. 서버는 로컬에만 바인딩하며 포트가 사용 중이면 실패합니다.

Visual Studio에서는 Backend와 Frontend를 다중 시작 프로젝트로 지정하거나 Backend 실행 후 별도 터미널에서 Frontend를 실행합니다. Backend WSL 프로필도 사용할 수 있습니다. Frontend는 연결된 Backend의 데이터만 표시하므로 VS/터미널의 WSL 저장소 차이는 `Backend__DataDirectory`로 해결합니다.

### Backend 주소 변경
Frontend 폴더의 `.env.local`에 Vite 실행 머신에서 접근 가능한 주소를 지정합니다.
```dotenv
RTSPCASTER_BACKEND_URL=http://127.0.0.1:5058
```
WSL localhost 전달이 안 되면 WSL IP로 바꾸고 Vite를 재시작하세요. 이 값은 브라우저 번들에 포함되지 않습니다. 기본 HTTP 프록시는 Host를 보존하므로 Backend CORS 변경이 필요 없습니다. 프런트/백엔드 스킴이 다르면 Backend `AllowedOrigins`에 실제 웹 Origin을 등록하세요.

## 동작상 주의
- 설정은 **설정 저장**으로 반영합니다. SSE는 편집 중인 값을 덮어쓰지 않습니다. **변경 취소**는 최근 서버 설정으로 복원합니다.
- 연결 확인은 저장된 MediaMTX 주소 기준입니다. 기본 주소 변경은 기존 채널을 자동 변경하지 않습니다.
- 템플릿 전체 적용은 입력한 템플릿으로 유휴 채널을 변경하며 설정 저장과 별개입니다.
- 시작 접수는 송출 성공이 아닙니다. 이후 상태와 로그를 확인하세요.
- 여러 파일을 선택하면 **파일당 요청 한 개씩 순차 업로드**합니다. 선택한 파일의 합계가 2 GiB를 넘어도 개별 파일이 서버 제한 이내이면 전송할 수 있습니다. `GET /api/uploads/limits`로 현재 제한을 확인하며 빈 파일·제한 초과 파일은 전송하지 않고 오류를 표시합니다. Backend도 업데이트 후 재시작해야 합니다.
- 현재 처리 파일과 완료 개수를 표시하고 파일별 결과를 즉시 보존합니다. 파일당 검사는 최대 60초이며 서버 등록 오류는 다음 파일과 별도로 표시합니다. 취소하면 현재 요청과 대기열을 중단하지만 이미 등록된 채널과 결과는 유지됩니다.
- 연결 끊김·HTTP 요청 실패 시 대기열을 중단하고 자동 재전송하지 않습니다. 서버에는 등록됐지만 응답만 유실됐을 수 있으므로 채널 목록을 확인한 뒤 미등록 파일만 다시 선택하세요. 개별 파일도 기본 2 GiB를 넘으면 `Backend__MaxUploadBytes` 및 앞단 프록시 제한을 조정해야 합니다. Backend 임시 디스크와 저장소의 여유 공간도 확인하세요.
- 채널 삭제 시 서버 미디어 파일은 유지됩니다. 별도 디스크 관리가 필요합니다.
- 로그는 최근 최대 500개이며 서버 재시작 시 초기화됩니다.

## 검증
```sh
npm run build
npm test
npx eslint .
npx oxlint .
npm run preview
```
`npm test`는 Node 내장 테스트 러너로 API 요청 계약과 표시 로직을 검사합니다. `preview`는 로컬 빌드 확인용이며 운영 서버가 아닙니다. VS 빌드도 npm run build를 실행합니다.

실제 Chromium/Edge 검증은 `BROWSER_PATH`를 브라우저 실행 파일 경로로 설정하고 `npm run test:browser`를 실행합니다. 임시 브라우저 프로필과 모의 API를 사용하며 사용자 Backend 데이터는 건드리지 않습니다.

## 운영 배포
`dist/`를 정적 웹 서버에 배포하고 **동일 Origin의 `/api/`를 Backend로 역방향 프록시**하세요. Backend 자체는 정적 파일을 제공하지 않습니다. Host와 Origin을 보존하고, TLS를 프록시에서 종료한다면 Backend `AllowedOrigins`에 실제 HTTPS 웹 Origin을 등록합니다. SSE 버퍼링을 끄고 업로드 크기/시간 제한도 맞추세요.

Nginx 예시(주소·TLS는 별도 설정):
```nginx
location / {
    root /srv/rtspcaster/frontend;
    try_files $uri $uri/ /index.html;
}
location /api/ {
    proxy_pass http://127.0.0.1:5058;
    proxy_set_header Host $http_host;
    proxy_buffering off;
    proxy_read_timeout 3600s;
    proxy_send_timeout 3600s;
    client_max_body_size 2049m;
}
```
**Backend에는 인증·권한 검사가 없습니다.** 인터넷에 직접 공개하지 말고 신뢰할 수 있는 네트워크 또는 별도 인증 프록시 뒤에서 사용하세요. `X-RTSPCaster-Client: web`은 인증 키가 아닙니다.
