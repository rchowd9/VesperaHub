using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using VesperaHub.Server.Data;
using VesperaHub.Server.Endpoints;
using VesperaHub.Server.Hubs;
using VesperaHub.Server.Options;
using VesperaHub.Server.Services;

var builder = WebApplication.CreateBuilder(args);

// ---- Configuration ----
var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
if (jwt.Key.Length < 32)
    throw new InvalidOperationException("Jwt:Key must be at least 32 characters.");
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));

// ---- Services ----
builder.Services.AddDbContext<VesperaDbContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=vesperahub.db"));

builder.Services.AddSingleton<EncryptionService>();
builder.Services.AddSingleton<PresenceTracker>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<IUserIdProvider, NameUserIdProvider>();

builder.Services.AddSignalR(o => o.EnableDetailedErrors = builder.Environment.IsDevelopment());

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = TokenService.CreateSigningKey(jwt.Key),
            NameClaimType = TokenService.NameClaim,
            ClockSkew = TimeSpan.FromMinutes(1),
        };

        // Browsers can't set headers on WebSocket requests, so SignalR sends the token in the query string.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/chathub"))
                    context.Token = accessToken;
                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(AuthEndpoints.RateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

// ---- Database ----
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<VesperaDbContext>().Database.EnsureCreated();
}

// ---- Pipeline ----
app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapRoomEndpoints();
app.MapHub<ChatHub>("/chathub");
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
