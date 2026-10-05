using System.Buffers.Binary;
using System.Net;
using System.Reflection;
using System.Text.Json;
using AstralParty.Chat;
using BepInEx.Logging;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("HTTP bytes at the exact limit remain intact", async () =>
    {
        using var content = new ByteArrayContent(Enumerable.Range(0, 32).Select(x => (byte)x).ToArray());
        var bytes = await RemotePayload.ReadBytesAsync(content, 32, CancellationToken.None);
        Check.True(bytes.SequenceEqual(Enumerable.Range(0, 32).Select(x => (byte)x)), "Boundary response changed.");
    }),
    ("oversized declared length is rejected before reading", async () =>
    {
        using var stream = new CountingStream(new byte[100]);
        using var content = new StreamContent(stream);
        content.Headers.ContentLength = 100;
        await Reject<InvalidDataException>(() => RemotePayload.ReadBytesAsync(content, 32, CancellationToken.None));
        Check.True(stream.BytesRead == 0, "Oversized declared body was read.");
    }),
    ("chunked oversize stops after one byte beyond the limit", async () =>
    {
        using var stream = new CountingStream(new byte[100_000]);
        using var content = new StreamContent(stream);
        await Reject<InvalidDataException>(() => RemotePayload.ReadBytesAsync(content, 32, CancellationToken.None));
        Check.True(stream.BytesRead == 33 && stream.Disposed, "Unbounded read or stream leak.");
    }),
    ("chunked body below the limit is preserved", async () =>
    {
        using var stream = new CountingStream(new byte[] { 1, 2, 3 });
        using var content = new StreamContent(stream);
        Check.True((await RemotePayload.ReadBytesAsync(content, 32, CancellationToken.None)).SequenceEqual(new byte[] { 1, 2, 3 }), "Chunked read changed.");
    }),
    ("stalled body honors cancellation and releases its stream", async () =>
    {
        using var stream = new CountingStream(Array.Empty<byte>(), stall: true);
        using var content = new StreamContent(stream);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Reject<OperationCanceledException>(() => RemotePayload.ReadBytesAsync(content, 32, cancellation.Token));
        Check.True(stream.Disposed, "Cancelled response stream retained.");
    }),
    ("invalid byte limits are rejected", async () =>
    {
        using var content = new ByteArrayContent(Array.Empty<byte>());
        await Reject<ArgumentOutOfRangeException>(() => RemotePayload.ReadBytesAsync(content, 0, CancellationToken.None));
    }),
    ("PNG header dimensions are inspected before native decoding", () =>
    {
        Check.True(RemotePayload.IsSafePortraitPng(PngHeader(160, 160), out var width, out var height)
            && width == 160 && height == 160, "Normal PNG metadata rejected.");
        return Task.CompletedTask;
    }),
    ("PNG dimensions reject zero, excessive and unsigned overflow values", () =>
    {
        foreach (var (width, height) in new[] { (0u, 160u), (160u, 0u), (1025u, 160u), (160u, 1025u), (uint.MaxValue, uint.MaxValue) })
            Check.True(!RemotePayload.IsSafePortraitPng(PngHeader(width, height), out _, out _), "Unsafe PNG size accepted.");
        return Task.CompletedTask;
    }),
    ("non-PNG, truncated and wrong IHDR data is rejected", () =>
    {
        var badSignature = PngHeader(160, 160); badSignature[0] = 0;
        var badHeader = PngHeader(160, 160); badHeader[12] = 0;
        var badLength = PngHeader(160, 160); badLength[11] = 12;
        foreach (var bytes in new[] { badSignature, badHeader, badLength, new byte[24] })
            Check.True(!RemotePayload.IsSafePortraitPng(bytes, out _, out _), "Invalid PNG header accepted.");
        return Task.CompletedTask;
    }),
    ("PNG byte cap applies before header processing", () =>
    {
        var bytes = new byte[RemotePayload.MaxPortraitBytes + 1];
        PngHeader(160, 160).CopyTo(bytes, 0);
        Check.True(!RemotePayload.IsSafePortraitPng(bytes, out _, out _), "Large portrait accepted.");
        return Task.CompletedTask;
    }),
    ("oversized successful room response retries with a bounded stream read", async () =>
    {
        using var f = new Fixture();
        var stream = new CountingStream(new byte[RemotePayload.MaxRoomBytes + 100_000]);
        f.Handler.Respond = (count, _) => Task.FromResult(count == 1
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }
            : FakeHttp.Response(HttpStatusCode.OK));
        f.Start(); await f.Joined();
        Check.True(f.Handler.RoomRequests == 2 && stream.BytesRead == RemotePayload.MaxRoomBytes + 1 && stream.Disposed,
            "Room response was buffered before validation or did not recover.");
    }),
    ("oversized character response falls back and still joins the room", async () =>
    {
        var stream = new CountingStream(new byte[RemotePayload.MaxCharactersBytes + 100_000]);
        using var handler = new CharacterHandler(() => new StreamContent(stream));
        var socket = new FakeSocket();
        using var client = CreateClient(handler, socket);
        client.UpdateGameState(Fixture.State("123456"));
        await Check.Eventually(() => client.GetUiSnapshot().Status == "연결됨");
        Check.True(stream.BytesRead == RemotePayload.MaxCharactersBytes + 1 && stream.Disposed, "Character response was buffered without its cap.");
    }),
    ("oversized 429 error keeps its rate-limit cooldown", async () =>
    {
        using var f = new Fixture();
        var stream = new CountingStream(new byte[RemotePayload.MaxRoomBytes + 100]);
        f.Handler.Respond = (count, _) => Task.FromResult(count == 1
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StreamContent(stream) }
            : FakeHttp.Response(HttpStatusCode.OK));
        f.Start(); await Task.Delay(70);
        Check.True(f.Handler.RoomRequests == 1, "Oversized error bypassed rate-limit cooldown.");
        await f.Joined();
        Check.True(f.Handler.RoomRequests == 2, "Rate-limit error did not recover.");
    }),
    ("oversized 400 error stays permanent instead of causing retries", async () =>
    {
        using var f = new Fixture();
        f.Handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            { Content = new StreamContent(new CountingStream(new byte[RemotePayload.MaxRoomBytes + 100])) });
        f.Start(); await Task.Delay(150);
        Check.True(f.Handler.RoomRequests == 1 && f.Sockets.Count == 0, "Permanent HTTP error retried after size rejection.");
    }),
    ("character cache accepts only known ids and caps Unicode names", async () =>
    {
        var longName = string.Concat(Enumerable.Repeat("😀", 100));
        var json = JsonSerializer.Serialize(new[] { new { id = "101", name = longName }, new { id = "999", name = "unknown" } });
        using var handler = new CharacterHandler(() => new StringContent(json));
        var socket = new FakeSocket();
        using var client = CreateClient(handler, socket);
        client.UpdateGameState(Fixture.State("123456"));
        await Check.Eventually(() => client.GetUiSnapshot().Status == "연결됨");
        client.SendChat("test");
        await Check.Eventually(() => client.GetUiSnapshot().Messages.Count == 1);
        Check.True(PartyProtocol.TextLength(client.GetUiSnapshot().Messages[0].CharacterName) == 64, "Character name was unbounded.");
        var cache = (Dictionary<string, string>)typeof(PartyChatClient).GetField("_characterNames", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        Check.True(cache.Count == 1 && !cache.ContainsKey("999"), "Unknown character entered cache.");
    }),
    ("excessive character array entries are discarded", async () =>
    {
        var json = JsonSerializer.Serialize(Enumerable.Repeat(new { id = "101", name = "name" }, 129));
        using var handler = new CharacterHandler(() => new StringContent(json));
        using var client = CreateClient(handler, new FakeSocket());
        client.UpdateGameState(Fixture.State("123456"));
        await Check.Eventually(() => client.GetUiSnapshot().Status == "연결됨");
        var cache = (Dictionary<string, string>)typeof(PartyChatClient).GetField("_characterNames", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        Check.True(cache.Count == 0, "Excessive character array was retained.");
    }),
    ("JSON HTTP error display is limited to 200 code points", async () =>
    {
        using var f = new Fixture();
        f.Handler.Respond = (_, _) => Task.FromResult(FakeHttp.Response(HttpStatusCode.BadRequest,
            JsonSerializer.Serialize(new { message = new string('x', 3000) })));
        f.Start();
        await Check.Eventually(() => f.Client.GetUiSnapshot().Status.StartsWith("채팅방 입장 실패:"));
        Check.True(PartyProtocol.TextLength(f.Client.GetUiSnapshot().Status) < 230, "HTTP error flooded the status label.");
    })
};
var failures = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine("PASS payload: " + name); }
    catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL payload: " + name + ": " + ex); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} remote payload checks passed.");
return failures == 0 ? 0 : 1;

static async Task Reject<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
static byte[] PngHeader(uint width, uint height)
{
    // Header metadata only; Unity still validates actual compressed PNG data.
    var bytes = new byte[33];
    new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 13);
    new byte[] { 73, 72, 68, 82 }.CopyTo(bytes, 12);
    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), width);
    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), height);
    return bytes;
}
static PartyChatClient CreateClient(HttpMessageHandler handler, FakeSocket socket) => new(new ManualLogSource(), new HttpClient(handler),
    new PartyChatOptions { CreateSocket = () => socket, ConnectSocket = (_, _, _) => Task.CompletedTask });

sealed class CharacterHandler : HttpMessageHandler
{
    private readonly Func<HttpContent> _createContent;
    public CharacterHandler(Func<HttpContent> createContent) { _createContent = createContent; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
        Task.FromResult(request.Method == HttpMethod.Get
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = _createContent() }
            : FakeHttp.Response(HttpStatusCode.OK));
}
sealed class CountingStream : Stream
{
    private readonly MemoryStream _inner;
    private readonly bool _stall;
    public CountingStream(byte[] bytes, bool stall = false) { _inner = new MemoryStream(bytes); _stall = stall; }
    public int BytesRead { get; private set; }
    public bool Disposed { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    { var read = _inner.Read(buffer, offset, count); BytesRead += read; return read; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_stall) await Task.Delay(Timeout.Infinite, cancellationToken);
        var read = await _inner.ReadAsync(buffer, cancellationToken);
        BytesRead += read;
        return read;
    }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { Disposed = true; if (disposing) _inner.Dispose(); base.Dispose(disposing); }
}
