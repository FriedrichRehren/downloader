using System.Globalization;
using System.Text.Json;

namespace Downloader;

public sealed record YtDlpReport(string? Title, string? FilePath, long? DownloadedBytes, long? TotalBytes, string? Error);

/// <summary>
/// Command line for a job container and how to read its output. The job
/// prints its own marker lines, so nothing here depends on yt-dlp's
/// human-readable output staying the same between releases.
/// </summary>
public static class YtDlp
{
    private const string Marker = "downloader:";

    // yt-dlp's field names are snake_case.
    private static readonly JsonSerializerOptions ProbeJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static bool Supports(JobMode mode, JobFormat format) => Selection(mode, format, resolution: null) is not null;

    /// <summary>
    /// Reads what a URL offers without downloading anything. --print implies
    /// --simulate. Only the fields the page needs: the full info dict runs to
    /// hundreds of kilobytes.
    /// </summary>
    public static IReadOnlyList<string> BuildProbeArguments(string url) =>
    [
        "--no-playlist",
        "--playlist-items", "1",
        "--no-cache-dir",
        // The Video default, so the top-level fields describe exactly what
        // "best quality" downloads, as yt-dlp itself chose it.
        "--format", "bv*+ba/b",
        "--print", $"{Marker}info %(.{{title,uploader,duration,thumbnail,vcodec,acodec,width,height,fps,abr,dynamic_range,filesize,filesize_approx}})j",
        "--print", $"{Marker}formats %(formats.:.{{vcodec,acodec,width,height,fps,dynamic_range}})j",
        "--",
        url,
    ];

    public static IReadOnlyList<string> BuildArguments(string url, JobMode mode, JobFormat format, int? resolution = null)
    {
        var arguments = new List<string>
        {
            "--paths", "/downloads",
            "--output", "%(title).180B [%(id)s].%(ext)s",
            // The file ends up on whatever machine downloads it.
            "--windows-filenames",
            // A watch URL with &list= would otherwise fetch the whole playlist,
            // and a playlist URL stops after its first entry.
            "--no-playlist",
            "--playlist-items", "1",
            "--no-cache-dir",
            "--no-mtime",
            "--embed-metadata",
            // --print implies --quiet and --simulate. Undo the second, and keep
            // progress coming despite the first.
            "--no-simulate",
            "--progress",
            "--newline",
            "--progress-delta", "1",
            "--progress-template", $"download:{Marker}progress %(progress.downloaded_bytes)s %(progress.total_bytes,progress.total_bytes_estimate)s",
            "--print", $"before_dl:{Marker}title %(title)s",
            "--print", $"after_move:{Marker}file %(filepath)s",
        };

        arguments.AddRange(Selection(mode, format, resolution)
            ?? throw new ArgumentOutOfRangeException(nameof(format), format, $"{format} is not available for {mode}."));

        // Everything after -- is a URL, even if it starts with a dash.
        arguments.Add("--");
        arguments.Add(url);
        return arguments;
    }

    /// <summary>
    /// Format options per mode and format, or null if the mode does not offer
    /// the format. MP4, MKV and MP3 follow yt-dlp's -t presets, spelled out so
    /// a new release cannot change them.
    /// </summary>
    private static string[]? Selection(JobMode mode, JobFormat format, int? resolution)
    {
        // yt-dlp ranks only its two fixed fields above a sort field given here,
        // so the best video within the limit wins over everything else. res is
        // the shorter side, like the resolution the probe reports.
        string[] limit = resolution is { } highest ? ["--format-sort", $"res:{highest}"] : [];

        return (mode, format) switch
        {
            // Best video and best audio, merged into the first container that
            // takes both codecs as they are, so nothing is re-encoded. For
            // YouTube's AV1 and Opus that is webm, which browsers play directly.
            (JobMode.Video, JobFormat.Original) => ["--format", "bv*+ba/b", .. limit, "--merge-output-format", "mp4/webm/mkv"],
            // H.264 and AAC sort first, still without re-encoding; on YouTube
            // that means 1080p at most. A resolution limit has to go ahead of
            // quality, which would otherwise prefer the larger stream.
            (JobMode.Video, JobFormat.Mp4) =>
            [
                "--format", "bv*+ba/b",
                "--format-sort", resolution is { } h264
                    ? $"vcodec:h264,res:{h264},lang,quality,fps,hdr:12,acodec:aac"
                    : "vcodec:h264,lang,quality,res,fps,hdr:12,acodec:aac",
                "--merge-output-format", "mp4",
                "--remux-video", "mp4",
            ],
            (JobMode.Video, JobFormat.Mkv) => ["--format", "bv*+ba/b", .. limit, "--merge-output-format", "mkv", "--remux-video", "mkv"],
            // The original audio stream, again without re-encoding.
            (JobMode.Audio, JobFormat.Original) => ["--format", "ba/b", "--extract-audio", "--audio-format", "best"],
            // A source already in the target codec is kept as it is, so prefer
            // one. --audio-quality 0 is the best VBR setting for MP3 (V0) and
            // AAC alike.
            (JobMode.Audio, JobFormat.Mp3) => ["--format", "ba[acodec^=mp3]/ba/b", "--extract-audio", "--audio-format", "mp3", "--audio-quality", "0"],
            (JobMode.Audio, JobFormat.M4a) => ["--format", "ba[acodec^=mp4a]/ba/b", "--extract-audio", "--audio-format", "m4a", "--audio-quality", "0"],
            (JobMode.Audio, JobFormat.Flac) => ["--format", "ba/b", "--extract-audio", "--audio-format", "flac"],
            (JobMode.Audio, JobFormat.Wav) => ["--format", "ba/b", "--extract-audio", "--audio-format", "wav"],
            _ => null,
        };
    }

    public static YtDlpReport Parse(string output)
    {
        string? title = null, file = null, error = null;
        long? downloaded = null, total = null;

        foreach (var line in output.Split('\n', '\r'))
        {
            if (line.StartsWith(Marker + "progress ", StringComparison.Ordinal))
            {
                var values = line[(Marker.Length + "progress ".Length)..].Split(' ');
                downloaded = ParseBytes(values.ElementAtOrDefault(0));
                total = ParseBytes(values.ElementAtOrDefault(1));
            }
            else if (line.StartsWith(Marker + "title ", StringComparison.Ordinal))
            {
                title = line[(Marker.Length + "title ".Length)..].Trim();
            }
            else if (line.StartsWith(Marker + "file ", StringComparison.Ordinal))
            {
                file = line[(Marker.Length + "file ".Length)..].Trim();
            }
            else if (line.StartsWith("ERROR: ", StringComparison.Ordinal))
            {
                error = line["ERROR: ".Length..].Trim();
            }
        }

        return new YtDlpReport(title, file, downloaded, total, error);
    }

    /// <exception cref="ProbeException">yt-dlp failed or printed nothing usable.</exception>
    public static Probe ParseProbe(string output)
    {
        string? info = null, formats = null, error = null;

        foreach (var line in output.Split('\n', '\r'))
        {
            if (line.StartsWith(Marker + "info ", StringComparison.Ordinal))
            {
                info = line[(Marker.Length + "info ".Length)..];
            }
            else if (line.StartsWith(Marker + "formats ", StringComparison.Ordinal))
            {
                formats = line[(Marker.Length + "formats ".Length)..];
            }
            else if (line.StartsWith("ERROR: ", StringComparison.Ordinal))
            {
                error = line["ERROR: ".Length..].Trim();
            }
        }

        if (info is null)
        {
            throw new ProbeException(error ?? "yt-dlp hat keine Formate geliefert.");
        }

        try
        {
            return Probe.From(
                JsonSerializer.Deserialize<ProbeInfo>(info, ProbeJson)!,
                formats is null or "NA" ? [] : JsonSerializer.Deserialize<List<ProbeFormat>>(formats, ProbeJson)!);
        }
        catch (JsonException)
        {
            throw new ProbeException("Die Antwort von yt-dlp ist nicht lesbar.");
        }
    }

    // Missing fields come out as "NA"; estimates can be fractional.
    private static long? ParseBytes(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bytes) ? (long)bytes : null;
}
