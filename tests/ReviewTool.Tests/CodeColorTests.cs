using ReviewTool.Core;
using static ReviewTool.Core.CodeColor;

namespace ReviewTool.Tests;

// 7단계 ⑪: 코드 문법 색칠. 글자는 하나도 바뀌지 않고(이어 붙이면 원래 줄), 종류만 나뉘어야 한다.
public class CodeColorTests
{
    private static string Show(List<Token> line) =>
        string.Join("|", line.Where(t => t.Kind != Kind.Plain).Select(t => $"{t.Kind}:{t.Text}"));

    [Fact]
    public void Cpp_키워드_문자열_숫자_주석()
    {
        var lines = new[] { "    const auto x = Foo(\"a \\\" b\", 1'000, 0x1F); // 끝", "#include <vector>" };
        var colored = Lines(lines, "cpp");

        Assert.Equal(lines, colored.Select(l => string.Concat(l.Select(t => t.Text))));   // 글자는 그대로
        Assert.Equal("Keyword:const|Keyword:auto|String:\"a \\\" b\"|Number:1'000|Number:0x1F|Comment:// 끝", Show(colored[0]));
        Assert.Equal("Preprocessor:#include <vector>", Show(colored[1]));
    }

    [Fact]
    public void 여러_줄_주석과_원시_문자열은_다음_줄로_이어진다()
    {
        var lines = new[] { "int a; /* 시작", "  가운데 return", "끝 */ return a;", "auto s = R\"x(줄1", "줄2 \" 따옴표)x\"; int b;" };
        var colored = Lines(lines, "cpp");

        Assert.Equal("Keyword:int|Comment:/* 시작", Show(colored[0]));
        Assert.Equal("Comment:  가운데 return", Show(colored[1]));                       // 주석 안 return 은 키워드 아님
        Assert.Equal("Comment:끝 */|Keyword:return", Show(colored[2]));
        Assert.Equal("Keyword:auto|String:R\"x(줄1", Show(colored[3]));
        Assert.Equal("String:줄2 \" 따옴표)x\"|Keyword:int", Show(colored[4]));
    }

    [Fact]
    public void CSharp_축자_보간_원시_문자열()
    {
        var lines = new[] { "var p = @\"C:\\a \"\"b\"\"", "끝\"; string s = $\"{x}\";", "var r = \"\"\"", "raw \" text", "\"\"\"; lock (gate_) { }" };
        var colored = Lines(lines, "csharp");

        Assert.Equal("Keyword:var|String:@\"C:\\a \"\"b\"\"", Show(colored[0]));
        Assert.Equal("String:끝\"|Keyword:string|String:$\"{x}\"", Show(colored[1]));
        Assert.Equal("Keyword:var|String:\"\"\"", Show(colored[2]));
        Assert.Equal("String:raw \" text", Show(colored[3]));
        Assert.Equal("String:\"\"\"|Keyword:lock", Show(colored[4]));
        Assert.DoesNotContain(Lines(["lock"], "cpp")[0], t => t.Kind == Kind.Keyword);         // 언어마다 키워드가 다르다
    }

    [Fact]
    public void 이름_안의_숫자와_키워드_일부는_색칠하지_않는다()
    {
        var colored = Lines(["int2 x = returnValue + v8;"], "cpp");
        Assert.Equal("", Show(colored[0]));
    }
}
