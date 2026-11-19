var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
// Errors are answered as RFC 9457 problem details; an unhandled exception becomes a plain 500 without a stack trace.
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.MapControllers();
// Liveness for now. Checks for SQL Server, Redis, MongoDB and RabbitMQ join this endpoint as each one arrives.
app.MapHealthChecks("/health");

app.Run();
