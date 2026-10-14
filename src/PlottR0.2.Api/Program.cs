using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
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

// Swagger / OpenAPI — exposes our endpoints at /swagger for interactive testing.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Allow the Vite React dev server to call this API from a browser.
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        policy.WithOrigins("http://localhost:5173")
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var connectionString = builder.Configuration.GetConnectionString("PlottR02")
    ?? throw new InvalidOperationException("The 'PlottR02' connection string is not configured.");

builder.Services.ConfigurePersistence(
    connectionString,
    enableDetailedErrors: builder.Environment.IsDevelopment());
builder.Services.ConfigureApplication(builder.Configuration);

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
        logger.LogWarning(ex, "Could not apply database migrations on startup. The database may be unavailable; the API will continue but persistence is not guaranteed.");
    }
}

// Configure the HTTP request pipeline.
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

app.Run();
