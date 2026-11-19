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

    [Fact]
    public void 한_줄_인라인_정의()
    {
        var lines = L("    int Get() const { return v_; }");
        var span = FunctionBody.Find(lines, 0, 8);
        Assert.True(span.HasBody);
        Assert.Equal(1, span.EndLine);
    }
}
