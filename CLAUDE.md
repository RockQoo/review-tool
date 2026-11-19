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
- 언어 어댑터는 `ITraceAnalyzer` 를 구현하고 `TraceGraph` 만 채운다. 화면은 언어를 모른다.
- "못 본 것"은 `TraceGraph.Diagnostics` / `TraceNode.Stop` 으로 남긴다. 빈 호출 목록을 "호출 없음"으로 보이게 하지 않는다.
- 주석과 문서는 한국어.

## clangd 사용 요령 (실측, 바꾸기 전에 읽을 것)

- `prepareCallHierarchy` 는 정의가 아니라 **헤더 선언**을 줄 때가 많고 `detail`(한정 이름)이 비어 있다.
- `outgoingCalls` 는 item 의 `data`(심볼 ID)로 색인을 조회한다. 위치가 선언이어도 호출 목록은 나오지만,
  **`fromRanges` 는 item 이 가리키는 파일 기준**이라 선언 item 으로 물으면 호출 위치가 비어 버린다.
  그래서 정의를 `workspace/symbol` 로 찾아 그 위치에서 다시 prepare 한다(`CallTreeWalker.RelocateItemAsync`).
- item 의 `range` 는 이름 토큰뿐이다. 본문 범위는 `FunctionBody` 가 소스에서 중괄호 짝으로 찾는다.
- 새 Visual Studio(18) 는 clang 이 스스로 못 찾아 `<cstddef>` 부터 깨진다. `MsvcToolchain` 이 vswhere 로 찾아 `-isystem` 으로 넘긴다.
- 색인이 끝나기 전 `outgoingCalls` 는 빈 답을 준다. `backgroundIndexProgress` 의 end 를 기다린다.
- **파일을 열수록(didOpen) 결과가 줄 수 있다.** 연 파일의 동적 색인이 그 파일이 포함한 헤더의 인라인 함수 호출 기록을
  가려 `outgoingCalls` 가 비는 것을 실측했다(asio-server 호출 908 → 901). 열기는 꼭 필요한 곳(진입점 prepare,
  정의 재탐색)만 하고, 오버로드 판별 같은 것은 소스 텍스트로 한다(`FunctionBody.ParameterTypes`).
  분석 로직을 바꾸면 asio-server 결과 수(진입점 28, 함수 304, 호출 908)가 줄지 않았는지 확인한다.

## 확인 방법

```powershell
dotnet test ReviewTool.slnx                                   # 단위 + clangd 종단 테스트(작은 예제)
dotnet run --project src/ReviewTool.Cli -- init <분석할 폴더>    # 설정이 없을 때
dotnet run --project src/ReviewTool.Cli -- analyze targets/<이름>.json
node tools/ui-check.mjs                                        # 서버가 떠 있을 때: 헤드리스 Edge 로 클릭·스크린샷(data/ui-check/)
```
