using ReviewTool.Core;
using ReviewTool.Cpp;

namespace ReviewTool.Tests;

public class LinkPassTests
{
    [Theory]
    [InlineData("    gatewayDispatcher_.Register(this, &BasicProcessor::HandleClientConnected);", 41, "gatewayDispatcher_")]
    [InlineData("    Table().Register(&PlayerStreamHandler::HandleMove);", 22, "Table()")]
    [InlineData("    router->Add(&A::B);", 18, "router")]
    [InlineData("    Register(&A::B);", 14, null)]
    public void 등록_줄의_받는_쪽(string line, int handlerColumn, string? expected) =>
        Assert.Equal(expected, LinkPass.RegistrationReceiver(line, handlerColumn));

    [Theory]
    [InlineData("    dispatcher_.Dispatch(id, session, payload);", "dispatcher_")]
    [InlineData("    Table().Dispatch(packetId, context, payload);", "Table()")]
    [InlineData("    Dispatch(id);", null)]
    public void 디스패치_호출의_받는_쪽(string line, string? expected) =>
        Assert.Equal(expected, LinkPass.ReceiverBefore(line, "Dispatch"));

    [Theory]
    [InlineData("public Network::IPacketHandler", "IPacketHandler")]
    [InlineData("public A, private ns::B<int, C<D>>, virtual protected E", "A|B|E")]
    public void 기반_클래스_이름(string bases, string expected) =>
        Assert.Equal(expected, string.Join('|', LinkPass.BaseNames(bases)));

    [Fact]
    public void 같은_파일_같은_받는_쪽의_핸들러만_잇는다()
    {
        var root = Directory.CreateTempSubdirectory("rt-link").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "a.cpp"), "void A::OnPacket()\n{\n    dispatcher_.Dispatch(id, s, p);\n}\n");
            var g = new TraceGraph();
            foreach (var (id, name) in new[] { ("on", "A::OnPacket"), ("d", "Packet::Dispatcher::Dispatch"), ("h1", "A::HandleX"), ("h2", "B::HandleY"), ("h3", "A::HandleZ") })
            {
                g.Nodes[id] = new TraceNode { Id = id, Name = name };
            }
            g.Edges.Add(new TraceEdge { From = "on", To = "d", CallSites = [new SourceRef { File = "a.cpp", Line = 3 }] });
            var regs = new List<LinkPass.Registration>
            {
                new("dispatcher_", "a.cpp", "h1"),
                new("dispatcher_", "b.cpp", "h2"),      // 다른 파일 → 안 이음
                new("otherDispatcher_", "a.cpp", "h3"), // 다른 받는 쪽 → 안 이음
            };
            var config = new TargetConfig { Root = root };

            var linked = LinkPass.LinkDispatchers(config, g, regs, p => File.ReadAllLines(p));

            Assert.Equal(1, linked);
            var edge = Assert.Single(g.Edges, e => e.Resolution == "Registration");
            Assert.Equal(("on", "h1"), (edge.From, edge.To));
            Assert.Equal(3, edge.CallSites.Single().Line);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
