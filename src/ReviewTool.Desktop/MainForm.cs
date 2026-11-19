using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ReviewTool.Desktop;

// WebView2 창. 화면 서버 주소(127.0.0.1) 밖으로 나가는 링크는 창 안에서 열지 않고 기본 브라우저로 넘긴다.
internal sealed class MainForm : Form
{
    private readonly string address_;
    private readonly WebView2 view_ = new() { Dock = DockStyle.Fill };

    public MainForm(string address)
    {
        address_ = address;
        Text = "review-tool (여는 중)";   // 화면이 열리면 페이지 제목으로 바뀐다
        Size = new Size(1600, 1000);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(view_);
        Load += async (_, _) => await StartAsync();
    }

    private async System.Threading.Tasks.Task StartAsync()
    {
        try
        {
            // 브라우저 데이터(쿠키·localStorage)는 exe 옆이 아니라 사용자 폴더에(읽기 전용 위치에서도 실행되게)
            var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "review-tool", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await view_.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // WebView2 런타임이 없으면(Windows 10 일부) 기본 브라우저로 연다
            MessageBox.Show("WebView2 런타임을 찾지 못해 기본 브라우저로 엽니다.\n" + ex.Message, "review-tool");
            Process.Start(new ProcessStartInfo(address_) { UseShellExecute = true });
            return;
        }
        view_.CoreWebView2.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenOutside(e.Uri);
        };
        view_.CoreWebView2.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(address_, StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                OpenOutside(e.Uri);
            }
        };
        view_.CoreWebView2.DocumentTitleChanged += (_, _) => Text = view_.CoreWebView2.DocumentTitle;
        view_.CoreWebView2.Navigate(address_);
    }

    private static void OpenOutside(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
        {
            Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
        }
    }
}
