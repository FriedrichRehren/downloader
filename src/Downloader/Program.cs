using System.Text.Json;
using System.Text.Json.Serialization;
using Downloader;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<DownloaderOptions>(builder.Configuration.GetSection(DownloaderOptions.Section));
builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddSingleton(services =>
    new DockerClient(DockerClient.CreateHttpClient(services.GetRequiredService<IOptions<DownloaderOptions>>().Value.DockerEndpoint)));
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<JobManager>();
builder.Services.AddHostedService(services => services.GetRequiredService<JobManager>());

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

const string InvalidUrl = "Bitte eine http- oder https-URL angeben.";

app.MapPost("/api/probe", async (ProbeRequest request, JobManager manager, CancellationToken cancellationToken) =>
{
    if (ParseUrl(request.Url) is not { } url)
    {
        return Results.Problem(InvalidUrl, statusCode: StatusCodes.Status400BadRequest);
    }

    try
    {
        return await manager.ProbeAsync(url.AbsoluteUri, cancellationToken) is { } probe
            ? Results.Ok(probe)
            : Results.Problem("Gerade werden zu viele Links eingelesen. Bitte gleich noch einmal.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (ProbeException exception)
    {
        return Results.Problem(exception.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
    }
    catch (DockerException exception)
    {
        return Results.Problem(exception.Message, statusCode: StatusCodes.Status502BadGateway);
    }
});

var jobs = app.MapGroup("/api/jobs");

jobs.MapGet("", (HttpContext context, JobManager manager) => manager.List(Client.Identify(context)));

jobs.MapPost("", (CreateJobRequest request, HttpContext context, JobManager manager) =>
{
    if (ParseUrl(request.Url) is not { } url)
    {
        return Results.Problem(InvalidUrl, statusCode: StatusCodes.Status400BadRequest);
    }

    if (!Enum.IsDefined(request.Mode))
    {
        return Results.Problem("Unbekannter Modus.", statusCode: StatusCodes.Status400BadRequest);
    }

    if (!YtDlp.Supports(request.Mode, request.Format))
    {
        return Results.Problem("Dieses Format gibt es für diesen Modus nicht.", statusCode: StatusCodes.Status400BadRequest);
    }

    if (request.Resolution is not null && (request.Mode != JobMode.Video || request.Resolution is not (> 0 and <= 10000)))
    {
        return Results.Problem("Ungültige Auflösung.", statusCode: StatusCodes.Status400BadRequest);
    }

    return manager.Enqueue(url.AbsoluteUri, request.Mode, request.Format, request.Resolution, Client.Identify(context)) is { } job
        ? Results.Created($"/api/jobs/{job.Id}", job)
        : Results.Problem("Es laufen schon zu viele Downloads.", statusCode: StatusCodes.Status429TooManyRequests);
});

jobs.MapDelete("{id}", async (string id, HttpContext context, JobManager manager) =>
    await manager.DeleteAsync(id, Client.Identify(context)) ? Results.NoContent() : Results.NotFound());

jobs.MapGet("{id}/file", (string id, HttpContext context, JobManager manager) =>
    manager.GetFile(id, Client.Identify(context)) is { } file
        ? Results.File(file.FullName, "application/octet-stream", file.Name, enableRangeProcessing: true)
        : Results.NotFound());

app.Run();

// Only absolute http and https URLs ever reach yt-dlp.
static Uri? ParseUrl(string? input) =>
    input?.Trim() is { Length: <= 4096 } trimmed
    && Uri.TryCreate(trimmed, UriKind.Absolute, out var url)
    && url.Scheme is "http" or "https"
        ? url
        : null;

internal sealed record ProbeRequest(string? Url);

internal sealed record CreateJobRequest(string? Url, JobMode Mode, JobFormat Format = JobFormat.Original, int? Resolution = null);
