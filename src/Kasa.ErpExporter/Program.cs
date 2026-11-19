using Kasa.Contracts;
using Kasa.ErpExporter;
using Kasa.ServiceDefaults;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

// Sends matched bank movements to the ERP. It reads them from RabbitMQ, so it needs no public API; it is a web
// host only to answer /health for Docker and the dashboard. When the ERP is down, work waits in the queue.
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
RabbitConnection.Add(builder.Services);
// Without RabbitMQ the exporter has nothing to do (unhealthy); without the ERP, work waits in the queue (degraded).
builder.Services.AddHealthChecks()
    .Add(new HealthCheckRegistration("rabbitmq", sp => new RabbitHealthCheck(sp.GetRequiredService<RabbitConnection>(), HealthStatus.Unhealthy), null, null))
    .AddCheck<ErpHealthCheck>("erp");
builder.Services.AddOptions<ErpOptions>().BindConfiguration("Erp").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddHttpClient<ErpClient>((sp, http) =>
{
    var erp = sp.GetRequiredService<IOptions<ErpOptions>>().Value;
    http.BaseAddress = new Uri(erp.BaseUrl.TrimEnd('/') + "/");
    http.Timeout = erp.Timeout;
});
builder.Services.AddHostedService<VoucherExporter>();

var app = builder.Build();
app.MapDefaultEndpoints();

app.Run();
