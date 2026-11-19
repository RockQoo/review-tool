using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.AspNetCore.Builder;
using ReviewTool.Web;

namespace ReviewTool.Desktop;

// 데스크톱 창 실행. 설치 없이 exe 하나로 쓰는 용도(회사 PC 등). 브라우저 대신 WebView2 창 안에 같은 화면을 띄운다.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        WebApplication app;
        try
        {
            // 대상 설정·분석 결과(targets/, data/)는 exe 옆(개발 중이면 저장소 루트)에 둔다.
            // 화면 파일은 exe 안의 zip 을 사용자 폴더에 풀어 쓴다(개발 중이면 빌드 출력).
            var web = WebAssets.Prepare();
            app = ReviewToolServer.Build(args, port: 0, root: DataRoot(), contentRoot: web.ContentRoot, staticAssetsManifest: web.Manifest);
            app.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            if (Array.IndexOf(args, "--self-test") >= 0)
            {
                Console.WriteLine("FAIL 서버 시작: " + ex);
                return 1;
            }
            MessageBox.Show("화면 서버를 시작하지 못했습니다.\n\n" + ex.Message, "review-tool", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        if (Array.IndexOf(args, "--self-test") >= 0)
        {
            try
            {
                return SelfTest(ReviewToolServer.Address(app)).GetAwaiter().GetResult();
            }
            finally
            {
                app.StopAsync().GetAwaiter().GetResult();
            }
        }
        try
        {
            Application.Run(new MainForm(ReviewToolServer.Address(app)));
        }
        finally
        {
            Task.Run(() => app.StopAsync()).Wait(TimeSpan.FromSeconds(5));
        }
        return 0;
    }

    // 창 없이 확인: 화면과 정적 파일(단일 exe 에서 풀린 wwwroot·framework 파일)이 오는지. 결과는 표준 출력, 실패하면 1.
    private static async Task<int> SelfTest(string address)
    {
        using var http = new System.Net.Http.HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(20) };
        var failed = 0;
        var home = await http.GetStringAsync("");
        const string styles = "ReviewTool.Web.styles.css";   // 컴포넌트 CSS 묶음(데스크톱도 Web 이름 그대로 제공된다)
        Console.WriteLine($"address {address}");
        foreach (var path in new[] { "app.css", styles, "_framework/blazor.web.js", "lib/cytoscape/cytoscape.min.js", "lib/lucide/shield-alert.svg", "js/graph.js" })
        {
            // 화면은 지문 붙은 이름(app.abc123.css)으로 참조할 수 있어 원래 이름으로도 확인한다
            using var res = await http.GetAsync(path);
            var ok = res.IsSuccessStatusCode;
            failed += ok ? 0 : 1;
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {(int)res.StatusCode} {path}");
        }
        // 화면 HTML 이 실제로 참조하는 파일(지문 붙은 이름)도 전부 받아 본다
        var refs = System.Text.RegularExpressions.Regex.Matches(home, @"(?:href|src)=""(?<u>[^""#?]+\.(?:css|js))""")
            .Select(m => m.Groups["u"].Value).Where(u => !u.Contains("://", StringComparison.Ordinal)).Distinct().ToList();
        foreach (var path in refs)
        {
            using var res = await http.GetAsync(path.TrimStart('/'));
            var ok = res.IsSuccessStatusCode;
            failed += ok ? 0 : 1;
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {(int)res.StatusCode} {path} (화면이 참조)");
        }
        failed += refs.Count >= 3 ? 0 : 1;
        Console.WriteLine($"root {DataRoot()}");
        return failed == 0 ? 0 : 1;
    }

    // exe 가 있는 폴더부터 위로 ReviewTool.slnx 를 찾으면(개발 중) 저장소 루트, 아니면 exe 폴더
    private static string DataRoot()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        for (var dir = new DirectoryInfo(exeDir); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ReviewTool.slnx")))
            {
                return dir.FullName;
            }
        }
        return exeDir;
    }
}
