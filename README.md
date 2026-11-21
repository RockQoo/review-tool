# review-tool

AI가 만든 서버 코드를 **사람이 흐름으로 리뷰**하기 위한 개인 도구.
진입점(패킷 핸들러)을 고르면 호출 트리가 나오고, 함수를 누르면 **진입점부터 그 함수까지의 코드가 위에서 아래로 이어 붙는다**.
VS에서 F12로 따라 들어가면 앞 코드가 안 보이는 문제를 없애는 "끊기지 않는 콜스택 뷰어"다.

## 목적

AI 로 서버 코드를 만들면 사람이 짜던 것보다 훨씬 많은 코드가 한 번에 생긴다. 파일을 하나씩 열어 읽는 방식으로는
리뷰가 따라가지 못하고, 그렇다고 AI 리뷰만 믿고 넘기면 문제가 생겼을 때 사람이 책임질 근거가 없다.

이 도구는 리뷰하는 사람이 **"이 패킷이 들어오면 무엇이 어떤 순서로 불리는가"를 빠르게 이해하고 직접 판단**하게 돕는다.

- 코드를 파일 단위가 아니라 **패킷(진입점) 단위의 흐름**으로 본다.
- 모든 호출에 근거(파일:줄)를 붙이고, 확실하지 않은 연결은 "추정"으로 따로 보여 준다. 분석이 못 본 것도 숨기지 않는다.
- AI 가 고친 뒤 **무엇이 바뀌었고 어떤 패킷에 영향이 가는지**를 구조로 보여 준다.

AI 코드 리뷰(지적 목록)나 테스트를 대신하지 않는다. 그 지적이 맞는지, 고친 결과가 의도대로인지 판단할 때 쓰는 지도다.
코드를 고치는 일은 편집기(Visual Studio / VS Code)에서 한다.

## 기술 스택

| 영역 | 사용 | 고른 이유 |
|---|---|---|
| 언어·런타임 | C# / .NET 10 | 분석·서버·화면을 한 언어로 |
| C++ 해석 | clangd (LLVM) — LSP 로 대화 | 컴파일러 수준의 정확도. C++ 파서를 직접 만들지 않는다 |
| C# 해석 | Roslyn (Microsoft.CodeAnalysis.CSharp) — .csproj 마다 컴파일 | 빌드·NuGet 복원 없이 .cs 만으로 호출 관계를 정확히 |
| 데스크톱 창 | WinForms + WebView2, self-contained 단일 exe | 회사 PC 에 .NET 설치 없이 exe 하나로 |
| 컴파일 옵션 | `.vcxproj` 읽기 → `compile_commands.json` 생성, vswhere 로 MSVC·Windows SDK 헤더 탐지 | Visual Studio 프로젝트를 그대로 분석 |
| 서버·화면 | ASP.NET Core Blazor Server (127.0.0.1 전용) | 브라우저 화면 + 내 PC 파일 접근. 소스를 보여 주는 서버라 밖에서 접속되지 않게 |
| 그래프 | Cytoscape.js | 노드 보기. 배치는 C# 이 계산해 넘긴다 |
| 아이콘 | Lucide | 보기 전환·버튼 아이콘 |
| 테스트 | xUnit (실제 clangd 로 작은 C++ 예제를 분석하는 종단 테스트 포함), 헤드리스 Edge 화면 점검(`tools/ui-check.mjs`) | 분석 결과와 화면 동작을 함께 확인 |

외부 라이브러리(Cytoscape.js, Lucide)는 `src/ReviewTool.Web/wwwroot/lib/` 에 라이선스와 함께 들어 있어 인터넷 없이 동작한다.

## 기능

| 기능 | 하는 일 |
|---|---|
| 호출 트리 / 노드 보기 | 진입점(패킷 핸들러)부터 호출을 트리·그래프로. 상단 아이콘으로 전환 |
| 코드 스택 | 함수를 누르면 진입점 → 그 함수까지의 코드가 위에서 아래로 쌓임. 호출 줄을 누르면 한 단계 더. 아래 칸으로 이어지는 줄은 파랑 ▶ |
| 문법 색칠 | 코드 스택의 C++·C# 키워드·문자열·주석·숫자·전처리 줄 색(외부 라이브러리 없이 `CodeColor`) |
| 함수 검색 | 왼쪽 검색칸에 함수 이름 → 닿는 진입점 목록과 진입점부터 그 함수까지의 경로 |
| 뒤로/앞으로 | 보던 진입점·경로 기록(Alt+←/→, 마우스 뒤로 단추). 새로 고침해도 마지막 위치 복원 |
| 같은 줄 묶음 | 한 문장에서 이어 부른 호출(`a().b()->c()`)을 트리·노드 보기에서 "N줄" 상자 하나로 |
| 칸 크기 조절 | 칸 경계선을 끌어 너비·높이 조절(두 번 누르면 기본, 브라우저가 기억). 왼쪽 위 이름 = 기본 화면으로 |
| 편집기에서 열기 | "열기"·줄 번호 → Visual Studio / VS Code 의 그 줄 (고치는 것은 편집기에서) |
| 신선도 | 분석 뒤 코드가 바뀌었는지 배지로 표시(최신 / 바뀜 N / 확인 불가) |
| 변경 영향 | 이 함수를 바꾸면 영향받는 패킷과 경로 |
| 전후 비교 | AI 가 고치기 전(기준)과 후의 진입점·함수·호출 변화 |
| 추정 연결 | 디스패처 → 등록 핸들러, 가상 함수 → 구현 후보 ("추정"으로 따로 표시) |
| 문맥 복사 | 코드 스택의 흐름(경로·코드·호출 근거 파일:줄·추정 연결·못 본 것)을 Markdown 으로 복사. 코드 없이 구조만도 가능 |
| AI 에게 질문 | 같은 문맥으로 Claude Code / Codex 를 헤드리스 실행해 답을 화면에 흘려 보여 줌. 자주 묻는 질문(실패 처리, 스레드, 입력 검증, 설명) 버튼 |
| MCP 도구 | Claude Code·Codex 가 분석 결과를 직접 조회(`review-tool mcp`). 아래 "AI 연동" |
| 패킷 여정 | 이 패킷이 오기까지(보낸 프로세스·함수·줄 → 중계 → 여기)와 여기서 보낸 패킷이 가는 프로세스·핸들러. 보낸 곳의 이름(`SendPacket(PacketId::X)`, 보낸 변수의 타입)과 X 를 받는 곳(핸들러, `case`)을 이어 **프로세스를 넘는 흐름**을 만든다 |
| 메시지 연결 | 같은 프로세스 안 메시지(레인 등: `PushMsg(EMsg::X)` → `Regist(EMsg::X, &C::H)`)를 호출 선으로 이어 트리·영향 분석이 레인을 넘는다 |
| 흐름 요약 | 진입점에서 소스 순서대로 표시(설정·스레드·등록·네트워크·DB·캐시·작업 단위)가 붙은 호출만. `main` 이면 시작 순서, 패킷 핸들러면 DB·캐시 접근 순서 |
| 전체 흐름도 | 프로세스(빌드 프로젝트) × 클래스 지도. 클래스 사이 호출 수, 프로세스를 넘는 패킷. 고른 진입점이 거치는 클래스만 밝게 |
| 클래스 다이어그램 | 흐름에 나온 클래스의 부모(상속)와 멤버(clangd 선언). 고른 흐름의 클래스만 또는 전체 |
| 공유 보고서 | 고른 진입점(또는 전체)의 호출 트리·여정·흐름 요약·점검을 Markdown 한 장으로. **기본은 소스 본문과 절대 경로를 뺀다**(회사 코드 공유 대비). 복사·내려받기(.md) |
| C# 대상 | C++ 와 같은 화면·여정·점검을 C# 서버에도. 폴더에 C++ 소스가 없고 .csproj 가 있으면 자동으로 C# 으로 본다 |
| 점검 규칙 | 대상 설정(`checks`)으로 켜는 규칙: 락 안 I/O, 작업 단위·레인 밖 접근, 차감 뒤 지급(되돌리기 없음), 금지된 의존. 지적마다 근거 파일:줄, 확실도(높음·보통·낮음)와 그 이유. 누르면 진입점부터의 경로가 코드 스택으로 열리고 근거 줄이 표시된다. 꺼진 규칙은 "꺼짐"으로 남는다 |

## 리뷰 순서 예

1. AI 에게 고치게 하기 전에 **비교 → 지금 결과를 기준으로 저장**
2. AI 가 고친 뒤 배지가 **바뀜**으로 바뀌면 **다시 분석**
3. **비교**에서 바뀐 함수 확인 → 함수를 누르면 **영향받는 패킷** → 경로를 코드 스택으로 읽기
4. 이상한 곳은 **열기**로 편집기에서 고친다

## AI 연동

**화면에서 질문**: 코드 스택 위 **AI 에게 질문** → AI 고르기 → 질문 → **보내기**. `claude` 또는 `codex` CLI 가 PATH 에 있으면 나온다.
보내기를 누를 때만 실행되고, 보내는 것은 그 흐름의 문맥과 질문뿐이다. AI 는 대상 폴더를 작업 폴더로 받아 **읽기만** 한다
(Claude: Read·Grep·Glob 도구만, 사용자 MCP 서버 없음 / Codex: read-only 샌드박스).

**MCP 로 연결**: 먼저 `dotnet build ReviewTool.slnx` 한 번. 그다음 쓰는 도구에 등록한다(경로는 내 PC 의 저장소 위치).

```powershell
claude mcp add review-tool -- dotnet <저장소>\src\ReviewTool.Cli\bin\Debug\net10.0\ReviewTool.Cli.dll mcp
codex mcp add review-tool -- dotnet <저장소>\src\ReviewTool.Cli\bin\Debug\net10.0\ReviewTool.Cli.dll mcp
```

| 도구 | 하는 일 |
|---|---|
| `list_targets` | 대상 목록, 분석 시각·규모, 신선도, 비교 기준 |
| `get_entrypoints` | 진입점(패킷 핸들러)과 함수 id |
| `find_functions` | 이름으로 함수 id 찾기 |
| `get_flow` | 호출 트리 (추정 연결·멈춘 이유 표시) |
| `get_source` | 함수 코드(줄 번호, 줄마다 부르는 함수)와 부르는 곳 |
| `get_flow_context` | 진입점 → 함수 경로 전체를 Markdown 으로 (화면의 문맥 복사와 같음) |
| `get_impact` | 바꾸면 영향받는 진입점과 경로 |
| `compare` | 기준(수정 전)과 지금 분석의 구조 차이 |
| `get_journey` | 패킷 여정: 오기까지와 나가는 곳(프로세스·함수·줄) |
| `get_flow_summary` | 흐름 요약: 표시(DB·캐시·스레드 등)가 붙은 호출을 순서대로 |
| `get_system_map` | 프로세스별 클래스, 클래스 사이 호출, 프로세스를 넘는 패킷 |
| `get_class` | 클래스 선언: 부모·자식, 멤버, 분석된 함수 |
| `get_checks` | 점검 규칙: 규칙마다 상태(돌았음·꺼짐·오류)와 지적(근거 파일:줄, 확실도, 경로). 규칙·진입점으로 거르기 |

MCP 서버는 저장된 분석 결과(`data/`)를 읽기만 한다. 분석을 돌리거나 파일을 쓰지 않는다.

> 회사 코드: 화면 질문과 MCP 모두 **코드가 AI 서비스로 전송된다**. 회사 PC 에서는 회사의 AI 사용 규정을 먼저 확인한다.
> 전송 없이 쓰려면 문맥 복사의 "코드 포함"을 끄면 구조(이름·파일:줄)만 복사된다.

## 구조

```
브라우저 화면  ──>  ReviewTool.Web (Blazor Server, 127.0.0.1 전용)
                       │ 다시 분석 버튼
                       ▼
                    ReviewTool.Cpp ──LSP──> clangd (C++ 해석, 색인)
                       │
                       ▼
                    data/<대상>/trace.json  (분석 결과: 함수·호출·근거·코드)
```

| 프로젝트 | 역할 |
|---|---|
| `ReviewTool.Core` | 언어와 무관한 결과 형식(`TraceGraph`), 대상 설정(`TargetConfig`), 저장, 조회(여정·요약·흐름도), 메시지 찾기(`MessagePass`, 소스 텍스트) |
| `ReviewTool.Cpp` | C++ 어댑터. vcxproj → compile_commands.json, clangd 호출 계층으로 트리 생성 |
| `ReviewTool.CSharp` | C# 어댑터. .csproj 마다 Roslyn 컴파일(ProjectReference 연결), 진입점·호출 트리·클래스 선언 |
| `ReviewTool.Desktop` | 데스크톱 창. 화면 서버를 같은 프로세스에서 127.0.0.1 빈 포트로 띄우고 WebView2 로 보여 준다 |
| `ReviewTool.Web` | 화면. 진입점 목록 · 호출 트리 · 여정 · 요약 · 흐름도 · 클래스 · 코드 스택 |
| `ReviewTool.Cli` | 화면 없이 분석(`analyze`), 요약(`show`), 공유 보고서(`report`), 점검(`check`, `--strict` 면 확실도 높음 지적이 있을 때 종료 코드 3), clangd 응답 확인(`probe`), MCP 서버(`mcp`) |

## 실행

필요한 것: .NET 10 SDK, LLVM(clangd), Visual Studio C++ 도구(표준 헤더용).

```powershell
.\run.ps1             # 빌드 → http://127.0.0.1:5057 열기 → 화면에서 "다시 분석"
.\run.ps1 -Analyze    # 화면 없이 분석만
```

**데스크톱 창(설치 없이 exe 하나)**: `.\tools\publish-desktop.ps1` → `dist\review-tool.exe`.
.NET 런타임이 들어 있어 설치가 필요 없고(WebView2 는 Windows 11 기본), 화면 파일은 처음 실행할 때 `%LOCALAPPDATA%\review-tool\web\` 에 풀린다.
대상 설정·분석 결과는 exe 옆의 `targets\`, `data\` 에 생긴다. C++ 분석(다시 분석)에는 LLVM(clangd)과 Visual Studio C++ 도구가 따로 있어야 한다(결과 보기·C# 분석은 없어도 된다).
`review-tool.exe --self-test` 는 창 없이 화면 파일이 제대로 나오는지 확인한다.

**공유 보고서**: 화면 위 **보고서** → 범위·항목 고르기 → **만들기** → 복사 또는 내려받기. 명령줄은
`review-tool report targets/<이름>.json [--entry C2SLogin]... [--source] [--abs-paths] [--out 보고서.md]`.

## 대상 추가

폴더만 고르면 설정(`targets/<이름>.json`)을 자동으로 만든다. 이 파일은 내 PC 경로가 들어 있어 git 에 올리지 않는다.

- 화면: 상단 **＋ 대상 추가** → 폴더 고르기 → 제안 확인 → **저장하고 분석**
- 명령줄: `dotnet run --project src/ReviewTool.Cli -- init <분석할 폴더> [--name 이름]`

원본 저장소에는 아무것도 쓰지 않는다(clangd 설정과 색인 캐시도 `data/<이름>/clangd/` 에 생긴다).
폴더에 C++ 소스(.cpp/.h, .vcxproj)가 없고 .csproj·.cs 가 있으면 C# 대상(`"language": "csharp"`)으로 만든다.
C# 진입점 규칙: 메서드 등록(`Register(PacketId.X, HandleX)`), 핸들러 특성(`[PacketHandler]`), API 컨트롤러 액션(`[HttpPost]`),
Handle* 메서드, 시작 함수(`static void Main`, 최상위 문). 패킷 통로 기본값은 C# 모양(`PacketId.X`, `case PacketId.X`)이다.
자동 감지가 틀리면 json 을 직접 고친다.

| 키 | 뜻 |
|---|---|
| `root` | 분석할 저장소 폴더 |
| `include` / `exclude` | 분석 범위(glob). 범위 밖 함수는 "외부"로 표시하고 들어가지 않는다 |
| `entryPoints[].pattern` | 진입점을 찾는 정규식. `(?<method>..)` 필수, `(?<class>..)` 권장 |
| `hideCallees` | 로그처럼 잡음인 함수(정규식). 보이되 안으로 들어가지 않는다 |
| `cpp.compileFrom` | `vcxproj`(프로젝트 파일에서 include·define 읽기) 또는 `flags` |
| `channels` | 메시지 통로. `send`(보내는 호출), `key`(문장에서 이름 뽑기, `(?<key>..)`), `keyName`(이름 모양), `receive`(본문의 받는 곳, case 등), `register`(핸들러 등록, `key`·`method`·`class` → 진입점이 됨), `crossProcess`(true = 다른 프로세스로 가는 패킷, false = 같은 프로세스 안 메시지). 기본값은 흔한 패킷 모양 하나 |
| `tags` | 함수 이름 규칙(정규식)으로 붙이는 표시. 흐름 요약에 쓴다. 기본값: 설정·스레드·등록·네트워크·DB·캐시·작업 단위 |

프로세스는 진입점 파일에서 가장 가까운 빌드 프로젝트 폴더(`.vcxproj`·`CMakeLists.txt`)로 정한다. 프로젝트 폴더가 없으면 진입점 파일들의 공통 폴더 아래 첫 폴더.

같은 프로세스 안 메시지 예(레인):

```json
"channels": [
  { "name": "패킷", "crossProcess": true, "send": "\\bSend\\w*\\s*\\(", "key": "\\bPacketId::(?<key>\\w+)", "keyName": "^[A-Z][A-Za-z0-9]?2[A-Z]\\w+$",
    "receive": "\\bcase\\s+(?:\\w+::)*PacketId::(?<key>\\w+)" },
  { "name": "레인 메시지", "crossProcess": false, "send": "\\bPushMsg\\b", "key": "\\b(?<key>E\\w+Msg::\\w+)",
    "register": "\\bRegist\\(\\s*(?<key>E\\w+Msg::\\w+)\\s*,\\s*&(?<class>\\w+)::(?<method>\\w+)" }
]
```

### 점검 규칙(`checks`)

규칙은 종류(`kind`) 넷 중 하나다. 정규식은 함수 한정 이름(`Ns::Class::Method`)에 건다(`scopeOpen`·`scopeClose` 만 본문 줄).
점검은 저장된 분석 결과로 그때그때 돌기 때문에 규칙을 고친 뒤 **재분석 없이** 화면의 "다시 점검"만 누르면 된다(설정 지문에도 넣지 않는다).

| `kind` | 걸리는 것 | 쓰는 키 |
|---|---|---|
| `forbidInScope` | 본문에서 `scopeOpen` 줄부터 그 블록 끝(또는 `scopeClose`)까지 `forbid` 함수·`forbidTags` 표시를 부름. `depth` 단계 아래 함수까지 본다. `scope: "statement"` 면 그 문장만(`ref.Write()->Foo()` 같은 임시 잠금) | `scopeOpen`, `scopeClose`, `scope`, `forbid`, `forbidTags`, `depth` |
| `requireWithin` | 진입점에서 `subject` 함수까지 `within` 함수를 거치지 않고 닿음. `withinEdges`(예: `["Message"]`)를 지나면 안으로 들어간 것, `withinEntry` 에 맞는 진입점(표시 이름·묶음·찾은 규칙 이름)은 처음부터 안 | `subject`, `within`, `withinEdges`, `withinEntry` |
| `order` | 한 흐름(소스 순서)에서 `first` 뒤에 `then` 을 부르는데 `unless` 가 흐름 어디에도 없음 | `first`, `then`, `unless` |
| `dependency` | `from` 파일(glob)의 함수가 `to` 파일 함수를 부르거나 `#include` 함 | `from`, `to` |

기본값은 범용 규칙 5개이고 **락 안 I/O 만 켜져 있다**. 나머지는 이름이 대상마다 달라 `"enabled": true` 와 정규식을 맞춰 켠다.
`checks` 를 json 에 적으면 기본값을 대신한다(쓰려는 규칙을 모두 적는다).

```json
"checks": [
  { "name": "레인 밖에서 존 상태 변경", "kind": "requireWithin",
    "subject": "(?:^|::)(?:UnitContainer|Zone)::(?:Add|Remove)\\w*$",
    "withinEntry": "^(레인 메시지|멤버 함수 등록)$", "withinEdges": ["Message"] },
  { "name": "Core 가 서버에 의존", "kind": "dependency", "from": ["Server/Core/**"], "to": ["Server/*Server/**"] }
]
```

확실도: **높음** = 정적 호출·`#include` 가 그 자리에 직접 있음, **보통** = 아래 함수 안에서·이름 표시로·소스 순서로 판단,
**낮음** = 추정 연결(등록·메시지)을 지남. 어느 것이든 이름 규칙에 기댄 추정이라 지적은 "볼 곳"이지 판정이 아니다.

## 한계 (화면의 "못 본 것"에도 나온다)

- **정적 분석**이다. 가상 함수·`std::function`·함수 포인터로 부르는 곳은 선언까지만 간다(`정의 없음`).
- 디스패처처럼 "등록 → 나중에 호출" 구조는 등록 지점을 진입점으로 잡는 방식으로 우회한다.
- 패킷 여정·메시지 연결·표시는 **소스 텍스트(정규식) 추정**이다. 실행 중에 정해지는 패킷 id(중계 봉투 안)는 이름으로 잇지 못하고 "안쪽 호출로 이어짐"으로 보인다. 보내는 곳은 분석한 함수(진입점에서 닿은 함수) 안에서만 찾는다.
- 함수 본문 범위는 중괄호 짝으로 찾는 근사치다(매크로로 만든 함수는 틀릴 수 있다).
- C# 은 .cs 파일만 해석한다(빌드·NuGet 복원 없음). 참조는 이 도구가 도는 .NET 런타임 어셈블리뿐이라 NuGet 패키지 형식의 호출은 풀지 못하고(진단에 수가 나온다),
  소스 생성기(Razor 컴포넌트 등)가 만든 코드는 보지 않는다. 한정 이름은 C++ 과 같이 `Ns::Class::Method` 로 보인다.
- 점검 규칙은 분석에서 펼친 함수만 본다(`hideCallees` 로 숨긴 함수 안, 람다 안, 깊이 제한 너머는 안 본다). 지적이 없다는 것은 "찾지 못했다"는 뜻이다.
  `#include` 는 컴파일러의 include 경로를 보지 않고, 같은 이름 파일 중 가장 가까운 폴더의 것으로 본다.
