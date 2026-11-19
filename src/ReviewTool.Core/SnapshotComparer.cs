namespace ReviewTool.Core;

// 기준 분석(수정 전)과 지금 분석(수정 후)의 구조 비교. 텍스트 diff 가 아니라
// "진입점·함수·호출 관계가 어떻게 바뀌었나"를 본다. AI 가 고친 뒤 의도하지 않은 흐름 변화를 찾는 용도.
public static class SnapshotComparer
{
    public sealed record FunctionRef(string Key, string Name, string File, int Line, string? NodeId);

    public sealed record CompareResult(
        IReadOnlyList<string> ConditionWarnings,
        IReadOnlyList<string> EntriesAdded,
        IReadOnlyList<string> EntriesRemoved,
        IReadOnlyList<FunctionRef> FunctionsAdded,
        IReadOnlyList<FunctionRef> FunctionsRemoved,
        IReadOnlyList<FunctionRef> BodyChanged,
        IReadOnlyList<(FunctionRef Before, FunctionRef After)> SignatureChanged,
        IReadOnlyList<(FunctionRef Before, FunctionRef After)> RenameCandidates,
        IReadOnlyList<(string From, string To)> CallsAdded,
        IReadOnlyList<(string From, string To)> CallsRemoved)
    {
        public bool HasChanges => EntriesAdded.Count + EntriesRemoved.Count + FunctionsAdded.Count + FunctionsRemoved.Count +
                                  BodyChanged.Count + SignatureChanged.Count + CallsAdded.Count + CallsRemoved.Count > 0;
    }

    public static readonly string[] Limits =
    [
        "\"차이 없음\"은 \"안전\"도 \"테스트 통과\"도 아닙니다. 분석이 본 범위 안의 구조만 비교합니다.",
        "진입점에서 닿는 함수만 비교합니다. 아무 진입점에서도 부르지 않는 함수의 변화는 나오지 않습니다.",
        "이름 변경 후보는 본문이 같은 삭제·추가 쌍일 뿐 자동으로 확정하지 않습니다.",
    ];

    public static CompareResult Compare(TraceGraph before, TraceGraph after)
    {
        var warnings = new List<string>();
        if (!string.Equals(before.RootDirectory, after.RootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"분석한 폴더가 다릅니다: {before.RootDirectory} → {after.RootDirectory}");
        }
        if (before.ConfigHash.Length > 0 && before.ConfigHash != after.ConfigHash)
        {
            warnings.Add("대상 설정(범위·진입점 규칙 등)이 바뀌었습니다. 차이 중 일부는 코드가 아니라 설정 때문일 수 있습니다.");
        }
        if (before.AnalyzerVersion != after.AnalyzerVersion)
        {
            warnings.Add($"분석기 버전이 다릅니다({before.AnalyzerVersion} → {after.AnalyzerVersion}).");
        }
        var compareBodies = before.FingerprintVersion == after.FingerprintVersion && before.FingerprintVersion > 0;
        if (!compareBodies)
        {
            warnings.Add("기준 분석의 본문 지문 방식이 지금과 다릅니다(이전 버전). 본문 변경은 비교하지 않았습니다. 다시 기준을 저장하세요.");
        }

        var entriesBefore = before.EntryPoints.Select(e => EntryKey(before, e)).ToHashSet();
        var entriesAfter = after.EntryPoints.Select(e => EntryKey(after, e)).ToHashSet();

        // 시그니처는 양쪽 모두 있을 때만 열쇠에 쓴다. 한쪽이 옛 결과면 전부 "시그니처 변경"으로 보이는 것을 막는다.
        var useSignature = compareBodies && HasSignatures(before) && HasSignatures(after);
        var fb = Functions(before, useSignature);
        var fa = Functions(after, useSignature);
        var added = fa.Keys.Except(fb.Keys).Select(k => fa[k]).ToList();
        var removed = fb.Keys.Except(fa.Keys).Select(k => fb[k]).ToList();
        var bodyChanged = fb.Keys.Intersect(fa.Keys)
            .Where(k => compareBodies && before.Nodes[fb[k].NodeId!].BodyHash.Length > 0 &&
                        before.Nodes[fb[k].NodeId!].BodyHash != after.Nodes[fa[k].NodeId!].BodyHash)
            .Select(k => fa[k]).OrderBy(f => f.Name).ToList();

        // 같은 이름이 하나 지워지고 하나 생겼으면 시그니처 변경으로 묶는다
        var signatureChanged = new List<(FunctionRef, FunctionRef)>();
        foreach (var group in removed.GroupBy(r => r.Name).ToList())
        {
            var counterpart = added.Where(a => a.Name == group.Key).ToList();
            if (group.Count() == 1 && counterpart.Count == 1)
            {
                signatureChanged.Add((group.First(), counterpart[0]));
                removed.Remove(group.First());
                added.Remove(counterpart[0]);
            }
        }

        // 본문이 똑같은 삭제·추가 쌍 = 이름 변경 후보(확정하지 않음)
        var renames = new List<(FunctionRef, FunctionRef)>();
        foreach (var r in removed)
        {
            var hash = before.Nodes[r.NodeId!].BodyHash;
            var same = added.Where(a => hash.Length > 0 && SameBodyIgnoringName(before.Nodes[r.NodeId!], after.Nodes[a.NodeId!])).ToList();
            if (same.Count == 1)
            {
                renames.Add((r, same[0]));
            }
        }

        // 호출은 함수 열쇠(오버로드·파일 구분)로 비교하고, 화면에 낼 때만 읽기 쉬운 이름으로 바꾼다
        var cb = Calls(before, fb);
        var ca = Calls(after, fa);
        var display = Display(fb.Values.Concat(fa.Values));
        string Show(string key) => display.TryGetValue(key, out var d) ? d : key.StartsWith("ext:", StringComparison.Ordinal) ? key[4..] : key;
        List<(string, string)> Readable(IEnumerable<(string From, string To)> calls) =>
            calls.Select(c => (Show(c.From), Show(c.To))).OrderBy(c => c.Item1).ThenBy(c => c.Item2).ToList();

        return new CompareResult(
            warnings,
            entriesAfter.Except(entriesBefore).Order().ToList(),
            entriesBefore.Except(entriesAfter).Order().ToList(),
            added.OrderBy(f => f.Name).ToList(),
            removed.OrderBy(f => f.Name).ToList(),
            bodyChanged,
            signatureChanged,
            renames,
            Readable(ca.Except(cb)),
            Readable(cb.Except(ca)));
    }

    // 이름이 겹치지 않으면 이름만, 겹치면(오버로드·파일별 static) 시그니처나 파일을 붙인다
    private static Dictionary<string, string> Display(IEnumerable<FunctionRef> functions)
    {
        var all = functions.GroupBy(f => f.Key).Select(g => g.First()).ToList();
        var nameCount = all.GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.Count());
        return all.ToDictionary(f => f.Key, f => nameCount[f.Name] == 1 ? f.Name : $"{f.Name} [{f.Key[(f.Name.Length + 1)..]}]");
    }

    private static string EntryKey(TraceGraph g, EntryPoint e) => $"{e.Group} · {e.Label} ({g.Nodes[e.NodeId].Name})";

    // 비교 단위: 범위 안의 함수. 열쇠 = 한정 이름 + 시그니처(오버로드 구분). 줄 번호는 쓰지 않는다(위에 코드가 늘면 다 바뀜).
    private static bool HasSignatures(TraceGraph g) =>
        g.Nodes.Values.Where(n => n.Kind != NodeKind.External).All(n => n.Signature.Length > 0);

    // 열쇠 = 이름|시그니처. 같은 열쇠가 둘 이상이면(다른 .cpp 의 같은 static 함수 등) 파일을, 그래도 겹치면 줄을 붙인다.
    // 조용히 하나를 버리지 않는다.
    private static Dictionary<string, FunctionRef> Functions(TraceGraph g, bool useSignature)
    {
        var result = new Dictionary<string, FunctionRef>();
        var internals = g.Nodes.Values.Where(n => n.Kind != NodeKind.External).ToList();
        foreach (var group in internals.GroupBy(n => $"{n.Name}|{(useSignature ? n.Signature : n.File)}"))
        {
            var many = group.Count() > 1;
            foreach (var n in group)
            {
                var key = many ? $"{group.Key}|{n.File}" : group.Key;
                if (!result.TryAdd(key, new FunctionRef(key, n.Name, n.File, n.StartLine, n.Id)))
                {
                    key = $"{key}:{n.StartLine}";
                    result.TryAdd(key, new FunctionRef(key, n.Name, n.File, n.StartLine, n.Id));
                }
            }
        }
        return result;
    }

    private static HashSet<(string From, string To)> Calls(TraceGraph g, Dictionary<string, FunctionRef> functions)
    {
        var keyOf = functions.Values.ToDictionary(f => f.NodeId!, f => f.Key);
        string Key(string id) => keyOf.TryGetValue(id, out var k) ? k : g.Nodes.TryGetValue(id, out var n) ? "ext:" + n.Name : id;
        return g.Edges.Select(e => (Key(e.From), Key(e.To))).ToHashSet();
    }

    // 이름만 바뀐 함수: 시그니처에서 이름을 뺀 나머지와 본문이 같다
    private static bool SameBodyIgnoringName(TraceNode a, TraceNode b)
    {
        static string Strip(TraceNode n)
        {
            var shortName = n.Name.Contains("::") ? n.Name[(n.Name.LastIndexOf("::", StringComparison.Ordinal) + 2)..] : n.Name;
            return CodeFingerprint.Normalize(n.Code).Replace(shortName, "#");
        }
        return a.Code.Length > 0 && Strip(a) == Strip(b);
    }
}
