using ReviewTool.Web;

// 명령줄 실행: http://127.0.0.1:5057 (설정 ReviewTool:Port). 데스크톱 창은 ReviewTool.Desktop.
var app = ReviewToolServer.Build(args);
if (app.Logger.IsEnabled(LogLevel.Information))
{
    app.Logger.LogInformation("review-tool: {Url}", "http://127.0.0.1:" + app.Configuration.GetValue("ReviewTool:Port", 5057));
}
app.Run();
