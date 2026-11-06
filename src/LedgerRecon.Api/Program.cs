using System.Diagnostics;
using System.Text.Json.Serialization;
using LedgerRecon.Api.Demo;
using LedgerRecon.Api.Features;
using LedgerRecon.Api.Infrastructure;
using LedgerRecon.Api.Ledger;
using LedgerRecon.Api.Persistence;
using LedgerRecon.Api.Reconciliation;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddObservability();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddPersistence(builder.Configuration);
builder.Services.Configure<LedgerOptions>(builder.Configuration.GetSection(LedgerOptions.Section));
builder.Services.Configure<ReconciliationOptions>(builder.Configuration.GetSection(ReconciliationOptions.Section));
builder.Services.Configure<DemoOptions>(builder.Configuration.GetSection(DemoOptions.Section));
builder.Services.AddSingleton<AccountStore>();
builder.Services.AddSingleton<PostingService>();
builder.Services.AddSingleton<ReconciliationService>();
builder.Services.AddSingleton<DemoSeeder>();

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: [HealthEndpoints.ReadyTag]);

// RFC 9457 problem details for every error, with the trace id so a client-reported error can be found in traces.
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions.TryAdd("traceId", Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier));
builder.Services.AddExceptionHandler<DatabaseExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddOpenApi("v1", options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "ledger-recon API";
    document.Info.Version = "v1";
    document.Info.Description = "Double-entry ledger with exactly-once posting and end-of-day reconciliation of card, EFT/FAST and correspondent-bank files.";
    return Task.CompletedTask;
}));

var app = builder.Build();

// One image, three roles: `--migrate-only` applies migrations as the schema owner and exits; `--seed-demo` loads
// the synthetic demo day as the application role and exits; with no flag the API serves requests.
if (args.Contains("--migrate-only"))
{
    await app.Services.MigrateDatabaseAsync();
    return;
}

if (args.Contains("--seed-demo"))
{
    await app.Services.GetRequiredService<DemoSeeder>().RunAsync(CancellationToken.None);
    return;
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging(options =>
    options.GetLevel = (context, _, exception) =>
        exception is null && context.Response.StatusCode < 400 && context.Request.Path.StartsWithSegments("/health")
            ? Serilog.Events.LogEventLevel.Verbose
            : Serilog.Events.LogEventLevel.Information);

if (app.Configuration.GetValue<bool>("OpenApi:Enabled"))
{
    app.MapOpenApi();
    app.MapScalarApiReference(options => options.WithTitle("ledger-recon API"));
}

app.MapHealthEndpoints();
app.MapGroup("/api/v1")
    .WithGroupName("v1")
    .MapAccountEndpoints()
    .MapJournalEntryEndpoints()
    .MapReconciliationEndpoints();

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await app.Services.MigrateDatabaseAsync();
}

await app.RunAsync();

/// <summary>Entry point; public so WebApplicationFactory can boot the app in integration tests.</summary>
public partial class Program;
