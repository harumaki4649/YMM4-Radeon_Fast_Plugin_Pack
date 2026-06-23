namespace RadeonAmfVideoWriterPlugin;

internal sealed class AmfSettings
{
    public AmfCodec Codec { get; set; } = AmfCodec.H264;
    public int BitrateKbps { get; set; } = 12000;
    public AmfQuality Quality { get; set; } = AmfQuality.Speed;
    public AmfRateControl RateControl { get; set; } = AmfRateControl.YouTubeRecommended;
    public int QueueDepth { get; set; } = 32;
    public bool EnablePreAnalysis { get; set; }
    public bool EnableDebugLog { get; set; }
}

internal enum AmfCodec
{
    H264,
    H265,
}

internal enum AmfQuality
{
    Speed,
    Balanced,
    Quality,
}

internal enum AmfRateControl
{
    Fixed,
    Variable,
    YouTubeRecommended,
}
