using Downloader;

namespace Downloader.Tests;

public class YtDlpTests
{
    [Fact]
    public void Url_comes_last_behind_the_end_of_options_marker()
    {
        var arguments = YtDlp.BuildArguments("--exec=rm -rf /", JobMode.Video, JobFormat.Original);

        Assert.Equal("--", arguments[^2]);
        Assert.Equal("--exec=rm -rf /", arguments[^1]);
    }

    [Fact]
    public void Video_merges_best_video_and_audio()
    {
        var arguments = YtDlp.BuildArguments("https://example.com/v", JobMode.Video, JobFormat.Original);

        Assert.Equal("bv*+ba/b", After(arguments, "--format"));
        Assert.DoesNotContain("--extract-audio", arguments);
    }

    [Fact]
    public void Audio_extracts_without_re_encoding()
    {
        var arguments = YtDlp.BuildArguments("https://example.com/v", JobMode.Audio, JobFormat.Original);

        Assert.Equal("ba/b", After(arguments, "--format"));
        Assert.Contains("--extract-audio", arguments);
        Assert.Equal("best", After(arguments, "--audio-format"));
    }

    [Fact]
    public void Mp4_prefers_h264_and_aac_in_an_mp4_container()
    {
        var arguments = YtDlp.BuildArguments("https://example.com/v", JobMode.Video, JobFormat.Mp4);

        Assert.StartsWith("vcodec:h264,", After(arguments, "--format-sort"));
        Assert.EndsWith(",acodec:aac", After(arguments, "--format-sort"));
        Assert.Equal("mp4", After(arguments, "--merge-output-format"));
        Assert.Equal("mp4", After(arguments, "--remux-video"));
    }

    [Fact]
    public void Mp3_converts_at_the_best_quality()
    {
        var arguments = YtDlp.BuildArguments("https://example.com/v", JobMode.Audio, JobFormat.Mp3);

        Assert.Contains("--extract-audio", arguments);
        Assert.Equal("mp3", After(arguments, "--audio-format"));
        Assert.Equal("0", After(arguments, "--audio-quality"));
    }

    [Theory]
    [InlineData(JobFormat.M4a, "m4a")]
    [InlineData(JobFormat.Flac, "flac")]
    [InlineData(JobFormat.Wav, "wav")]
    public void Audio_formats_extract_to_their_codec(JobFormat format, string codec)
    {
        var arguments = YtDlp.BuildArguments("https://example.com/v", JobMode.Audio, format);

        Assert.Contains("--extract-audio", arguments);
        Assert.Equal(codec, After(arguments, "--audio-format"));
    }

    [Fact]
    public void Mkv_keeps_the_best_streams_in_mkv()
    {
        var arguments = YtDlp.BuildArguments("https://example.com/v", JobMode.Video, JobFormat.Mkv);

        Assert.Equal("bv*+ba/b", After(arguments, "--format"));
        Assert.Equal("mkv", After(arguments, "--remux-video"));
        Assert.DoesNotContain("--format-sort", arguments);
    }

    [Theory]
    [InlineData(JobFormat.Original)]
    [InlineData(JobFormat.Mkv)]
    public void Resolution_limit_sorts_first(JobFormat format)
    {
        var arguments = YtDlp.BuildArguments("https://example.com/v", JobMode.Video, format, resolution: 720);

        Assert.Equal("res:720", After(arguments, "--format-sort"));
    }

    [Fact]
    public void Mp4_resolution_limit_goes_ahead_of_quality()
    {
        var arguments = YtDlp.BuildArguments("https://example.com/v", JobMode.Video, JobFormat.Mp4, resolution: 720);

        Assert.StartsWith("vcodec:h264,res:720,", After(arguments, "--format-sort"));
        Assert.DoesNotContain(",res,", After(arguments, "--format-sort"));
    }

    [Fact]
    public void Every_format_belongs_to_a_mode()
    {
        Assert.All(Enum.GetValues<JobFormat>(), format =>
            Assert.True(YtDlp.Supports(JobMode.Video, format) || YtDlp.Supports(JobMode.Audio, format)));
    }

    [Theory]
    [InlineData(JobMode.Video, JobFormat.Mp3)]
    [InlineData(JobMode.Video, JobFormat.Wav)]
    [InlineData(JobMode.Audio, JobFormat.Mp4)]
    [InlineData(JobMode.Audio, JobFormat.Mkv)]
    [InlineData(JobMode.Audio, (JobFormat)42)]
    public void Formats_belong_to_one_mode(JobMode mode, JobFormat format)
    {
        Assert.False(YtDlp.Supports(mode, format));
        Assert.Throws<ArgumentOutOfRangeException>(() => YtDlp.BuildArguments("https://example.com/v", mode, format));
    }

    [Fact]
    public void Probe_only_prints_and_keeps_the_url_last()
    {
        var arguments = YtDlp.BuildProbeArguments("--exec=rm -rf /");

        Assert.Equal(2, arguments.Count(argument => argument == "--print"));
        Assert.DoesNotContain("--paths", arguments);
        Assert.Equal("--", arguments[^2]);
        Assert.Equal("--exec=rm -rf /", arguments[^1]);
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
