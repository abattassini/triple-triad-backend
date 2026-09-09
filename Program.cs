using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using TripleTriadApi.Data;
using TripleTriadApi.Hubs;
using TripleTriadApi.Repositories;
using TripleTriadApi.Services;
using TripleTriadApi.Validators;

var builder = WebApplication.CreateBuilder(args);

// Load environment variables from .env file (for local development)
if (builder.Environment.IsDevelopment())
{
    // Use NoClobber so real environment variables (e.g. set by "Run Backend" VS Code
    // tasks, CI, or your shell) take precedence over .env file values.
    // This lets you switch databases without editing any file:
    //   UseInMemoryDatabase=true  → local in-memory DB
    //   UseInMemoryDatabase=false → remote Supabase PostgreSQL
    DotNetEnv.Env.Load((string)null, DotNetEnv.LoadOptions.NoClobber());
    // Reload configuration so .env values (e.g. UseInMemoryDatabase,
    // ConnectionStrings__DefaultConnection) are picked up by the config system.
    ((IConfigurationRoot)builder.Configuration).Reload();
}

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddOpenApi();

// Database configuration
var useInMemoryDatabase = builder.Configuration.GetValue<bool>("UseInMemoryDatabase", true);

if (useInMemoryDatabase)
{
    // Use in-memory database for development
    Console.WriteLine("🗄️  Using In-Memory Database");
    builder.Services.AddDbContext<TripleTriadContext>(options =>
        options.UseInMemoryDatabase("TripleTriadDb")
    );
}
else
{
    // Use PostgreSQL for production
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

    if (string.IsNullOrEmpty(connectionString))
    {
        throw new InvalidOperationException(
            "PostgreSQL connection string is required when UseInMemoryDatabase is false. "
                + "Set the 'ConnectionStrings:DefaultConnection' configuration or set 'UseInMemoryDatabase' to true."
        );
    }

    Console.WriteLine("🐘 Using PostgreSQL Database");
    builder.Services.AddDbContext<TripleTriadContext>(options =>
        options.UseNpgsql(connectionString)
    );
}

// Register services
builder.Services.AddScoped<IGameRepository, GameRepository>();
builder.Services.AddScoped<IPlayerRepository, PlayerRepository>();
builder.Services.AddScoped<GameLogicService>();
builder.Services.AddScoped<GamePlayService>();
builder.Services.AddScoped<CardSeederService>();
builder.Services.AddScoped<PasswordHasherService>();
builder.Services.AddScoped<RegisterPlayerRequestValidator>();
builder.Services.AddScoped<TokenService>();

// JWT authentication (HS256, same SymmetricSecurityKey as the issued tokens).
var jwtSecret =
    builder.Configuration["Supabase:JwtSecret"]
    ?? Environment.GetEnvironmentVariable("Supabase__JwtSecret");

if (string.IsNullOrEmpty(jwtSecret))
{
    Console.WriteLine("⚠️  JWT Secret not found — sign-in/token issuance will not work.");
    Console.WriteLine("   Set Supabase__JwtSecret in .env or as an environment variable.");
}
else
{
    Console.WriteLine("🔐 JWT authentication configured");
    builder
        .Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
                ValidateIssuer = false, // self-issued tokens, no issuer claim required
                ValidateAudience = false, // no audience claim required
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero,
            };

            // Allow SignalR to authenticate via ?access_token= query parameter.
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    var accessToken = context.Request.Query["access_token"];
                    var path = context.HttpContext.Request.Path;
                    if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/gamehub"))
                    {
                        context.Token = accessToken;
                    }

                    return Task.CompletedTask;
                },
            };
        });

    builder.Services.AddAuthorization();
}

// Add SignalR
builder.Services.AddSignalR();

// Add CORS
var allowedOrigins =
    builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? new[]
    {
        "http://localhost:5173",
        "https://abattassini.github.io",
    };

builder.Services.AddCors(options =>
{
    options.AddPolicy(
        "AllowFrontend",
        policy =>
        {
            policy.WithOrigins(allowedOrigins).AllowAnyMethod().AllowAnyHeader().AllowCredentials();
        }
    );

    // Add a permissive policy for local testing (file:// protocol and local testing)
    options.AddPolicy(
        "AllowAll",
        policy =>
        {
            policy
                .SetIsOriginAllowed(_ => true) // Allow any origin including file://
                .AllowAnyMethod()
                .AllowAnyHeader()
                .AllowCredentials();
        }
    );
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// CORS must come before routing
if (app.Environment.IsDevelopment())
{
    app.UseCors("AllowAll");
}
else
{
    app.UseCors("AllowFrontend");
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Map SignalR hub with lowercase URL for consistency
app.MapHub<GameHub>("/gamehub");

// Seed database
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<TripleTriadContext>();

    // Apply migrations only if using a real database (not in-memory)
    if (!context.Database.IsInMemory())
    {
        try
        {
            await context.Database.MigrateAsync();
        }
        catch (Exception ex)
        {
            var migrationLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            migrationLogger.LogError(ex, "An error occurred while migrating the database.");
        }
    }

    await CardSeederService.SeedCardsAsync(context);
}

// Log startup information
var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("Triple Triad API starting...");
logger.LogInformation("Environment: {Environment}", app.Environment.EnvironmentName);

app.Run();
