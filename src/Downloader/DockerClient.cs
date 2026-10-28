using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Downloader;

public sealed class DockerException(string message) : Exception(message);

public sealed record ContainerState(bool Running, int ExitCode, bool OOMKilled);

public sealed record ContainerMount(string Type, string? Name, string Destination);

public sealed record ContainerDetails(string Id, ContainerState State, IReadOnlyList<ContainerMount> Mounts);

/// <summary>
/// The few Docker Engine API calls this app makes. Paths carry no API version,
/// so the daemon answers in its own.
/// </summary>
public sealed class DockerClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static HttpClient CreateHttpClient(string endpoint)
    {
        var uri = new Uri(endpoint);
        var timeout = TimeSpan.FromSeconds(30);

        if (uri.Scheme != "unix")
        {
            // tcp://host:port as Docker spells it is plain HTTP underneath.
            return new HttpClient { BaseAddress = new UriBuilder(uri) { Scheme = "http" }.Uri, Timeout = timeout };
        }

        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(uri.AbsolutePath), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        return new HttpClient(handler) { BaseAddress = new Uri("http://docker/"), Timeout = timeout };
    }

    /// <returns>The image ID, or null if the image is not on the host.</returns>
    public async Task<string?> GetImageIdAsync(string image, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"images/{image}/json", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        using var details = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return details.RootElement.GetProperty("Id").GetString();
    }

    /// <returns>False if the daemon refused because the image is still in use.</returns>
    public async Task<bool> RemoveImageAsync(string id, CancellationToken cancellationToken)
    {
        // No force: the daemon refuses while a container uses the image or
        // another tag points at it, and either way it is not ours to remove.
        using var response = await http.DeleteAsync($"images/{id}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }

        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await EnsureSuccessAsync(response, cancellationToken);
        }

        return true;
    }

    public async Task PullImageAsync(string image, CancellationToken cancellationToken)
    {
        // Without a tag the daemon would pull every tag of the repository.
        var hasTag = image.LastIndexOf(':') > image.LastIndexOf('/');
        if (!hasTag && !image.Contains('@'))
        {
            throw new DockerException($"Image reference {image} has no tag.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"images/create?fromImage={Uri.EscapeDataString(image)}");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        // The daemon answers 200 right away and reports failures inside the
        // progress stream, one JSON object per line.
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken));
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            using var message = JsonDocument.Parse(line);
            if (message.RootElement.TryGetProperty("error", out var error))
            {
                throw new DockerException($"Pulling {image} failed: {error.GetString()}");
            }
        }
    }

    public async Task<string> CreateContainerAsync(string name, object spec, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync($"containers/create?name={Uri.EscapeDataString(name)}", spec, Json, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var created = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return created.RootElement.GetProperty("Id").GetString()!;
    }

    public async Task StartContainerAsync(string id, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync($"containers/{id}/start", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<ContainerDetails> InspectContainerAsync(string id, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"containers/{id}/json", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<ContainerDetails>(Json, cancellationToken))!;
    }

    /// <param name="tail">Lines from the end, or null for all of them.</param>
    public async Task<string> GetLogsAsync(string id, int? tail, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"containers/{id}/logs?stdout=true&stderr=true&tail={tail?.ToString() ?? "all"}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return Demultiplex(await response.Content.ReadAsByteArrayAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<string>> ListContainersAsync(string label, CancellationToken cancellationToken)
    {
        var filters = Uri.EscapeDataString(JsonSerializer.Serialize(new { label = new[] { label } }));
        using var response = await http.GetAsync($"containers/json?all=true&filters={filters}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var containers = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return containers.RootElement.EnumerateArray().Select(container => container.GetProperty("Id").GetString()!).ToList();
    }

    public async Task RemoveContainerAsync(string id, CancellationToken cancellationToken)
    {
        // force also kills a container that is still running.
        using var response = await http.DeleteAsync($"containers/{id}?force=true", cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await EnsureSuccessAsync(response, cancellationToken);
        }
    }

    /// <summary>
    /// Without a TTY the daemon frames every write: one byte naming the stream,
    /// three bytes padding, a four-byte big-endian length, then the payload.
    /// stdout and stderr come back interleaved in the order they were written.
    /// </summary>
    public static string Demultiplex(ReadOnlySpan<byte> data)
    {
        using var payload = new MemoryStream();
        while (data.Length >= 8)
        {
            var length = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data[4..8]), (uint)(data.Length - 8));
            payload.Write(data.Slice(8, length));
            data = data[(8 + length)..];
        }

        return Encoding.UTF8.GetString(payload.GetBuffer(), 0, (int)payload.Length);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotModified)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var message = body;
        try
        {
            using var error = JsonDocument.Parse(body);
            if (error.RootElement.TryGetProperty("message", out var text))
            {
                message = text.GetString();
            }
        }
        catch (JsonException)
        {
            // Not every error (the socket proxy's, for one) is JSON.
        }

        var request = response.RequestMessage!;
        throw new DockerException($"{request.Method} {request.RequestUri?.AbsolutePath}: {(int)response.StatusCode} {message?.Trim()}");
    }
}
