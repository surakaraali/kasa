using System.Text.Json.Serialization;
using Kasa.Api.Auth;
using Kasa.Api.Banking;
using Kasa.Api.Common;
using Kasa.Api.Data;
using Kasa.Api.Erp;
using Kasa.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

// Fail at start-up, not on the first request, when the database is not configured.
var sql = builder.Configuration.GetConnectionString("Sql")
    ?? throw new InvalidOperationException("ConnectionStrings:Sql is not configured.");

builder.Services
    .AddControllers(o =>
        // Validation errors name the fields as they are written in JSON ("companyName"), not as C# properties.
        o.ModelMetadataDetailsProviders.Add(new SystemTextJsonValidationMetadataProvider()))
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
// Errors are answered as RFC 9457 problem details; an unhandled exception becomes a plain 500 without a stack trace.
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddOpenApi(o => o.AddDocumentTransformer((document, _, _) =>
{
    // Lets Swagger UI send a bearer token with every request.
    document.Components ??= new OpenApiComponents();
    document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
    document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" };
    document.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", document)] = [] }];
    return Task.CompletedTask;
}));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<TenantContext>();
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlServer(sql));
KasaRedis.Add(builder.Services);
builder.Services.AddKasaAuth();
builder.Services.AddKasaBanking(builder.Configuration);
builder.Services.AddKasaErp();
builder.Services.AddSignalR();
builder.Services.AddSingleton<Kasa.Api.Dashboard.CompanyEvents>();

var app = builder.Build();

// shortcut: migrations run at start-up, fine for one instance; with several instances run them as a deploy step.
if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
}

app.UseExceptionHandler(new ExceptionHandlerOptions
{
    // In Development, minimal endpoints throw on a bad request instead of answering 400; this keeps it a 400.
    StatusCodeSelector = ex => ex is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError,
});
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
    app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "Kasa API"));
}

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<Kasa.Api.Dashboard.KasaHub>("/hubs/kasa");
// Liveness for now. Checks for SQL Server, Redis, MongoDB and RabbitMQ join it as each one arrives.
app.MapHealthChecks("/health").AllowAnonymous();

app.Run();
