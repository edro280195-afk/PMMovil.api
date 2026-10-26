using System.Text;
using System.Text.Json;

using Dapper;

using FluentValidation;
using FluentValidation.AspNetCore;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

using PMMovil.Api.Attributes;
using PMMovil.Api.Data;
using PMMovil.Api.Hubs;
using PMMovil.Api.Middleware;
using PMMovil.Api.Services;

using Polly;

using QuestPDF.Infrastructure;

using Serilog;

// QuestPDF exige declarar el tipo de licencia antes de generar cualquier
// documento -- Community es gratis para una empresa de este tamaño. Sin
// esta linea, RevisionReportPdfService truena en tiempo de ejecucion.
QuestPDF.Settings.License = LicenseType.Community;

string logFilePath = ResolveLogFilePath();
string? logDirectory = Path.GetDirectoryName(logFilePath);
if (!string.IsNullOrWhiteSpace(logDirectory))
{
    Directory.CreateDirectory(logDirectory);
}

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File(
        logFilePath,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        shared: true,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Registrar el conversor antes de construir servicios o atender workers.
    // MySQL puede entregar fechas inválidas como MySqlDateTime; Dapper debe
    // conocer este handler desde el inicio para mapear correctamente DateTime?.
    SqlMapper.AddTypeHandler(new MySqlDateTimeHandler());

    builder.Host.UseSerilog((context, services, configuration) =>
    {
        configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(
            logFilePath,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30,
            shared: true,
            flushToDiskInterval: TimeSpan.FromSeconds(1),
            outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj}{NewLine}{Exception}")
        .Enrich.FromLogContext();
    });

    // El despliegue existente de PMMovil conserva los secretos operativos en
    // appsettings.Local.json. Se carga también en Production para mantener la
    // compatibilidad con ese esquema; el archivo debe protegerse en el
    // servidor y nunca exponerse al navegador.
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
    ValidateProductionConfiguration(builder);

    builder.Services.AddFluentValidationAutoValidation();

    builder.Services.AddControllers()
   .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    });

    builder.Services.AddDataProtection()
        .SetApplicationName("PMMovil.Api");

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new OpenApiInfo { Title = "PMMovil API", Version = "v1" });
        c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Description = "JWT Authorization header using the Bearer scheme. Example: \"Authorization: Bearer {token}\"",
            Name = "Authorization",
            In = ParameterLocation.Header,
            Type = SecuritySchemeType.ApiKey,
            Scheme = "Bearer"
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

    builder.Services.AddValidatorsFromAssemblyContaining<Program>();

    builder.Services.AddScoped<IAuthService, AuthService>();
    builder.Services.AddScoped<IModulePermissionService, ModulePermissionService>();
    builder.Services.AddScoped<BaseRepository>();
    builder.Services.AddScoped<ISmtpService, SmtpService>();
    builder.Services.AddScoped<IRealtimeNotificationService, RealtimeNotificationService>();
    builder.Services.AddScoped<IRevisionReportService, RevisionReportService>();
    builder.Services.AddScoped<EntradaService>();
    builder.Services.AddScoped<IEntradaShareService, EntradaShareService>();
    builder.Services.AddScoped<IRevisionShareService, RevisionShareService>();
    builder.Services.AddScoped<IReportShareService, ReportShareService>();
    builder.Services.AddScoped<IMonitorTvLinkService, MonitorTvLinkService>();
    builder.Services.AddSingleton<MonitorTvRealtimePublisher>();
    builder.Services.AddScoped<SalidaService>();
    builder.Services.AddScoped<CasetaService>();
    builder.Services.AddScoped<SalidaCasetaService>();
    builder.Services.AddScoped<CartaPorteService>();
    builder.Services.AddScoped<PrevioService>();
    builder.Services.AddScoped<ImexService>();
    builder.Services.AddScoped<InventarioService>();
    builder.Services.AddScoped<ProveedorService>();
    builder.Services.AddScoped<ReportService>();
    builder.Services.AddScoped<RevisionActivityService>();
    builder.Services.AddScoped<RevisionAuditService>();
    builder.Services.AddScoped<RevisionService>();
    builder.Services.AddScoped<LabelCaptureService>();
    builder.Services.AddScoped<EtiquetaGrupoClienteService>();
    builder.Services.AddScoped<RevisionNormalCompletionService>();
    builder.Services.AddScoped<TrazabilidadService>();
    builder.Services.AddScoped<RevisionReportPdfService>();
    builder.Services.AddScoped<WarehouseLayoutService>();
    builder.Services.AddScoped<ClaseModelService>();
    builder.Services.AddScoped<ZebraPrinterCatalogService>();
    builder.Services.AddScoped<BodegaDestinoService>();
    builder.Services.AddScoped<PreregistroService>();
    builder.Services.AddScoped<MetricasRevisionService>();
    builder.Services.AddScoped<IRouteEtaService, GoogleRoutesEtaService>();
    // Laboratorio biometrico facial: banco de pruebas, no modulo de produccion.
    // El controller se apaga solo fuera de Development (ver BiometriaLabController).
    builder.Services.AddScoped<BiometriaLabService>();
    builder.Services.AddScoped<FaceService>();
    builder.Services.AddSingleton<CasetaPhotoStorage>();
    builder.Services.AddMemoryCache();
    builder.Services.AddSignalR();

    builder.Services.AddHttpClient("GeminiClient")
   .AddTransientHttpErrorPolicy(policy => policy.WaitAndRetryAsync(3, _ => TimeSpan.FromSeconds(2)));

    builder.Services.AddHttpClient("GoogleRoutesClient", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(8);
    }).AddTransientHttpErrorPolicy(policy => policy.WaitAndRetryAsync(2, _ => TimeSpan.FromSeconds(1)));

    builder.Services.AddRateLimiter(options =>
    {
        options.OnRejected = (context, _) =>
        {
            var httpContext = context.HttpContext;
            var logger = httpContext.RequestServices.GetRequiredService<ILogger<Program>>();
            TimeSpan? retryAfter = null;

            if (context.Lease.TryGetMetadata(
                    System.Threading.RateLimiting.MetadataName.RetryAfter,
                    out TimeSpan retryAfterValue))
            {
                retryAfter = retryAfterValue;
                httpContext.Response.Headers.RetryAfter =
                    Math.Ceiling(retryAfterValue.TotalSeconds)
                        .ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            logger.LogWarning(
                "Rate limit rechazó la solicitud {Method} {Path}. IP={RemoteIp}, UserAgent={UserAgent}, RetryAfterSeconds={RetryAfterSeconds}, RequestId={RequestId}",
                httpContext.Request.Method,
                httpContext.Request.Path,
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "N/D",
                httpContext.Request.Headers.UserAgent.ToString(),
                retryAfter?.TotalSeconds,
                httpContext.TraceIdentifier);

            return ValueTask.CompletedTask;
        };

        options.AddFixedWindowLimiter("LoginPolicy", opt =>
        {
            opt.PermitLimit = 10;
            opt.Window = TimeSpan.FromMinutes(1);
            opt.QueueLimit = 0;
        });
        // Acceso facial: anonimo y con una busqueda 1:N detras, asi que se
        // limita POR IP y no de forma global -- una tableta atorada no debe
        // dejar sin entrar a las demas. Dos intentos por presencia dan de
        // sobra para varias personas por minuto en el mismo equipo.
        options.AddPolicy("RostroPolicy", context =>
            System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "sin-ip",
                factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                {
                    PermitLimit = 20,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));
        // Portal publico (sin autenticacion previa): limites mas agresivos
        // que el login interno, es la unica superficie del API expuesta a
        // internet. Por IP -- ver PartitionedRateLimiter mas abajo.
        options.AddPolicy("PreregistroLoginPolicy", context =>
            System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "sin-ip",
                factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));
        options.AddPolicy("PreregistroCrearPolicy", context =>
            System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "sin-ip",
                factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                {
                    PermitLimit = 20,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));
        options.AddPolicy("PreregistroPortalPolicy", context =>
            System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "sin-ip",
                factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                {
                    PermitLimit = 120,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));
        // Enlaces públicos de "Ver reporte" / "Seguimiento de entrada" /
        // "Seguimiento de revisión" (PublicoController), por IP. Una sola persona abriendo su enlace
        // y viendo varias fotos no debería acercarse a este límite, pero sí
        // frena un scraping o fuerza bruta automatizado desde la misma IP.
        options.AddPolicy("PublicoLinksPolicy", context =>
            System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "sin-ip",
                factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    });

    bool disableBackgroundWorkers = builder.Configuration.GetValue<bool>("DisableBackgroundWorkers");
    if (!disableBackgroundWorkers)
    {
        builder.Services.AddHostedService<AutoFinalizerService>();
        builder.Services.AddHostedService<RevisionHygieneService>();
        builder.Services.AddHostedService<CasetaPhotoSyncWorker>();
        builder.Services.AddHostedService<PreregistroPurgeWorker>();
        builder.Services.AddHostedService<PreregistroClosureWorker>();
    }

    var jwtSettings = builder.Configuration.GetSection("Jwt");
    var key = Encoding.UTF8.GetBytes(jwtSettings["Secret"] ?? throw new InvalidOperationException("JWT Secret not found"));

    builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.SaveToken = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSettings["Issuer"],
            ValidAudience = jwtSettings["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(key)
        };
        // Los navegadores no pueden mandar el header Authorization en el
        // handshake de un WebSocket ni en una conexion SSE -- el cliente de
        // SignalR manda el token como query string (?access_token=...) para
        // esos dos transportes. Sin este handler, esa conexion responde 401,
        // SignalR cae de vuelta a LongPolling, y todo el tiempo real del
        // Monitor de revisadores queda mas lento de lo necesario.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                string? accessToken = context.HttpContext.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    })
    // Esquema separado para el portal de pre-registro (lineas transportistas).
    // Secreto y audiencia PROPIOS (PreregistroJwt en appsettings.Local.json):
    // un token del portal falla la validacion de firma/audiencia en CUALQUIER
    // endpoint que use el esquema por defecto (Bearer) -- es decir, en todo
    // el resto del API -- sin depender de logica adicional. Los controllers
    // del portal usan [Authorize(AuthenticationSchemes = "Portal")]
    // explicitamente; ningun endpoint interno existente cambia.
    .AddJwtBearer("Portal", options =>
    {
        var portalJwtSettings = builder.Configuration.GetSection("PreregistroJwt");
        var portalSecret = portalJwtSettings["Secret"];
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.SaveToken = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = portalJwtSettings["Issuer"],
            ValidAudience = portalJwtSettings["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(portalSecret)
                    ? throw new InvalidOperationException("PreregistroJwt:Secret no configurado. Ver appsettings.Local.json.")
                    : portalSecret))
        };
    });

    var configuredOrigins = builder.Configuration
        .GetSection("Cors:AllowedOrigins")
        .Get<string[]>() ?? Array.Empty<string>();
    var allowedOrigins = new[]
    {
        "http://localhost:4200",
        "http://localhost:4201",
        "http://127.0.0.1:4200",
        "http://127.0.0.1:4201",
        "http://localhost",
        "https://localhost",
        "capacitor://localhost",
        "https://revision.grupopmm.com",
        "http://revision.grupopmm.com",
        "http://192.168.10.29:85"
    }
    .Concat(configuredOrigins)
    .Select(origin => origin.Trim().TrimEnd('/'))
    .Where(origin => Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri)
                     && !string.IsNullOrWhiteSpace(uri.Scheme)
                     && !string.IsNullOrWhiteSpace(uri.Host))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("PMMovilCors", policy =>
        {
            policy.WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials();
        });
    });

    var app = builder.Build();

    // Modo admin de un solo comando: reenviar el correo de una entrada de caseta
    // ya guardada (caso "no me llegó el correo"), sin pasar por HTTP/JWT.
    // Reutiliza el mismo contenedor de DI que arma el resto de este archivo, así
    // que usa exactamente la misma configuración/cadena de conexión/SMTP que la
    // API real según el entorno con el que se corra este proceso. No arranca
    // Kestrel ni mapea ningún endpoint.
    //   dotnet run -- --reenviar-correo-caseta <casetaId> <soloValidacion:true|false>
    if (args.Length >= 1 && args[0] == "--reenviar-correo-caseta")
    {
        string casetaIdArg = args.Length > 1 ? args[1] : "";
        bool soloValidacionArg = args.Length <= 2 || !bool.TryParse(args[2], out bool parsedSoloValidacion) || parsedSoloValidacion;

        using IServiceScope scope = app.Services.CreateScope();
        var casetaService = scope.ServiceProvider.GetRequiredService<CasetaService>();
        (bool success, string mensaje) = await casetaService.ReenviarCorreoEntradaAsync(
            casetaIdArg, soloValidacionArg, "sistema (reenvio manual)", CancellationToken.None);

        Console.WriteLine(success ? $"OK: {mensaje}" : $"ERROR: {mensaje}");
        return;
    }

    // Mismo modo admin, para localizar el casetaid antes de reenviar el correo:
    //   dotnet run -- --buscar-por-caja <nequipo>
    if (args.Length >= 1 && args[0] == "--buscar-por-caja")
    {
        string nequipoArg = args.Length > 1 ? args[1] : "";
        using IServiceScope scope = app.Services.CreateScope();
        var casetaService = scope.ServiceProvider.GetRequiredService<CasetaService>();
        var items = await casetaService.BuscarEntradasPorCajaAsync(nequipoArg, CancellationToken.None);

        if (items.Count == 0)
        {
            Console.WriteLine($"Sin resultados para la caja '{nequipoArg}'.");
        }
        else
        {
            foreach (var item in items)
            {
                Console.WriteLine(
                    $"CasetaId={item.CasetaId} | Nequipo={item.Nequipo} | Planta={item.Planta} | " +
                    $"TipoOp={item.TipoOp} | TipoMov={item.TipoMov} | Cliente={item.IdCliente}-{item.NombreCliente} | " +
                    $"Farribo={item.Farribo:yyyy-MM-dd HH:mm}");
            }
        }
        return;
    }

    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate =
            "HTTP {RequestMethod} {RequestPath} respondió {StatusCode} en {Elapsed:0.0000} ms. RequestId={RequestId}";
        options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        {
            diagnosticContext.Set("RequestId", httpContext.TraceIdentifier);
            diagnosticContext.Set("User", httpContext.User.Identity?.Name ?? "N/D");
        };
    });

    // CORS debe ejecutarse antes de los middlewares que pueden devolver errores.
    // Así el navegador no convierte un 4xx/5xx de la API en un error engañoso de CORS.
    app.UseCors("PMMovilCors");
    app.UseMiddleware<SecurityHeadersMiddleware>();
    app.UseMiddleware<GlobalExceptionMiddleware>();

    // Swagger/OpenAPI solo en Development -- en producción quedaba en
    // /swagger/index.html sin autenticación, regalando el mapa completo de
    // rutas y modelos internos a cualquiera en internet (verificado en vivo
    // el 2026-09-28). Todo endpoint sigue detrás de [Authorize] de todas
    // formas, pero no hace falta facilitarle el reconocimiento a nadie.
    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseStaticFiles();
    if (!app.Environment.IsDevelopment())
    {
        app.UseHsts();
        app.UseHttpsRedirection();
    }
    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllers();
    app.MapHub<WarehouseHub>("/hubs/warehouse");
    app.MapHub<MonitorTvHub>("/hubs/monitor-tv").AllowAnonymous();
    app.MapHub<BodegaHub>("/hubs/bodega").AllowAnonymous();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

static string ResolveLogFilePath()
{
    string? configuredPath = Environment.GetEnvironmentVariable("PMMOVIL_LOG_PATH");
    if (!string.IsNullOrWhiteSpace(configuredPath))
    {
        return configuredPath;
    }

    const string productionLogDirectory = @"C:\inetpub\wwwroot\pmmovil\_api\Logs";
    if (Directory.Exists(productionLogDirectory))
    {
        return Path.Combine(productionLogDirectory, "pmmovil-api-.log");
    }

    // En desarrollo no se debe depender de que exista IIS ni de permisos
    // sobre C:\inetpub. El log queda junto al ejecutable para poder leerlo
    // desde Android Studio o desde el explorador de archivos.
    return Path.Combine(AppContext.BaseDirectory, "Logs", "pmmovil-api-.log");
}

static void ValidateProductionConfiguration(WebApplicationBuilder builder)
{
    if (!builder.Environment.IsProduction())
        return;

    // La configuración puede venir de variables de entorno o de los archivos
    // appsettings.json/appsettings.Local.json del despliegue existente.
    string[] requiredConfigurationKeys =
    [
        "ConnectionStrings:ReportesDb",
        "ConnectionStrings:PmmtrafDb",
        "ConnectionStrings:PmmtoolsDb",
        "Jwt:Secret",
        "PreregistroJwt:Secret",
        "Smtp:Password",
        "PushNotifications:Vapid:PrivateKey",
        "DashboardApiKey",
        "GeminiApiKey"
    ];

    string[] missing = requiredConfigurationKeys
        .Where(key => string.IsNullOrWhiteSpace(builder.Configuration[key]))
        .ToArray();

    if (missing.Length > 0)
    {
        throw new InvalidOperationException(
            $"Configuracion de produccion incompleta. Faltan claves de configuracion: {string.Join(", ", missing)}.");
    }

    string[] shortSecrets = new[] { "Jwt:Secret", "PreregistroJwt:Secret" }
        .Where(key => (builder.Configuration[key]?.Length ?? 0) < 32)
        .ToArray();

    if (shortSecrets.Length > 0)
    {
        throw new InvalidOperationException(
            $"Los secretos JWT de produccion deben tener al menos 32 caracteres: {string.Join(", ", shortSecrets)}.");
    }
}
