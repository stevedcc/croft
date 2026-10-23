// WORKAROUND-FILE(W-031, EF Core): EF has no ON CONFLICT DO NOTHING; the page-view insert is raw SQL whose names come from the model
// WORKAROUND-FILE(W-093, EF Core): EF has no ON CONFLICT DO UPDATE; the view-count upsert is raw SQL whose names come from the model
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Croft.Api.Services;

/// <summary>
/// The raw analytics statements as composite-format strings ({0}, {1}, ... are the values), with the
/// table and column names taken from the EF model and quoted. Built once per model — the model is the
/// same instance for the whole run — instead of on every beacon; the page-view insert, the view-count
/// upsert and the retention purge all use it, so a renamed table or column reaches every statement.
/// </summary>
public sealed class AnalyticsTables
{
    private static readonly ConditionalWeakTable<IModel, AnalyticsTables> Cache = [];

    private AnalyticsTables(IModel model)
    {
        var pageView = Names(model, typeof(Models.PageView));
        var viewCount = Names(model, typeof(Models.PostViewCount));

        var columns = string.Join(", ",
        [
            pageView.Column(nameof(Models.PageView.Id)),
            pageView.Column(nameof(Models.PageView.PostId)),
            pageView.Column(nameof(Models.PageView.Timestamp)),
            pageView.Column(nameof(Models.PageView.Day)),
            pageView.Column(nameof(Models.PageView.IpHash)),
            pageView.Column(nameof(Models.PageView.UserAgent)),
            pageView.Column(nameof(Models.PageView.Referrer))
        ]);
        var conflictTarget = string.Join(", ",
        [
            pageView.Column(nameof(Models.PageView.PostId)),
            pageView.Column(nameof(Models.PageView.IpHash)),
            pageView.Column(nameof(Models.PageView.Day))
        ]);
        InsertPageView =
            $"INSERT INTO {pageView.Table} ({columns}) VALUES ({{0}}, {{1}}, {{2}}, {{3}}, {{4}}, {{5}}, {{6}}) ON CONFLICT ({conflictTarget}) DO NOTHING";

        var postColumn = viewCount.Column(nameof(Models.PostViewCount.PostId));
        var countColumn = viewCount.Column(nameof(Models.PostViewCount.ViewCount));
        IncrementViewCount =
            $"INSERT INTO {viewCount.Table} ({postColumn}, {countColumn}) VALUES ({{0}}, 1) ON CONFLICT ({postColumn}) DO UPDATE SET {countColumn} = {viewCount.Table}.{countColumn} + 1";

        var id = pageView.Column(nameof(Models.PageView.Id));
        var day = pageView.Column(nameof(Models.PageView.Day));
        PurgePageViews =
            $"DELETE FROM {pageView.Table} WHERE {id} IN (SELECT {id} FROM {pageView.Table} WHERE {day} < {{0}} LIMIT {{1}})";
    }

    /// <summary>Values {0}..{6}: id, post id, timestamp, day, IP hash, user agent, referrer. 1 row if inserted, 0 on a repeat view.</summary>
    public string InsertPageView { get; }

    /// <summary>Value {0}: the post id. Creates the count row or adds one view to it.</summary>
    public string IncrementViewCount { get; }

    /// <summary>Values {0}, {1}: the cutoff day (rows before it go) and the batch size.</summary>
    public string PurgePageViews { get; }

    public static AnalyticsTables For(IModel model) => Cache.GetValue(model, static m => new AnalyticsTables(m));

    private static (string Table, Func<string, string> Column) Names(IModel model, Type entityType)
    {
        var entity = model.FindEntityType(entityType)
            ?? throw new InvalidOperationException($"{entityType.Name} is missing from the EF model.");
        var tableName = entity.GetTableName()
            ?? throw new InvalidOperationException($"{entityType.Name} is not mapped to a table.");
        var storeObject = StoreObjectIdentifier.Table(tableName, entity.GetSchema());

        string Column(string propertyName) => Quote(entity.FindProperty(propertyName)?.GetColumnName(storeObject)
            ?? throw new InvalidOperationException($"{entityType.Name}.{propertyName} is not mapped to a column."));

        return (Quote(tableName), Column);
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
