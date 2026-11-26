using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.Google;
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
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));
var google = builder.Configuration.GetSection("Authentication:Google");
bool googleEnabled = !string.IsNullOrWhiteSpace(google["ClientId"]) &&
    !string.IsNullOrWhiteSpace(google["ClientSecret"]);

// ---- Services ----
// Resolve a relative SQLite path against the content root so the database location
// doesn't depend on which folder the server was started from.
var sqlite = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(
    builder.Configuration.GetConnectionString("Default") ?? "Data Source=vesperahub.db");
if (!Path.IsPathRooted(sqlite.DataSource) && sqlite.DataSource != ":memory:")
    sqlite.DataSource = Path.Combine(builder.Environment.ContentRootPath, sqlite.DataSource);
builder.Services.AddDbContext<VesperaDbContext>(o => o.UseSqlite(sqlite.ToString()));

builder.Services.AddSingleton<EncryptionService>();
builder.Services.AddSingleton<MessageMapper>();
builder.Services.AddSingleton<ChatRateLimiter>();
builder.Services.AddSingleton<PresenceTracker>();
builder.Services.AddSingleton<VoicePresenceTracker>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<GoogleLoginHandoff>();
builder.Services.AddScoped<EmailVerificationService>();
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

if (googleEnabled)
{
    builder.Services.AddAuthentication()
        .AddCookie("External", options =>
        {
            options.Cookie.Name = "Vespera.External";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
        })
        .AddGoogle(GoogleDefaults.AuthenticationScheme, options =>
        {
            options.ClientId = google["ClientId"]!;
            options.ClientSecret = google["ClientSecret"]!;
            options.SignInScheme = "External";
            options.Scope.Add("email");
            options.Scope.Add("profile");
            options.ClaimActions.MapJsonKey("email_verified", "email_verified");
            options.ClaimActions.MapJsonKey("picture", "picture");
        });
}

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
    SchemaUpgrader.Upgrade(
        scope.ServiceProvider.GetRequiredService<VesperaDbContext>(),
        scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Database"));
}

// ---- Pipeline ----
app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapProfileEndpoints();
app.MapRoomEndpoints();
app.MapUploadEndpoints();
app.MapHub<ChatHub>("/chathub");
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
