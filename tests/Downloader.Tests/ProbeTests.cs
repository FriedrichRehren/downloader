namespace Downloader.Tests;

public class ProbeTests
{
    // Shaped like a real probe of a 4K HDR video: a storyboard, two audio
    // streams and video from 1080p H.264 up to 2160p AV1.
    private const string Output = """
        WARNING: [youtube] Some warning
        downloader:info {"title": "Costa Rica", "uploader": "Someone", "duration": 314, "thumbnail": "https://example.com/t.jpg", "vcodec": "av01.0.13M.10.0.110.09.16.09.0", "acodec": "opus", "width": 3840, "height": 2160, "fps": 60, "abr": 132.567, "dynamic_range": "HDR10", "filesize": null, "filesize_approx": 971332472}
        downloader:formats [{"vcodec": "none", "acodec": "none", "width": 48, "height": 27}, {"vcodec": "none", "acodec": "mp4a.40.2"}, {"vcodec": "none", "acodec": "opus"}, {"vcodec": "avc1.640028", "acodec": "none", "width": 1920, "height": 1080, "fps": 30.0, "dynamic_range": "SDR"}, {"vcodec": "vp09.00.51.08", "acodec": "none", "width": 3840, "height": 2160, "fps": 60, "dynamic_range": "SDR"}, {"vcodec": "av01.0.13M.10", "acodec": "none", "width": 3840.0, "height": 2160.0, "fps": 60.0, "dynamic_range": "HDR10"}]
        """;

    [Fact]
    public void Best_is_what_yt_dlp_chose()
    {
        var probe = YtDlp.ParseProbe(Output);

        Assert.Equal("Costa Rica", probe.Title);
        Assert.Equal(new ProbeBest(2160, 60, true, "AV1", "Opus", 132.567, 971332472), probe.Best);
    }

    [Fact]
    public void Resolutions_come_highest_first_without_storyboards()
    {
        var probe = YtDlp.ParseProbe(Output);

        Assert.Equal(
            [new ProbeResolution(2160, 2160, 60, true, false, null), new ProbeResolution(1080, 1080, 30, false, true, 30)],
            probe.Resolutions);
        Assert.Equal(["AAC", "Opus"], probe.AudioCodecs);
    }

    [Fact]
    public void Vertical_video_counts_the_shorter_side()
    {
        var probe = Probe.From(
            new ProbeInfo(null, null, null, null, "avc1", "mp4a.40.2", 1080, 1920, 30, 128, "SDR", null, null),
            [new ProbeFormat("avc1", "none", 1080, 1920, 30, "SDR"), new ProbeFormat("avc1", "none", 720, 1280, 30, "SDR")]);

        Assert.Equal(1080, probe.Best.Tier);
        Assert.Equal([1080, 720], probe.Resolutions.Select(offer => offer.Tier));
    }

    [Fact]
    public void Odd_resolutions_join_the_nearest_tier_and_keep_their_highest()
    {
        var probe = Probe.From(
            new ProbeInfo(null, null, null, null, "vp9", "opus", 872, 480, 30, 128, "SDR", null, null),
            [
                new ProbeFormat("vp9", "none", 872, 480, 30, "SDR"),
                new ProbeFormat("avc1", "none", 854, 470, 30, "SDR"),
                new ProbeFormat("avc1", "none", 640, 352, 30, "SDR"),
                new ProbeFormat("avc1", "none", 1422, 800, 30, "SDR"),
            ]);

        Assert.Equal(480, probe.Best.Tier);
        Assert.Equal(
            [new ProbeResolution(720, 800, 30, false, true, 30), new ProbeResolution(480, 480, 30, false, true, 30), new ProbeResolution(360, 352, 30, false, true, 30)],
            probe.Resolutions);
    }

    [Fact]
    public void Audio_only_source_has_no_video()
    {
        var probe = YtDlp.ParseProbe("""
            downloader:info {"title": "Song", "vcodec": "none", "acodec": "mp4a.40.2", "abr": 160, "filesize_approx": 4277720}
            downloader:formats [{"vcodec": "none", "acodec": "mp4a.40.2"}, {"vcodec": "none", "acodec": "opus"}]
            """);

        Assert.Null(probe.Best.Tier);
        Assert.Null(probe.Best.VideoCodec);
        Assert.Equal("AAC", probe.Best.AudioCodec);
        Assert.Empty(probe.Resolutions);
    }

    [Fact]
    public void Missing_format_list_is_empty()
    {
        var probe = YtDlp.ParseProbe("downloader:info {\"title\": \"x\", \"vcodec\": \"h264\", \"height\": 720}\ndownloader:formats NA\n");

        Assert.Equal(720, probe.Best.Tier);
        Assert.Empty(probe.Resolutions);
    }

    [Fact]
    public void Error_from_yt_dlp_becomes_the_message()
    {
        var exception = Assert.Throws<ProbeException>(() =>
            YtDlp.ParseProbe("WARNING: x\nERROR: [generic] Unsupported URL: https://example.com\n"));

        Assert.Equal("[generic] Unsupported URL: https://example.com", exception.Message);
    }

    [Fact]
    public void Unreadable_output_is_a_probe_error()
    {
        Assert.Throws<ProbeException>(() => YtDlp.ParseProbe("downloader:info {not json"));
    }
}
