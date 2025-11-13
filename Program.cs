using PaymentRateLimiter.Core.Services;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Serialize enums as strings instead of numbers
        options.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

// Configure Azure Service Bus options (AzureServiceBusOptions is defined in PaymentServiceBusService.cs)
builder.Services.Configure<PaymentRateLimiter.Core.Services.AzureServiceBusOptions>(
    builder.Configuration.GetSection("AzureServiceBus"));

// Register our custom services
// Singleton ensures the same instance is shared across all requests
builder.Services.AddSingleton<PaymentServiceBusService>();
builder.Services.AddSingleton<PaymentStatusService>();

// Register background services (hosted services)
builder.Services.AddHostedService<PaymentProcessorWorker>();

// Add API documentation
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add CORS for web client support
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(
                "http://localhost:4200",  // Angular dev server
                "http://localhost:3000",  // React dev server
                "http://localhost:5173",  // Vite dev server
                "http://localhost:8080",  // Vue dev server
                "http://127.0.0.1:5500"   // Live Server (VS Code extension)
            )
            .AllowAnyMethod()
            .AllowAnyHeader()
            .WithExposedHeaders("Content-Type"); // For SSE
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// Enable CORS
app.UseCors();

app.UseAuthorization();

app.MapControllers();

app.Run();

