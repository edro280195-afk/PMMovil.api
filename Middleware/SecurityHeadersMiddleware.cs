namespace PMMovil.Api.Middleware;

/// <summary>
/// Encabezados de endurecimiento que aplican a TODAS las respuestas del API,
/// para no depender de que cada controller nuevo se acuerde de ponerlos.
/// Los enlaces públicos con token (ver PublicoController.SetPublicShareHeaders)
/// siguen fijando los suyos propios más estrictos (Cache-Control/Referrer-Policy)
/// caso por caso -- TryAdd aquí abajo respeta esos valores en vez de pisarlos.
///
/// Server/X-Powered-By: hoy los agrega IIS a nivel nativo (Microsoft-IIS/10.0,
/// ASP.NET), fuera del pipeline administrado -- quitarlos del todo requiere
/// "removeServerHeader" y &lt;customHeaders&gt; en web.config del sitio en IIS,
/// no solo aquí. Se intentan quitar de todas formas por si el hosting cambia.
/// </summary>
public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            IHeaderDictionary headers = context.Response.Headers;

            headers.Remove("Server");
            headers.Remove("X-Powered-By");

            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers.TryAdd("Referrer-Policy", "strict-origin-when-cross-origin");

            return Task.CompletedTask;
        });

        return _next(context);
    }
}
