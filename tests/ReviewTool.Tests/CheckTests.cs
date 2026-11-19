using ReviewTool.Core;

namespace ReviewTool.Tests;

// 4단계: 점검 규칙(Checks). 그래프를 손으로 만들어 규칙 종류마다 걸리는 것·안 걸리는 것을 본다.
public class CheckTests
{
    private sealed class G
    {
        public TraceGraph Graph { get; } = new();

        public G Node(string id, string name, string file, int start, string code, NodeKind kind = NodeKind.Method)
        {
            var lines = code.Split('\n');
            Graph.Nodes[id] = new TraceNode
            {
                Id = id, Name = name, File = file, StartLine = start, EndLine = start + lines.Length - 1, Kind = kind, Code = code,
            };
            return this;
        }

        public G Ext(string id, string name)
        {
            Graph.Nodes[id] = new TraceNode { Id = id, Name = name, Kind = NodeKind.External };
            return this;
        }

        public G Call(string from, string to, int line, string resolution = "Static")
        {
            Graph.Edges.Add(new TraceEdge
            {
                From = from, To = to, Resolution = resolution,
                CallSites = [new SourceRef { File = Graph.Nodes[from].File, Line = line }],
            });
            return this;
        }

        public G Entry(string id, string label)
        {
            Graph.EntryPoints.Add(new EntryPoint { NodeId = id, Label = label, Group = "S / X", Detector = "패킷" });
            return this;
        }

        public Checks.Report Run(CheckRule rule, Func<string, string[]?>? read = null) =>
            Checks.Run([rule], Graph, new TraceIndex(Graph), read ?? (_ => null));
    }

    private static CheckRule Rule(string kind) => CheckRule.Defaults().First(r => r.Kind == kind) is var r
        ? new CheckRule
        {
            Name = r.Name, Kind = r.Kind, Enabled = true, ScopeOpen = r.ScopeOpen, ScopeClose = r.ScopeClose, Forbid = r.Forbid,
            ForbidTags = [.. r.ForbidTags], Subject = r.Subject, Within = r.Within, WithinEdges = [.. r.WithinEdges],
            First = r.First, Then = r.Then, Unless = r.Unless, From = [.. r.From], To = [.. r.To],
        }
        : throw new InvalidOperationException();

    [Fact]
    public void 락_범위_안의_직접_호출은_높음_블록이_닫힌_뒤와_주석은_뺀다()
    {
        var g = new G()
            .Node("F", "Net::Hub::Broadcast", "Core/Hub.cpp", 10, """
                void Hub::Broadcast()
                {
                    // std::lock_guard fake(m_);
                    Send(a);
                    {
                        std::lock_guard lock(mutex_);
                        session->Send(b);
                        Copy();
                    }
                    Send(c);
                }
                """)
            .Node("S", "Net::Session::Send", "Core/Session.cpp", 40, "void Session::Send() {}")
            .Node("C", "Net::Hub::Copy", "Core/Hub.cpp", 30, "void Hub::Copy() {}")
            .Call("F", "S", 13).Call("F", "S", 16).Call("F", "C", 17).Call("F", "S", 19)
            .Entry("F", "C2SBroadcast");

        var report = g.Run(Rule(Checks.ForbidInScope));

        var f = Assert.Single(report.Findings);
        Assert.Equal(Certainty.High, f.Certainty);
        Assert.Equal(15, f.Site.Line);
        Assert.Equal([16], f.Evidence.Select(e => e.Site.Line));
        Assert.Equal(["F", "S"], f.Path);
        Assert.Equal(RuleState.Ran, report.Runs[0].State);
    }

    [Fact]
    public void 락_안에서_부른_함수_아래의_I_O는_보통_unlock_뒤는_뺀다()
    {
        var g = new G()
            .Node("F", "Log::Logger::Write", "Core/Log.cpp", 1, """
                void Logger::Write()
                {
                    mutex_.lock();
                    Format();
                    mutex_.unlock();
                    Flush();
                }
                """)
            .Node("M", "Log::Logger::Format", "Core/Log.cpp", 20, "void Logger::Format() { Db::Query(); }")
            .Node("Q", "Db::Query", "Core/Db.cpp", 30, "void Query() {}", NodeKind.Function)
            .Node("L", "Log::Logger::Flush", "Core/Log.cpp", 40, "void Logger::Flush() {}")
            .Call("F", "M", 4).Call("M", "Q", 20).Call("F", "L", 6);

        var f = Assert.Single(g.Run(Rule(Checks.ForbidInScope)).Findings);

        Assert.Equal(Certainty.Medium, f.Certainty);
        Assert.Contains("1단계 아래", f.Evidence.Single().Note);
        Assert.Equal(["F", "M", "Q"], f.Path);   // 진입점이 없으면 그 함수부터
    }

    [Fact]
    public void 문장_범위는_그_문장만_본다()
    {
        var rule = Rule(Checks.ForbidInScope);
        rule.ScopeOpen = @"\.Write\(\)->";
        rule.Scope = "statement";
        var g = new G()
            .Node("F", "Zone::Do", "Z/Zone.cpp", 1, """
                void Do()
                {
                    players.Write()->SendAll(
                        x);
                    SendAll(y);
                }
                """)
            .Node("S", "Zone::Players::SendAll", "Z/P.cpp", 10, "void SendAll() {}")
            .Call("F", "S", 3).Call("F", "S", 5);

        var f = Assert.Single(g.Run(rule).Findings);
        Assert.Equal([3], f.Evidence.Select(e => e.Site.Line));
    }

    [Fact]
    public void 안에서만_규칙은_작업_단위를_거치지_않는_경로만_지적한다()
    {
        var g = new G()
            .Node("A", "Shop::OnBuy", "S/Shop.cpp", 1, "void OnBuy()\n{\n    Model::SetGold();\n}")
            .Node("B", "Shop::OnSafeBuy", "S/Shop.cpp", 10, "void OnSafeBuy()\n{\n    UnitOfWork::Run();\n}")
            .Node("U", "Db::UnitOfWork::Run", "S/Uow.cpp", 20, "void Run()\n{\n    Model::SetGold();\n}")
            .Node("M", "Game::PlayerModel::SetGold", "S/Model.cpp", 30, "void SetGold() {}")
            .Call("A", "M", 3).Call("B", "U", 12).Call("U", "M", 22)
            .Entry("A", "C2SBuy").Entry("B", "C2SSafeBuy");

        var report = g.Run(Rule(Checks.RequireWithin));

        var f = Assert.Single(report.Findings);
        Assert.Equal("M", f.NodeId);
        Assert.Equal(Certainty.High, f.Certainty);
        Assert.Equal(["A", "M"], f.Path);
        Assert.Equal(("S/Shop.cpp", 3), (f.Site.File, f.Site.Line));
        Assert.Single(f.Evidence);
    }

    [Fact]
    public void 메시지_연결을_지나면_레인_안으로_본다()
    {
        var rule = Rule(Checks.RequireWithin);
        rule.Subject = @"ZoneState::\w+$";
        rule.Within = "";
        rule.WithinEdges = ["Message"];
        var g = new G()
            .Node("N", "Net::OnRecv", "Z/Net.cpp", 1, "void OnRecv()\n{\n    PushMsg();\n    ZoneState::Peek();\n}")
            .Node("H", "Zone::OnMove", "Z/Zone.cpp", 10, "void OnMove()\n{\n    ZoneState::Move();\n}")
            .Node("P", "Zone::ZoneState::Peek", "Z/State.cpp", 20, "void Peek() {}")
            .Node("V", "Zone::ZoneState::Move", "Z/State.cpp", 30, "void Move() {}")
            .Call("N", "H", 3, "Message").Call("N", "P", 4).Call("H", "V", 12)
            .Entry("N", "OnRecv");

        var report = g.Run(rule);

        Assert.Equal(["P"], report.Findings.Select(f => f.NodeId));
    }

    [Fact]
    public void 대상_함수가_없으면_그렇게_적는다()
    {
        var g = new G().Node("A", "X::Y", "a.cpp", 1, "void Y() {}").Entry("A", "Y");
        var run = g.Run(Rule(Checks.RequireWithin)).Runs.Single();
        Assert.Equal(RuleState.Ran, run.State);
        Assert.Contains("subject 에 맞는 함수가", run.Note);
    }

    [Fact]
    public void 차감_뒤_지급은_되돌리기가_없을_때만_지적한다()
    {
        var g = new G()
            .Node("A", "Shop::OnBuy", "S/Shop.cpp", 1, "void OnBuy()\n{\n    Wallet::Spend();\n    Grant();\n}")
            .Node("B", "Shop::OnBuySafe", "S/Shop.cpp", 10, "void OnBuySafe()\n{\n    Wallet::Spend();\n    Grant();\n    if (!ok) Wallet::Refund();\n}")
            .Node("C", "Shop::OnGift", "S/Shop.cpp", 20, "void OnGift()\n{\n    Grant();\n    Wallet::Spend();\n}")
            .Node("S", "Wallet::Spend", "S/Wallet.cpp", 30, "void Spend() {}")
            .Node("R", "Wallet::Refund", "S/Wallet.cpp", 40, "void Refund() {}")
            .Node("G2", "Shop::Grant", "S/Shop.cpp", 50, "void Grant()\n{\n    Inventory::AddItem();\n}")
            .Node("I", "Inventory::AddItem", "S/Inv.cpp", 60, "void AddItem() {}")
            .Call("A", "S", 3).Call("A", "G2", 4).Call("G2", "I", 52)
            .Call("B", "S", 12).Call("B", "G2", 13).Call("B", "R", 14)
            .Call("C", "G2", 22).Call("C", "S", 23)
            .Entry("A", "C2SBuy").Entry("B", "C2SBuySafe").Entry("C", "C2SGift");

        var report = g.Run(Rule(Checks.Order));

        var f = Assert.Single(report.Findings);
        Assert.Equal("C2SBuy: Wallet::Spend 뒤 Shop::Grant", f.Title);
        Assert.Equal(Certainty.Medium, f.Certainty);
        Assert.Equal([3, 4], f.Evidence.Select(e => e.Site.Line));
        Assert.Contains("넘긴 흐름 1개", report.Runs[0].Note);
    }

    [Fact]
    public void 의존_금지는_호출과_include_둘_다_본다()
    {
        var g = new G()
            .Node("A", "Common::Util::Do", "Server/Common/Util.cpp", 1, "void Do()\n{\n    Quest::Give();\n    Common::Log();\n}")
            .Node("Q", "Quest::Give", "Server/Content/Quest.cpp", 10, "void Give() {}")
            .Node("L", "Common::Log", "Server/Common/Log.cpp", 20, "void Log() {}")
            .Call("A", "Q", 3).Call("A", "L", 4);
        g.Graph.Inputs =
        [
            new InputFile { File = "Server/Common/Util.cpp" }, new InputFile { File = "Server/Common/Log.cpp" },
            new InputFile { File = "Server/Content/Quest.h" }, new InputFile { File = "Server/Content/Quest.cpp" },
            new InputFile { File = "Server/Common/pch.h" }, new InputFile { File = "Server/Content/pch.h" },
        ];
        string[]? Read(string file) => file switch
        {
            "Server/Common/Util.cpp" => ["#include \"pch.h\"", "#include \"../Content/Quest.h\"", "#include \"Log.h\"", "#include <vector>"],
            "Server/Common/Log.cpp" => ["#include \"Content/Quest.h\""],
            _ => null,
        };

        var report = g.Run(Rule(Checks.Dependency), Read);

        var call = Assert.Single(report.Findings, f => f.NodeId == "A");
        Assert.Equal(Certainty.High, call.Certainty);
        Assert.Equal([3], call.Evidence.Select(e => e.Site.Line));
        var includes = report.Findings.Where(f => f.NodeId == "").Select(f => (f.Site.File, f.Site.Line)).ToList();
        Assert.Equal([("Server/Common/Log.cpp", 1), ("Server/Common/Util.cpp", 2)], includes);   // pch.h 는 가까운 Common 쪽
    }

    [Fact]
    public void 꺼진_규칙과_잘못된_규칙은_돌지_않고_이유를_남긴다()
    {
        var g = new G().Node("A", "X::Y", "a.cpp", 1, "void Y() {}");
        var off = Rule(Checks.Order);
        off.Enabled = false;
        var bad = Rule(Checks.ForbidInScope);
        bad.ScopeOpen = "(";
        var unknown = new CheckRule { Name = "?", Kind = "nope" };

        var report = Checks.Run([off, bad, unknown], g.Graph, new TraceIndex(g.Graph), _ => null);

        Assert.Equal([RuleState.Off, RuleState.Error, RuleState.Error], report.Runs.Select(r => r.State));
        Assert.Contains("scopeOpen 정규식 오류", report.Runs[1].Note);
        Assert.Contains("모르는 종류", report.Runs[2].Note);
        var text = Checks.ToText(g.Graph, report, "t");
        Assert.Contains("| 꺼짐 |", text);
        Assert.Contains("증명하지 않는 것", text);
    }

    [Fact]
    public void 기본_규칙은_락_I_O만_켜져_있다()
    {
        var defaults = CheckRule.Defaults();
        Assert.Equal([Checks.ForbidInScope], defaults.Where(r => r.Enabled).Select(r => r.Kind));
        Assert.Equal(4, defaults.Select(r => r.Kind).Distinct().Count());
    }

    [Fact]
    public void 점검_규칙만_바꾸면_설정_지문은_그대로다()
    {
        var config = new TargetConfig { Name = "t", Root = "C:/x" };
        var before = config.ContentHash();
        config.Checks[0].Enabled = false;
        config.Checks.Add(new CheckRule { Name = "새 규칙" });
        Assert.Equal(before, config.ContentHash());
        config.MaxDepth++;
        Assert.NotEqual(before, config.ContentHash());
    }

    [Fact]
    public void 주석과_문자열_안은_비우고_위치는_그대로다()
    {
        var clean = CodeText.Clean(["a(\"lock()\"); // b.lock()", "/* x.lock() */ c.lock(); '}'", "/* a", "b */ d"]);
        Assert.Equal(["a(\"      \");" + new string(' ', 12), new string(' ', 14) + " c.lock(); ' '", "    ", "     d"], clean);
    }
}
