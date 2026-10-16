using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PlottR0._2.Repository;
using PlottR0._2.Repository.Persistence;
using PlottR0._2.Api;
using PlottR0._2.Contracts;
using PlottR0._2.Services;
using PlottR0._2.Services.Configuration;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddHttpContextAccessor();

// Render (and most PaaS edges) terminate TLS and forward the original scheme/host via
// X-Forwarded-Proto / X-Forwarded-For. Trust those headers so Request.Scheme/Host reflect the
// public origin; otherwise UseHttpsRedirection and generated links see the internal http hop.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    // The edge is not a fixed proxy IP, so don't cap the forwarding chain depth.
    options.ForwardLimit = null;
});

// Swagger / OpenAPI — exposes our endpoints at /swagger for interactive testing.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Allow the SPA to call this API from a browser. Origins come from configuration
// (AllowedOrigins), falling back to the Vite React dev server when the section is absent.
// NOTE: no AllowCredentials() — auth travels as a Bearer header, not cookies.
var allowedOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:5173" };

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var connectionString = builder.Configuration.GetConnectionString("PlottR02")
    ?? throw new InvalidOperationException("The 'PlottR02' connection string is not configured.");

builder.Services.ConfigurePersistence(
    connectionString,
    enableDetailedErrors: builder.Environment.IsDevelopment(),
    configuration: builder.Configuration);
builder.Services.ConfigureApplication(builder.Configuration);

// Auth feature flags (Auth section) — the anonymous self-signup switch lives here.
// Bound FAIL-CLOSED through the SAME parser the service layer uses: a malformed value
// (e.g. "yes"/"notabool"/"") is OFF instead of a throwing strong binder, so an anonymous
// request can never surface as a 500. Default is OFF (appsettings.example.json / a missing
// key both mean false); flip at runtime with the Auth__AllowAnonymousSignup env var
// (e.g. Render: Auth__AllowAnonymousSignup=true).
builder.Services.AddOptions<AuthOptions>()
    .Configure<IConfiguration>((options, configuration) =>
        options.AllowAnonymousSignup = AuthOptions.ParseAllowAnonymousSignup(configuration));

// JWT bearer authentication — validates the access tokens issued by ITokenService
// using the same TokenOptions bound from the "Jwt" appsettings section.
var tokenOptions = builder.Configuration
    .GetSection(TokenOptions.SectionName)
    .Get<TokenOptions>() ?? new TokenOptions();

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    // Do NOT remap JWT claims onto the long ClaimTypes URIs. Without this, the
    // inbound "sub"/"email"/etc. claims are rewritten and controllers reading
    // User.FindFirstValue("sub") would get null (never auth). NOTE: on the
    // Microsoft.IdentityModel 8.x line, MapInboundClaims lives on JwtBearerOptions
    // rather than on TokenValidationParameters where it was removed.
    options.MapInboundClaims = false;

    // Built by JwtBearerConfiguration so the RoleClaimType wiring stays unit-testable
    // (see JwtBearerConfigurationTests). RoleClaimType MUST be "role": tokens carry a
    // literal "role" claim and MapInboundClaims is off, so [Authorize(Roles = ...)] —
    // which uses User.IsInRole — would otherwise look for the legacy WS-* role URI and
    // deny every caller.
    options.TokenValidationParameters =
        JwtBearerConfiguration.CreateTokenValidationParameters(tokenOptions);
});
builder.Services.AddAuthorization();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();

var app = builder.Build();

// Startup diagnostic: report which speech-to-text provider the composition root selected so a
// production "empty transcript" incident is diagnosable from the boot log alone. The selection
// mirrors ServiceCollectionExtensions (Cloudflare Workers AI when its credentials are present,
// otherwise the FasterWhisper sidecar) — see CloudflareAiOptions.IsConfigured.
{
    var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
    var cloudflare = app.Services.GetRequiredService<IOptions<CloudflareAiOptions>>().Value;
    if (cloudflare.IsConfigured)
    {
        startupLogger.LogInformation(
            "Speech-to-text provider: Cloudflare Workers AI (model {Model}).", cloudflare.Model);
    }
    else
    {
        var fasterWhisper = app.Services.GetRequiredService<IOptions<FasterWhisperOptions>>().Value;
        if (app.Environment.IsProduction())
        {
            startupLogger.LogWarning(
                "Speech-to-text provider misconfigured: CloudflareAi credentials are NOT configured " +
                "and the FasterWhisper sidecar at {BaseUrl} is not deployed, so transcription will fail.",
                fasterWhisper.BaseUrl);
        }
        else
        {
            startupLogger.LogInformation(
                "Speech-to-text provider: FasterWhisper sidecar at {BaseUrl}.", fasterWhisper.BaseUrl);
        }
    }
}

// Apply any pending EF Core migrations on startup.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PlottR0DbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        await db.Database.MigrateAsync();
    }
    catch (Exception ex)
    {
        // Dev: tolerate a missing/unreachable database so the API still boots for local work.
        if (app.Environment.IsDevelopment())
        {
            logger.LogWarning(ex, "Could not apply database migrations on startup. The database may be unavailable; the API will continue but persistence is not guaranteed.");
        }
        // Non-dev: a broken schema must abort startup with a non-zero exit rather than boot silently.
        else
        {
            logger.LogCritical(ex, "Could not apply database migrations on startup. Aborting startup because the database schema is not guaranteed.");
            throw;
        }
    }
}

// Configure the HTTP request pipeline. Forwarded headers must run first so downstream
// middleware (CORS, HTTPS redirection) sees the real client scheme/host behind the edge proxy.
app.UseForwardedHeaders();

app.UseCors("Frontend");

// Swagger UI at /swagger — only in Development, where we test endpoints interactively.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthentication();

app.UseAuthorization();

app.MapControllers();

// Liveness probe for the platform (Render healthCheckPath: /health). Anonymous, no dependency checks.
app.MapGet("/health", () => Results.Ok());

app.Run();
