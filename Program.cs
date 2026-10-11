using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
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
builder.Services.AddScoped<IPlayerCardRepository, PlayerCardRepository>();
builder.Services.AddScoped<IPlayerPackRepository, PlayerPackRepository>();
builder.Services.AddScoped<GameLogicService>();
builder.Services.AddScoped<GamePlayService>();

// What a player may play this turn, and what each move would do — the board's own answer to "what happens if I drop
// this card here" (services/MovePreviewService.cs). A read: it never writes a match.
builder.Services.AddScoped<MovePreviewService>();
builder.Services.AddScoped<CardSeederService>();
builder.Services.AddScoped<PasswordHasherService>();
builder.Services.AddScoped<RegisterPlayerRequestValidator>();
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<MatchRewardService>();

// Match hand readiness/timeout reads (used by the endpoints and the hub) and the pushes the REST side needs
// (filing a hand, cancelling a match) — the hub itself only broadcasts what happens inside a connection.
builder.Services.AddScoped<MatchStateService>();
builder.Services.AddScoped<MatchmakingService>();

// The one rule every way into a match passes through: a player needs a hand's worth of different cards to play
// (plans/PLAN-030-minimum-cards-and-board-menu/plan.md).
builder.Services.AddScoped<MatchEligibilityService>();
builder.Services.AddScoped<IMatchNotifier, SignalRMatchNotifier>();

// Settles matches whose deadlines passed: a waiting match nobody joined, a hand that never arrived, a stalled game.
builder.Services.AddHostedService<MatchTimeoutService>();

// The bot opponent: its moves are chosen by BotMoveSelector and played by a sweep through the shared play pipeline,
// so a match against it is an ordinary match with a server-side mover on player 2.
builder.Services.AddScoped<BotMoveSelector>();
builder.Services.AddHostedService<BotTurnService>();

// Card shop: the pack draw needs randomness behind a seam (tests script it), so it is a singleton.
builder.Services.AddSingleton<IRandomSource, SystemRandomSource>();
builder.Services.AddScoped<PackService>();

// Password recovery (see plans/PLAN-013-password-recovery/plan.md). The token store is also the rate-limit ledger, the service
// owns the rules, and the mail transport is the only provider-aware piece — which is why it sits behind a seam.
builder.Services.AddScoped<IPasswordResetRepository, PasswordResetRepository>();
builder.Services.AddScoped<ResetPasswordRequestValidator>();
builder.Services.AddScoped<PasswordResetService>();

// The inbox, and the friendships it announces (see plans/PLAN-022-notifications-and-friends/plan.md). The notifier is
// the seam tests replace: it is what turns "a row was written" into a push to that player's own connections, since a
// REST request has no way to address a connection itself.
builder.Services.AddScoped<IFriendshipRepository, FriendshipRepository>();
builder.Services.AddScoped<INotificationRepository, NotificationRepository>();
builder.Services.AddScoped<IPlayerNotifier, SignalRPlayerNotifier>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<FriendService>();

// Challenging a friend (see plans/PLAN-027-friend-challenge/plan.md): one service, plus the narrow seam its two other
// writers (matchmaking, the timeout sweep) use, so neither has to take the whole challenge graph.
builder.Services.AddScoped<ChallengeService>();
builder.Services.AddScoped<IPendingChallenges>(services =>
    services.GetRequiredService<ChallengeService>()
);

// Presence (see plans/PLAN-023-social-friends-list/plan.md §3.1): the registry is a **singleton**, because the state it
// holds is this process's own sockets and would be meaningless per-request; the service that announces changes is
// scoped, like every other service that reaches the database.
//
// The interface is the bot-aware decorator (plans/PLAN-025-bots/plan.md §3.2), so every caller that asks "is this
// login online?" — the friend list, a profile — also learns a bot's emulated state. The connections registry stays
// concrete because both the hub (a real socket arriving) and the decorator (a human's answer) need its Add/Remove.
builder.Services.AddSingleton<ConnectionPresence>();
builder.Services.AddSingleton<BotRegistry>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPlayerPresence, BotAwarePresence>();
builder.Services.AddScoped<PresenceService>();

// Recovery configuration, bound through the options system rather than read from environment variables directly.
// A service reading the environment while Program.cs reads configuration is precisely the divergence that already
// caused a 401 bug once (plans/PLAN-010-welcome-onboarding/plan.md §326); one source of truth is the fix.
builder.Services.Configure<EmailOptions>(
    builder.Configuration.GetSection(EmailOptions.SectionName)
);
builder.Services.Configure<PasswordResetOptions>(
    builder.Configuration.GetSection(PasswordResetOptions.SectionName)
);
builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.SectionName));

// Which transport actually sends recovery mail.
//
// The rule is "use the mailbox you were given": SMTP whenever the Email:* settings are configured, and the console
// sender only as a Development convenience for a machine with no mailbox at all. So a developer with credentials in
// .env sends real mail, a developer without them still gets a fully usable flow (the code is printed), and production
// is *always* SMTP — deliberately, because falling back to the console on a misconfigured server would silently stop
// delivering recoveries and print live reset codes into the hosting logs.
//
// SMTP here means "the settings in Email:*", which covers the current dedicated Gmail account and any paid relay
// (Resend, Brevo, SendGrid and Mailgun all expose SMTP relays). That switch is configuration, not code.
var emailConfigured =
    builder.Configuration.GetSection(EmailOptions.SectionName).Get<EmailOptions>()?.IsConfigured
    ?? false;

if (builder.Environment.IsDevelopment() && !emailConfigured)
{
    builder.Services.AddScoped<IEmailSender, ConsoleEmailSender>();
}
else
{
    builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();
}

// Endpoint rate limiting for password recovery — built into .NET 9, no package needed.
//
// Deliberately a *second* mechanism alongside the database-backed caps in PasswordResetService: they protect
// different things. This one stops a script hammering the code-guessing endpoint and is per-process, so it is cheap;
// the database one survives a restart, which is what the limits protecting the sending mailbox require.
var resetAttemptsPerAddressPerHour = builder.Configuration.GetValue(
    "PasswordReset:ResetAttemptsPerIpPerHour",
    new PasswordResetOptions().ResetAttemptsPerIpPerHour
);

// The friend actions get their own window (see plans/PLAN-022-notifications-and-friends/plan.md §3.4): the pending
// cap in FriendService bounds how many *outstanding* requests one account may have, while this bounds how fast one
// address may ask and withdraw — which the cap alone cannot see.
var friendRequestsPerAddressPerHour = builder.Configuration.GetValue(
    "FriendRequests:RequestsPerIpPerHour",
    60
);

builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    limiter.AddPolicy(
        PasswordResetOptions.RateLimitPolicyName,
        context =>
        {
            // Partitioned by client address. Behind a proxy this may be the proxy's address unless ForwardedHeaders
            // is configured, in which case it behaves more like a global cap; see the plan's notes.
            var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            return RateLimitPartition.GetFixedWindowLimiter(
                $"password-reset:{address}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = resetAttemptsPerAddressPerHour,
                    Window = TimeSpan.FromHours(1),
                    QueueLimit = 0,
                }
            );
        }
    );

    limiter.AddPolicy(
        FriendService.RateLimitPolicyName,
        context =>
        {
            // Partitioned by client address, like the recovery policy above and with the same proxy caveat: behind a
            // proxy this is the proxy's address unless ForwardedHeaders is configured.
            var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            return RateLimitPartition.GetFixedWindowLimiter(
                $"friend-request:{address}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = friendRequestsPerAddressPerHour,
                    Window = TimeSpan.FromHours(1),
                    QueueLimit = 0,
                }
            );
        }
    );
});

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

                // Retire tokens minted before the player's most recent password change.
                //
                // A JWT is otherwise trusted for its whole 7-day life, so without this a password reset would leave an
                // attacker who already held a token signed in — the one thing a reset exists to prevent. The token
                // carries its generation as "ver" (see TokenService) and the player row holds the current one (see
                // Player.SessionVersion). Cost: one player read per authenticated request.
                OnTokenValidated = async context =>
                {
                    var login =
                        context.Principal?.FindFirst("sub")?.Value
                        ?? context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                    if (string.IsNullOrEmpty(login))
                    {
                        context.Fail("The token has no subject.");

                        return;
                    }

                    // A token issued before password recovery existed has no session_version and so belongs to
                    // generation 0, which is what an untouched player row holds. The claim name is deliberately not
                    // the short "ver", which the JWT handler maps to ClaimTypes.Version on the way in — see
                    // TokenService.IssueToken.
                    var versionClaim = context.Principal?.FindFirst("session_version")?.Value;
                    var tokenVersion = int.TryParse(versionClaim, out var parsed) ? parsed : 0;

                    var players =
                        context.HttpContext.RequestServices.GetRequiredService<IPlayerRepository>();
                    var player = await players.FindByLoginAsync(login);

                    if (player is null || player.SessionVersion != tokenVersion)
                    {
                        context.Fail("This session was signed out by a password change.");
                    }
                },
            };
        });

    builder.Services.AddAuthorization();
}

// Password recovery needs a mailbox in production. Say so loudly, but never fail: recovery being unavailable is not a
// reason for the game to be down. The sender itself refuses at send time with an actionable message — see
// SmtpEmailSender for why that check deliberately lives there rather than in a constructor.
if (!emailConfigured && builder.Environment.IsDevelopment())
{
    Console.WriteLine("📧 Password recovery email: writing to the log (no mailbox configured).");
}
else if (!emailConfigured)
{
    Console.WriteLine("⚠️  Email is not configured — password recovery cannot send codes.");
    Console.WriteLine(
        "   Set Email__Smtp__Host / __Port / __User / __Password and Email__FromAddress."
    );
}
else
{
    Console.WriteLine("📧 Password recovery email: SMTP via the configured mailbox.");
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

// Runs after routing (which the WebApplication builder wires up automatically), so the endpoint's policy is known by
// the time a request reaches the limiter.
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Map SignalR hub with lowercase URL for consistency
app.MapHub<GameHub>("/gamehub");

// Apply migrations and seed the database
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<TripleTriadContext>();
    var startupLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    // Apply migrations only if using a real database (not in-memory)
    if (!context.Database.IsInMemory())
    {
        try
        {
            var pendingMigrations = (await context.Database.GetPendingMigrationsAsync()).ToList();
            if (pendingMigrations.Count > 0)
            {
                startupLogger.LogInformation(
                    "Applying {Count} pending migration(s): {Migrations}",
                    pendingMigrations.Count,
                    string.Join(", ", pendingMigrations)
                );
            }

            await context.Database.MigrateAsync();
            startupLogger.LogInformation("Database schema is up to date with all migrations.");
        }
        catch (Exception ex)
        {
            // Fail fast: a swallowed migration error lets the app run against a stale or
            // divergent schema, which later surfaces as confusing runtime errors
            // (e.g. Postgres 42703 "column ... does not exist"). A startup failure with
            // the real cause is far easier to diagnose.
            startupLogger.LogCritical(
                ex,
                "Database migration failed - refusing to start against an inconsistent schema. "
                    + "Verify the connection string (.env / environment variables), then run "
                    + "'dotnet ef migrations list' and 'dotnet ef database update' to inspect "
                    + "and reconcile the schema."
            );
            throw;
        }
    }

    await CardSeederService.SeedCardsAsync(context);

    // The bots: the cohort of emulated players a match can always fall back to (plans/PLAN-025-bots/plan.md). Seeded
    // beside the cards and for the same reason — it is what makes the game playable on a fresh database.
    await BotSeederService.SeedBotsAsync(
        context,
        scope.ServiceProvider.GetRequiredService<IRandomSource>()
    );

    // Hand the cohort to the presence cache, so a bot's emulated online state can be answered without a read
    // (plans/PLAN-025-bots/plan.md §3.2). Loaded here, after the seeder, since nothing else creates a bot.
    var botRegistry = scope.ServiceProvider.GetRequiredService<BotRegistry>();
    var botRows = await context
        .Players.Where(player => player.IsBot)
        .Select(player => new
        {
            player.Login,
            player.Activity,
            player.CpuPlayingProfile,
        })
        .ToListAsync();
    botRegistry.Load(
        botRows.Select(row => new BotRegistry.Bot(row.Login, row.Activity, row.CpuPlayingProfile))
    );
}

// Log startup information
var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("Triple Triad API starting...");
logger.LogInformation("Environment: {Environment}", app.Environment.EnvironmentName);

app.Run();
