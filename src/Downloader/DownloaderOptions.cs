namespace Downloader;

public sealed class DownloaderOptions
{
    public const string Section = "Downloader";

    // unix:///path/to/docker.sock, or tcp://host:port for a socket proxy.
    public string DockerEndpoint { get; set; } = "unix:///var/run/docker.sock";

    // Image every job runs in. Its entrypoint has to be yt-dlp.
    public string JobImage { get; set; } = "friedrichrehren/yt-dlp:latest";

    // Must be the user this app runs as, otherwise one side cannot read or
    // delete what the other wrote. 65532 is the user of the yt-dlp image.
    public string JobUser { get; set; } = "65532:65532";

    // Job containers need outbound internet access and nothing else.
    public string JobNetwork { get; set; } = "bridge";

    public long JobMemoryBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    public TimeSpan JobTimeout { get; set; } = TimeSpan.FromHours(1);

    // How long a finished or failed job stays before its file is deleted.
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(1);

    public int MaxConcurrentJobs { get; set; } = 2;

    // Queued plus running. Anything beyond is refused rather than queued.
    public int MaxActiveJobs { get; set; } = 10;

    public string DataPath { get; set; } = "/downloads";

    // Docker volume mounted at DataPath. Empty means: read it off this
    // container's own mounts.
    public string? Volume { get; set; }
}
