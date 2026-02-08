using HideSolFound.MarketData.Core;
using HideSolFound.MarketData.Gateway.Hubs;
using HideSolFound.MarketData.Gateway.Services;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// ── Redis ──
string redisConnection = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnection));

// ── Core pipeline (singleton, shared across the application lifetime) ──
builder.Services.AddSingleton<MarketDataPipeline>();

// ── Application services ──
builder.Services.AddSingleton<SymbolMapper>();
builder.Services.AddSingleton<IRedisBookPersistence, RedisBookPersistence>();
builder.Services.AddSingleton<BookSnapshotManager>();
builder.Services.AddSingleton<FeedControlService>();

// ── Background services ──
builder.Services.AddHostedService<PipelineColdPathService>();
builder.Services.AddHostedService<BookHubNotifier>();

// ── ASP.NET Core ──
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:5100")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

var app = builder.Build();

// ── Middleware ──
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.MapControllers();
app.MapHub<BookHub>("/bookHub");

app.MapGet("/", () => Results.Content(
    "<h1>HideSolFound Market Data Gateway</h1><p><a href='/swagger'>API Documentation</a></p>",
    "text/html"));

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();
