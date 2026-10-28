using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Downloader;

/// <summary>
/// Runs every download in its own throwaway yt-dlp container that writes into
/// the job's directory on the shared volume, and deletes the result once the
/// retention period is over.
/// </summary>
public sealed class JobManager(
    DockerClient docker,
    JobStore store,
    IOptions<DownloaderOptions> options,
    ILogger<JobManager> logger) : BackgroundService
{
    // Set on every job container, so startup can remove what a crash left behind.
    public const string JobLabel = "io.rehren.downloader.job";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    // Someone is waiting for the probe, and it only takes seconds.
    private static readonly TimeSpan ProbePollInterval = TimeSpan.FromMilliseconds(250);
    // Reading a URL and downloading it a moment later would otherwise pull
    // the image twice within seconds, and Docker Hub counts every pull.
    private static readonly TimeSpan PullInterval = TimeSpan.FromMinutes(5);

    private readonly DownloaderOptions settings = options.Value;
    private readonly ConcurrentDictionary<string, Entry> entries = new();
    private readonly SemaphoreSlim slots = new(options.Value.MaxConcurrentJobs);
    private readonly SemaphoreSlim probeSlots = new(options.Value.MaxConcurrentProbes);
    private readonly SemaphoreSlim pullLock = new(1, 1);
    // Images a pull replaced. Removing one fails while a job still runs in it,
    // so it stays here until an attempt after a later job goes through.
    private readonly HashSet<string> replacedImages = [];
    private DateTimeOffset lastPull;
    private string? volume;
    private volatile bool stopping;

    public IReadOnlyList<JobView> List(string owner) =>
        entries.Values
            .Where(entry => entry.Job.Owner == owner)
            .Select(entry => Snapshot(entry.Job))
            .OrderByDescending(job => job.CreatedAt)
            .ToList();

    public JobView? Enqueue(string url, JobMode mode, JobFormat format, int? resolution, string owner)
    {
        if (entries.Values.Count(entry => entry.Job.State is JobState.Queued or JobState.Running) >= settings.MaxActiveJobs)
        {
            return null;
        }

        var job = new Job
        {
            Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
            Owner = owner,
            Url = url,
            Mode = mode,
            Format = format,
            Resolution = resolution,
            CreatedAt = DateTimeOffset.UtcNow,
            State = JobState.Queued,
        };
        store.Save(job);

        var entry = new Entry(job);
        entries[job.Id] = entry;
        entry.Execution = Task.Run(() => RunAsync(entry));
        return Snapshot(job);
    }

    public FileInfo? GetFile(string id, string owner)
    {
        if (!TryGet(id, owner, out var entry))
        {
            return null;
        }

        lock (entry.Job)
        {
            return entry.Job is { State: JobState.Completed, FileName: { } name } ? store.GetFile(id, name) : null;
        }
    }

    /// <summary>Cancels the job if it is still running and removes it with its file.</summary>
    public async Task<bool> DeleteAsync(string id, string owner)
    {
        if (!TryGet(id, owner, out var entry))
        {
            return false;
        }

        await RemoveAsync(entry);
        return true;
    }

    /// <summary>
    /// Reads which formats a URL offers, in a throwaway container like a job's
    /// but without the volume. Probes run beside jobs, not in their slots:
    /// someone is waiting for the answer.
    /// </summary>
    /// <returns>Null if too many probes are running already.</returns>
    /// <exception cref="ProbeException">yt-dlp could not read the URL.</exception>
    public async Task<Probe?> ProbeAsync(string url, CancellationToken cancellationToken)
    {
        if (!await probeSlots.WaitAsync(0, cancellationToken))
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.ProbeTimeout);
        string? containerId = null;

        try
        {
            await UpdateImageAsync(timeout.Token);
            containerId = await docker.CreateContainerAsync(
                $"downloader-probe-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}",
                ContainerSpec(YtDlp.BuildProbeArguments(url), label: "probe", mounts: []),
                timeout.Token);
            await docker.StartContainerAsync(containerId, timeout.Token);

            ContainerState state;
            do
            {
                await Task.Delay(ProbePollInterval, timeout.Token);
                state = (await docker.InspectContainerAsync(containerId, timeout.Token)).State;
            }
            while (state.Running);

            return YtDlp.ParseProbe(await docker.GetLogsAsync(containerId, tail: null, timeout.Token));
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ProbeException($"Nach {settings.ProbeTimeout.TotalSeconds:0} Sekunden abgebrochen.");
        }
        finally
        {
            if (containerId is not null)
            {
                await RemoveContainerQuietlyAsync(containerId);
                await RemoveReplacedImagesAsync();
            }

            probeSlots.Release();
        }
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var job in store.Load())
        {
            if (job.State is JobState.Queued or JobState.Running)
            {
                Fail(job, "Abgebrochen, weil der Dienst neu gestartet wurde.");
            }

            entries[job.Id] = new Entry(job);
        }

        await base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        stopping = true;
        foreach (var entry in entries.Values)
        {
            await entry.Cancellation.CancelAsync();
        }

        await Task.WhenAll(entries.Values.Select(entry => entry.Execution)).WaitAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RemoveOrphanedContainersAsync(stoppingToken);
            // Every job pulls anyway; this only spares the first one the wait.
            await UpdateImageAsync(stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Not fatal: the first job tries again and shows the error.
            logger.LogWarning(exception, "Preparing the job image failed");
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            do
            {
                foreach (var entry in entries.Values.Where(entry => entry.Job.ExpiresAt <= DateTimeOffset.UtcNow))
                {
                    logger.LogInformation("Job {Id} expired", entry.Job.Id);
                    await RemoveAsync(entry);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunAsync(Entry entry)
    {
        var job = entry.Job;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(entry.Cancellation.Token);
        string? containerId = null;

        try
        {
            await slots.WaitAsync(entry.Cancellation.Token);
            try
            {
                timeout.CancelAfter(settings.JobTimeout);
                Update(job, persist: true, job => job.State = JobState.Running);

                containerId = await CreateContainerAsync(job, timeout.Token);
                await docker.StartContainerAsync(containerId, timeout.Token);
                logger.LogInformation("Job {Id} started: {Mode} {Url}", job.Id, job.Mode, job.Url);

                ContainerState state;
                do
                {
                    await Task.Delay(PollInterval, timeout.Token);
                    state = (await docker.InspectContainerAsync(containerId, timeout.Token)).State;
                    var progress = YtDlp.Parse(await docker.GetLogsAsync(containerId, tail: 20, timeout.Token));
                    Update(job, persist: false, job =>
                    {
                        job.Title = progress.Title ?? job.Title;
                        job.DownloadedBytes = progress.DownloadedBytes ?? job.DownloadedBytes;
                        job.TotalBytes = progress.TotalBytes ?? job.TotalBytes;
                    });
                }
                while (state.Running);

                // The file and error lines come last; the title may have scrolled
                // out of the short tail if the download was quick.
                var report = YtDlp.Parse(await docker.GetLogsAsync(containerId, tail: 200, timeout.Token));
                Finish(job, state, report);
            }
            finally
            {
                slots.Release();
            }
        }
        catch (OperationCanceledException) when (entry.Cancellation.IsCancellationRequested)
        {
            Fail(job, stopping ? "Abgebrochen, weil der Dienst neu gestartet wurde." : "Abgebrochen.");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            Fail(job, $"Nach {settings.JobTimeout:h\\:mm} Stunden abgebrochen.");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Job {Id} failed", job.Id);
            Fail(job, exception.Message);
        }
        finally
        {
            if (containerId is not null)
            {
                await RemoveContainerQuietlyAsync(containerId);
                await RemoveReplacedImagesAsync();
            }
        }
    }

    private bool TryGet(string id, string owner, [NotNullWhen(true)] out Entry? entry) =>
        entries.TryGetValue(id, out entry) && entry.Job.Owner == owner;

    private async Task RemoveAsync(Entry entry)
    {
        await entry.Cancellation.CancelAsync();
        // RunAsync catches everything, and its container is gone once it returns.
        await entry.Execution;
        if (entries.TryRemove(entry.Job.Id, out _))
        {
            store.Delete(entry.Job.Id);
        }
    }

    private async Task<string> CreateContainerAsync(Job job, CancellationToken cancellationToken)
    {
        await UpdateImageAsync(cancellationToken);
        var source = await ResolveVolumeAsync(cancellationToken);
        store.CreateFilesDirectory(job.Id);

        // The job sees its own directory and nothing else of the volume.
        object[] mounts =
        [
            new
            {
                Type = "volume",
                Source = source,
                Target = "/downloads",
                VolumeOptions = new { Subpath = JobStore.FilesSubpath(job.Id) },
            },
        ];

        return await docker.CreateContainerAsync(
            $"downloader-job-{job.Id}",
            ContainerSpec(YtDlp.BuildArguments(job.Url, job.Mode, job.Format, job.Resolution), job.Id, mounts),
            cancellationToken);
    }

    // Jobs and probes alike. The label lets startup remove what a crash left behind.
    private object ContainerSpec(IReadOnlyList<string> command, string label, object[] mounts) => new
    {
        Image = settings.JobImage,
        Cmd = command,
        User = settings.JobUser,
        // deno and Python want a writable home for their caches.
        Env = new[] { "HOME=/tmp" },
        Labels = new Dictionary<string, string> { [JobLabel] = label },
        HostConfig = new
        {
            NetworkMode = settings.JobNetwork,
            Mounts = mounts,
            ReadonlyRootfs = true,
            Tmpfs = new Dictionary<string, string> { ["/tmp"] = "size=512m" },
            CapDrop = new[] { "ALL" },
            SecurityOpt = new[] { "no-new-privileges:true" },
            Memory = settings.JobMemoryBytes,
            PidsLimit = 256,
            LogConfig = new
            {
                Type = "json-file",
                Config = new Dictionary<string, string> { ["max-size"] = "10m", ["max-file"] = "1" },
            },
        },
    };

    private void Finish(Job job, ContainerState state, YtDlpReport report)
    {
        var file = state.ExitCode == 0 ? store.FindFile(job.Id, Path.GetFileName(report.FilePath)) : null;
        if (file is null)
        {
            Fail(job, report.Error
                ?? (state.OOMKilled ? "Speicherlimit überschritten." : $"yt-dlp hat mit Code {state.ExitCode} ohne Datei beendet."));
            return;
        }

        Update(job, persist: true, job =>
        {
            job.State = JobState.Completed;
            job.Title = report.Title ?? job.Title;
            job.FileName = file.Name;
            job.FileSize = file.Length;
            job.ExpiresAt = DateTimeOffset.UtcNow + settings.Retention;
        });
        logger.LogInformation("Job {Id} completed: {File} ({Size} bytes)", job.Id, file.Name, file.Length);
    }

    private void Fail(Job job, string error)
    {
        // Partial downloads are useless; only the error message stays around.
        store.DeleteFiles(job.Id);
        Update(job, persist: true, job =>
        {
            job.State = JobState.Failed;
            job.Error = error;
            job.ExpiresAt = DateTimeOffset.UtcNow + settings.Retention;
        });
    }

    private void Update(Job job, bool persist, Action<Job> change)
    {
        lock (job)
        {
            change(job);
            if (persist)
            {
                store.Save(job);
            }
        }
    }

    private JobView Snapshot(Job job)
    {
        lock (job)
        {
            var expiresIn = job.ExpiresAt is { } expiresAt
                ? (long)Math.Max(0, (expiresAt - DateTimeOffset.UtcNow).TotalSeconds)
                : (long?)null;
            return new JobView(job.Id, job.Url, job.Mode, job.Format, job.Resolution, job.State, job.Title, job.FileName, job.FileSize,
                job.DownloadedBytes, job.TotalBytes, job.Error, job.CreatedAt, expiresIn);
        }
    }

    /// <summary>
    /// Pulls the job image before every job and probe, at most once per
    /// PullInterval, so each download runs the newest yt-dlp. If the registry
    /// is down or rate-limits, the local copy will do.
    /// </summary>
    private async Task UpdateImageAsync(CancellationToken cancellationToken)
    {
        await pullLock.WaitAsync(cancellationToken);
        try
        {
            var previous = await docker.GetImageIdAsync(settings.JobImage, cancellationToken);
            if (previous is not null && DateTimeOffset.UtcNow - lastPull < PullInterval)
            {
                return;
            }

            try
            {
                await docker.PullImageAsync(settings.JobImage, cancellationToken);
                lastPull = DateTimeOffset.UtcNow;
            }
            catch (Exception exception) when (previous is not null && exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Pulling {Image} failed, using the local copy", settings.JobImage);
                return;
            }

            var current = await docker.GetImageIdAsync(settings.JobImage, cancellationToken);
            if (previous is not null && previous != current)
            {
                logger.LogInformation("Pulled a new {Image}: {Id}", settings.JobImage, current);
                replacedImages.Add(previous);
            }

            // Should the tag ever move back to an image it replaced, that image
            // is current again and must stay.
            if (current is not null)
            {
                replacedImages.Remove(current);
            }
        }
        finally
        {
            pullLock.Release();
        }

        await RemoveReplacedImagesAsync();
    }

    private async Task RemoveReplacedImagesAsync()
    {
        // A pull is under way and tries again once it is done. Waiting for it
        // would hold up whoever is cancelling this job.
        if (!await pullLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            foreach (var id in replacedImages.ToList())
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    if (await docker.RemoveImageAsync(id, timeout.Token))
                    {
                        replacedImages.Remove(id);
                        logger.LogInformation("Removed replaced job image {Id}", id);
                    }
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Removing replaced job image {Id} failed", id);
                }
            }
        }
        finally
        {
            pullLock.Release();
        }
    }

    private async Task<string> ResolveVolumeAsync(CancellationToken cancellationToken)
    {
        if (volume is not null)
        {
            return volume;
        }

        if (!string.IsNullOrEmpty(settings.Volume))
        {
            return volume = settings.Volume;
        }

        // Compose prefixes the volume with the project name, and Dockhand sets
        // that to the stack name. Reading it off our own mount avoids repeating
        // either here. A container's hostname is its short ID.
        var self = await docker.InspectContainerAsync(Environment.MachineName, cancellationToken);
        var mount = self.Mounts.FirstOrDefault(mount => mount.Destination == settings.DataPath && mount.Type == "volume")
            ?? throw new InvalidOperationException($"{settings.DataPath} is not a Docker volume. Set Downloader__Volume.");
        return volume = mount.Name!;
    }

    private async Task RemoveOrphanedContainersAsync(CancellationToken cancellationToken)
    {
        foreach (var id in await docker.ListContainersAsync(JobLabel, cancellationToken))
        {
            logger.LogInformation("Removing orphaned job container {Id}", id);
            await docker.RemoveContainerAsync(id, cancellationToken);
        }
    }

    private async Task RemoveContainerQuietlyAsync(string id)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await docker.RemoveContainerAsync(id, timeout.Token);
        }
        catch (Exception exception)
        {
            // The next start sweeps it up by label.
            logger.LogWarning(exception, "Removing job container {Id} failed", id);
        }
    }

    private sealed class Entry(Job job)
    {
        public Job Job { get; } = job;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Execution { get; set; } = Task.CompletedTask;
    }
}
