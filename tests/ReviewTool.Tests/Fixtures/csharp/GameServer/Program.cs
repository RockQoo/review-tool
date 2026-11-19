using Game.Common;

namespace Game.Server;

public static class Program
{
    public static void Main(string[] args)
    {
        Config.Load();
        var dispatcher = new Dispatcher();
        var handler = new LoginHandler(new SqlRepository());
        dispatcher.Register(PacketId.C2SLogin, handler.HandleLogin);
    }
}

public static class Config
{
    public static void Load() { }
}
