# review-tool 실행: 빌드하고 화면 서버를 띄운 뒤 브라우저를 연다. 끝내려면 Ctrl+C.
#   .\run.ps1                       화면 (http://127.0.0.1:5057). 대상이 없으면 화면에서 "대상 추가"
#   .\run.ps1 -Analyze -Target 이름  화면 없이 targets\이름.json 분석만 (대상이 하나뿐이면 -Target 생략 가능)
param(
    [switch]$Analyze,
    [string]$Target = "",
    [int]$Port = 5057
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

dotnet build ReviewTool.slnx -v q --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($Analyze) {
    if ($Target -eq "") {
        $configs = @(Get-ChildItem targets -Filter *.json -ErrorAction SilentlyContinue)
        if ($configs.Count -ne 1) {
            Write-Host "대상을 고르세요: .\run.ps1 -Analyze -Target <이름>  (있는 것: $($configs.BaseName -join ', '))"
            Write-Host "대상 만들기: dotnet run --project src/ReviewTool.Cli -- init <분석할 폴더>"
            exit 2
        }
        $Target = $configs[0].BaseName
    }
    dotnet run --project src/ReviewTool.Cli --no-build -- analyze "targets/$Target.json"
    exit $LASTEXITCODE
}

$url = "http://127.0.0.1:$Port/"
Start-Job -ScriptBlock {
    param($u)
    for ($i = 0; $i -lt 60; $i++) {
        try { Invoke-WebRequest $u -UseBasicParsing -TimeoutSec 1 | Out-Null; Start-Process $u; return } catch { Start-Sleep -Milliseconds 500 }
    }
} -ArgumentList $url | Out-Null

dotnet run --project src/ReviewTool.Web --no-build --no-launch-profile -- --ReviewTool:Port=$Port
