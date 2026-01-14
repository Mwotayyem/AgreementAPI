using AgreementAPI.Repositories;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Allow JSON parsing even without Content-Type header
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
    });

// Register repository
builder.Services.AddScoped<AgreementRepository>();

var app = builder.Build();

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();

