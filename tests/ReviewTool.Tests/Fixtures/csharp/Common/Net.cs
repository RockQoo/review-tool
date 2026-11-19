namespace Game.Common;

// 예제: 패킷·세션·저장소(분석기 테스트용, 실행하지 않음)
public enum PacketId { C2SLogin = 1, S2CLoginResult = 2 }

public sealed class C2SLogin { public int Id { get; set; } }
public sealed class S2CLoginResult { public bool Ok { get; set; } }

public sealed class Session
{
    public void Send(PacketId id, object packet) { _ = id; _ = packet; }
}

public sealed class Player { public int Id { get; init; } }

public interface IRepository
{
    Player Load(int id);
}

public static class Audit
{
    public static void Write(string line) => File.AppendAllText("audit.log", line);
}

public sealed class Dispatcher
{
    public void Register(PacketId id, Action<Session, C2SLogin> handler) { _ = id; _ = handler; }
}
