using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using LinkdUnified.Data;
using LinkdUnified.Services;
using LinkdUnified.Services.Sync;
using LinkdUnified.Services.Webhooks;

var builder = WebApplication.CreateBuilder(args);

// Add Controllers
builder.Services.AddControllers();

// Add Database Context (SQL Server)
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
    ?? "Server=DESKTOP-UF45NGP\\SQLEXPRESS;Database=LinkedInUnifiedDb;Trusted_Connection=True;TrustServerCertificate=True;";

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseSqlServer(connectionString);
});

// Add HTTP Client Factory for Webhooks
builder.Services.AddHttpClient("WebhookClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});

// Add Swagger / OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "LinkedIn Integration & Real-Time Webhook API",
        Version = "v1",
        Description = "Proof-of-concept API for connecting LinkedIn accounts, reading messaging conversations, SQL Server synchronization, and real-time Webhook event dispatching."
    });

    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath);
    }
});

// Register Core LinkedIn services
builder.Services.AddSingleton<ILinkedInSessionStore, InMemoryLinkedInSessionStore>();
builder.Services.AddScoped<LinkdUnified.Services.Browser.ILinkedInBrowserAuthService, LinkdUnified.Services.Browser.LinkedInBrowserAuthService>();
builder.Services.AddScoped<ILinkedInAuthService, LinkedInAuthService>();
builder.Services.AddScoped<ILinkedInMessagingService, LinkedInMessagingService>();

// Register Sync & Webhook services
builder.Services.AddScoped<IWebhookDispatcher, WebhookDispatcher>();
builder.Services.AddScoped<ILinkedInSyncService, LinkedInSyncService>();
builder.Services.AddHostedService<LinkedInMessagePollerWorker>();

var app = builder.Build();

// Ensure Database & Tables are created
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    try
    {
        db.Database.EnsureCreated();
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogWarning(ex, "Could not automatically create SQL Server database on startup. Ensure SQL Server is running.");
    }
}

// Enable Swagger and Swagger UI
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "LinkedIn Integration API v1");
});

// Redirect root to swagger
app.MapGet("/", () => Results.Redirect("/swagger"));

// Configure the HTTP request pipeline
app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();
