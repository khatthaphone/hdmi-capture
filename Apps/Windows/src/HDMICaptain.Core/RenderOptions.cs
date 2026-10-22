namespace HDMICaptain.Core;

// Picture options, same cases as CaptureCore's RenderOptions and the Kotlin port. The values are
// passed to the HLSL shader as ints, so keep the order in sync with D3DRenderer's shader source.

public enum ScaleMode { Fit, Fill, Stretch, Integer }

public enum FilterMode { Linear, Nearest }

/// <summary>Auto trusts the source's full-range flag.</summary>
public enum ColorRange { Auto, Limited, Full }

/// <summary>YCbCr matrix. Auto picks BT.709 at HD sizes (height ≥ 720) and BT.601 below, like CoreVideo's default.</summary>
public enum ColorMatrix { Auto, Bt601, Bt709 }

public sealed record RenderOptions(
    ScaleMode Scale = ScaleMode.Fit,
    FilterMode Filter = FilterMode.Linear,
    ColorRange ColorRange = ColorRange.Auto,
    ColorMatrix Matrix = ColorMatrix.Auto);
