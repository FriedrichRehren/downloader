using System.Globalization;

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

    public static bool Supports(JobMode mode, JobFormat format) =>
        (mode, format) is (JobMode.Video or JobMode.Audio, JobFormat.Original)
            or (JobMode.Video, JobFormat.Mp4)
            or (JobMode.Audio, JobFormat.Mp3);

    public static IReadOnlyList<string> BuildArguments(string url, JobMode mode, JobFormat format)
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

        arguments.AddRange((mode, format) switch
        {
            // Best video and best audio, merged into the first container that
            // takes both codecs as they are, so nothing is re-encoded. For
            // YouTube's AV1 and Opus that is webm, which browsers play directly.
            (JobMode.Video, JobFormat.Original) => ["--format", "bv*+ba/b", "--merge-output-format", "mp4/webm/mkv"],
            // yt-dlp's -t mp4, spelled out so a new release cannot change it.
            // H.264 and AAC sort first, still without re-encoding; on YouTube
            // that means 1080p at most.
            (JobMode.Video, JobFormat.Mp4) =>
            [
                "--format", "bv*+ba/b",
                "--format-sort", "vcodec:h264,lang,quality,res,fps,hdr:12,acodec:aac",
                "--merge-output-format", "mp4",
                "--remux-video", "mp4",
            ],
            // The original audio stream, again without re-encoding.
            (JobMode.Audio, JobFormat.Original) => ["--format", "ba/b", "--extract-audio", "--audio-format", "best"],
            // yt-dlp's -t mp3, but at the best VBR quality instead of its
            // default of 5. An MP3 source is kept as it is.
            (JobMode.Audio, JobFormat.Mp3) =>
            [
                "--format", "ba[acodec^=mp3]/ba/b",
                "--extract-audio",
                "--audio-format", "mp3",
                "--audio-quality", "0",
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, $"{format} is not available for {mode}."),
        });

        // Everything after -- is a URL, even if it starts with a dash.
        arguments.Add("--");
        arguments.Add(url);
        return arguments;
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

    // Missing fields come out as "NA"; estimates can be fractional.
    private static long? ParseBytes(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bytes) ? (long)bytes : null;
}
