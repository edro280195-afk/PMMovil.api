using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Dapper;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

using PMMovil.Api.Attributes;
using PMMovil.Api.Data;
using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

[Authorize]
[RequiereModulo(MobileModuleIds.Review)]
[ApiController]
[Route("api/[controller]")]
public class RevisionController : ControllerBase
{
    private readonly RevisionService _revisionService;
    private readonly ILogger<RevisionController> _logger;
    private readonly BaseRepository _db;
    private readonly RevisionReportPdfService _reportPdfService;
    private readonly IReportShareService _reportShareService;
    private readonly IRevisionShareService _revisionShareService;
    private readonly IRealtimeNotificationService _notifications;

    public RevisionController(
        RevisionService revisionService,
        ILogger<RevisionController> logger,
        BaseRepository db,
        RevisionReportPdfService reportPdfService,
        IReportShareService reportShareService,
        IRevisionShareService revisionShareService,
        IRealtimeNotificationService notifications)
    {
        _revisionService = revisionService;
        _logger = logger;
        _db = db;
        _reportPdfService = reportPdfService;
        _reportShareService = reportShareService;
        _revisionShareService = revisionShareService;
        _notifications = notifications;
    }

    [HttpGet("server-time")]
    public IActionResult GetServerTime()
    {
        return Ok(new
        {
            success = true,
            serverTime = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff")
        });
    }

    // De uso interno por ahora -- ver RevisionReportPdfService. El correo
    // automático al cliente (RevisionReportService) sigue tal cual está.
    [HttpGet("reporte/{creferencia}.pdf")]
    public async Task<IActionResult> GetReportePdf(string creferencia)
    {
        try
        {
            (byte[]? pdf, string? error) = await _reportPdfService.GenerarPdfAsync(creferencia);
            if (pdf == null)
            {
                return NotFound(new
                {
                    success = false,
                    message = error ?? "No se pudo generar el reporte."
                });
            }
            return File(pdf, "application/pdf", $"reporte-revision-{creferencia}.pdf");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error generando el reporte PDF de {cref}", creferencia);
            return StatusCode(500, new
            {
                success = false,
                message = "No se pudo generar el reporte."
            });
        }
    }

    // "Ver reporte" en Monitor abría este PDF con window.open() directo,
    // sin sesión -- 401 siempre. Este endpoint arma un link de 20 minutos
    // que sí abre sin cabecera Authorization (ver PublicoController).
    [RequiereModulo(MobileModuleIds.Review, "Lectura", true, MobileModuleIds.SystemsMonitor)]
    [HttpPost("reporte/{creferencia}/link")]
    public async Task<IActionResult> GenerarLinkReporte(string creferencia)
    {
        try
        {
            string usuario = base.User.Identity?.Name ?? "Desconocido";
            (string token, DateTime expira) = await _reportShareService.GenerarLinkVivoAsync(creferencia, usuario);
            string url = $"{Request.Scheme}://{Request.Host}/api/publico/reporte/{token}.pdf";
            return Ok(new ReportShareLinkResponse { Success = true, Url = url, Expira = expira });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error generando link de reporte para {cref}", creferencia);
            return StatusCode(500, new ReportShareLinkResponse { Success = false, Message = "No se pudo generar el enlace" });
        }
    }

    // Genera el link público de "Seguimiento de revisión" (ver
    // RevisionShareService / PublicoController) -- vigente 72h, sin sesión
    // de PMMovil para abrirlo. Página SEPARADA de "Seguimiento de entrada"
    // (Entrada/{cref}/compartir): antes Revisión reusaba ese mismo endpoint
    // y por eso ambos botones de Compartir generaban el mismo enlace con el
    // mismo contenido (solo datos de entrada), aunque Entrada y Revisión
    // muestran información distinta.
    [HttpPost("{crefAgrupada}/compartir")]
    public async Task<IActionResult> Compartir(string crefAgrupada)
    {
        try
        {
            string usuario = base.User.Identity?.Name ?? "Desconocido";
            (string Token, DateTime Expira)? resultado = await _revisionShareService.GenerarLinkAsync(crefAgrupada, usuario);
            if (resultado == null)
            {
                return NotFound(new EntradaShareLinkResponse { Success = false, Message = "Referencia no encontrada" });
            }

            string url = $"{Request.Scheme}://{Request.Host}/api/publico/revision/{resultado.Value.Token}";
            return Ok(new EntradaShareLinkResponse
            {
                Success = true,
                Url = url,
                Expira = resultado.Value.Expira
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al generar link de seguimiento de revisión para {Cref}", crefAgrupada);
            return StatusCode(500, new EntradaShareLinkResponse { Success = false, Message = "Error al generar el enlace" });
        }
    }

    [HttpGet("pendientes")]
    public async Task<IActionResult> GetEntradasPendientes([FromQuery] string? filtro = "", [FromQuery] bool strict = false)
    {
        try
        {
            var (items, truncado) = await _revisionService.GetEntradasPendientesAsync(filtro ?? "", strict);
            return Ok(new
            {
                success = true,
                items,
                truncado
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en GetEntradasPendientes");
            return StatusCode(500, new
            {
                success = false,
                message = "Error interno del servidor"
            });
        }
    }

    [HttpGet("buscar/{crefAgrupada}")]
    public async Task<IActionResult> BuscarPorCrefAgrupada(string crefAgrupada)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(crefAgrupada))
            {
                return BadRequest(new { success = false, message = "crefAgrupada requerida" });
            }
            crefAgrupada = crefAgrupada.Substring(0, Math.Min(crefAgrupada.Length, 15)); // Limitar a 15 caracteres
            var item = await _revisionService.BuscarPorCreferenciaAsync(crefAgrupada.Trim());
            if (item == null)
            {
                return NotFound(new
                {
                    success = false,
                    message = $"La referencia {crefAgrupada.Trim()} no existe o no es una entrada agrupada."
                });
            }

            return Ok(new { success = true, item });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en BuscarPorCrefAgrupada({Cref})", crefAgrupada);
            return StatusCode(500, new
            {
                success = false,
                message = "Error interno del servidor"
            });
        }
    }

    /// <summary>
    /// Búsqueda para la pantalla de Revisión: escribe unos dígitos de la entrada o parte del cliente y devuelve las
    /// mejores coincidencias (8 por omisión) con su estado real. Con menos de 3 caracteres útiles no busca.
    /// </summary>
    [HttpGet("buscar-rapido")]
    public async Task<IActionResult> BuscarRapido(
        [FromQuery(Name = "q")] string? consulta,
        [FromQuery] int limit = EntrySearchRules.DefaultLimit,
        CancellationToken ct = default)
    {
        try
        {
            string texto = (consulta ?? string.Empty).Trim();
            if (texto.Length > 60)
            {
                texto = texto.Substring(0, 60);
            }

            BusquedaRapidaResultado resultado = await _revisionService.BuscarRapidoAsync(texto, limit, ct);
            return Ok(new
            {
                success = true,
                hits = resultado.Hits,
                total = resultado.Total,
                tolerante = resultado.Tolerante
            });
        }
        catch (OperationCanceledException)
        {
            // El equipo siguió escribiendo y abandonó la consulta anterior: no es un error del servidor.
            return StatusCode(499);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en BuscarRapido({Consulta})", consulta);
            return StatusCode(500, new
            {
                success = false,
                message = "Error interno del servidor"
            });
        }
    }

    [HttpGet("mis-activas")]
    public async Task<IActionResult> GetMisActivas()
    {
        try
        {
            string username = base.User.Identity?.Name ?? "";
            var items = await _revisionService.GetMisActivasAsync(username);
            return Ok(new
            {
                success = true,
                items
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en GetMisActivas");
            return StatusCode(500, new
            {
                success = false,
                message = "Error interno del servidor"
            });
        }
    }

    [HttpPost("ar-entradas")]
    public async Task<IActionResult> GetEntryArCards([FromBody] EntryArBatchRequest request)
    {
        if (request?.References == null)
        {
            return BadRequest(new { success = false, message = "Envía la lista de referencias." });
        }

        List<string> references = request.References
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Select(reference => reference.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (references.Count is < 1 or > 6 || references.Any(reference => reference.Length > 30))
        {
            return BadRequest(new
            {
                success = false,
                message = "La consulta debe incluir de una a seis referencias de hasta 30 caracteres."
            });
        }

        try
        {
            List<EntryArCardDto> items = await _revisionService.GetEntryArCardsAsync(references);
            return Ok(new { success = true, items });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error al consultar tarjetas AR de entradas");
            return StatusCode(500, new
            {
                success = false,
                message = "No se pudieron consultar las entradas para AR."
            });
        }
    }

    [HttpGet("facturas/{cref_agrupada}")]
    public async Task<IActionResult> GetFacturasEntrada(string cref_agrupada)
    {
        try
        {
            var items = await _revisionService.GetFacturasEntradaAsync(cref_agrupada);
            return Ok(new
            {
                success = true,
                items
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en GetFacturasEntrada para {cref}", cref_agrupada);
            return StatusCode(500, new
            {
                success = false,
                message = "Error al obtener facturas"
            });
        }
    }

    [HttpGet("factura/{cnofact}")]
    public async Task<IActionResult> GetRevisionFactura(string cnofact)
    {
        try
        {
            var data = await _revisionService.GetRevisionFacturaAsync(cnofact);
            if (data == null)
            {
                return NotFound(new
                {
                    success = false,
                    message = "Factura no encontrada"
                });
            }
            return Ok(new
            {
                success = true,
                data = data
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en GetRevisionFactura para {factura}", cnofact);
            return StatusCode(500, new
            {
                success = false,
                message = "Error al obtener detalle de factura"
            });
        }
    }

    [HttpGet("partidas/{cref_agrupada}")]
    public async Task<IActionResult> GetPartidasEntrada(string cref_agrupada)
    {
        try
        {
            var items = await _revisionService.GetPartidasEntradaAsync(cref_agrupada);
            return Ok(new
            {
                success = true,
                items
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en GetPartidasEntrada para {cref}", cref_agrupada);
            return StatusCode(500, new
            {
                success = false,
                message = "Error al obtener partidas"
            });
        }
    }

    [HttpGet("catalogos/unidades-comerciales")]
    public async Task<IActionResult> GetUnidadesComerciales()
    {
        try
        {
            var items = await _revisionService.GetUnidadesComercialesAsync();
            return Ok(new
            {
                success = true,
                items
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en GetUnidadesComerciales");
            return StatusCode(500, new
            {
                success = false,
                message = "Error al obtener unidades comerciales"
            });
        }
    }

    [RequiereModulo(MobileModuleIds.Review, "Lectura", true, MobileModuleIds.SystemsMonitor)]
    [HttpGet("entrada-completa/{cref_agrupada}")]
    public async Task<IActionResult> GetEntradaCompleta(string cref_agrupada)
    {
        try
        {
            var tFacturas = _revisionService.GetFacturasEntradaAsync(cref_agrupada);
            var tPartidas = _revisionService.GetPartidasEntradaAsync(cref_agrupada);
            var tObservacion = _revisionService.GetObservacionRevisionAsync(cref_agrupada);
            var tSegundosAcumulados = _revisionService.GetSegundosAcumuladosAsync(cref_agrupada);
            await Task.WhenAll(tFacturas, tPartidas, tObservacion, tSegundosAcumulados);

            return Ok(new
            {
                success = true,
                facturas = tFacturas.Result,
                partidas = tPartidas.Result,
                observacionRevision = tObservacion.Result,
                segundosAcumulados = tSegundosAcumulados.Result,
                serverTime = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff")
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en GetEntradaCompleta para {cref}", cref_agrupada);
            return StatusCode(500, new
            {
                success = false,
                message = "Error al obtener datos de la entrada"
            });
        }
    }

    [HttpPatch("factura/campo")]
    public async Task<IActionResult> GuardarCampoFactura([FromBody] GuardarCampoFacturaDto payload)
    {
        try
        {
            string cnofact = payload.cnofact;
            string campo = payload.campo;
            string valor = NormalizarValorCampo(payload.valor);
            string usuario = base.User.Identity?.Name ?? "Desconocido";

            bool success = await _revisionService.GuardarCampoFacturaAsync(cnofact, campo, valor, usuario);
            return Ok(new { success });
        }
        catch (ArgumentException argEx)
        {
            return BadRequest(new { success = false, message = argEx.Message });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en GuardarCampoFactura");
            return StatusCode(500, new { success = false, message = "Error al guardar campo" });
        }
    }

    /// <summary>Guarda UN, clase y grupo de embalaje del rombo, cada uno en su columna.</summary>
    [HttpPatch("entrada/hazmat")]
    public async Task<IActionResult> GuardarHazmatRevision([FromBody] GuardarHazmatRevisionDto payload)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(payload?.crefAgrupada))
            {
                return BadRequest(new { success = false, message = "Falta la referencia" });
            }

            // Las columnas son cortas a propósito; mejor rechazar que truncar en
            // silencio, que es justo como se ensució este campo históricamente.
            if ((payload.un?.Trim().Length ?? 0) > 15)
            {
                return BadRequest(new { success = false, message = "El número UN no cabe en 15 caracteres" });
            }
            if ((payload.clase?.Trim().Length ?? 0) > 4)
            {
                return BadRequest(new { success = false, message = "La clase no cabe en 4 caracteres" });
            }
            if ((payload.grupo?.Trim().Length ?? 0) > 3)
            {
                return BadRequest(new { success = false, message = "El grupo de embalaje no cabe en 3 caracteres" });
            }

            string usuario = base.User.Identity?.Name ?? "Desconocido";
            bool success = await _revisionService.GuardarHazmatRevisionAsync(
                payload.crefAgrupada, payload.un, payload.clase, payload.grupo, usuario);
            return Ok(new { success });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { success = false, message = exception.Message });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en GuardarHazmatRevision para {cref}", payload?.crefAgrupada);
            return StatusCode(500, new { success = false, message = "Error al guardar los datos de HazMat" });
        }
    }

    [HttpPatch("entrada/observacion")]
    public async Task<IActionResult> GuardarObservacionEntrada([FromBody] GuardarObservacionEntradaDto payload)
    {
        try
        {
            string usuario = base.User.Identity?.Name ?? "Desconocido";
            bool success = await _revisionService.GuardarObservacionRevisionAsync(payload.crefAgrupada, payload.valor, usuario);
            return Ok(new { success });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en GuardarObservacionEntrada para {cref}", payload?.crefAgrupada);
            return StatusCode(500, new { success = false, message = "Error al guardar la observación" });
        }
    }

    [HttpPatch("partida/campo")]
    public async Task<IActionResult> GuardarCampoProducto([FromBody] GuardarCampoPartidaDto payload)
    {
        try
        {
            int id = payload.id;
            string campo = payload.campo;
            string valor = NormalizarValorCampo(payload.valor);
            string usuario = base.User.Identity?.Name ?? "Desconocido";

            bool success = await _revisionService.GuardarCampoProductoAsync(id, campo, valor, usuario);
            return Ok(new { success });
        }
        catch (ArgumentException argEx)
        {
            return BadRequest(new { success = false, message = argEx.Message });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en GuardarCampoProducto");
            return StatusCode(500, new { success = false, message = "Error al guardar campo del producto" });
        }
    }

    [HttpPatch("partida/cantidad-comercial")]
    public async Task<IActionResult> GuardarCantidadComercial([FromBody] GuardarCantidadComercialDto payload)
    {
        try
        {
            string usuario = base.User.Identity?.Name ?? "Desconocido";
            bool success = await _revisionService.GuardarCantidadComercialAsync(
                payload.id,
                payload.cantidad,
                payload.unidad,
                usuario);
            return Ok(new { success });
        }
        catch (ArgumentException argEx)
        {
            return BadRequest(new { success = false, message = argEx.Message });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en GuardarCantidadComercial para partida {id}", payload?.id);
            return StatusCode(500, new { success = false, message = "Error al guardar cantidad comercial" });
        }
    }

    [HttpPost("partida/agregar")]
    public async Task<IActionResult> AgregarProducto([FromBody] AgregarProductoDto payload)
    {
        try
        {
            string cnofact = payload.cnofact;
            string cref = payload.cref;
            string usuario = base.User.Identity?.Name ?? "Desconocido";

            int id = await _revisionService.AgregarProductoAsync(cnofact, cref, usuario);
            return Ok(new { success = true, id });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en AgregarProducto");
            return StatusCode(500, new { success = false, message = "Error al agregar producto" });
        }
    }

    [HttpDelete("partida/{id}")]
    public async Task<IActionResult> EliminarProducto(int id)
    {
        try
        {
            string usuario = base.User.Identity?.Name ?? "Desconocido";
            bool success = await _revisionService.EliminarProductoAsync(id, usuario);
            return Ok(new { success });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en EliminarProducto");
            return StatusCode(500, new { success = false, message = "Error al eliminar producto" });
        }
    }

    [HttpPost("factura/aclarar-discrepancias")]
    [RequiereModulo(MobileModuleIds.Review, "Modificar")]
    public async Task<IActionResult> AclararDiscrepancias([FromBody] AccionFacturaDto payload)
    {
        try
        {
            string cnofact = payload.cnofact;
            string usuario = base.User.Identity?.Name ?? "Desconocido";

            bool success = await _revisionService.AclararDiscrepanciasAsync(cnofact, usuario);
            return Ok(new { success });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en AclararDiscrepancias");
            return StatusCode(500, new
            {
                success = false,
                message = "Error al aclarar discrepancias"
            });
        }
    }

    [HttpGet("factura/validar-hazmat/{cnofact}")]
    public async Task<IActionResult> ValidarHojasSeguridad(string cnofact)
    {
        try
        {
            var problemas = await _revisionService.ValidarHojasSeguridadAsync(cnofact);
            return Ok(new
            {
                success = true,
                problemas
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en ValidarHojasSeguridad");
            return StatusCode(500, new
            {
                success = false,
                message = "Error al validar seguridad HazMat"
            });
        }
    }

    [HttpGet("catalogos/paises")]
    public async Task<IActionResult> GetPaises()
    {
        try
        {
            var paises = await _revisionService.GetPaisesAsync();
            _logger.LogInformation("Catálogo de países cargado. Items={Count}", paises.Count());
            return Ok(new
            {
                success = true,
                items = paises
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en GetPaises");
            return StatusCode(500, new
            {
                success = false,
                message = "Error al obtener catálogo de países"
            });
        }
    }

    [HttpGet("catalogos/paises-frecuentes")]
    public async Task<IActionResult> GetPaisesFrecuentes()
    {
        try
        {
            var items = await _revisionService.GetPaisesFrecuentesAsync();
            _logger.LogInformation("Catálogo de países frecuentes cargado. Items={Count}", items.Count());
            return Ok(new
            {
                success = true,
                items
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en GetPaisesFrecuentes");
            return StatusCode(500, new
            {
                success = false,
                message = "Error al obtener países más usados"
            });
        }
    }

    [HttpPost("solicitar-desbloqueo")]
    [RequiereModulo(MobileModuleIds.Review, "Modificar")]
    public async Task<IActionResult> SolicitarDesbloqueo([FromBody] SolicitarDesbloqueoDto payload)
    {
        try
        {
            string cnofact = payload.cnofact;
            string motivo = payload.motivo;
            string usuario = base.User.Identity?.Name ?? "Desconocido";

            var (success, message, id) = await _revisionService.SolicitarDesbloqueoAsync(cnofact, motivo, usuario);
            if (!success)
            {
                return BadRequest(new { success = false, message });
            }

            try
            {
                using var conn = _db.CreateConnection();
                var supervisors = await conn.QueryAsync<string>(@"
SELECT DISTINCT TRIM(claveusuario)
FROM usuarios
WHERE inactivo = 0
  AND TRIM(COALESCE(claveusuario, '')) <> ''
  AND (UPPER(TRIM(COALESCE(area, ''))) IN ('HARDWARE', 'SOFTWARE')
       OR iddepto = @systemsDepartmentId)",
                    new { systemsDepartmentId = ModulePermissionService.SystemsDepartmentId });

                string? crefAgrupada = cnofact.StartsWith("ENTRY_", StringComparison.OrdinalIgnoreCase)
                    ? cnofact[6..]
                    : await conn.QueryFirstOrDefaultAsync<string>(
                        "SELECT cref_agrupada FROM fact WHERE cnofact = @cnofact LIMIT 1",
                        new { cnofact });
                var data = new Dictionary<string, string>
                {
                    ["type"] = "solicitud_desbloqueo",
                    ["cnofact"] = cnofact,
                    ["solicitudId"] = id.ToString()
                };
                if (!string.IsNullOrWhiteSpace(crefAgrupada)) data["crefAgrupada"] = crefAgrupada;

                foreach (string supervisor in supervisors.Where(name =>
                    !name.Equals(usuario, StringComparison.OrdinalIgnoreCase)))
                {
                    await _notifications.SendToUserOrInboxAsync(
                        supervisor,
                        "Permiso de revisión solicitado",
                        $"{usuario} solicita editar {cnofact}: {motivo}",
                        data);
                }
            }
            catch (Exception exception)
            {
                _logger.LogError(exception,
                    "La solicitud {Id} quedó registrada, pero no se pudo avisar al centro de control.", id);
            }

            return Ok(new
            {
                success = true,
                id
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en SolicitarDesbloqueo");
            return StatusCode(500, new
            {
                success = false,
                message = "Error al enviar solicitud de desbloqueo"
            });
        }
    }

    [HttpGet("verificar-desbloqueo/{cnofact}")]
    public async Task<IActionResult> VerificarDesbloqueo(string cnofact)
    {
        try
        {
            dynamic data = await _revisionService.VerificarDesbloqueoAsync(cnofact);
            return Ok(new
            {
                success = true,
                autorizado = (object)(data != null),
                autorizadoPor = (object)data?.AutorizadoPor,
                fechaExpiracion = (object)data?.FechaExpiracion
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en VerificarDesbloqueo");
            return StatusCode(500, new
            {
                success = false,
                message = "Error al verificar estatus de desbloqueo"
            });
        }
    }

    [HttpGet("validar-cierre/{creferencia}")]
    public async Task<IActionResult> ValidarEntradaAntesDeCerrar(string creferencia)
    {
        try
        {
            var facturasSinRevisor = await _revisionService.ValidarEntradaAntesDeCerrarAsync(creferencia);
            return Ok(new
            {
                success = true,
                facturasSinRevisor
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en ValidarEntradaAntesDeCerrar");
            return StatusCode(500, new
            {
                success = false,
                message = "Error al validar la entrada"
            });
        }
    }

    [Authorize]
    [HttpPost("entrada/asignar-revisores/{creferencia}")]
    [RequiereModulo(MobileModuleIds.Review, "Modificar")]
    public async Task<IActionResult> AsignarRevisoresPendientes(string creferencia)
    {
        try
        {
            string usuario = base.User.Identity?.Name ?? "Desconocido";
            await _revisionService.AsignarRevisoresPendientesAsync(creferencia, usuario);
            return Ok(new
            {
                success = true
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en AsignarRevisoresPendientes");
            return StatusCode(500, new
            {
                success = false,
                message = "Error al asignar revisores a facturas pendientes."
            });
        }
    }

    [HttpPost("factura/limpiar-revisador")]
    [RequiereModulo(MobileModuleIds.Review, "Modificar")]
    public async Task<IActionResult> LimpiarUsurev([FromBody] AccionFacturaDto payload)
    {
        try
        {
            string cnofact = payload.cnofact;
            string usuario = base.User.Identity?.Name ?? "Desconocido";
            bool success = await _revisionService.LimpiarUsurevAsync(cnofact, usuario);
            return Ok(new
            {
                success
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en LimpiarUsurev");
            return StatusCode(500, new
            {
                success = false,
                message = "Error al limpiar revisor"
            });
        }
    }

    [HttpGet("usuario/lugar")]
    public async Task<IActionResult> GetLugarUsuario()
    {
        try
        {
            string usuario = base.User.Identity?.Name ?? "";
            string lugar = await _revisionService.GetLugarUsuarioAsync(usuario);
            return Ok(new
            {
                success = true,
                lugar = lugar
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en GetLugarUsuario");
            return StatusCode(500, new
            {
                success = false,
                message = "Error al obtener lugar del usuario"
            });
        }
    }

    [HttpPost("registrar-actividad/{creferencia}")]
    [RequiereModulo(MobileModuleIds.Review, "Modificar")]
    public async Task<IActionResult> RegistrarTomaDocumento(string creferencia)
    {
        try
        {
            string usuario = base.User.Identity?.Name ?? "Desconocido";
            await _revisionService.RegistrarTomaDocumentoAsync(creferencia, usuario);
            return Ok(new
            {
                success = true
            });
        }
        catch (RevisionEnUsoException exception)
        {
            return Conflict(new
            {
                success = false,
                message = exception.Message
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en RegistrarTomaDocumento");
            return StatusCode(500, new
            {
                success = false,
                message = "Error al registrar actividad"
            });
        }
    }

    [HttpPost("factura/registrar-editor")]
    [RequiereModulo(MobileModuleIds.Review, "Modificar")]
    public async Task<IActionResult> RegistrarEditorFactura([FromBody] AccionFacturaDto payload)
    {
        try
        {
            string cnofact = payload.cnofact;
            string usuario = base.User.Identity?.Name ?? "Desconocido";
            bool success = await _revisionService.RegistrarEditorFacturaAsync(cnofact, usuario);
            return Ok(new
            {
                success
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en RegistrarEditorFactura");
            return StatusCode(500, new
            {
                success = false,
                message = "Error al registrar editor"
            });
        }
    }

    [HttpPost("factura/limpiar-editor")]
    [RequiereModulo(MobileModuleIds.Review, "Modificar")]
    public async Task<IActionResult> LimpiarEditorFactura([FromBody] AccionFacturaDto payload)
    {
        try
        {
            string cnofact = payload.cnofact;
            bool success = await _revisionService.LimpiarEditorFacturaAsync(cnofact);
            return Ok(new
            {
                success
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en LimpiarEditorFactura");
            return StatusCode(500, new
            {
                success = false,
                message = "Error al limpiar editor"
            });
        }
    }

    [HttpPost("entrada/limpiar-editores/{cref}")]
    [RequiereModulo(MobileModuleIds.Review, "Modificar")]
    public async Task<IActionResult> LimpiarEditoresEntrada(string cref)
    {
        try
        {
            await _revisionService.LimpiarEditoresEntradaAsync(cref);
            return Ok(new
            {
                success = true
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error en LimpiarEditoresEntrada");
            return StatusCode(500, new
            {
                success = false,
                message = "Error al limpiar editores de la entrada"
            });
        }
    }

    private static string NormalizarValorCampo(JsonElement valor)
    {
        return valor.ValueKind switch
        {
            JsonValueKind.True => "1",
            JsonValueKind.False => "0",
            JsonValueKind.Null => string.Empty,
            _ => valor.ToString()
        };
    }
}
