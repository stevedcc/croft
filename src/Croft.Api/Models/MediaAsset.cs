using Croft.Shared.Enums;

namespace Croft.Api.Models;

public class MediaAsset
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string OriginalName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string? AltText { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; }
    public string UploadedBy { get; set; } = string.Empty;
    public MediaAssetScope Scope { get; set; }

    public Guid? PostId { get; set; }
    public Post? Post { get; set; }
}
