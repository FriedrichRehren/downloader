using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Downloader;

/// <summary>
/// Keeps jobs on the downloads volume so a redeploy does not lose finished
/// files: metadata under jobs/&lt;id&gt;.json, the download itself under
/// files/&lt;id&gt;/. Only the files directory is ever mounted into a job.
/// </summary>
public sealed class JobStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string jobsPath;
    private readonly string filesPath;
    private readonly ILogger<JobStore> logger;

    public JobStore(IOptions<DownloaderOptions> options, ILogger<JobStore> logger)
    {
        jobsPath = Path.Combine(options.Value.DataPath, "jobs");
        filesPath = Path.Combine(options.Value.DataPath, "files");
        this.logger = logger;
        Directory.CreateDirectory(jobsPath);
        Directory.CreateDirectory(filesPath);
    }

    // Relative to the volume root, as a mount subpath expects it.
    public static string FilesSubpath(string id) => $"files/{id}";

    public List<Job> Load()
    {
        var jobs = new List<Job>();
        foreach (var path in Directory.EnumerateFiles(jobsPath, "*.json"))
        {
            try
            {
                jobs.Add(JsonSerializer.Deserialize<Job>(File.ReadAllText(path), Json)!);
            }
            catch (Exception exception) when (exception is JsonException or IOException)
            {
                logger.LogWarning(exception, "Discarding unreadable job file {Path}", path);
                File.Delete(path);
            }
        }

        // A files directory without metadata belongs to nobody.
        var known = jobs.Select(job => job.Id).ToHashSet();
        foreach (var directory in Directory.EnumerateDirectories(filesPath))
        {
            if (!known.Contains(Path.GetFileName(directory)))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        return jobs;
    }

    public void Save(Job job)
    {
        var path = MetadataPath(job.Id);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(job, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public void CreateFilesDirectory(string id) => Directory.CreateDirectory(FilesPath(id));

    /// <summary>
    /// The file yt-dlp reported, or failing that the largest one it left behind.
    /// </summary>
    public FileInfo? FindFile(string id, string? reportedName)
    {
        var directory = new DirectoryInfo(FilesPath(id));
        if (!directory.Exists)
        {
            return null;
        }

        var files = directory.GetFiles().Where(file => file.Extension is not (".part" or ".ytdl")).ToList();
        return files.FirstOrDefault(file => file.Name == reportedName)
            ?? files.OrderByDescending(file => file.Length).FirstOrDefault();
    }

    public FileInfo? GetFile(string id, string fileName)
    {
        var file = new FileInfo(Path.Combine(FilesPath(id), fileName));
        return file.Exists ? file : null;
    }

    public void DeleteFiles(string id)
    {
        if (Directory.Exists(FilesPath(id)))
        {
            Directory.Delete(FilesPath(id), recursive: true);
        }
    }

    public void Delete(string id)
    {
        DeleteFiles(id);
        File.Delete(MetadataPath(id));
    }

    private string MetadataPath(string id) => Path.Combine(jobsPath, id + ".json");

    private string FilesPath(string id) => Path.Combine(filesPath, id);
}
