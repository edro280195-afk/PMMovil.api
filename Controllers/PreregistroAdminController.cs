using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using PMMovil.Api.Attributes;
using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

/// <summary>
/// Administracion de cuentas del portal de pre-registro (alta, baja, reseteo
/// de contrasena, auditoria). Uso interno de trafico/sistemas -- esquema JWT
/// por defecto (nunca el esquema "Portal"), protegido por el modulo nuevo 163.
/// </summary>
[Authorize]
[RequiereModulo(MobileModuleIds.TransporterPreRegistration)]
[ApiController]
[Route("api/[controller]")]
public class PreregistroAdminController : ControllerBase
{
    private readonly PreregistroService _service;
    private readonly ILogger<PreregistroAdminController> _logger;

    public PreregistroAdminController(PreregistroService service, ILogger<PreregistroAdminController> logger)
    {
        _service = service;
        _logger = logger;
    }

    private string ObtenerUsuario() => User.Identity?.Name ?? "sistema";

    [HttpGet("cuentas")]
    public async Task<IActionResult> ListarCuentas()
    {
        return Ok(await _service.ListarCuentasAsync());
    }

    [HttpGet("registros")]
    public async Task<IActionResult> ListarRegistros([FromQuery] string? filtro = null)
    {
        return Ok(await _service.ListarRegistrosAdminAsync(filtro));
    }

    /// <summary>Catálogo de líneas transportistas para el combo de "Nueva cuenta".</summary>
    [HttpGet("lineas-transporte")]
    public async Task<IActionResult> LineasTransporte()
    {
        return Ok(await _service.ObtenerLineasTransporteAsync());
    }

    [HttpPost("cuentas")]
    [RequiereModulo(MobileModuleIds.TransporterPreRegistration, "Agregar")]
    public async Task<IActionResult> CrearCuenta([FromBody] PreregistroCuentaCrearRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var result = await _service.CrearCuentaAsync(request, ObtenerUsuario());
        if (!result.Success)
            return BadRequest(result);

        return Ok(result);
    }

    [HttpPost("cuentas/{id}/reset-password")]
    [RequiereModulo(MobileModuleIds.TransporterPreRegistration, "Modificar")]
    public async Task<IActionResult> ResetPassword(int id)
    {
        var result = await _service.ResetPasswordAsync(id, ObtenerUsuario());
        if (!result.Success)
            return NotFound(result);

        return Ok(result);
    }

    [HttpPost("cuentas/{id}/desactivar")]
    [RequiereModulo(MobileModuleIds.TransporterPreRegistration, "Modificar")]
    public async Task<IActionResult> Desactivar(int id)
    {
        bool ok = await _service.CambiarEstadoCuentaAsync(id, activo: false, ObtenerUsuario());
        if (!ok)
            return NotFound(new { message = "Cuenta no encontrada." });

        return Ok(new { success = true, message = "Cuenta desactivada." });
    }

    [HttpPost("cuentas/{id}/reactivar")]
    [RequiereModulo(MobileModuleIds.TransporterPreRegistration, "Modificar")]
    public async Task<IActionResult> Reactivar(int id)
    {
        bool ok = await _service.CambiarEstadoCuentaAsync(id, activo: true, ObtenerUsuario());
        if (!ok)
            return NotFound(new { message = "Cuenta no encontrada." });

        return Ok(new { success = true, message = "Cuenta reactivada." });
    }

    [HttpGet("auditoria")]
    public async Task<IActionResult> Auditoria([FromQuery] PreregistroAuditoriaFiltro filtro)
    {
        return Ok(await _service.ObtenerAuditoriaAsync(filtro));
    }

    [HttpGet("auditoria/pagina")]
    public async Task<IActionResult> AuditoriaPagina([FromQuery] GaritaAuditoriaFiltro filtro, CancellationToken ct) =>
        Ok(await _service.ObtenerAuditoriaPaginaAsync(filtro, ct));

    [HttpGet("auditoria/exportar")]
    public async Task<IActionResult> ExportarAuditoria([FromQuery] GaritaAuditoriaFiltro filtro, CancellationToken ct) =>
        File(await _service.ExportarAuditoriaAsync(filtro, ct), "text/csv; charset=utf-8", "auditoria-preregistro.csv");

    [HttpGet("ficha")]
    public async Task<IActionResult> Ficha([FromQuery] string folio, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(folio) || folio.Length > 24) return BadRequest(new { message = "Folio inválido." });
        var ficha = await _service.ObtenerFichaAsync(folio.Trim(), ct);
        return ficha is null ? NotFound(new { message = "Folio no encontrado." }) : Ok(ficha);
    }

    // Los folios incluyen '/'; por query también funcionan detrás de IIS.
    [HttpGet("recorrido")]
    public async Task<IActionResult> RecorridoPorQuery([FromQuery] string folio, [FromQuery] int limite = 500, CancellationToken ct = default) =>
        Ok(await _service.ObtenerRecorridoAsync(folio, limite, ct));

    /// <summary>
    /// Recorrido completo de un folio: las migas que dejo el chofer mientras
    /// venia. Es lo que alimenta el rastro del mapa y la pestana "Recorrido"
    /// de la ficha. Area interna, mismo modulo 163 que el resto del panel.
    /// </summary>
    [HttpGet("registros/{folio}/recorrido")]
    public async Task<IActionResult> Recorrido(string folio, [FromQuery] int limite = 500)
    {
        return Ok(await _service.ObtenerRecorridoAsync(folio, limite));
    }

    /// <summary>
    /// Camiones en camino para el mapa del panel.
    ///
    /// A proposito NO se reusa <c>Caseta/preregistro/en-camino</c>, que pide el
    /// modulo 12 (Gate): un usuario de trafico con acceso a pre-registro (163)
    /// no necesariamente tiene acceso a Caseta, y con el endpoint de garita el
    /// mapa le tronaba con un 403 silencioso. Mismo criterio que ya se habia
    /// usado con el catalogo de lineas transportistas.
    /// </summary>
    [HttpGet("en-camino")]
    public async Task<IActionResult> EnCamino([FromQuery] string? idBodega = null)
    {
        try
        {
            return Ok(await _service.EnCaminoAsync(idBodega));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obteniendo camiones en camino para el panel");
            return StatusCode(500, new { message = "No se pudieron consultar los camiones en camino." });
        }
    }

    /// <summary>Catálogo de destinos operativos editable por tráfico.</summary>
    [HttpGet("bodegas")]
    public async Task<IActionResult> Bodegas(CancellationToken ct) => Ok(await _service.ListarBodegasAsync(false, ct));

    [HttpPut("bodegas/{idBodega}")]
    [RequiereModulo(MobileModuleIds.TransporterPreRegistration, "Modificar")]
    public async Task<IActionResult> GuardarBodega(
        string idBodega,
        [FromBody] PreregistroBodegaGuardarRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);
        if (!string.Equals(idBodega.Trim(), request.IdBodega.Trim(), StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "El código de la ruta no coincide con el código enviado." });

        try
        {
            return Ok(await _service.GuardarBodegaAsync(request, ObtenerUsuario(), ct));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error actualizando la bodega {IdBodega}", idBodega);
            return StatusCode(500, new { message = "No se pudo guardar la bodega. Ejecuta la migración de destinos y vuelve a intentar." });
        }
    }

    /// <summary>Coordenadas de la bodega destino, para centrar el mapa y pintar la geocerca.</summary>
    [HttpGet("bodega-coordenadas/{idBodega}")]
    public async Task<IActionResult> BodegaCoordenadas(string idBodega, CancellationToken ct)
    {
        var bodega = (await _service.ListarBodegasAsync(false, ct))
            .FirstOrDefault(x => string.Equals(x.IdBodega, idBodega.Trim(), StringComparison.OrdinalIgnoreCase));
        if (bodega is null)
        {
            return Ok(new BodegaCoordenadasResponse
            {
                Success = false,
                Message = "No hay coordenadas configuradas para esta bodega."
            });
        }

        return Ok(new BodegaCoordenadasResponse
        {
            Success = true,
            IdBodega = bodega.IdBodega,
            Nombre = bodega.Nombre,
            Direccion = bodega.Direccion,
            ReferenciaEntrada = bodega.ReferenciaEntrada,
            GooglePlaceId = bodega.GooglePlaceId,
            Lat = bodega.Lat,
            Lon = bodega.Lon,
            GeocercaLat = bodega.GeocercaLat,
            GeocercaLon = bodega.GeocercaLon,
            GeocercaMetros = bodega.GeocercaMetros
        });
    }
}
