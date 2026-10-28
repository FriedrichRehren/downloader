using Downloader;

namespace Downloader.Tests;

public class YtDlpTests
{
    [Fact]
    public void Url_comes_last_behind_the_end_of_options_marker()
    {
        var arguments = YtDlp.BuildArguments("--exec=rm -rf /", JobMode.Video);

        Assert.Equal("--", arguments[^2]);
        Assert.Equal("--exec=rm -rf /", arguments[^1]);
    }

    [Fact]
    public void Video_merges_best_video_and_audio()
    {
        var arguments = YtDlp.BuildArguments("https://example.com/v", JobMode.Video);

        Assert.Equal("bv*+ba/b", After(arguments, "--format"));
        Assert.DoesNotContain("--extract-audio", arguments);
    }

    [Fact]
    public void Audio_extracts_without_re_encoding()
    {
        var arguments = YtDlp.BuildArguments("https://example.com/v", JobMode.Audio);

        Assert.Equal("ba/b", After(arguments, "--format"));
        Assert.Contains("--extract-audio", arguments);
        Assert.Equal("best", After(arguments, "--audio-format"));
    }

    [Fact]
    public void Parse_takes_the_latest_progress_and_the_marker_lines()
    {
        var report = YtDlp.Parse(string.Join('\n',
            "downloader:title Me at the zoo",
            "downloader:progress 1024 4096",
            "downloader:progress 2048 4096.5",
            "downloader:file /downloads/Me at the zoo [jNQXAC9IVRw].mp4",
            ""));

        Assert.Equal("Me at the zoo", report.Title);
        Assert.Equal(2048, report.DownloadedBytes);
        Assert.Equal(4096, report.TotalBytes);
        Assert.Equal("/downloads/Me at the zoo [jNQXAC9IVRw].mp4", report.FilePath);
        Assert.Null(report.Error);
    }

    [Fact]
    public void Parse_treats_unknown_sizes_as_missing()
    {
        var report = YtDlp.Parse("downloader:progress 512 NA");

        Assert.Equal(512, report.DownloadedBytes);
        Assert.Null(report.TotalBytes);
    }

    [Fact]
    public void Parse_keeps_the_last_error()
    {
        var report = YtDlp.Parse("WARNING: something\r\nERROR: [generic] Unsupported URL: https://example.com\n");

        Assert.Equal("[generic] Unsupported URL: https://example.com", report.Error);
    }

    private static string After(IReadOnlyList<string> arguments, string option) =>
        arguments[arguments.ToList().IndexOf(option) + 1];
}
