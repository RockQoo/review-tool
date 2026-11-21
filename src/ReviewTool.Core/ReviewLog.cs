using System.Text.Json;

namespace ReviewTool.Core;

// 검토 기록: 함수마다 사람이 "확인" 또는 "보류"를 표시하고 메모를 남긴다. data/<대상>/review.json (로컬 전용, 커밋 안 함).
// 함수는 분석할 때마다 id 가 바뀔 수 있어 열쇠는 이름|시그니처(없으면 이름|파일)다.
// 표시할 때의 본문 지문을 같이 저장해, 그 뒤 본문이 바뀌면 "다시 봐야 함"으로 보여 준다(자동으로 지우지 않는다).
public sealed class ReviewLog
{
    public enum Mark { Checked, Hold }

    public sealed class Item
    {
        public string Name { get; set; } = "";
        public string File { get; set; } = "";
        public Mark Mark { get; set; }
        public string Note { get; set; } = "";
        public string BodyHash { get; set; } = "";
        public DateTimeOffset At { get; set; }
    }

    // 지금 함수 기준의 상태: 표시 없음 / 확인 / 보류 / 다시 봐야 함(표시 뒤 본문이 바뀜)
    public enum State { None, Checked, Hold, Stale }

    public Dictionary<string, Item> Items { get; set; } = [];

    public static string KeyOf(TraceNode node) => node.Signature.Length > 0 ? $"{node.Name}|{node.Signature}" : $"{node.Name}|{node.File}";

    private static string HashOf(TraceNode node) => node.BodyHash.Length > 0 ? node.BodyHash : CodeFingerprint.Body(node.Code);

    public Item? Find(TraceNode node) => Items.GetValueOrDefault(KeyOf(node));

    public State StateOf(TraceNode node) => Find(node) switch
    {
        null => State.None,
        var i when i.BodyHash != HashOf(node) => State.Stale,
        { Mark: Mark.Checked } => State.Checked,
        _ => State.Hold,
    };

    // mark 가 null 이면 표시를 지운다(메모가 있으면 메모만 남기지 않고 같이 지운다)
    public void Set(TraceNode node, Mark? mark, string? note = null, DateTimeOffset? now = null)
    {
        var key = KeyOf(node);
        if (mark is null)
        {
            Items.Remove(key);
            return;
        }
        var item = Items.GetValueOrDefault(key) ?? new Item();
        item.Name = node.Name;
        item.File = node.File;
        item.Mark = mark.Value;
        item.BodyHash = HashOf(node);
        item.At = now ?? DateTimeOffset.Now;
        if (note is not null)
        {
            item.Note = note.Trim();
        }
        Items[key] = item;
    }

    public void SetNote(TraceNode node, string note)
    {
        if (Find(node) is { } item)
        {
            item.Note = note.Trim();
        }
        else if (note.Trim().Length > 0)
        {
            Set(node, Mark.Hold, note);   // 메모만 쓰면 보류로 남긴다
        }
    }

    // 진입점에서 닿는(분석 범위 안) 함수 중 지금 본문 기준으로 확인한 수
    public (int Checked, int Total) Progress(TraceGraph graph, TraceIndex index, string entryNodeId)
    {
        var seen = new HashSet<string> { entryNodeId };
        var queue = new Queue<string>(seen);
        var total = 0;
        var done = 0;
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!graph.Nodes.TryGetValue(id, out var node) || node.Kind == NodeKind.External)
            {
                continue;
            }
            total++;
            if (StateOf(node) == State.Checked)
            {
                done++;
            }
            foreach (var e in index.ChildrenOf(id))
            {
                if (seen.Add(e.To))
                {
                    queue.Enqueue(e.To);
                }
            }
        }
        return (done, total);
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static string PathOf(WorkPaths paths) => System.IO.Path.Combine(paths.Directory, "review.json");

    public static ReviewLog Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<ReviewLog>(File.ReadAllText(path), Json) ?? new ReviewLog() : new ReviewLog();
        }
        catch (JsonException)
        {
            return new ReviewLog();
        }
    }

    // 임시 파일에 쓰고 바꿔치기(쓰다 끊겨도 기존 기록이 깨지지 않게)
    public void Save(string path)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);
    }
}
