using Croft.Api.Models;
using Croft.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Croft.Api.Data.Configurations;

public class PostConfiguration : IEntityTypeConfiguration<Post>
{
    public void Configure(EntityTypeBuilder<Post> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => x.Slug).IsUnique();

        // No partial indexes over PublishedAt here: the scheduled and published partial indexes
        // live in the hand-written PublishedAtPartialIndexes migration, so both definitions and
        // the enum values their filters encode sit in one reviewed file and no future
        // migrations add run can churn them.

        builder.Property(x => x.Title).HasColumnType("text").IsRequired();
        builder.Property(x => x.Slug).HasMaxLength(512).IsRequired();
        builder.Property(x => x.Excerpt).HasMaxLength(2048);
        builder.Property(x => x.MarkdownContent).IsRequired();

        builder.HasMany(x => x.Tags)
            .WithMany(x => x.Posts);

        builder.HasOne(x => x.Category)
            .WithMany(x => x.Posts)
            .HasForeignKey(x => x.CategoryId)
            .OnDelete(DeleteBehavior.SetNull);

        // The column keeps its name: pods of the release before brief 13c (0.6.3 and older) still update "ViewCount" while they roll
        // out (see "Pending contracts" in AGENTS.md); the totals live in PostViewCounts.
        builder.Property(x => x.LegacyViewCount).HasColumnName("ViewCount");

        builder.Property(x => x.Kind).HasDefaultValue(PostKind.Post);
        // The retired ShowInNavigation flag stays in the table until a later contract migration
        // drops it (see "Pending contracts" in AGENTS.md): releases older than 0.6.1 still read
        // and write it while the API pods roll. Kept as a shadow property so the model, the
        // snapshot and the migrations know the column; no code of this release sets it, so inserts
        // leave it at its database default (false).
        builder.Property<bool>("ShowInNavigation").HasDefaultValue(false);
        builder.Property(x => x.NavigationPlacement)
            .HasConversion<string>()
            .HasMaxLength(16)
            .HasDefaultValue(NavigationPlacement.None);
        builder.Property(x => x.NavigationOrder).HasDefaultValue(0);

        // Announcing (brief 12d). The database default is off: a pod of the previous release inserts posts without these columns, and they must not announce.
        builder.Property(x => x.AnnounceMastodon).HasDefaultValue(false);
        builder.Property(x => x.AnnounceBluesky).HasDefaultValue(false);

        builder.Property(x => x.Subtitle).HasMaxLength(300);
        builder.Property(x => x.Summary).HasMaxLength(320);
        // WORKAROUND(W-029, EF Core): EF omits a property equal to its CLR default from the INSERT when a database default exists, so the sentinel is 1
        // The sentinel is the database default 1 (and the property initialiser), not the int
        // default 0: with 0 as sentinel an explicit 0 would be omitted from the INSERT and come
        // back as 1, so an exported 0 would not survive an import.
        builder.Property(x => x.ReadingMinutes).HasDefaultValue(1).HasSentinel(1);
        // END WORKAROUND(W-029)

        builder.HasMany(x => x.MediaAssets)
            .WithOne(x => x.Post)
            .HasForeignKey(x => x.PostId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.Feedback)
            .WithOne(x => x.Post)
            .HasForeignKey(x => x.PostId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.PageViews)
            .WithOne(x => x.Post)
            .HasForeignKey(x => x.PostId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.BannerImage)
            .WithMany()
            .HasForeignKey(nameof(Post.BannerImageId))
            .OnDelete(DeleteBehavior.SetNull);

        builder.Property(x => x.SearchVector)
            .HasComputedColumnSql(
                """
                to_tsvector('english', coalesce("Title", '') || ' ' || coalesce("Excerpt", '') || ' ' || coalesce("MarkdownContent", ''))
                """,
                stored: true);

        builder.HasIndex(x => x.SearchVector)
            .HasMethod("GIN");
    }
}
