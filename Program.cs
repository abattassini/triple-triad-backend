using Microsoft.EntityFrameworkCore;
using TripleTriadApi.Data;
using TripleTriadApi.Hubs;
using TripleTriadApi.Repositories;
using TripleTriadApi.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddOpenApi();

// Database configuration
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrEmpty(connectionString))
{
    // Use in-memory database for development if no connection string is provided
    builder.Services.AddDbContext<TripleTriadContext>(options =>
        options.UseInMemoryDatabase("TripleTriadDb")
    );
}
else
{
    builder.Services.AddDbContext<TripleTriadContext>(options =>
        options.UseNpgsql(connectionString)
    );
}

// Register services
builder.Services.AddScoped<IGameRepository, GameRepository>();
builder.Services.AddScoped<GameLogicService>();
builder.Services.AddScoped<GamePlayService>();
builder.Services.AddScoped<CardSeederService>();

// Add SignalR
builder.Services.AddSignalR();

// Add CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy(
        "AllowFrontend",
        policy =>
        {
            policy
                .WithOrigins("http://localhost:5173", "https://argentinaluiz.github.io")
                .AllowAnyMethod()
                .AllowAnyHeader()
                .AllowCredentials();
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

// Don't redirect to HTTPS in development to avoid certificate issues
// app.UseHttpsRedirection();

// Use CORS - Use permissive policy in development
if (app.Environment.IsDevelopment())
{
    app.UseCors("AllowAll");
}
else
{
    app.UseCors("AllowFrontend");
}

app.UseAuthorization();

app.MapControllers();

// Map SignalR hub with lowercase URL for consistency
app.MapHub<GameHub>("/gamehub");

// Seed database
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<TripleTriadContext>();
    await CardSeederService.SeedCardsAsync(context);
}

app.Run();
