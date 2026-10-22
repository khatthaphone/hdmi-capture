namespace HDMICaptain.Core;

/// <summary>What a capture endpoint reports (WASAPI shared-mode format), reduced to what the fix needs.</summary>
public readonly record struct CaptureAudioFormat(int SampleRate, int Channels, int BitsPerSample);

/// <summary>
/// The MS2109 audio quirk (docs/research/windows.md section 5): the chip advertises 96 kHz mono 16-bit
/// but really sends 48 kHz stereo (same byte rate, possibly with L/R swapped). Reinterpret that
/// exact format as 48 kHz stereo and leave every other format alone.
/// Shared fixture: docs/fixtures/ms2109-audio.json.
/// </summary>
public static class AudioFormatFix
{
    public static readonly CaptureAudioFormat Ms2109Reported = new(96_000, 1, 16);
    public static readonly CaptureAudioFormat Ms2109Actual = new(48_000, 2, 16);

    public static bool NeedsReinterpretation(CaptureAudioFormat reported) => reported == Ms2109Reported;

    /// <summary>The format to read the stream as, and whether it differs from what was reported.</summary>
    public static (CaptureAudioFormat Format, bool Reinterpreted) Apply(CaptureAudioFormat reported) =>
        NeedsReinterpretation(reported) ? (Ms2109Actual, true) : (reported, false);
}
