using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using CIPAdmin.Backend.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "CIP Admin API",
        Version = "v1",
        Description = "CIP Admin portal backend - auth, CIBIL report fetching with PAN-level caching, employee DID management."
    });

    // JWT bearer entry in Swagger UI
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste the JWT returned by /api/auth/login (or /api/auth/verify-otp)."
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

// CORS for the React dev server
const string CorsPolicy = "_cipFrontend";
builder.Services.AddCors(o => o.AddPolicy(CorsPolicy, p => p
    .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
    .AllowAnyHeader()
    .AllowAnyMethod()));

// JWT auth
var jwt = builder.Configuration.GetSection("Jwt");
var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt["Secret"]!));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt["Issuer"],
            ValidAudience = jwt["Audience"],
            IssuerSigningKey = key,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });
builder.Services.AddAuthorization(options =>
{
    // Role 1 = SUPER ADMIN, 2 = ADMIN in TBL_CIP_ROLE_MASTER.
    options.AddPolicy("Admin", p => p.RequireRole("1", "2"));
});

// App services
builder.Services.AddSingleton<IDbContext, DbContext>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IHierarchyService, HierarchyService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IAccessControlService, AccessControlService>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<ITotpService, TotpService>();
builder.Services.AddHttpClient<ICibilService, CibilService>();
builder.Services.AddSingleton<IOcrService, OcrService>();

var app = builder.Build();

// When hosted as an IIS sub-application (e.g. https://1up.co.in/CIPAdmin_API/),
// strip the path base so controllers keep matching /api/... routes.
var pathBase = app.Configuration["PathBase"];
if (!string.IsNullOrEmpty(pathBase)) app.UsePathBase(pathBase);

// Serve Swagger only outside Production so the API surface isn't exposed on the deployed server.
var swaggerEnabled = app.Configuration.GetValue("Swagger:Enabled", app.Environment.IsDevelopment());
if (swaggerEnabled)
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        // Relative so it works at site root or under an IIS sub-application path base.
        c.SwaggerEndpoint("v1/swagger.json", "CIP Admin API v1");
        c.DocumentTitle = "CIP Admin API";
    });
}

app.UseCors(CorsPolicy);

// Serve the built React frontend from wwwroot (IIS single-site deployment) and
// fall back non-API paths to index.html so client-side routes like /employees work.
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// Create the storage folder for CIBIL PDFs on startup
CibilService.EnsureStorageFolder(app.Configuration);

// Create the storage folder for OCR uploads on startup
Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "ocr uploads"));

app.Run();
