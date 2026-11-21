# review-tool

C++(나중에 C#) 서버 코드의 호출 흐름을 보여 주는 개인 리뷰 도구. 사람용 설명은 [README.md](README.md).
현재 상태와 다음 할 일은 `STATUS.md`(로컬 전용).

## 규칙

- **분석 대상 저장소에는 아무것도 쓰지 않는다.** compile_commands.json, clangd 색인, 결과는 전부 `data/<대상>/`.
  clangd 는 `--compile-commands-dir` 로 이 폴더를 받아 색인도 여기에 둔다.
- `data/` 는 **대상 소스 코드가 들어 있어 커밋 금지**(`.gitignore`). 회사 코드로 만든 결과는 회사 PC 밖으로 나가면 안 된다.
- `targets/*.json`(분석 대상 설정)도 **커밋 금지**. 내 PC 경로가 들어 있다. 손으로 쓰지 않고 `init` 이나 화면의 "대상 추가"로 만든다(`CppTargetDetector`).
- 프로젝트 파일·커밋 메시지에 참고한 사람이나 다른 프로젝트 이름을 적지 않는다. 기능은 자체 용어로 설명한다
  (가져다 쓴 라이브러리의 LICENSE 동봉은 예외: `wwwroot/lib/`).
- 화면 서버는 `127.0.0.1` 에만 바인딩한다. 바꾸는 옵션을 만들지 않는다.
- 언어 어댑터는 `ITraceAnalyzer` 를 구현하고 `TraceGraph` 만 채운다. 화면은 언어를 모른다(C++: clangd, C#: Roslyn).
- "못 본 것"은 `TraceGraph.Diagnostics` / `TraceNode.Stop` 으로 남긴다. 빈 호출 목록을 "호출 없음"으로 보이게 하지 않는다.
- 주석과 문서는 한국어.
- AI 연동(2단계): AI CLI 는 사용자가 "보내기"를 누를 때만 실행한다. 인자는 고정값만 쓰고, 질문·코드는 표준 입력으로 넘긴다.
  셸(cmd)을 거치지 않는다(codex 는 node 로 스크립트 직접 실행). AI 는 읽기 전용(Claude: `--tools Read,Grep,Glob`, Codex: `--sandbox read-only`).
  AI 답은 `MiniMarkdown` 이 먼저 전부 이스케이프한 뒤 일부만 태그로 바꾼다. `MarkupString` 에 원문을 그대로 넣지 않는다.
- MCP 서버(`review-tool mcp`)는 표준 출력에 프로토콜 메시지만 쓴다(안내·로그는 표준 오류). 저장된 결과를 읽기만 한다.
- 화면·MCP·문맥 복사가 같은 말을 하도록 조회와 문구는 Core(`TraceQueries`, `FlowContext`, `Journey`, `FlowSummary`, `SystemMap`)에 둔다.
- 3단계(게임 서버 보기)는 **범용 규칙 + 대상 설정**이다. 특정 저장소의 이름(EZoneMsg 같은)을 코드에 넣지 않고 `channels`·`tags` 기본값이나
  `targets/*.json` 으로 뺀다. 확인은 asio-server(실측)와 `Fixtures/multi`(여러 프로세스 예제) 둘 다.
- 프로세스 = 진입점 파일에서 가장 가까운 빌드 프로젝트 폴더(`.vcxproj`·`CMakeLists.txt`). `ProcessMap` 은 진입점 묶음 이름으로 파일을 나눈다.
- 다른 프로세스로 가는 패킷(`crossProcess: true`)은 호출 선으로 잇지 않는다(트리·영향 분석이 프로세스를 넘지 않게). 여정(`Journey`)만 잇는다.
  같은 프로세스 안 메시지는 `Resolution = "Message"` 선으로 잇는다.
- `Register(this, &C::H)` 처럼 함수 주소만 넘긴 줄을 clangd 는 호출로 준다. 여정·흐름 요약은 `TraceQueries.IsAddressOnly` 로 이런 선을 따라가지 않는다.
- clangd 는 프로세스마다 있는 `main` 에 같은 심볼 ID 를 준다. 정의 파일이 다르면 다른 노드로 나누고(`CallTreeWalker`) 제 본문 안 호출만 남긴다.
- 클래스 선언(`TypePass`)은 **모든 호출 조회가 끝난 뒤**에 파일을 연다(아래 "파일을 열수록 결과가 줄 수 있다").
- 4단계 점검 규칙(`Checks`)은 **분석 결과에 저장하지 않고** 저장된 결과 + 설정 `checks` 로 그때그때 돈다. 그래서 `checks` 는
  `TargetConfig.ContentHash` 에서 뺀다(규칙만 고쳤는데 "설정 바뀜"이 되지 않게). 규칙 종류는 범용 4가지
  (`forbidInScope`·`requireWithin`·`order`·`dependency`)이고, 특정 저장소 이름은 기본값이 아니라 `targets/*.json` 에 둔다.
- 5단계: 공유 보고서(`ShareReport`)는 기본으로 소스 본문·절대 경로를 뺀다(회사 코드). 넣는 것은 사용자가 옵션을 켤 때만.
- 화면 서버 구성은 `ReviewToolServer.Build` 하나다. 명령줄(`ReviewTool.Web`)과 데스크톱 창(`ReviewTool.Desktop`, 127.0.0.1 빈 포트 + WebView2)이 같이 쓴다.
  단일 exe 는 Web SDK 가 정적 파일을 묶음에서 빼므로 화면 파일을 zip 리소스로 넣고 `%LOCALAPPDATA%\review-tool\web\<해시>` 에 풀어 쓴다(`WebAssets`).
- C# 어댑터(`ReviewTool.CSharp`)는 .csproj 마다 Roslyn 컴파일(같은 이름 `Program` 이 섞이지 않게), 노드 id 에 어셈블리 이름을 붙인다.
  한정 이름은 C++ 과 같이 `::` 로 잇는다(화면·흐름도·표시 규칙이 언어와 무관하게). 언어와 무관한 도우미(`SourceFiles`, `TargetSuggestion`)는 Core 에 둔다.
- 모든 지적은 근거(파일:줄)·확실도·그 이유를 단다. 꺼진 규칙은 "꺼짐", 잘못된 규칙은 "오류"로 남긴다(조용히 빼지 않는다).

## 사용법 문서 갱신 (사용자 결정 2026-11-21)

- 화면 기능을 **추가·삭제·개선했으면 push 전에 사용법을 갱신**한다. 순서: 개발 → Codex 리뷰 → 수정 → **사용법 갱신** → QA → push.
- 사용법은 `docs/usage/` 폴더에 모은다: `index.html`(스크린샷 + 버튼 번호 + 키워드 설명, `tools/usage-shots.mjs` 가 만듦), `detail.html`(글로 된 자세한 설명, 손으로 고침), `img/`(스크린샷). 서버를 띄운 뒤 `node tools/usage-shots.mjs`.
  - 버튼을 새로 만들거나 이름·위치를 바꾸면 스크립트의 장면(`scene(...)`)과 표시 목록(선택자, 이름, 키워드)도 같이 고친다.
  - 실행 출력의 "표시 못 함"이 0인지 확인하고, 결과 페이지를 열어 번호가 제자리에 붙었는지 본다.
  - 설명은 키워드 한 줄로 짧게 쓴다(자세한 질문은 사용자가 따로 한다).
- `detail.html` 과 README 의 기능 표도 바뀐 기능이 있으면 맞춘다.

## clangd 사용 요령 (실측, 바꾸기 전에 읽을 것)

- `prepareCallHierarchy` 는 정의가 아니라 **헤더 선언**을 줄 때가 많고 `detail`(한정 이름)이 비어 있다.
- `outgoingCalls` 는 item 의 `data`(심볼 ID)로 색인을 조회한다. 위치가 선언이어도 호출 목록은 나오지만,
  **`fromRanges` 는 item 이 가리키는 파일 기준**이라 선언 item 으로 물으면 호출 위치가 비어 버린다.
  그래서 정의를 `workspace/symbol` 로 찾아 그 위치에서 다시 prepare 한다(`CallTreeWalker.RelocateItemAsync`).
- item 의 `range` 는 이름 토큰뿐이다. 본문 범위는 `FunctionBody` 가 소스에서 중괄호 짝으로 찾는다.
- 새 Visual Studio(18) 는 clang 이 스스로 못 찾아 `<cstddef>` 부터 깨진다. `MsvcToolchain` 이 vswhere 로 찾아 `-isystem` 으로 넘긴다.
- 색인이 끝나기 전 `outgoingCalls` 는 빈 답을 준다. `backgroundIndexProgress` 의 end 를 기다린다.
- **파일을 열수록(didOpen) 결과가 줄 수 있다.** 연 파일의 동적 색인이 그 파일이 포함한 헤더의 인라인 함수 호출 기록을
  가려 `outgoingCalls` 가 비는 것을 실측했다(실제 서버 저장소에서 호출 908 → 901). 열기는 꼭 필요한 곳(진입점 prepare,
  정의 재탐색)만 하고, 오버로드 판별 같은 것은 소스 텍스트로 한다(`FunctionBody.ParameterTypes`).
  분석 로직을 바꾸면 기준으로 삼는 대상의 결과 수(진입점·함수·호출)가 줄지 않았는지 바꾸기 전과 비교한다.

## 확인 방법

```powershell
dotnet test ReviewTool.slnx                                   # 단위 + clangd 종단 테스트(작은 예제) + C# 어댑터(Fixtures/csharp)
.\tools\publish-desktop.ps1                                    # 데스크톱 단일 exe(dist/) + self-test
dotnet run --project src/ReviewTool.Cli -- init <분석할 폴더>    # 설정이 없을 때
dotnet run --project src/ReviewTool.Cli -- analyze targets/<이름>.json
node tools/ui-check.mjs                                        # 서버가 떠 있을 때: 헤드리스 Edge 로 클릭·스크린샷(data/ui-check/)
$env:UI_ASK=1; $env:UI_ASK_AI="codex"; node tools/ui-check.mjs # AI 질문까지 실제 전송(비용 듦, 답은 매번 다름)
```

`dotnet test` 는 Web 도 빌드하지만, 서버가 떠 있으면 DLL 이 잠겨 Web 빌드만 조용히 실패하고 옛 DLL 로 뜰 수 있다. 화면을 확인하기 전에 서버를 끄고 `dotnet build` 로 오류 0 을 본다.
