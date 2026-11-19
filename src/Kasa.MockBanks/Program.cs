// Test banks: an open banking REST bank and an older SOAP bank (sprint 2). Test data only.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();

var app = builder.Build();
app.MapGet("/", () => "Kasa.MockBanks");
app.MapHealthChecks("/health");

app.Run();
