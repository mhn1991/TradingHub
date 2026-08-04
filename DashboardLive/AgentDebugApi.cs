using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dashboard.Live;

public static class AgentDebugApi
{
    public static void MapAgentDebugEndpoints(this WebApplication app)
    {
        app.MapGet("/api/agent-debug/instruments", () =>
        {
            string cacheDirectory = ResolveCacheDirectory();
            return Results.Ok(AgentDebugRunner.ListCachedInstruments(cacheDirectory));
        });

        app.MapPost("/api/agent-debug/runs", (AgentDebugRunRequest request, AgentDebugJobRegistry registry) =>
        {
            if (request.MonitoredTimeframes.Count == 0)
                return Results.BadRequest(new { error = "At least one monitored timeframe is required." });
            if (request.RunStart < request.WarmupStart || request.RunEnd <= request.RunStart)
            {
                return Results.BadRequest(new
                {
                    error = "Expected WarmupStart <= RunStart < RunEnd."
                });
            }

            string cacheDirectory = ResolveCacheDirectory();
            AgentDebugJob job = registry.Start(request, cacheDirectory);
            return Results.Ok(new { id = job.Id });
        });

        app.MapGet("/api/agent-debug/runs/{id:guid}/stream", async (
            Guid id,
            HttpContext context,
            AgentDebugJobRegistry registry,
            IHostApplicationLifetime applicationLifetime,
            CancellationToken requestAborted) =>
        {
            if (!registry.TryGet(id, out AgentDebugJob job))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            using CancellationTokenSource requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(
                requestAborted, applicationLifetime.ApplicationStopping);
            CancellationToken cancellationToken = requestLifetime.Token;

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache, no-transform";
            context.Response.Headers.Append("X-Accel-Buffering", "no");

            JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
            {
                Converters = { new JsonStringEnumConverter() },
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            try
            {
                await foreach (AgentDebugEvent evt in job.Reader.ReadAllAsync(cancellationToken))
                {
                    string json = JsonSerializer.Serialize(evt, jsonOptions);
                    await context.Response.WriteAsync($"data: {json}\n\n", cancellationToken);
                    await context.Response.Body.FlushAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected or server shutting down - nothing to clean up beyond this.
            }
        });
    }

    private static string ResolveCacheDirectory()
    {
        string? root = FindSolutionRoot();
        return Path.Combine(root ?? Directory.GetCurrentDirectory(), ".cache", "historical");
    }

    private static string? FindSolutionRoot()
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TradingHub.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        return null;
    }
}
