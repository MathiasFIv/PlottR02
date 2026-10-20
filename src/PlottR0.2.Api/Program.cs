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

builder.Services.AddControllers();
builder.Services.AddHttpContextAccessor();

// Render and most PaaS edges terminate TLS and forward the original scheme/host; trust those
// headers so Request.Scheme/Host reflect the public origin rather than the internal http hop.
// The edge IP is not fixed, so the proxy/network lists are cleared and the chain depth is uncapped.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();

    options.ForwardLimit = null;
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

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

builder.Services.AddOptions<AuthOptions>()
    .Configure<IConfiguration>((options, configuration) =>
        options.AllowAnonymousSignup = AuthOptions.ParseAllowAnonymousSignup(configuration));

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
    // Do NOT let the handler remap inbound claims onto the long ClaimTypes URIs: controllers read
    // User.FindFirstValue("sub") and RoleClaimType is a literal "role", so remapping would make
    // every caller look unauthenticated.
    options.MapInboundClaims = false;

    options.TokenValidationParameters =
        JwtBearerConfiguration.CreateTokenValidationParameters(tokenOptions);
});
builder.Services.AddAuthorization();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();

var app = builder.Build();

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
        if (app.Environment.IsDevelopment())
        {
            logger.LogWarning(ex, "Could not apply database migrations on startup. The database may be unavailable; the API will continue but persistence is not guaranteed.");
        }
        else
        {
            logger.LogCritical(ex, "Could not apply database migrations on startup. Aborting startup because the database schema is not guaranteed.");
            throw;
        }
    }
}

app.UseForwardedHeaders();

app.UseCors("Frontend");

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthentication();

app.UseAuthorization();

app.MapControllers();

app.MapGet("/health", () => Results.Ok());

app.Run();
