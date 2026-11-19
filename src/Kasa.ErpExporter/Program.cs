// Sends matched bank movements to the ERP. It reads them from RabbitMQ (sprint 3), so it needs no public API;
// it is a web host only to answer /health for Docker and the dashboard.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();

var app = builder.Build();
app.MapHealthChecks("/health");

app.Run();
