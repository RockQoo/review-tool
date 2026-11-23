namespace ReviewTool.Core;

// 폴더를 보고 만든 설정 제안(언어 어댑터의 감지기가 채운다). 화면·명령줄 init 이 보여 주고 저장한다.
public sealed record EntryPreset(string Name, string Pattern, List<string> Files, string Description, bool Macro = false)
{
    public EntryPointRule ToRule() => new() { Name = Name, Pattern = Pattern, Files = [.. Files], Macro = Macro };
}

public sealed class DetectedPreset(EntryPreset preset, int matches, List<string> samples)
{
    public EntryPreset Preset { get; } = preset;
    public int Matches { get; } = matches;
    public List<string> Samples { get; } = samples;
    public bool Enabled { get; set; }
}

public sealed record TargetSuggestion(TargetConfig Config, List<DetectedPreset> Presets, List<string> Notes, int SourceCount);
