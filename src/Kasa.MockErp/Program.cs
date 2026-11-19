using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Kasa.MockErp;
using Microsoft.Extensions.Options;

// A test ERP in the style of Logo Tiger: current accounts and vouchers, over a small REST API with an API key.
// Test data only. "/" shows the vouchers as a page for the demo; /mock takes the ERP down or makes it slow.

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ErpWorld>();
builder.Services.Configure<MockErpOptions>(builder.Configuration.GetSection("MockErp"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<MockErpOptions>>().Value);
var app = builder.Build();

app.MapHealthChecks("/health");

// Faults apply to the ERP API only, never to the page, /mock or /health.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        var world = context.RequestServices.GetRequiredService<ErpWorld>();
        switch (world.Fault)
        {
            case Fault.Down:
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsync("Service Unavailable");
                return;
            case Fault.Slow:
                await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted);
                break;
        }

        var options = context.RequestServices.GetRequiredService<MockErpOptions>();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(context.Request.Headers["X-Api-Key"].ToString()), Encoding.UTF8.GetBytes(options.ApiKey)))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Geçersiz API anahtarı" });
            return;
        }
    }

    await next();
});

app.MapGet("/api/firms/{firm}/current-accounts", (string firm) =>
    firm == ErpWorld.Firm ? Results.Json(ErpWorld.CurrentAccounts) : Results.NotFound(new { error = "Firma bulunamadı" }));

app.MapGet("/api/firms/{firm}/vouchers", (string firm, ErpWorld world) =>
    firm == ErpWorld.Firm ? Results.Json(world.Vouchers()) : Results.NotFound(new { error = "Firma bulunamadı" }));

app.MapPost("/api/firms/{firm}/vouchers", (string firm, VoucherRequest body, HttpRequest request, ErpWorld world, TimeProvider clock) =>
{
    if (firm != ErpWorld.Firm)
    {
        return Results.NotFound(new { error = "Firma bulunamadı" });
    }

    var key = request.Headers["Idempotency-Key"].ToString();
    if (key.Length is 0 or > 100)
    {
        return Results.BadRequest(new { error = "Idempotency-Key başlığı gerekli" });
    }

    if (body.Type is not ("Tahsilat" or "Tediye") || body.Amount <= 0 || string.IsNullOrWhiteSpace(body.BankIban))
    {
        return Results.UnprocessableEntity(new { error = "Fiş türü Tahsilat ya da Tediye, tutar sıfırdan büyük, banka IBAN'ı dolu olmalı" });
    }

    var account = ErpWorld.CurrentAccounts.FirstOrDefault(a => a.Code == body.CurrentAccountCode);
    if (account is null)
    {
        return Results.UnprocessableEntity(new { error = $"Cari kart bulunamadı: {body.CurrentAccountCode}" });
    }

    var (voucher, replayed, error) = world.Create(key, body, account, clock.GetUtcNow());
    return error is not null
        ? Results.UnprocessableEntity(new { error })
        : Results.Json(voucher, statusCode: replayed ? StatusCodes.Status200OK : StatusCodes.Status201Created);
});

app.MapPost("/mock/fault", (FaultRequest body, ErpWorld world) =>
{
    if (!Enum.TryParse<Fault>(body.Mode, ignoreCase: true, out var fault))
    {
        return Results.BadRequest("mode is none, down or slow");
    }

    world.Fault = fault;
    return Results.NoContent();
});

app.MapGet("/", (ErpWorld world) =>
{
    var tr = CultureInfo.GetCultureInfo("tr-TR");
    var rows = string.Concat(world.Vouchers().AsEnumerable().Reverse().Select(v =>
        $"<tr><td>{v.VoucherNo}</td><td>{v.Date:dd.MM.yyyy}</td><td>{v.Type}</td><td>{WebUtility.HtmlEncode(v.CurrentAccountCode)} {WebUtility.HtmlEncode(v.CurrentAccountTitle)}</td>"
        + $"<td style=\"text-align:right\">{v.Amount.ToString("N2", tr)}</td><td>{WebUtility.HtmlEncode(v.Description)}</td></tr>"));
    return Results.Content(
        "<!doctype html><html lang=\"tr\"><meta charset=\"utf-8\"><meta http-equiv=\"refresh\" content=\"3\"><title>Test ERP</title>"
        + "<body style=\"font-family:system-ui;margin:2rem;line-height:1.4\"><h1>Test ERP · Firma 001 · Banka fişleri</h1>"
        + $"<p>{world.Vouchers().Count} fiş · ERP durumu: {world.Fault} · sayfa 3 saniyede bir yenilenir</p>"
        + "<table cellpadding=\"6\" style=\"border-collapse:collapse\"><tr><th>Fiş no</th><th>Tarih</th><th>Tür</th><th>Cari</th><th>Tutar</th><th>Açıklama</th></tr>"
        + rows + "</table><p style=\"color:#777\">Test ERP'si, örnek veri.</p></body></html>",
        "text/html; charset=utf-8", Encoding.UTF8);
});

app.Run();

internal sealed record FaultRequest(string? Mode);
