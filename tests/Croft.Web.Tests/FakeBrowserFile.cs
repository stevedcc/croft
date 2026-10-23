using Microsoft.AspNetCore.Components.Forms;

namespace Croft.Web.Tests;

/// <summary>Minimal <see cref="IBrowserFile"/> for tests that drive file-picker callbacks.</summary>
internal sealed class FakeBrowserFile(string name, string contentType = "image/png") : IBrowserFile
{
    public string Name => name;
    public long Size => 1;
    public string ContentType => contentType;
    public DateTimeOffset LastModified => new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
    public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken ct = default) => Stream.Null;
}
