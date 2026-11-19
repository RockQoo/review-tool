using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace ReviewTool.Desktop;

// 단일 exe 에 넣은 화면 파일(webassets.zip: wwwroot/ + *.staticwebassets.endpoints.json)을 사용자 폴더에 풀어 둔다.
// 내용 해시별 폴더라 같은 exe 는 한 번만 푼다. 리소스가 없으면(개발 중 dotnet run) 빌드 출력을 그대로 쓴다.
internal static class WebAssets
{
    public sealed record Location(string ContentRoot, string? Manifest);

    public static Location Prepare()
    {
        using var zip = Assembly.GetExecutingAssembly().GetManifestResourceStream("webassets.zip");
        if (zip is null)
        {
            return new Location(AppContext.BaseDirectory, null);
        }
        var hash = Convert.ToHexString(SHA256.HashData(zip))[..16].ToLowerInvariant();
        zip.Position = 0;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "review-tool", "web");
        var dir = Path.Combine(root, hash);
        var done = Path.Combine(dir, ".done");
        if (!File.Exists(done))
        {
            // 다른 창이 동시에 풀어도 깨지지 않게 임시 폴더에 푼 뒤 옮긴다
            var temp = Path.Combine(root, hash + "." + Environment.ProcessId + ".tmp");
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
            ZipFile.ExtractToDirectory(zip, temp);
            File.WriteAllText(Path.Combine(temp, ".done"), hash);
            try
            {
                Directory.Move(temp, dir);
            }
            catch (IOException) when (File.Exists(done))
            {
                Directory.Delete(temp, recursive: true);   // 다른 프로세스가 먼저 풀었다
            }
        }
        var manifest = Directory.GetFiles(dir, "*.staticwebassets.endpoints.json");
        return new Location(dir, manifest.Length > 0 ? manifest[0] : null);
    }
}
