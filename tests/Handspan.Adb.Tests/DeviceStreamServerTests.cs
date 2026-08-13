using System.Net;
using System.Net.Http.Headers;
using Handspan.Core.Models;
using Handspan.Media;
using Microsoft.Extensions.Logging.Abstractions;

namespace Handspan.Adb.Tests;

/// <summary>
/// The loopback streaming server (spec §58).
/// </summary>
/// <remarks>
/// Exercised over real HTTP against the fake device, because the behaviour that matters — range requests
/// turning into bounded device reads — only exists when both halves are real. Media players are unforgiving
/// about range semantics, so the edge cases are tested rather than assumed.
/// </remarks>
public sealed class DeviceStreamServerTests : IAsyncLifetime
{
    private FakeServerFixture _fixture = null!;
    private DeviceStreamServer _server = null!;
    private HttpClient _client = null!;
    private byte[] _content = [];
    private Uri _url = null!;

    private const string RemotePath = "/storage/emulated/0/Movies/clip.mp4";

    public Task InitializeAsync()
    {
        _fixture = FakeServerFixture.Start();

        // Deterministic content so a range response can be checked byte for byte.
        _content = new byte[600_000];
        for (var i = 0; i < _content.Length; i++)
        {
            _content[i] = (byte)(i % 251);
        }

        _fixture.Server.Files.AddFile(RemotePath, _content);

        _server = new DeviceStreamServer(NullLogger<DeviceStreamServer>.Instance);
        _server.Start();

        _url = _server.Register(
            _fixture.CreateFileSystem(),
            KnownPaths.Movies.Combine("clip.mp4"),
            _content.Length);

        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();
        await _fixture.DisposeAsync();
    }

    [Fact]
    public async Task Serves_the_whole_file_when_no_range_is_requested()
    {
        var response = await _client.GetAsync(_url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("video/mp4", response.Content.Headers.ContentType?.MediaType);

        // Advertising range support is what makes a player attempt to seek at all.
        Assert.Contains("bytes", response.Headers.AcceptRanges);

        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(_content.Length, body.Length);
        Assert.Equal(_content, body);
    }

    [Fact]
    public async Task Serves_a_byte_range_exactly()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _url);
        request.Headers.Range = new RangeHeaderValue(100_000, 149_999);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 100000-149999/600000",
            response.Content.Headers.ContentRange?.ToString());

        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(50_000, body.Length);
        Assert.Equal(_content.AsSpan(100_000, 50_000).ToArray(), body);
    }

    [Fact]
    public async Task Serves_an_open_ended_range_to_the_end_of_the_file()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _url);
        request.Headers.Range = new RangeHeaderValue(599_000, null);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);

        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(1000, body.Length);
        Assert.Equal(_content.AsSpan(599_000).ToArray(), body);
    }

    [Fact]
    public async Task Serves_a_suffix_range()
    {
        // "bytes=-N" asks for the last N bytes. Players use it to find a trailing moov atom, so an MP4
        // written with its index at the end is unplayable without this.
        using var request = new HttpRequestMessage(HttpMethod.Get, _url);
        request.Headers.Range = new RangeHeaderValue(null, 2048);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);

        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(2048, body.Length);
        Assert.Equal(_content.AsSpan(_content.Length - 2048).ToArray(), body);
    }

    [Fact]
    public async Task A_range_past_the_end_is_refused_rather_than_hanging()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _url);
        request.Headers.Range = new RangeHeaderValue(900_000, 950_000);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
    }

    [Fact]
    public async Task Head_reports_the_size_without_transferring_the_file()
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, _url);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(_content.Length, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_wrong_token_is_rejected()
    {
        // Anything on this machine can reach the port, so the token is the only thing keeping another
        // process from reading the phone.
        var forged = new Uri(_url.ToString().Replace(_server.Token, new string('0', 32),
            StringComparison.Ordinal));

        var response = await _client.GetAsync(forged);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_unregistered_id_is_rejected()
    {
        // Only explicitly registered files can be served: the server is not a general device reader.
        var guessed = new Uri($"http://127.0.0.1:{_server.Port}/{_server.Token}/deadbeefdeadbeef");

        var response = await _client.GetAsync(guessed);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unregistering_stops_serving_the_file()
    {
        _server.Unregister(_url);

        var response = await _client.GetAsync(_url);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void The_server_binds_only_to_loopback()
    {
        // Binding to a routable address would expose the user's phone to their network.
        Assert.Equal("127.0.0.1", _url.Host);
    }

    /// <summary>
    /// Starting must survive the port being claimed between the free-port probe and the bind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The regression test for an intermittent failure that appeared to belong to whichever test in this
    /// class ran first. The cause was in <c>Start</c>: probing for a free port releases it before
    /// HttpListener can take it, so a colliding claim threw HttpListenerException out of
    /// <see cref="InitializeAsync"/> rather than out of any test body.
    /// </para>
    /// <para>
    /// The collision is forced through the port provider. Simply starting many servers at once does not
    /// reproduce it — each probe holds its port while bound, so concurrent probes get different numbers,
    /// and a test written that way passes against the broken code too.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Starting_survives_the_probed_port_being_taken_first()
    {
        // Occupy a port for real, so the first bind attempt genuinely fails.
        var occupied = new HttpListener();
        var occupiedPort = FreeLoopbackPort();
        occupied.Prefixes.Add($"http://127.0.0.1:{occupiedPort}/");
        occupied.Start();

        try
        {
            var server = new DeviceStreamServer(NullLogger<DeviceStreamServer>.Instance);

            // Hand out the taken port twice, then let it find a real one.
            var handouts = 0;
            server.PortProvider = () =>
            {
                handouts++;
                return handouts <= 2 ? occupiedPort : FreeLoopbackPort();
            };

            server.Start();

            await using (server)
            {
                Assert.True(server.IsRunning);
                Assert.NotEqual(occupiedPort, server.Port);
                Assert.Equal(3, handouts);

                // And it is actually serving on the port it reports, not merely holding a number.
                var url = server.Register(
                    _fixture.CreateFileSystem(), KnownPaths.Movies.Combine("clip.mp4"), _content.Length);

                Assert.Equal(server.Port, url.Port);

                using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var response = await probe.GetAsync(url);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }
        finally
        {
            occupied.Close();
        }
    }

    private static int FreeLoopbackPort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Theory]
    [InlineData(null, 0, 1000, false)]
    [InlineData("", 0, 1000, false)]
    [InlineData("bytes=0-99", 0, 100, true)]
    [InlineData("bytes=500-", 500, 500, true)]
    [InlineData("bytes=-200", 800, 200, true)]
    [InlineData("bytes=0-99999", 0, 1000, true)]          // clamped to the file's length
    [InlineData("bytes=abc", 0, 1000, false)]             // malformed: fall back to the whole file
    [InlineData("items=0-99", 0, 1000, false)]            // wrong unit
    [InlineData("bytes=0-99,200-299", 0, 100, true)]      // multi-range: honour the first
    public void Parses_range_headers(string? header, long offset, long length, bool isPartial)
    {
        var parsed = DeviceStreamServer.ParseRange(header, 1000);

        Assert.Equal(offset, parsed.Offset);
        Assert.Equal(length, parsed.Length);
        Assert.Equal(isPartial, parsed.IsPartial);
    }

    [Fact]
    public async Task Seeking_around_the_file_returns_consistent_data()
    {
        // What a player actually does: jump about rather than read linearly.
        foreach (var (offset, length) in new (long, int)[]
                 {
                     (0, 4096), (550_000, 4096), (250_000, 8192), (1, 3), (599_999, 1),
                 })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _url);
            request.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);

            var response = await _client.SendAsync(request);
            var body = await response.Content.ReadAsByteArrayAsync();

            Assert.Equal(length, body.Length);
            Assert.Equal(_content.AsSpan((int)offset, length).ToArray(), body);
        }
    }
}
