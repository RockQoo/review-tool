using Game.Common;

namespace Game.Server;

public sealed class LoginHandler(IRepository repository)
{
    private readonly object gate_ = new();
    private readonly List<string> lines_ = [];

    public void HandleLogin(Session session, C2SLogin packet)
    {
        var player = repository.Load(packet.Id);
        // 일부러 둔 문제: 잠근 채 Send
        lock (gate_)
        {
            session.Send(PacketId.S2CLoginResult, new S2CLoginResult { Ok = player.Id > 0 });
        }
        lines_.ForEach(line => Audit.Write(line));   // 람다 안 호출도 따라간다
    }
}

public sealed class SqlRepository : IRepository
{
    public Player Load(int id) => new() { Id = Query(id) };

    private static int Query(int id) => id;
}
