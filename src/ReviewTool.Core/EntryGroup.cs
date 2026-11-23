namespace ReviewTool.Core;

// 진입점 묶음 이름("폴더 / 클래스")을 화면의 2단 트리(폴더 ▸ 클래스 ▸ 진입점)로 나눈다.
// 묶음 이름 자체는 그대로 둔다(ProcessMap 등이 앞부분을 프로세스로 쓴다).
public static class EntryGroup
{
    private const string Separator = " / ";

    // 앞(폴더·프로젝트). 구분자가 없으면 전체가 폴더다.
    public static string Folder(string group)
    {
        var i = group.IndexOf(Separator, StringComparison.Ordinal);
        return i < 0 ? group : group[..i];
    }

    // 뒤(클래스). 없으면 빈 문자열(진입점을 폴더 바로 아래에 둔다).
    public static string Class(string group)
    {
        var i = group.IndexOf(Separator, StringComparison.Ordinal);
        return i < 0 ? "" : group[(i + Separator.Length)..];
    }
}
