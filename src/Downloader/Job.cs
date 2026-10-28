namespace Downloader;

public enum JobMode
{
    Video,
    Audio,
}

// What a job delivers. Which mode offers which format is up to YtDlp.
public enum JobFormat
{
    // The best the source offers, kept as it is.
    Original,
    // Video in MP4 with H.264 and AAC, which every player takes.
    Mp4,
    // The best video and audio in an MKV container.
    Mkv,
    // Audio in the named format, converted only if the source is something else.
    Mp3,
    M4a,
    Flac,
    Wav,
}

public enum JobState
{
    Queued,
    Running,
    Completed,
    Failed,
}

public sealed class Job
{
    public required string Id { get; init; }
    // The browser that started the job, see Client.
    public required string Owner { get; init; }
    public required string Url { get; init; }
    public required JobMode Mode { get; init; }
    // Not required: jobs saved before formats existed are Original.
    public JobFormat Format { get; init; }
    // Video only: the highest resolution wanted, counting the shorter side
    // like 1080p does. Null means the highest there is.
    public int? Resolution { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public JobState State { get; set; }
    public string? Title { get; set; }
    public string? FileName { get; set; }
    public long? FileSize { get; set; }
    public long? DownloadedBytes { get; set; }
    public long? TotalBytes { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed record JobView(
    string Id,
    string Url,
    JobMode Mode,
    JobFormat Format,
    int? Resolution,
    JobState State,
    string? Title,
    string? FileName,
    long? FileSize,
    long? DownloadedBytes,
    long? TotalBytes,
    string? Error,
    DateTimeOffset CreatedAt,
    // Seconds rather than a timestamp, so the countdown in the browser does not
    // depend on the client's clock.
    long? ExpiresIn);
