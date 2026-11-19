using ReviewTool.Cpp;

namespace ReviewTool.Tests;

public class FunctionBodyTests
{
    private static string[] L(string code) => code.Replace("\r\n", "\n").Split('\n');

    [Fact]
    public void 선언만_있으면_본문_없음()
    {
        var lines = L("class A\n{\n    void Run(int x);\n};");
        var span = FunctionBody.Find(lines, 2, 9);
        Assert.False(span.HasBody);
    }

    [Fact]
    public void 정의는_중괄호_짝까지()
    {
        var lines = L("void A::Run(int x)\n{\n    if (x) { return; }\n    Go();\n}\nvoid Next() {}");
        var span = FunctionBody.Find(lines, 0, 8);
        Assert.True(span.HasBody);
        Assert.Equal(1, span.StartLine);
        Assert.Equal(5, span.EndLine);
    }

    [Fact]
    public void 반환형이_윗줄에_있으면_포함()
    {
        var lines = L("int x = 0;\ntemplate <typename T>\nvoid\nA::Run()\n{\n}");
        var span = FunctionBody.Find(lines, 3, 3);
        Assert.Equal(2, span.StartLine);
        Assert.Equal(6, span.EndLine);
    }

    [Fact]
    public void 생성자_초기화_목록의_중괄호는_본문이_아님()
    {
        var lines = L("A::A(int v)\n    : a_{v}\n    , b_{1}\n{\n    Init();\n}");
        var span = FunctionBody.Find(lines, 0, 3);
        Assert.True(span.HasBody);
        Assert.Equal(6, span.EndLine);
    }

    [Fact]
    public void 문자열과_주석_속_괄호는_세지_않음()
    {
        var lines = L("void A::Run()\n{\n    Log(\"}\"); // }\n    /* { */\n    Go('{');\n}");
        var span = FunctionBody.Find(lines, 0, 8);
        Assert.Equal(6, span.EndLine);
    }

    [Theory]
    [InlineData("PlayerInfo::SPtr PlayerManager::Get(const Network::SessionId clientSessionId) const", 26, "const Network::SessionId")]
    [InlineData("void F(int a = 3, const char* name, std::vector<int, A> v)", 5, "int|const char*|std::vector<int, A>")]
    [InlineData("void F(const int, Foo&)", 5, "const int|Foo&")]
    [InlineData("void F()", 5, "")]
    [InlineData("void F(void)", 5, "")]
    public void 매개변수_타입만_뽑는다(string code, int nameCol, string expected)
    {
        var types = FunctionBody.ParameterTypes(L(code), 0, nameCol);
        Assert.NotNull(types);
        Assert.Equal(expected, string.Join('|', types));
    }

    [Fact]
    public void 선언과_정의의_매개변수_이름이_달라도_같은_오버로드()
    {
        var decl = FunctionBody.ParameterTypes(L("    void Run(const Packet& p, // 받은 패킷\n             int retry = 0);"), 0, 9);
        var def = FunctionBody.ParameterTypes(L("void A::Run(const Packet &packet, int count)\n{\n}"), 0, 8);
        var other = FunctionBody.ParameterTypes(L("void A::Run(const Packet& packet)\n{\n}"), 0, 8);
        Assert.Equal(decl, def);
        Assert.NotEqual(decl, other);
    }

    [Fact]
    public void 한_줄_인라인_정의()
    {
        var lines = L("    int Get() const { return v_; }");
        var span = FunctionBody.Find(lines, 0, 8);
        Assert.True(span.HasBody);
        Assert.Equal(1, span.EndLine);
    }
}
