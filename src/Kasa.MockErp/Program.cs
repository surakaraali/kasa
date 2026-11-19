// A test ERP in the style of Logo Tiger: current accounts and vouchers (sprint 3). Test data only.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();

var app = builder.Build();
app.MapGet("/", () => "Kasa.MockErp");
app.MapHealthChecks("/health");

app.Run();
