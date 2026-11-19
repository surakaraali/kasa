using Kasa.Api.Data;
using Kasa.Api.Domain;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Kasa.Api.Auth;

public static class AuthSetup
{
    public static IServiceCollection AddKasaAuth(this IServiceCollection services)
    {
        services.AddOptions<JwtOptions>().BindConfiguration("Jwt").ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<LoginRateLimitOptions>().BindConfiguration("LoginRateLimit").ValidateDataAnnotations().ValidateOnStart();

        // Identity for the user store, password hashing and lockout. Tokens are ours (TokenService), not Identity's
        // own bearer tokens, because those are opaque and have no refresh rotation.
        services.AddIdentityCore<AppUser>(o =>
            {
                o.User.RequireUniqueEmail = true;
                o.Password.RequiredLength = 10;
                o.Password.RequireDigit = true;
                o.Password.RequireLowercase = true;
                o.Password.RequireUppercase = true;
                o.Password.RequireNonAlphanumeric = false;
                o.Lockout.AllowedForNewUsers = true;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddErrorDescriber<TurkishIdentityErrors>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwt) =>
            {
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwt.Value.Issuer,
                    ValidAudience = jwt.Value.Audience,
                    IssuerSigningKey = jwt.Value.Key(),
                    // Only the algorithm we sign with. A token that names another one ("none", RS256) is refused.
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    NameClaimType = KasaClaims.Name,
                    RoleClaimType = KasaClaims.Role,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
                // Browsers cannot set headers on a WebSocket, so SignalR sends the token in the query string.
                // Accepted only on the hub path, so a token in a normal URL is still ignored.
                bearer.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        if (context.HttpContext.Request.Path.StartsWithSegments("/hubs")
                            && context.Request.Query["access_token"].ToString() is { Length: > 0 } token)
                        {
                            context.Token = token;
                        }

                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Admin, p => p.RequireRole(nameof(UserRole.Admin)))
            .AddPolicy(Policies.CanEdit, p => p.RequireRole(nameof(UserRole.Admin), nameof(UserRole.Accountant)))
            // Every endpoint needs a signed-in user unless it says otherwise. A new endpoint cannot be left open by mistake.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        services.AddScoped<TokenService>();
        services.AddScoped<RefreshTokens>();
        services.AddScoped<LoginRateLimiter>();
        return services;
    }
}

/// <summary>Identity's messages in Turkish, for the ones a user can trigger.</summary>
public sealed class TurkishIdentityErrors : IdentityErrorDescriber
{
    public override IdentityError DuplicateEmail(string email) => new() { Code = nameof(DuplicateEmail), Description = "Bu e-posta adresiyle bir hesap zaten var." };
    public override IdentityError DuplicateUserName(string userName) => new() { Code = nameof(DuplicateUserName), Description = "Bu e-posta adresiyle bir hesap zaten var." };
    public override IdentityError InvalidEmail(string? email) => new() { Code = nameof(InvalidEmail), Description = "Geçerli bir e-posta adresi girin." };
    public override IdentityError PasswordTooShort(int length) => new() { Code = nameof(PasswordTooShort), Description = $"Şifre en az {length} karakter olmalı." };
    public override IdentityError PasswordRequiresDigit() => new() { Code = nameof(PasswordRequiresDigit), Description = "Şifrede en az bir rakam olmalı." };
    public override IdentityError PasswordRequiresLower() => new() { Code = nameof(PasswordRequiresLower), Description = "Şifrede en az bir küçük harf olmalı." };
    public override IdentityError PasswordRequiresUpper() => new() { Code = nameof(PasswordRequiresUpper), Description = "Şifrede en az bir büyük harf olmalı." };
    public override IdentityError PasswordMismatch() => new() { Code = nameof(PasswordMismatch), Description = "Mevcut şifre hatalı." };
}
