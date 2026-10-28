using System.Text;
using Downloader;

namespace Downloader.Tests;

public class DockerClientTests
{
    [Fact]
    public void Demultiplex_joins_stdout_and_stderr_frames_in_order()
    {
        var data = Frame(1, "downloader:title Ä\n").Concat(Frame(2, "ERROR: nope\n")).ToArray();

        Assert.Equal("downloader:title Ä\nERROR: nope\n", DockerClient.Demultiplex(data));
    }

    [Fact]
    public void Demultiplex_tolerates_a_truncated_last_frame()
    {
        var frame = Frame(1, "complete\n");
        var data = frame.Concat(Frame(1, "cut off")[..10]).ToArray();

        Assert.Equal("complete\ncu", DockerClient.Demultiplex(data));
    }

    private static byte[] Frame(byte stream, string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var header = new byte[] { stream, 0, 0, 0, 0, 0, 0, (byte)payload.Length };
        return [.. header, .. payload];
    }
}
