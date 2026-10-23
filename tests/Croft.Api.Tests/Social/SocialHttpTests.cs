using System.Net;
using System.Net.Http.Headers;
using AwesomeAssertions;
using Croft.Api.Social;
using Croft.Api.Tests.Fixtures.Social;

namespace Croft.Api.Tests.Social;

/// <summary>Brief 12a: every outbound social response is read with a cap, never with an unbounded ReadAsStringAsync.</summary>
public class SocialHttpTests
{
    private static HttpClient ClientFor(HttpMessageHandler handler) => new(handler);

    [Fact]
    public async Task ABodyAtTheCap_IsRead()
    {
        var body = new byte[1000];
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });

        var response = await SocialHttp.GetAsync(ClientFor(handler), "https://x.example/a", 1000, CancellationToken.None);

        response.Body.Should().HaveCount(1000);
        response.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ABodyOneByteOverTheCap_IsRefused_EvenWithoutAContentLength()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new UnknownLengthStream(1001)),
        });

        var act = () => SocialHttp.GetAsync(ClientFor(handler), "https://x.example/a", 1000, CancellationToken.None);

        await act.Should().ThrowAsync<SocialResponseTooLargeException>();
    }

    [Fact]
    public async Task ADeclaredLengthOverTheCap_IsRefusedBeforeAnyByteIsRead()
    {
        var content = new StreamContent(new ThrowingReadStream());
        content.Headers.ContentLength = 5000;
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        var act = () => SocialHttp.GetAsync(ClientFor(handler), "https://x.example/a", 1000, CancellationToken.None);

        await act.Should().ThrowAsync<SocialResponseTooLargeException>();
    }

    [Fact]
    public async Task AFailureStatus_ReturnsItsHeaders_AndNoBody()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("slow down") };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return response;
        });

        var response = await SocialHttp.GetAsync(ClientFor(handler), "https://x.example/a", 1000, CancellationToken.None);

        response.Status.Should().Be(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(120));
        response.Body.Should().BeEmpty();
    }

    [Theory]
    [InlineData("https://blog.example", true)]
    [InlineData(null, false)]
    [InlineData("  ", false)]
    public void TheUserAgent_NamesTheSoftwareAndTheSite(string? baseUrl, bool namesTheSite) =>
        SocialHttp.UserAgent(baseUrl).Should().Be(namesTheSite
            ? $"Croft/{ExpectedVersion()} (+https://blog.example)"
            : $"Croft/{ExpectedVersion()}");

    private static string ExpectedVersion()
    {
        var version = typeof(SocialHttp).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion;
        return version[..version.IndexOf('+', StringComparison.Ordinal)];
    }

    private sealed class UnknownLengthStream(int length) : Stream
    {
        private int _remaining = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(count, _remaining);
            _remaining -= n;
            return n;
        }
    }

    private sealed class ThrowingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("the body must not be read");
    }
}
