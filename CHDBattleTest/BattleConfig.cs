namespace CHDBattleTest;

public sealed class BattleConfig
{
    public string InputDir { get; set; } = @"H:\CHDTest";
    public string OutputRoot { get; set; } = "";
    public string Filter { get; set; } = "*.chd";
    public int MaxFiles { get; set; }
    public double MinMb { get; set; }
    public double MaxMb { get; set; }
    public string CodecRaw { get; set; } = "zstd";
    public string CodecCd { get; set; } = "cdzl";
    public int Workers { get; set; } = Environment.ProcessorCount;
    public bool Decode { get; set; } = true;
    public bool Encode { get; set; } = true;
    public bool IncludeAv { get; set; }
    public bool LibDecode { get; set; }
    public int TimeoutMinutes { get; set; } = 45;
    public bool KeepTemp { get; set; }
    public bool Resume { get; set; }
    public bool ListOnly { get; set; }
    public bool Verbose { get; set; }

    public string ChdmanPath { get; set; } = "";
    public string ChdSharpPath { get; set; } = "";

    public string ResultsDir => OutputRoot;
    public string WorkRoot => Path.Combine(OutputRoot, "work");
    public string CsvPath => Path.Combine(OutputRoot, "results.csv");
    public string MdPath => Path.Combine(OutputRoot, "report.md");
    public string LogPath => Path.Combine(OutputRoot, "battle.log");

    public string CodecFor(MediaKind kind)
    {
        return kind switch
        {
            MediaKind.Cd or MediaKind.GdRom => CodecCd,
            MediaKind.LaserDisc => "avhu",
            _ => CodecRaw
        };
    }
}