using Kasa.Contracts;
using Kasa.ErpExporter;
using Microsoft.Extensions.Options;

// Sends matched bank movements to the ERP. It reads them from RabbitMQ, so it needs no public API; it is a web
// host only to answer /health for Docker and the dashboard. When the ERP is down, work waits in the queue.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
RabbitConnection.Add(builder.Services);
builder.Services.AddOptions<ErpOptions>().BindConfiguration("Erp").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddHttpClient<ErpClient>((sp, http) =>
{
    var erp = sp.GetRequiredService<IOptions<ErpOptions>>().Value;
    http.BaseAddress = new Uri(erp.BaseUrl.TrimEnd('/') + "/");
    http.Timeout = erp.Timeout;
});
builder.Services.AddHostedService<VoucherExporter>();

var app = builder.Build();
app.MapHealthChecks("/health");

app.Run();
