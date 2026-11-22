using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Kasa.ServiceDefaults;

/// <summary>
/// What every Kasa service shares, in the style of .NET Aspire's ServiceDefaults: OpenTelemetry traces, metrics and
/// logs, and the two health endpoints. Telemetry is exported only when OTEL_EXPORTER_OTLP_ENDPOINT is set (the Aspire
/// dashboard in docker compose), so tests and a bare `dotnet run` send nothing anywhere.
/// </summary>
public static class ServiceDefaultsExtensions
{
    /// <summary>Kasa's own spans: outbox publishing, the assistant, bank sync.</summary>
    public static readonly ActivitySource Source = new("Kasa");

    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        builder.Logging.AddOpenTelemetry(o =>
        {
            o.IncludeFormattedMessage = true;
            o.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(builder.Environment.ApplicationName))
            .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddRuntimeInstrumentation())
            .WithTracing(t => t
                .AddSource(Source.Name)
                // RabbitMQ.Client 7 traces publish and receive itself and carries the trace in message headers, so one
                // trace runs from the HTTP request through the queue to the ERP call.
                .AddSource("RabbitMQ.Client.Publisher", "RabbitMQ.Client.Subscriber")
                .AddAspNetCoreInstrumentation(o => o.Filter = http => !http.Request.Path.StartsWithSegments("/health") && !http.Request.Path.StartsWithSegments("/alive"))
                .AddHttpClientInstrumentation()
                .AddSqlClientInstrumentation());

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy());
        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Liveness: for a container orchestrator's restart decision. Never depends on another service.
        app.MapHealthChecks("/alive", new HealthCheckOptions { Predicate = c => c.Name == "self" }).AllowAnonymous();
        // Readiness: every dependency, with each one's state. Degraded (Redis, MongoDB down) still answers 200.
        // Only states and our own descriptions: no exception text, no host names.
        app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = WriteAsync }).AllowAnonymous();
        return app;
    }

    private static Task WriteAsync(HttpContext http, HealthReport report)
    {
        http.Response.ContentType = "application/json";
        return http.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(e => e.Key, e => new { status = e.Value.Status.ToString(), description = e.Value.Description, ms = (int)e.Value.Duration.TotalMilliseconds }),
        }));
    }
}
