using System.Net;
using ReviewTool.Web.Components;
using ReviewTool.Web.Services;

namespace ReviewTool.Web;

// 화면 서버 구성. 명령줄 실행(Program.cs)과 데스크톱 창(ReviewTool.Desktop)이 같이 쓴다.
public static class ReviewToolServer
{
    // port 0 이면 빈 포트를 고른다(데스크톱). 주소는 항상 루프백(127.0.0.1)이다. 바꾸는 옵션은 두지 않는다.
    public static WebApplication Build(string[] args, int? port = null, string? root = null, string? contentRoot = null,
                                       string? staticAssetsManifest = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = contentRoot,
            // 정적 파일 목록(staticwebassets)은 실행 파일 이름으로 찾는다. 데스크톱이 이 구성을 빌려 써도 자기 것을 찾게.
            ApplicationName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name,
        });
        // appsettings.json 과 같은 기본값(데스크톱은 화면 파일 폴더에서 시작해 appsettings 가 없다)
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        if (root is not null)
        {
            builder.Configuration["ReviewTool:Root"] = root;
        }

        // 소스 코드를 보여 주는 서버라 내 PC(루프백)에서만 접속되게 한다.
        var listen = port ?? builder.Configuration.GetValue("ReviewTool:Port", 5057);
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, listen));
        // 게시(publish)하지 않고 dotnet run 으로 쓸 때는 빌드 출력의 정적 파일을 쓴다(게시본에는 목록이 없어 아무것도 안 함).
        builder.WebHost.UseStaticWebAssets();

        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();
        builder.Services.AddSingleton<TraceWorkspace>();
        builder.Services.AddSingleton<EditorLauncher>();
        builder.Services.AddSingleton<AiAssistant>();

        var app = builder.Build();

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
        }
        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

        app.UseAntiforgery();

        app.MapStaticAssets(staticAssetsManifest);
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();
        return app;
    }

    // 시작한 서버의 실제 주소(port 0 으로 띄웠을 때 고른 포트)
    public static string Address(WebApplication app) =>
        app.Urls.FirstOrDefault()?.Replace("[::1]", "127.0.0.1").TrimEnd('/') + "/";
}
