namespace HDMICaptain.Core;

/// <summary>Port of CaptureCore's ProFeature (same cases and titles). Streaming and virtual camera stay hidden on Windows for now.</summary>
public enum ProFeature { Recording, ReplayBuffer, Streaming, VirtualCamera }

public static class ProFeatureExtensions
{
    public static string Title(this ProFeature feature) => feature switch
    {
        ProFeature.Recording => "Recording",
        ProFeature.ReplayBuffer => "Save Recent",
        ProFeature.Streaming => "Live streaming",
        ProFeature.VirtualCamera => "Virtual camera",
        _ => feature.ToString(),
    };
}
