namespace Ledgerline.BuildingBlocks.Web.Pagination;

public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);
