# review-tool

AI가 만든 서버 코드를 **사람이 흐름으로 리뷰**하기 위한 개인 도구.
진입점(패킷 핸들러)을 고르면 호출 트리가 나오고, 함수를 누르면 **진입점부터 그 함수까지의 코드가 위에서 아래로 이어 붙는다**.
VS에서 F12로 따라 들어가면 앞 코드가 안 보이는 문제를 없애는 "끊기지 않는 콜스택 뷰어"다.

## 구조

```
브라우저 화면  ──>  ReviewTool.Web (Blazor Server, 127.0.0.1 전용)
                       │ 재분석 버튼
                       ▼
                    ReviewTool.Cpp ──LSP──> clangd (C++ 해석, 색인)
                       │
                       ▼
                    data/<대상>/trace.json  (분석 결과: 함수·호출·근거·코드)
```

| 프로젝트 | 역할 |
|---|---|
| `ReviewTool.Core` | 언어와 무관한 결과 형식(`TraceGraph`), 대상 설정(`TargetConfig`), 저장 |
| `ReviewTool.Cpp` | C++ 어댑터. vcxproj → compile_commands.json, clangd 호출 계층으로 트리 생성 |
| `ReviewTool.Web` | 화면. 진입점 목록 · 호출 트리 · 코드 스택 |
| `ReviewTool.Cli` | 화면 없이 분석(`analyze`), 요약(`show`), clangd 응답 확인(`probe`) |

## 실행

필요한 것: .NET 10 SDK, LLVM(clangd), Visual Studio C++ 도구(표준 헤더용).

```powershell
.\run.ps1             # 빌드 → http://127.0.0.1:5057 열기 → 화면에서 "재분석"
.\run.ps1 -Analyze    # 화면 없이 분석만
```

## 대상 추가

폴더만 고르면 설정(`targets/<이름>.json`)을 자동으로 만든다. 이 파일은 내 PC 경로가 들어 있어 git 에 올리지 않는다.

- 화면: 상단 **＋ 대상 추가** → 폴더 고르기 → 제안 확인 → **저장하고 분석**
- 명령줄: `dotnet run --project src/ReviewTool.Cli -- init <분석할 폴더> [--name 이름]`

원본 저장소에는 아무것도 쓰지 않는다(clangd 설정과 색인 캐시도 `data/<이름>/clangd/` 에 생긴다).
자동 감지가 틀리면 json 을 직접 고친다.

| 키 | 뜻 |
|---|---|
| `root` | 분석할 저장소 폴더 |
| `include` / `exclude` | 분석 범위(glob). 범위 밖 함수는 "외부"로 표시하고 들어가지 않는다 |
| `entryPoints[].pattern` | 진입점을 찾는 정규식. `(?<method>..)` 필수, `(?<class>..)` 권장 |
| `hideCallees` | 로그처럼 잡음인 함수(정규식). 보이되 안으로 들어가지 않는다 |
| `cpp.compileFrom` | `vcxproj`(프로젝트 파일에서 include·define 읽기) 또는 `flags` |

## 한계 (화면의 "못 본 것"에도 나온다)

- **정적 분석**이다. 가상 함수·`std::function`·함수 포인터로 부르는 곳은 선언까지만 간다(`정의 없음`).
- 디스패처처럼 "등록 → 나중에 호출" 구조는 등록 지점을 진입점으로 잡는 방식으로 우회한다.
- 함수 본문 범위는 중괄호 짝으로 찾는 근사치다(매크로로 만든 함수는 틀릴 수 있다).
