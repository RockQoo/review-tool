using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ReviewTool.CSharp;

// .csproj 마다 컴파일 하나(ProjectReference 는 CompilationReference 로). 프로젝트마다 있는 Program 같은
// 같은 이름의 형식이 한 컴파일에 섞여 이름 풀기가 깨지지 않게 한다. .csproj 밖의 .cs 는 "프로젝트 없음" 하나로 묶는다.
internal sealed class ProjectSet
{
    // 프로젝트에 ImplicitUsings 가 켜져 있으면 컴파일러가 만들어 넣는 global using(Microsoft.NET.Sdk 기본값)
    private const string ImplicitUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;

    private readonly Dictionary<SyntaxTree, CSharpCompilation> owner_ = [];
    private readonly Dictionary<SyntaxTree, SemanticModel> models_ = [];

    public List<CSharpCompilation> Compilations { get; } = [];
    public List<SyntaxTree> Trees { get; } = [];   // 분석 범위의 소스(implicit using 은 빼고)
    public List<string> Notes { get; } = [];       // 분석 진단에 그대로 남길 것

    public SemanticModel Model(SyntaxTree tree) =>
        models_.TryGetValue(tree, out var m) ? m : models_[tree] = owner_[tree].GetSemanticModel(tree);

    public bool Owns(SyntaxTree tree) => owner_.ContainsKey(tree);

    public static ProjectSet Build(string root, IReadOnlyList<string> sourceFiles, IEnumerable<string> projectFiles, CancellationToken ct)
    {
        var set = new ProjectSet();
        var parse = new CSharpParseOptions(LanguageVersion.Preview);
        var references = RuntimeReferences();
        // 폴더 → 그 폴더의 .csproj 들. 한 폴더에 여럿이면(멀티 타기팅 변형 등) 소스를 나눌 수 없어 하나로 묶는다(참조는 합집합).
        var projects = projectFiles.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .GroupBy(p => Path.GetDirectoryName(p)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Order(StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, list) in projects.Where(kv => kv.Value.Count > 1))
        {
            set.Notes.Add($"한 폴더에 .csproj 가 {list.Count}개라 하나로 묶어 해석함: {string.Join(", ", list.Select(Path.GetFileName))}");
        }

        // 파일 → 가장 가까운 프로젝트 폴더("" = 프로젝트 없음)
        var byProject = sourceFiles.GroupBy(f => Owner(f, projects.Keys) ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var trees = sourceFiles.AsParallel().WithCancellation(ct)
            .Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), parse, path: f, cancellationToken: ct))
            .ToDictionary(t => t.FilePath, StringComparer.OrdinalIgnoreCase);

        var built = new Dictionary<string, CSharpCompilation?>(StringComparer.OrdinalIgnoreCase);
        CSharpCompilation? Compile(string dir, int depth)
        {
            if (built.TryGetValue(dir, out var done))
            {
                return done;   // null = 만드는 중(순환 참조)
            }
            built[dir] = null;
            var refs = new List<MetadataReference>(references);
            if (depth < 32 && projects.TryGetValue(dir, out var csprojs))
            {
                foreach (var dep in csprojs.SelectMany(ProjectReferences).Select(Path.GetDirectoryName).OfType<string>()
                             .Distinct(StringComparer.OrdinalIgnoreCase).Where(d => !string.Equals(d, dir, StringComparison.OrdinalIgnoreCase)))
                {
                    if (projects.ContainsKey(dep) && Compile(dep, depth + 1) is { } c)
                    {
                        refs.Add(c.ToMetadataReference());
                    }
                }
            }
            var own = (byProject.GetValueOrDefault(dir) ?? []).Select(f => trees[f]).ToList();
            var all = own.Append(CSharpSyntaxTree.ParseText(ImplicitUsings, parse, path: "")).ToList();
            var mine = projects.GetValueOrDefault(dir) ?? [];
            var name = mine.Count == 0 ? "loose" : Path.GetFileNameWithoutExtension(mine[0]);
            var compilation = CSharpCompilation.Create(name, all, refs,
                new CSharpCompilationOptions(mine.Any(IsExe) ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary,
                                             allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));
            foreach (var t in own)
            {
                set.owner_[t] = compilation;
                set.Trees.Add(t);
            }
            set.Compilations.Add(compilation);
            built[dir] = compilation;
            return compilation;
        }
        foreach (var dir in byProject.Keys.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            Compile(dir, 0);
        }
        return set;
    }

    private static string? Owner(string file, IEnumerable<string> projectDirs) =>
        projectDirs.Where(d => file.StartsWith(d.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.Length).FirstOrDefault();

    private static IEnumerable<string> ProjectReferences(string csproj)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(csproj);
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var r in doc.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
        {
            var include = (string?)r.Attribute("Include");
            if (!string.IsNullOrWhiteSpace(include) && !include.Contains("$("))
            {
                yield return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(csproj)!, include.Replace('\\', Path.DirectorySeparatorChar)));
            }
        }
    }

    private static bool IsExe(string csproj)
    {
        try
        {
            var text = File.ReadAllText(csproj);
            return text.Contains("<OutputType>Exe</OutputType>", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("<OutputType>WinExe</OutputType>", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    // 이 도구가 도는 .NET 런타임의 어셈블리(System.*, 웹 화면에서 돌면 ASP.NET Core 도). 대상의 NuGet 패키지는 없다.
    private static List<MetadataReference> RuntimeReferences() =>
        ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

    // 최상위 문(top-level statements) 진입점: 컴파일러가 만드는 Main. 있으면 프로젝트마다 하나.
    public IEnumerable<IMethodSymbol> TopLevelEntryPoints(CancellationToken ct)
    {
        // GetEntryPoint 는 컴파일 오류(풀지 못한 패키지 형식)가 있으면 비어서, 최상위 문이 있는 파일에 직접 묻는다
        foreach (var tree in Trees)
        {
            if (tree.GetRoot(ct) is CompilationUnitSyntax unit && unit.Members.OfType<GlobalStatementSyntax>().Any() &&
                Model(tree).GetDeclaredSymbol(unit, ct) is IMethodSymbol main)
            {
                yield return main;
            }
        }
    }

    public int UnresolvedNames(CancellationToken ct) =>
        Compilations.Sum(c => c.GetDiagnostics(ct).Count(d => d.Id is "CS0246" or "CS0234" or "CS0103"));
}
