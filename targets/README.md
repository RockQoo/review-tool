# targets/

분석 대상 설정(`<이름>.json`)이 생기는 곳. **내 PC 경로가 들어 있어 git 에 올리지 않는다**(`.gitignore`).

만드는 방법:

- 화면: 상단 **＋ 대상 추가** → 폴더 고르기 → 제안 확인 → 저장
- 명령줄: `dotnet run --project src/ReviewTool.Cli -- init <분석할 폴더> [--name 이름]`

자동 감지가 하는 일: `.vcxproj` 위치로 분석 범위, `3rd`/`external` 등 서드파티 제외,
패킷 핸들러 등록 모양(멤버 함수 등록, 핸들러 표 대입, OnPacket 구현, Handle* 함수)을 실제로 세어 맞는 규칙을 켠다.
맞는 규칙이 없으면 `entryPoints` 에 정규식을 직접 적는다(`(?<method>..)` 필수, `(?<class>..)` 권장).
