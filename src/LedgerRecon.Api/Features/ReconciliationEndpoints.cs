using LedgerRecon.Api.Infrastructure;
using LedgerRecon.Api.Reconciliation;
using LedgerRecon.Core.Ledger;
using LedgerRecon.Core.Reconciliation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace LedgerRecon.Api.Features;

internal sealed record ReconItemResponse(
    string Kind,
    int? Line,
    string? FileReference,
    DateOnly? FileDate,
    decimal? FileAmount,
    long? PostingId,
    Guid? EntryId,
    string? LedgerReference,
    DateOnly? LedgerValueDate,
    decimal? LedgerAmount,
    decimal Difference,
    string? Currency,
    string Detail)
{
    public static ReconItemResponse From(ReconItem i) => new(
        i.Kind.ToString(), i.LineNumber, i.Record?.Reference, i.Record?.Date, i.Record is null ? null : Money.ToMajor(i.Record.AmountMinor),
        i.Ledger?.PostingId, i.Ledger?.EntryId, i.Ledger?.Reference, i.Ledger?.ValueDate, i.Ledger is null ? null : Money.ToMajor(i.Ledger.AmountMinor),
        Money.ToMajor(i.DifferenceMinor), i.Record?.Currency ?? i.Ledger?.Currency, i.Detail);
}

internal static class ReconciliationEndpoints
{
    public static RouteGroupBuilder MapReconciliationEndpoints(this RouteGroupBuilder group)
    {
        var runs = group.MapGroup("/reconciliation/runs").WithTags("Reconciliation");

        runs.MapPost("/", RunAsync)
            .WithSummary("Reconcile an external file against the ledger")
            .WithDescription(
                "The request body is the raw file (card settlement CSV, EFT/FAST CSV, or MT940 statement). Query: source=card|eft|correspondent, " +
                "businessDate=yyyy-MM-dd, optional account (defaults per source) and fileName. Uploading the same file again returns the existing run.");

        runs.MapGet("/", ListAsync).WithSummary("Most recent runs");
        runs.MapGet("/{id:guid}", GetAsync).WithName("GetReconRun").WithSummary("Run summary with counts per kind");
        runs.MapGet("/{id:guid}/items", ItemsAsync)
            .WithSummary("Difference report")
            .WithDescription("differencesOnly=true leaves out matched and pending items; format=csv returns text/csv.");
        return group;
    }

    private static async Task<Results<Created<ReconRunView>, Ok<ReconRunView>, ProblemHttpResult>> RunAsync(
        HttpRequest request,
        string? source,
        DateOnly? businessDate,
        string? account,
        string? fileName,
        ReconciliationService service,
        IOptions<ReconciliationOptions> options,
        CancellationToken cancellationToken)
    {
        if (!ReconFiles.TryParseSource(source, out var reconSource))
        {
            return Problems.Validation("recon.source_invalid", "source must be card, eft or correspondent.", "source");
        }

        if (businessDate is null)
        {
            return Problems.Validation("recon.business_date_missing", "businessDate (yyyy-MM-dd) is required.", "businessDate");
        }

        var accountCode = account is null ? null : JournalEntryDraft.NormaliseAccountCode(account);
        if (account is not null && accountCode is null)
        {
            return Problems.Validation("recon.account_invalid", "account is not a valid account code.", "account");
        }

        var content = await ReadBodyAsync(request, options.Value.MaxFileBytes, cancellationToken);
        if (content is null)
        {
            return Problems.Validation("file.size_invalid", $"The file must be at most {options.Value.MaxFileBytes} bytes.");
        }

        var safeName = fileName is { Length: > 0 and <= 200 } ? Path.GetFileName(fileName) : null;
        var result = await service.RunAsync(reconSource, businessDate.Value, accountCode, safeName, content, cancellationToken);
        if (!result.IsSuccess)
        {
            return Problems.From(result.Error);
        }

        return result.Value.Replayed
            ? TypedResults.Ok(result.Value.Run)
            : TypedResults.Created($"/api/v1/reconciliation/runs/{result.Value.Run.Id}", result.Value.Run);
    }

    private static async Task<Ok<IReadOnlyList<ReconRunView>>> ListAsync(ReconciliationService service, int? limit, CancellationToken cancellationToken) =>
        TypedResults.Ok(await service.ListRunsAsync(Math.Clamp(limit ?? 20, 1, 200), cancellationToken));

    private static async Task<Results<Ok<ReconRunView>, NotFound>> GetAsync(Guid id, ReconciliationService service, CancellationToken cancellationToken)
    {
        var run = await service.GetRunAsync(id, cancellationToken);
        return run is null ? TypedResults.NotFound() : TypedResults.Ok(run);
    }

    private static async Task<Results<Ok<ReconItemResponse[]>, ContentHttpResult, NotFound>> ItemsAsync(
        Guid id, bool? differencesOnly, string? format, ReconciliationService service, CancellationToken cancellationToken)
    {
        if (await service.GetRunAsync(id, cancellationToken) is null)
        {
            return TypedResults.NotFound();
        }

        var items = await service.GetItemsAsync(id, differencesOnly ?? false, cancellationToken);
        return string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase)
            ? TypedResults.Text(ReconFiles.WriteReport(items), "text/csv; charset=utf-8")
            : TypedResults.Ok(items.Select(ReconItemResponse.From).ToArray());
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, int maxBytes, CancellationToken cancellationToken)
    {
        if (request.ContentLength > maxBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
