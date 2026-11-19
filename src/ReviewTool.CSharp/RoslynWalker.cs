using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ReviewTool.Core;
using TypeInfo = ReviewTool.Core.TypeInfo;

namespace ReviewTool.CSharp;

// 진입점에서 호출을 따라 내려가며 노드·선을 만든다. 한정 이름은 C++ 쪽과 같이 "::" 로 잇는다(Ns::Class::Method)
// — 화면·흐름도·표시 규칙이 이름을 언어와 무관하게 다루게.
internal sealed class RoslynWalker(TargetConfig config, TraceGraph graph, ProjectSet projects, Func<string, bool> filter)
{
    private readonly Regex[] hide_ = config.HideCallees.Select(p => new Regex(p)).ToArray();
    private readonly Dictionary<string, IMethodSymbol> symbols_ = [];
    private readonly Dictionary<(string From, string To), TraceEdge> edges_ = [];
    private List<INamedTypeSymbol>? sourceTypes_;
    // 구현 후보로 찾은 형식(호출 트리에 직접 안 나와도 클래스 다이어그램에 넣는다)
    private readonly HashSet<INamedTypeSymbol> implTypes_ = new(SymbolEqualityComparer.Default);

    public bool NodeLimitHit { get; private set; }
    public int UnresolvedCalls { get; private set; }

    private SemanticModel Model(SyntaxTree tree) => projects.Model(tree);

    private string Relative(string path) => Path.GetRelativePath(config.Root, path).Replace('\\', '/');

    private bool InScope(SyntaxTree tree) =>
        projects.Owns(tree) && filter(Relative(tree.FilePath));

    // ---- 진입점 ----

    public string? ResolveEntry(CSharpTraceAnalyzer.EntryCandidate candidate)
    {
        var tree = projects.Trees.FirstOrDefault(t => string.Equals(t.FilePath, candidate.File, StringComparison.OrdinalIgnoreCase));
        IEnumerable<BaseMethodDeclarationSyntax> Methods(SyntaxTree t) =>
            t.GetRoot().DescendantNodes().OfType<BaseMethodDeclarationSyntax>().Where(m => NameOf(m) == candidate.Method &&
                (candidate.ClassName is null || m.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text == candidate.ClassName));

        // 1) 같은 파일: 정규식이 걸린 자리를 품은 선언, 없으면 가장 가까운 것
        BaseMethodDeclarationSyntax? decl = null;
        if (tree is not null)
        {
            var local = Methods(tree).ToList();
            decl = local.FirstOrDefault(m => m.FullSpan.Contains(candidate.Index)) ??
                   local.OrderBy(m => Math.Abs(m.SpanStart - candidate.Index)).FirstOrDefault();
        }
        // 2) 다른 파일(등록하는 줄만 이 파일에 있을 때): 이름(과 클래스)이 하나로 정해지면
        if (decl is null)
        {
            var all = projects.Trees.Where(InScope).SelectMany(Methods).ToList();
            decl = all.Count == 1 ? all[0] : null;
        }
        if (decl is null || Model(decl.SyntaxTree).GetDeclaredSymbol(decl) is not IMethodSymbol symbol)
        {
            return null;
        }
        return EnsureNode(symbol);
    }

    private static string NameOf(BaseMethodDeclarationSyntax m) => m switch
    {
        MethodDeclarationSyntax x => x.Identifier.Text,
        ConstructorDeclarationSyntax x => x.Identifier.Text,
        _ => "",
    };

    public string EnsureEntry(IMethodSymbol symbol) => EnsureNode(symbol);

    // ---- 걷기 ----

    public void Walk(string rootId, CancellationToken ct)
    {
        var depth = new Dictionary<string, int> { [rootId] = 0 };
        var queue = new Queue<string>();
        queue.Enqueue(rootId);
        var expanded = new HashSet<string>();
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var id = queue.Dequeue();
            var node = graph.Nodes[id];
            if (node.Stop is StopReason.External or StopReason.Hidden or StopReason.NoDefinition || !expanded.Add(id))
            {
                continue;
            }
            if (depth[id] >= config.MaxDepth)
            {
                node.Stop = StopReason.DepthLimit;
                continue;
            }
            if (node.Stop == StopReason.DepthLimit)
            {
                node.Stop = null;   // 다른 진입점에서 더 얕게 닿았다
            }
            foreach (var callee in Calls(id))
            {
                if (graph.Nodes.Count >= config.MaxNodes && !graph.Nodes.ContainsKey(callee))
                {
                    node.Stop = StopReason.NodeLimit;
                    NodeLimitHit = true;
                    break;
                }
                if (depth.TryAdd(callee, depth[id] + 1) || depth[callee] > depth[id] + 1)
                {
                    depth[callee] = depth[id] + 1;
                    expanded.Remove(callee);
                    queue.Enqueue(callee);
                }
            }
        }
    }

    // 함수 본문(람다·지역 함수 포함)의 호출과 객체 생성. 같은 대상을 여러 번 부르면 선 하나에 위치 여러 개.
    private List<string> Calls(string fromId)
    {
        if (!symbols_.TryGetValue(fromId, out var from) || Declaration(from) is not { } decl)
        {
            return [];
        }
        var model = Model(decl.SyntaxTree);
        var result = new List<string>();
        // 최상위 문 진입점은 파일 전체가 아니라 최상위 문만(같은 파일의 클래스 안 호출은 그 메서드 것)
        var body = decl is CompilationUnitSyntax unit
            ? unit.Members.OfType<GlobalStatementSyntax>().SelectMany(g => g.DescendantNodes())
            : decl.DescendantNodes();
        foreach (var expr in body.Where(n => n is InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax
                                                                   or ConstructorInitializerSyntax))
        {
            var info = model.GetSymbolInfo(expr);
            if ((info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) is not IMethodSymbol target)
            {
                UnresolvedCalls++;
                continue;
            }
            if (target.IsImplicitlyDeclared)
            {
                continue;   // 컴파일러가 만든 기본 생성자 등: 코드가 없다
            }
            var toId = EnsureNode(target);
            var pos = (NameToken(expr).GetLocation() ?? expr.GetLocation()).GetLineSpan().StartLinePosition;
            var key = (fromId, toId);
            if (!edges_.TryGetValue(key, out var edge))
            {
                edge = new TraceEdge { From = fromId, To = toId };
                edges_[key] = edge;
                graph.Edges.Add(edge);
                result.Add(toId);
            }
            var site = new SourceRef { File = Relative(decl.SyntaxTree.FilePath), Line = pos.Line + 1, Column = pos.Character };
            if (!edge.CallSites.Any(s => s.Line == site.Line && s.Column == site.Column))
            {
                edge.CallSites.Add(site);
            }
        }
        return result;
    }

    // 호출 위치 = 부르는 메서드 이름이 있는 곳(C++ 쪽 색인과 같은 기준)
    private static SyntaxNodeOrToken NameToken(SyntaxNode expr) => expr switch
    {
        InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax m } => m.Name,
        InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax b } => b.Name,
        InvocationExpressionSyntax i => i.Expression,
        ObjectCreationExpressionSyntax o => o.Type,
        _ => expr,
    };

    private SyntaxNode? Declaration(IMethodSymbol symbol) =>
        symbol.DeclaringSyntaxReferences.Select(r => r.GetSyntax())
            .FirstOrDefault(s => s is BaseMethodDeclarationSyntax { Body: not null } or BaseMethodDeclarationSyntax { ExpressionBody: not null }
                                     or LocalFunctionStatementSyntax or AccessorDeclarationSyntax)
        ?? symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();

    // ---- 노드 ----

    private string EnsureNode(IMethodSymbol raw)
    {
        var symbol = (raw.ReducedFrom ?? raw).OriginalDefinition;
        var decl = Declaration(symbol);
        var inside = decl is not null && InScope(decl.SyntaxTree);
        var name = QualifiedName(symbol);
        // 프로젝트마다 같은 이름이 있을 수 있어(최상위 문의 Program.<Main>$ 등) 어셈블리(프로젝트) 이름을 붙인다
        var id = inside ? (symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString()) + "@" + symbol.ContainingAssembly?.Name : "ext:" + name;
        if (graph.Nodes.ContainsKey(id))
        {
            return id;
        }
        var node = new TraceNode
        {
            Id = id,
            Name = name,
            Detail = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            Kind = !inside ? NodeKind.External
                : symbol.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor ? NodeKind.Constructor
                : symbol.MethodKind == MethodKind.LocalFunction ? NodeKind.Function
                : NodeKind.Method,
        };
        graph.Nodes[id] = node;
        if (!inside)
        {
            node.Stop = StopReason.External;
            return id;
        }
        symbols_[id] = symbol;
        var globals = decl is CompilationUnitSyntax cu ? cu.Members.OfType<GlobalStatementSyntax>().ToList() : [];
        var span = decl!.SyntaxTree.GetLineSpan(globals.Count > 0 ? Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(globals[0].SpanStart, globals[^1].Span.End) : decl.Span);
        node.File = Relative(decl.SyntaxTree.FilePath);
        node.StartLine = span.StartLinePosition.Line + 1;
        node.EndLine = span.EndLinePosition.Line + 1;
        var lines = decl.SyntaxTree.GetText().Lines;
        var all = Enumerable.Range(span.StartLinePosition.Line, node.EndLine - node.StartLine + 1).Select(i => lines[i].ToString()).ToList();
        node.Code = string.Join('\n', all.Take(config.MaxCodeLines));
        node.CodeTruncated = all.Count > config.MaxCodeLines;
        var full = string.Join('\n', all);
        node.Signature = CodeFingerprint.Signature(full);
        node.BodyHash = CodeFingerprint.Body(full);
        var hasBody = decl is BaseMethodDeclarationSyntax b ? b.Body is not null || b.ExpressionBody is not null : true;
        if (!hasBody)
        {
            // 인터페이스·추상 메서드: 구현은 소스의 형식에서 찾는다(Roslyn 이 상속 관계를 알아 이름 추측이 아니다)
            node.Stop = StopReason.NoDefinition;
            node.Implementations = Implementations(symbol);
        }
        else if (hide_.Any(r => r.IsMatch(name)))
        {
            node.Stop = StopReason.Hidden;
        }
        return id;
    }

    private List<ImplementationRef> Implementations(IMethodSymbol member)
    {
        sourceTypes_ ??= projects.Trees.Where(InScope)
            .SelectMany(t => t.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>()
                .Select(d => Model(t).GetDeclaredSymbol(d)).OfType<INamedTypeSymbol>())
            .Distinct(SymbolEqualityComparer.Default).Cast<INamedTypeSymbol>().ToList();
        var found = new List<ImplementationRef>();
        foreach (var type in sourceTypes_.Where(t => t.TypeKind == TypeKind.Class || t.TypeKind == TypeKind.Struct))
        {
            ISymbol? impl = member.ContainingType.TypeKind == TypeKind.Interface
                ? type.FindImplementationForInterfaceMember(member)
                : type.GetMembers(member.Name).OfType<IMethodSymbol>().FirstOrDefault(m => Overrides(m, member));
            if (impl is IMethodSymbol m && !SymbolEqualityComparer.Default.Equals(m.ContainingType, member.ContainingType) &&
                m.Locations.FirstOrDefault(l => l.IsInSource) is { } loc && InScope(loc.SourceTree!))
            {
                var line = loc.GetLineSpan();
                implTypes_.Add(m.ContainingType);
                found.Add(new ImplementationRef { Name = QualifiedName(m), File = Relative(line.Path), Line = line.StartLinePosition.Line + 1 });
            }
        }
        return found.DistinctBy(f => (f.File, f.Line)).ToList();
    }

    private static bool Overrides(IMethodSymbol method, IMethodSymbol baseMethod)
    {
        for (var m = method.OverriddenMethod; m is not null; m = m.OverriddenMethod)
        {
            if (SymbolEqualityComparer.Default.Equals(m.OriginalDefinition, baseMethod))
            {
                return true;
            }
        }
        return false;
    }

    internal static string QualifiedName(IMethodSymbol symbol)
    {
        var type = TypePath(symbol.ContainingType);
        var name = symbol.Name.StartsWith('<') ? "Main" : symbol.MethodKind switch   // <Main>$ = 최상위 문
        {
            MethodKind.Constructor or MethodKind.StaticConstructor => symbol.ContainingType.Name,
            MethodKind.LocalFunction => (symbol.ContainingSymbol is IMethodSymbol outer ? outer.Name + "::" : "") + symbol.Name,
            _ => symbol.Name,
        };
        return type.Length > 0 ? type + "::" + name : name;
    }

    private static string TypePath(INamedTypeSymbol? type)
    {
        if (type is null)
        {
            return "";
        }
        var parts = new List<string>();
        for (var t = type; t is not null; t = t.ContainingType)
        {
            parts.Add(t.Name);
        }
        for (var ns = type.ContainingNamespace; ns is { IsGlobalNamespace: false }; ns = ns.ContainingNamespace)
        {
            parts.Add(ns.Name);
        }
        parts.Reverse();
        return string.Join("::", parts);
    }

    // ---- 클래스 선언(클래스 다이어그램) ----

    public List<TypeInfo> CollectTypes()
    {
        var types = symbols_.Values.Select(s => s.ContainingType).Concat(implTypes_).OfType<INamedTypeSymbol>()
            .Distinct(SymbolEqualityComparer.Default).Cast<INamedTypeSymbol>().ToList();
        var result = new List<TypeInfo>();
        foreach (var t in types.Take(400))
        {
            var loc = t.Locations.FirstOrDefault(l => l.IsInSource);
            if (loc?.SourceTree is null || !InScope(loc.SourceTree))
            {
                continue;
            }
            var bases = new List<string>();
            if (t.BaseType is { SpecialType: not SpecialType.System_Object and not SpecialType.System_ValueType } b)
            {
                bases.Add(b.Name);
            }
            bases.AddRange(t.Interfaces.Select(i => i.Name));
            result.Add(new TypeInfo
            {
                Name = TypePath(t),
                Kind = t.IsRecord ? "record" : t.TypeKind == TypeKind.Struct ? "struct" : t.TypeKind == TypeKind.Interface ? "interface" : "class",
                File = Relative(loc.SourceTree.FilePath),
                Line = loc.GetLineSpan().StartLinePosition.Line + 1,
                Bases = bases,
                Members = t.GetMembers().Where(m => !m.IsImplicitlyDeclared).Select(Member).OfType<TypeMember>().Take(80).ToList(),
            });
        }
        return result;
    }

    private static TypeMember? Member(ISymbol m) => m switch
    {
        IFieldSymbol f when !f.Name.StartsWith('<') => new TypeMember { Name = f.Name, Kind = "field", Detail = f.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) },
        IPropertySymbol p => new TypeMember { Name = p.Name, Kind = "field", Detail = p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) },
        IMethodSymbol { MethodKind: MethodKind.Constructor } c => new TypeMember { Name = c.ContainingType.Name, Kind = "ctor", Detail = c.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) },
        IMethodSymbol { MethodKind: MethodKind.Ordinary } x => new TypeMember { Name = x.Name, Kind = "method", Detail = x.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) },
        _ => null,
    };
}
