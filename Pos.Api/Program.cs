using System.Reflection;
using DotNetEnv;
using FluentValidation;
using MediatR;
using Pos.Api.Behaviors;
using Pos.Api.Database;
using Pos.Api.Interfaces;
using Pos.Api.Middleware;
using Serilog;

// Load .env before anything else so IConfiguration/Environment can see it.
// .env is git-ignored — never commit real credentials.
Env.Load();

var builder = WebApplication.CreateBuilder(args);
// Bridge .env's MYSQL_CONNECTION_STRING into the standard ConnectionStrings:Default
// config key that PosDatabase.cs expects.
builder.Configuration["ConnectionStrings:Default"] =
    Environment.GetEnvironmentVariable("MYSQL_CONNECTION_STRING");
// ---- Serilog ----
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();
builder.Host.UseSerilog();

// ---- Controllers + Swagger ----
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ---- Dapper / MySQL (NOT EF Core) ----
builder.Services.AddSingleton<IPosDatabase, PosDatabase>();

// ---- MediatR ----
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(Assembly.GetExecutingAssembly()));
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

// ---- FluentValidation ----
builder.Services.AddValidatorsFromAssembly(Assembly.GetExecutingAssembly());

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();