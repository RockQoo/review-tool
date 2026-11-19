using System.Net;
using ReviewTool.Web.Components;
using ReviewTool.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// 소스 코드를 보여 주는 서버라 내 PC(루프백)에서만 접속되게 한다. 바꾸는 옵션은 두지 않는다.
var port = builder.Configuration.GetValue("ReviewTool:Port", 5057);
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
// 게시(publish)하지 않고 dotnet run 으로 쓰는 로컬 도구라, 환경과 무관하게 빌드 출력의 정적 파일을 쓴다.
builder.WebHost.UseStaticWebAssets();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<TraceWorkspace>();
builder.Services.AddSingleton<EditorLauncher>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Logger.LogInformation("review-tool: http://127.0.0.1:{Port}", port);
app.Run();
