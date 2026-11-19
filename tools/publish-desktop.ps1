# 데스크톱 창(단일 exe) 만들기: dist\review-tool.exe
#   .\tools\publish-desktop.ps1
# .NET 런타임을 함께 넣어(self-contained) 설치 없이 실행된다. WebView2 런타임은 Windows 11 에 기본으로 있다.
# 화면 파일(wwwroot)은 exe 안에 zip 으로 들어가 처음 실행할 때 %LOCALAPPDATA%\review-tool\web\ 에 풀린다.
# 대상 설정·분석 결과는 exe 옆의 targets\, data\ 에 생긴다. 분석(재분석)에는 LLVM(clangd)과 Visual Studio C++ 도구가 따로 필요하다.
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo "src\ReviewTool.Desktop\ReviewTool.Desktop.csproj"
$work = Join-Path $repo "src\ReviewTool.Desktop\obj\desktop-publish"
$stage = Join-Path $work "stage"
$single = Join-Path $work "single"
$zip = Join-Path $work "webassets.zip"
$out = Join-Path $repo "dist"

if (Test-Path $work) { Remove-Item -Recurse -Force $work }

# 1) 일반 게시: 화면 파일(wwwroot, 정적 파일 목록)을 얻는다
dotnet publish $project -c Release -r win-x64 --self-contained -o $stage --nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$manifest = "ReviewTool.Desktop.staticwebassets.endpoints.json"
if (-not (Test-Path (Join-Path $stage $manifest))) { Write-Host "정적 파일 목록이 없음: $manifest"; exit 1 }

# 2) 화면 파일을 zip 으로(경로 구분자 '/', Windows 기본 tar)
& tar.exe -a -c -f $zip -C $stage wwwroot $manifest
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# 3) 단일 exe: zip 을 리소스로 넣는다
dotnet publish $project -c Release -r win-x64 --self-contained -o $single --nologo -v q `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    "-p:WebAssetsZip=$zip"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

New-Item -ItemType Directory -Force $out | Out-Null
$exe = Join-Path $out "review-tool.exe"
Copy-Item (Join-Path $single "ReviewTool.Desktop.exe") $exe -Force
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "만듦: $exe ($size MB)"

# 4) 창 없이 확인(화면·정적 파일이 오는지)
& $exe --self-test | Write-Host
if ($LASTEXITCODE -ne 0) { Write-Host "self-test 실패"; exit 1 }
Write-Host "self-test 통과"
