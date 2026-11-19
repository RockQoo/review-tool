# targets/

분석 대상 설정(`<이름>.json`)이 생기는 곳. **내 PC 경로가 들어 있어 git 에 올리지 않는다**(`.gitignore`).

만드는 방법:

- 화면: 상단 **＋ 대상 추가** → 폴더 고르기 → 제안 확인 → 저장
- 명령줄: `dotnet run --project src/ReviewTool.Cli -- init <분석할 폴더> [--name 이름]`

자동 감지가 하는 일: `.vcxproj` 위치로 분석 범위, `3rd`/`external` 등 서드파티 제외,
패킷 핸들러 등록 모양(멤버 함수 등록, 핸들러 표 대입, OnPacket 구현, Handle* 함수)을 실제로 세어 맞는 규칙을 켠다.
맞는 규칙이 없으면 `entryPoints` 에 정규식을 직접 적는다(`(?<method>..)` 필수, `(?<class>..)` 권장).

`main` 이 있으면 "시작 함수" 규칙도 켠다(흐름 요약에서 시작 순서를 본다).

3단계 보기(패킷 여정, 흐름 요약)용 설정:

- `channels`: 메시지 통로. 기본값은 흔한 패킷 모양(`Send*(`, `PacketId::X`, `X::kPacketId`, `case PacketId::X`) 하나.
  레인처럼 같은 프로세스 안 메시지는 `"crossProcess": false` 와 `register`(핸들러 등록 정규식)를 더한다. 예는 README "대상 추가".
- `tags`: 함수 이름(`pattern`)·본문 줄(`code`) 규칙으로 붙이는 표시. 비우면 흐름 요약이 비어 보인다.

4단계 점검 규칙용 설정:

- `checks`: 규칙 목록. 종류는 `forbidInScope`(락 안 I/O 등), `requireWithin`(작업 단위·레인 밖 접근), `order`(차감 뒤 지급),
  `dependency`(금지된 의존). 기본값은 락 안 I/O 만 켜져 있고 나머지는 꺼져 있다. 대상의 이름(클래스·폴더)에 맞춰 정규식·glob 을 고치고
  `"enabled": true` 로 켠다. 예와 키 설명은 README "점검 규칙". 점검만 바꾸면 재분석할 필요가 없다(화면의 "다시 점검").

C# 대상: 폴더에 C++ 소스가 없고 `.csproj`·`.cs` 가 있으면 `"language": "csharp"` 설정을 만든다. 진입점 규칙은 C# 모양
(메서드 등록, `[PacketHandler]`, `[HttpPost]` 액션, Handle* 메서드, `Main`·최상위 문)이고, `channels` 기본값도 `PacketId.X` 모양이다.
테스트 폴더(`*Tests`)와 bin/obj 는 범위에서 뺀다.
