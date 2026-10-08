using System.Text;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using CIPAdmin.Backend.Services;

// Pin the content root to the executable's own folder instead of letting it default
// to the process working directory. The default makes appsettings.json loadable ONLY
// from wherever the app happened to be launched: run the published exe from any other
// directory and Jwt:Secret comes back null, so startup dies at the JWT key with a bare
// ArgumentNullException ("Value cannot be null. (Parameter 's')") that names neither the
// missing setting nor the folder it looked in. On IIS the two coincide, so this changes
// nothing there - it just makes the app independent of its launch directory.
//
// Web root needs care. `dotnet publish` copies wwwroot next to the EXE, so on IIS and
// in publish/backend it sits under AppContext.BaseDirectory. But `dotnet run` executes
// from bin/<config>/net8.0, and the Web SDK does NOT copy wwwroot there - it stays in
// the project folder. Pinning the content root therefore silently breaks the SPA
// during local development (GET / returns 404) while leaving production correct, which
// is a confusing failure. So: prefer the folder beside the exe, and fall back to the
// project folder when running from a build output.
var contentRoot = AppContext.BaseDirectory;
var webRoot = Path.Combine(contentRoot, "wwwroot");
if (!Directory.Exists(webRoot))
{
    var devWebRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
    if (Directory.Exists(devWebRoot))
    {
        contentRoot = Directory.GetCurrentDirectory();
        webRoot = devWebRoot;
    }
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = contentRoot,
    WebRootPath = webRoot
});

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
    // X-Session-End carries the sign-out reason; without exposing it the dev client
    // cannot tell "idle" from "signed in elsewhere".
    .WithExposedHeaders("X-Session-End")
    .AllowAnyMethod()));

// JWT auth
var jwt = builder.Configuration.GetSection("Jwt");
var jwtSecret = jwt["Secret"];
if (string.IsNullOrWhiteSpace(jwtSecret))
{
    // Fail with something an operator can act on. The old behaviour was an
    // ArgumentNullException from Encoding.GetBytes deep inside the JWT setup.
    throw new InvalidOperationException(
        $"Jwt:Secret is missing or empty in the configuration loaded from " +
        $"'{Path.Combine(AppContext.BaseDirectory, "appsettings.json")}'. " +
        "Copy appsettings.json next to CIPAdmin.Backend.exe (it is excluded from the " +
        "repository on purpose) before starting the API.");
}

var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));
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

        // Single active session per employee + 15-minute idle logout.
        // The signature is already trusted at this point; this asks a different question:
        // "is this still the one live login for this employee, and is it still fresh?".
        o.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var empCodeClaim = context.Principal?.FindFirst("empCode")?.Value;
                var jti = context.Principal?.FindFirst(JwtRegisteredClaimNames.Jti)?.Value
                          ?? context.Principal?.FindFirst("jti")?.Value;
                if (!int.TryParse(empCodeClaim, out var empCode) || string.IsNullOrEmpty(jti))
                    return; // malformed token — leave the default validation outcome alone

                var access = context.HttpContext.RequestServices.GetRequiredService<IAccessControlService>();
                if (!await access.IsEligibleEmployeeAsync(empCode))
                {
                    context.Fail("This account is not an active employee eligible for CIPAdmin.");
                    return;
                }

                var sessions = context.HttpContext.RequestServices.GetRequiredService<IUserSessionService>();
                var check = await sessions.ValidateAndTouchAsync(empCode, jti);

                if (check == SessionCheck.Ok) return;

                // Lets the SPA say WHY it was signed out instead of a bare "session expired".
                // NoSession means the row is simply gone — an explicit sign-out, or a session
                // already reaped — which is different from being displaced by a second login.
                context.HttpContext.Response.Headers["X-Session-End"] = check switch
                {
                    SessionCheck.IdleTimeout => "idle",
                    SessionCheck.Superseded => "superseded",
                    _ => "ended"
                };

                context.Fail(check switch
                {
                    SessionCheck.IdleTimeout => "Your session expired after 15 minutes of inactivity. Please sign in again.",
                    SessionCheck.Superseded => "You have been signed out because this account was signed in somewhere else.",
                    _ => "Your session has ended. Please sign in again."
                });
            }
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
builder.Services.AddScoped<IUserSessionService, UserSessionService>();
builder.Services.AddScoped<ITotpService, TotpService>();
builder.Services.AddHttpClient<ICibilService, CibilService>();
builder.Services.AddSingleton<IOcrService, OcrService>();
// Error log: writes to the DB, so it needs the DB. Singleton, matching IDbContext —
// it holds no per-request state, and a failure must still be logged on a request whose
// scoped services have already been torn down.
builder.Services.AddSingleton<IErrorLogService, ErrorLogService>();

var app = builder.Build();

// When hosted as an IIS sub-application (e.g. https://1up.co.in/CIPAdmin_API/),
// strip the path base so controllers keep matching /api/... routes.
//
// ORDER IS LOAD-BEARING. WebApplication would otherwise insert UseRouting at the very
// start of the pipeline, and routing matches endpoints against the REQUEST path. With the
// prefix still attached, /CIPAdmin_API/api/auth/login matches no controller route but does
// match the catch-all from MapFallbackToFile("index.html"), so every prefixed API call
// returned the SPA - or 405 for POST, because the static-file fallback only allows
// GET/HEAD. Stripping the prefix first, then routing explicitly, keeps real API routes
// winning over the SPA fallback.
var pathBase = app.Configuration["PathBase"];
if (!string.IsNullOrEmpty(pathBase)) app.UsePathBase(pathBase);

// Records EVERY server-side failure in TBL_CIP_API_ERROR_LOG so admins can read them
// on the Error Log screen instead of hunting through IIS logs.
//
// Placed BEFORE UseRouting/UseExceptionHandler on purpose, and that ordering is
// load-bearing twice over:
//
//   1. It sits UPSTREAM of UseExceptionHandler, so when a controller throws, the
//      handler downstream catches the exception, writes the 500 response, and
//      RETURNS NORMALLY. This middleware therefore sees a completed 500 response and
//      logs it with no exception object. If it sat downstream, the exception would
//      unwind straight past it and the failure would never be recorded.
//
//   2. It sits after UsePathBase, so REQUEST_PATH is the route the app actually
//      matched ("/api/applications"), not the prefixed form. The screen groups by
//      this, and grouping "/CIPAdmin_API/api/applications" and "/api/applications"
//      as two different failures would be actively misleading.
//
// The exception handler sets ErrorLoggedKey when it has already written a row for
// this request, so the same failure is not recorded twice.
const string ErrorLoggedKey = "cip.errorLogged";
const string ErrorExceptionKey = "cip.errorException";

app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        // Only reached for exceptions thrown UPSTREAM of UseExceptionHandler (i.e.
        // not by a controller), which the handler cannot see. Log it, then rethrow so
        // normal hosting behaviour still applies.
        await SafeLog(ctx, ex, null);
        throw;
    }

    // A 5xx that was RETURNED rather than thrown. Controllers deliberately catch
    // database failures and return StatusCode(500, ...) with a helpful message - for
    // example ApplicationsController's Search/ExistingLoans. Those never reach the
    // exception handler at all, so without this they would be the errors most likely
    // to go unrecorded.
    //
    // The controller stashes the caught EXCEPTION (not just its message) so the row
    // carries the real exception type, the SQL error number and the stack trace. Passing
    // only a message string would file every database fault under SOURCE "Application"
    // with a NULL exception type, which defeats the point of the screen.
    if (ctx.Response.StatusCode >= 500 && ctx.Items[ErrorLoggedKey] is not true)
    {
        var caught = ctx.Items.TryGetValue(ErrorExceptionKey, out var e) ? e as Exception : null;
        if (caught is not null)
        {
            await SafeLog(ctx, caught, ctx.Response.StatusCode);
        }
        else
        {
            var summary = ctx.Items.TryGetValue("cip.errorSummary", out var s) ? s as string : null;
            await SafeLog(ctx, null, ctx.Response.StatusCode, summary);
        }
    }
});

async Task SafeLog(HttpContext ctx, Exception? ex, int? status, string? summary = null)
{
    try
    {
        var svc = ctx.RequestServices.GetService<IErrorLogService>();
        if (svc is null) return;
        ctx.Items[ErrorLoggedKey] = true;

        if (ex is not null)
            await svc.LogAsync(ex, ctx, status ?? 500);
        else if (!string.IsNullOrWhiteSpace(summary))
            await svc.LogAsync(summary!, ctx, status ?? 500);
    }
    catch
    {
        // Never let logging turn one failure into a different, worse one.
    }
}

// Declaring UseRouting here (instead of letting WebApplication add it implicitly at the
// front) guarantees it runs AFTER UsePathBase.
app.UseRouting();

// Without this, an unhandled exception in a controller returns a 500 with an EMPTY body,
// so the browser shows only "Request failed (500)" and nothing in the UI explains why.
// Production has no developer exception page, so the real cause is invisible to the
// operator unless they have stdout logs switched on. This turns it into a JSON body the
// SPA can display and the app log records with a full stack trace.
app.UseExceptionHandler(errApp => errApp.Run(async ctx =>
{
    var feature = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
    var ex = feature?.Error;
    var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("UnhandledException");
    logger.LogError(ex, "Unhandled exception on {Method} {Path}", ctx.Request.Method, ctx.Request.Path);

    // Persist it for the admin Error Log screen. Uses the request's own services, which
    // are still alive here, and swallows its own failures internally.
    try
    {
        var errorLog = ctx.RequestServices.GetService<IErrorLogService>();
        if (errorLog is not null)
        {
            ctx.Items[ErrorLoggedKey] = true;
            await errorLog.LogAsync(ex!, ctx, StatusCodes.Status500InternalServerError);
        }
    }
    catch (Exception logEx)
    {
        logger.LogError(logEx, "Could not record the unhandled exception in TBL_CIP_API_ERROR_LOG");
    }

    // A missing legacy CIP object is a deployment gap, not a code bug, so say so and
    // point at the probe instead of leaving the operator with a raw SQL message.
    var sqlNumber = (ex as Microsoft.Data.SqlClient.SqlException)?.Number;
    var message = ex?.Message ?? "Unexpected error.";
    if (sqlNumber is 2812 or 2715 or 2714 or 208 or 207)
        message = "This database is missing a legacy CIP object. Run DIAGNOSE-500.sql against this server to find which.";

    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
    ctx.Response.ContentType = "application/json";
    await ctx.Response.WriteAsJsonAsync(new { error = message, sqlErrorNumber = sqlNumber });
}));

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
