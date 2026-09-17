using System.Net;
using System.Text.Json;

using PMMovil.Api.Models;

namespace PMMovil.Api.Middleware;

public class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException)
            when (context.RequestAborted.IsCancellationRequested)
        {
            // El cliente cerró, canceló o reemplazó la solicitud. Esto es
            // normal cuando Android/Angular cambia de pantalla o repite una
            // consulta; no es un error 500 del API y no debe intentar escribir
            // una respuesta sobre una conexión que ya no existe.
            _logger.LogInformation(
                "Solicitud cancelada por el cliente: {Method} {Path}. RequestId={RequestId}",
                context.Request.Method,
                context.Request.Path,
                context.TraceIdentifier);
        }
        catch (OperationCanceledException exception)
        {
            // Si la cancelación no provino del cliente, sí necesitamos dejar
            // evidencia y responder como indisponibilidad temporal, no como
            // una excepción interna genérica.
            _logger.LogError(
                exception,
                "Operación cancelada dentro del API: {Method} {Path}. RequestId={RequestId}",
                context.Request.Method,
                context.Request.Path,
                context.TraceIdentifier);

            if (!context.Response.HasStarted)
            {
                context.Response.ContentType = "application/json";
                context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(new ApiResponse
                {
                    Success = false,
                    Message = "La operación fue cancelada temporalmente. Intenta de nuevo.",
                    StatusCode = (int)HttpStatusCode.ServiceUnavailable
                }, cancellationToken: CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception processing {Method} {Path}",
           context.Request.Method, context.Request.Path);

            context.Response.ContentType = "application/json";
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;

            var response = new ApiResponse
            {
                Success = false,
                Message = "Error interno del servidor",
                StatusCode = 500
            };

            var json = JsonSerializer.Serialize(response, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            await context.Response.WriteAsync(json);
        }
    }
}
