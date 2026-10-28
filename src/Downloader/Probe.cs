namespace Downloader;

/// <summary>yt-dlp could not read the URL; the message is its own where it gave one.</summary>
public sealed class ProbeException(string message) : Exception(message);

// As the probe prints them, see YtDlp.BuildProbeArguments. Numbers are
// doubles throughout: some sources declare even sizes as 1080.0. For the merged
// "best" selection yt-dlp fills the video fields from the video stream and
// the audio fields from the audio stream.
public sealed record ProbeInfo(
    string? Title,
    string? Uploader,
    double? Duration,
    string? Thumbnail,
    string? Vcodec,
    string? Acodec,
    double? Width,
    double? Height,
    double? Fps,
    double? Abr,
    string? DynamicRange,
    double? Filesize,
    double? FilesizeApprox);

public sealed record ProbeFormat(string? Vcodec, string? Acodec, double? Width, double? Height, double? Fps, string? DynamicRange);

// Resolutions count the shorter side, as yt-dlp and YouTube do: a vertical
// 1080x1920 video is 1080p. Tier is the usual name nearest to it, so 854x470
// shows as 480p.

// What "best quality" downloads.
public sealed record ProbeBest(
    int? Tier,
    double? Fps,
    bool Hdr,
    string? VideoCodec,
    string? AudioCodec,
    double? AudioBitrate,
    long? Size);

// One entry per tier. Resolution is the highest within it, the limit a job
// passes on, so a stream between two names is not cut off. Fps and Hdr cover
// every stream of the tier; H264Fps only the H.264 ones, which MP4 picks and
// which are never HDR.
public sealed record ProbeResolution(int Tier, int Resolution, double? Fps, bool Hdr, bool H264, double? H264Fps);

/// <summary>What a URL offers, boiled down to what the page shows.</summary>
public sealed record Probe(
    string? Title,
    string? Uploader,
    double? Duration,
    string? Thumbnail,
    ProbeBest Best,
    // Every resolution on offer, highest first. Empty for audio-only sources.
    IReadOnlyList<ProbeResolution> Resolutions,
    // So the page can tell which audio formats need no conversion.
    IReadOnlyList<string> AudioCodecs)
{
    public static Probe From(ProbeInfo info, IReadOnlyList<ProbeFormat> formats)
    {
        var hasVideo = info.Vcodec != "none";
        var best = new ProbeBest(
            hasVideo && Resolution(info.Width, info.Height) is { } resolution ? Tier(resolution) : null,
            hasVideo ? info.Fps : null,
            hasVideo && IsHdr(info.DynamicRange),
            hasVideo ? Codec(info.Vcodec) : null,
            Codec(info.Acodec),
            info.Abr,
            (long?)(info.Filesize ?? info.FilesizeApprox));

        // Storyboards carry a height too, but no video codec.
        var resolutions = formats
            .Where(format => format.Vcodec != "none" && Resolution(format.Width, format.Height) is not null)
            .GroupBy(format => Tier(Resolution(format.Width, format.Height)!.Value))
            .OrderByDescending(group => group.Key)
            .Select(group => new ProbeResolution(
                group.Key,
                group.Max(format => Resolution(format.Width, format.Height)!.Value),
                group.Max(format => format.Fps),
                group.Any(format => IsHdr(format.DynamicRange)),
                group.Any(format => Codec(format.Vcodec) == "H.264"),
                group.Where(format => Codec(format.Vcodec) == "H.264").Max(format => format.Fps)))
            .ToList();

        var audioCodecs = formats
            .Select(format => Codec(format.Acodec))
            .OfType<string>()
            .Distinct()
            .ToList();

        return new Probe(info.Title, info.Uploader, info.Duration, info.Thumbnail, best, resolutions, audioCodecs);
    }

    private static int? Resolution(double? width, double? height) =>
        (width, height) switch
        {
            ( > 0, > 0) => (int)Math.Min(width.Value, height.Value),
            (_, > 0) => (int)height.Value,
            _ => null,
        };

    private static readonly int[] Tiers = [144, 240, 360, 480, 720, 1080, 1440, 2160, 4320];

    // Nearest by ratio rather than difference: 800 is closer to 720 than to 1080.
    private static int Tier(int resolution) =>
        resolution > Tiers[^1] ? resolution : Tiers.MinBy(tier => Math.Abs(Math.Log((double)resolution / tier)));

    private static bool IsHdr(string? range) => range is not (null or "SDR");

    // yt-dlp reports codecs as the stream declares them, e.g. avc1.640028.
    private static string? Codec(string? codec) =>
        codec switch
        {
            null or "none" => null,
            _ when codec.StartsWith("av01", StringComparison.Ordinal) => "AV1",
            _ when codec.StartsWith("vp09", StringComparison.Ordinal) || codec.StartsWith("vp9", StringComparison.Ordinal) => "VP9",
            _ when codec.StartsWith("avc", StringComparison.Ordinal) || codec == "h264" => "H.264",
            _ when codec.StartsWith("hev1", StringComparison.Ordinal) || codec.StartsWith("hvc1", StringComparison.Ordinal) || codec == "h265" => "H.265",
            _ when codec.StartsWith("mp4a", StringComparison.Ordinal) || codec == "aac" => "AAC",
            "opus" => "Opus",
            "vorbis" => "Vorbis",
            "mp3" => "MP3",
            "flac" => "FLAC",
            _ => codec,
        };
}
