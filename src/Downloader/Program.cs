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

var jobs = app.MapGroup("/api/jobs");

jobs.MapGet("", (HttpContext context, JobManager manager) => manager.List(Client.Identify(context)));

jobs.MapPost("", (CreateJobRequest request, HttpContext context, JobManager manager) =>
{
    var input = request.Url?.Trim();
    if (input is not { Length: <= 4096 }
        || !Uri.TryCreate(input, UriKind.Absolute, out var url)
        || url.Scheme is not ("http" or "https"))
    {
        return Results.Problem("Bitte eine http- oder https-URL angeben.", statusCode: StatusCodes.Status400BadRequest);
    }

    if (!Enum.IsDefined(request.Mode))
    {
        return Results.Problem("Unbekannter Modus.", statusCode: StatusCodes.Status400BadRequest);
    }

    return manager.Enqueue(url.AbsoluteUri, request.Mode, Client.Identify(context)) is { } job
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

internal sealed record CreateJobRequest(string? Url, JobMode Mode);
