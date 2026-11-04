using System.Globalization;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using PMMovil.Api.Attributes;
using PMMovil.Api.Data;
using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

[Authorize]
[RequiereModulo(MobileModuleIds.Gate)]
[ApiController]
[Route("api/[controller]")]
public class CasetaController : ControllerBase
{
    private readonly CasetaService _casetaService;
    private readonly PreregistroService _preregistroService;
    private readonly ILogger<CasetaController> _logger;

    public CasetaController(
        CasetaService casetaService,
        PreregistroService preregistroService,
        ILogger<CasetaController> logger)
    {
        _casetaService = casetaService;
        _preregistroService = preregistroService;
        _logger = logger;
    }

    private string ObtenerUsuario() => User.Identity?.Name ?? "sistema";

    [HttpGet("ficha")]
    public async Task<IActionResult> FichaControl([FromQuery] string? casetaId, [FromQuery] string? relno, CancellationToken ct)
    {
        if ((string.IsNullOrWhiteSpace(casetaId) && string.IsNullOrWhiteSpace(relno)) || casetaId?.Length > 80 || relno?.Length > 30)
            return BadRequest(new { message = "Indica una entrada de caseta o relación válida." });
        var ficha = await _casetaService.ObtenerFichaControlAsync(casetaId, relno, ct);
        return ficha is null ? NotFound(new { message = "Registro no encontrado." }) : Ok(ficha);
    }

    [HttpGet("foto")]
    public async Task<IActionResult> FotoControl([FromQuery] string referencia, [FromQuery] int indice, [FromQuery] string etapa, CancellationToken ct)
    {
        var path = await _casetaService.ObtenerRutaFotoControlAsync(referencia, indice, etapa, ct);
        if (path is null) return NotFound();
        var mime = Path.GetExtension(path).ToLowerInvariant() switch { ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg" };
        return PhysicalFile(path, mime);
    }

    [HttpGet("entrada/catalogos")]
    public async Task<IActionResult> GetCatalogosEntrada()
    {
        try
        {
            var result = await _casetaService.ObtenerCatalogosEntradaAsync();
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener catalogos de entrada caseta");
            return Ok(new CasetaEntradaCatalogosResult { Success = false, Message = "Error: " + ex.Message });
        }
    }

    [HttpGet("entrada/clientes")]
    public async Task<IActionResult> BuscarClientesEntrada([FromQuery] string? filtro = "", [FromQuery] int limite = 50)
    {
        try
        {
            var items = await _casetaService.BuscarClientesEntradaAsync(filtro, limite);
            return Ok(new { success = true, items });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al buscar clientes de caseta");
            return Ok(new { success = false, message = ex.Message, items = Array.Empty<CasetaEntradaCatalogItem>() });
        }
    }

    [HttpGet("entrada/transportes")]
    public async Task<IActionResult> BuscarTransportesEntrada([FromQuery] string? filtro = "", [FromQuery] int limite = 50)
    {
        try
        {
            var items = await _casetaService.BuscarTransportesEntradaAsync(filtro, limite);
            return Ok(new { success = true, items });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al buscar transportes de caseta");
            return Ok(new { success = false, message = ex.Message, items = Array.Empty<CasetaEntradaCatalogItem>() });
        }
    }

    [HttpGet("entrada/frecuentes")]
    public async Task<IActionResult> GetFrecuentesEntrada(
[FromQuery] string tipo,
[FromQuery] int limite = 8,
[FromQuery] int dias = 90)
    {
        try
        {
            var items = await _casetaService.ObtenerFrecuentesEntradaAsync(tipo ?? "", limite, dias);
            return Ok(new CasetaEntradaFrequentResult { Success = true, Items = items });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener frecuentes de entrada caseta");
            return Ok(new CasetaEntradaFrequentResult { Success = false, Message = ex.Message });
        }
    }

    [HttpGet("entrada/tipos-caja")]
    public async Task<IActionResult> GetTiposCajaEntrada()
    {
        try
        {
            var items = await _casetaService.ObtenerTiposCajaEntradaAsync();
            return Ok(new { success = true, items });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener tipos de caja");
            return Ok(new { success = false, message = ex.Message, items = Array.Empty<CasetaEntradaCatalogItem>() });
        }
    }

    /// <summary>
    /// Busqueda de entradas de caseta. `termino` busca contra caja, placas,
    /// unidad, linea y cliente; `tipoMov`, `cliente` y `linea` son filtros
    /// exactos que se combinan con el termino (son los chips del hub movil).
    /// </summary>
    [HttpGet("entrada/buscar")]
    public async Task<IActionResult> BuscarEntradasCaseta(
[FromQuery] string? termino = "",
[FromQuery] string? tipo = "general",
[FromQuery] int offset = 0,
[FromQuery] int limit = 20,
[FromQuery] string? tipoMov = null,
[FromQuery] string? cliente = null,
[FromQuery] string? linea = null)
    {
        try
        {
            var items = await _casetaService.BuscarEntradasCasetaAsync(
                termino ?? "", tipo ?? "general", offset, limit, tipoMov, cliente, linea);
            return Ok(new { success = true, items });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al buscar entradas caseta");
            return Ok(new { success = false, message = ex.Message, items = Array.Empty<CasetaEntradaBusquedaItem>() });
        }
    }

    [HttpGet("entrada/{casetaId}")]
    public async Task<IActionResult> GetEntradaCaseta(string casetaId, [FromQuery] bool fotosBase64 = true)
    {
        try
        {
            var result = await _casetaService.ObtenerEntradaCasetaAsync(casetaId, fotosBase64);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener entrada caseta {CasetaId}", casetaId);
            return Ok(new CasetaEntradaObtenerResult { Success = false, Message = "Error: " + ex.Message });
        }
    }

    [HttpGet("entrada/detalle")]
    public async Task<IActionResult> GetEntradaCasetaDetalle([FromQuery] string casetaId, [FromQuery] bool fotosBase64 = true)
    {
        try
        {
            var result = await _casetaService.ObtenerEntradaCasetaAsync(casetaId, fotosBase64);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener entrada caseta {CasetaId}", casetaId);
            return Ok(new CasetaEntradaObtenerResult { Success = false, Message = "Error: " + ex.Message });
        }
    }

    [HttpPost("entrada")]
    [RequiereModulo(MobileModuleIds.Gate, "Agregar")]
    public async Task<IActionResult> GuardarEntradaCaseta([FromBody] CasetaEntradaGuardarRequest request, CancellationToken ct)
    {
        if (request.Actualizado || !string.IsNullOrWhiteSpace(request.Casetaid) || !string.IsNullOrWhiteSpace(request.Idcaseta))
        {
            return BadRequest(new CasetaEntradaGuardarResult
            {
                Success = false,
                Message = "Las actualizaciones de entradas deben enviarse mediante la operación de modificar."
            });
        }
        try
        {
            var result = await _casetaService.GuardarEntradaCasetaAsync(request, ObtenerUsuario(), ct);

            // Guardar la entrada y consumir el QR son pasos distintos. Se
            // ejecuta despues de confirmar la entrada para que un fallo de
            // este enlace nunca borre ni deshaga la entrada ya registrada.
            if (result.Success)
            {
                try
                {
                    var preregistro = await _preregistroService.UsarPorEntradaAsync(
                        request,
                        result.CasetaId,
                        ObtenerUsuario(),
                        ct);

                    if (preregistro is { Success: false })
                    {
                        await _preregistroService.RegistrarCierrePendienteAsync(
                            request,
                            result.CasetaId,
                            ObtenerUsuario(),
                            preregistro.Message,
                            ct);
                        _logger.LogWarning(
                            "Entrada de caseta {CasetaId} guardada, pero el pre-registro quedo pendiente: {Message}",
                            result.CasetaId,
                            preregistro.Message);
                        result.Message += " El pre-registro quedó en cola para reintento automático.";
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Entrada de caseta {CasetaId} guardada, pero fallo el cierre del pre-registro.",
                        result.CasetaId);
                    try
                    {
                        await _preregistroService.RegistrarCierrePendienteAsync(
                            request,
                            result.CasetaId,
                            ObtenerUsuario(),
                            "Fallo temporal al cerrar el pre-registro.",
                            ct);
                    }
                    catch (Exception queueException)
                    {
                        _logger.LogError(queueException,
                            "No se pudo encolar el cierre pendiente de la entrada {CasetaId}.",
                            result.CasetaId);
                    }

                    result.Message += " El pre-registro quedó pendiente de sincronización.";
                }
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al guardar entrada caseta");
            return StatusCode(500, new CasetaEntradaGuardarResult
            {
                Success = false,
                Message = "No fue posible guardar la entrada de caseta. Intenta nuevamente."
            });
        }
    }

    [HttpPut("entrada/{casetaId}")]
    [RequiereModulo(MobileModuleIds.Gate, "Modificar")]
    public async Task<IActionResult> ActualizarEntradaCaseta(
        string casetaId,
        [FromBody] CasetaEntradaGuardarRequest request,
        CancellationToken ct)
    {
        // La ruta es la identidad del registro que el usuario abrió para
        // editar. Se valida también contra el cuerpo para evitar actualizar
        // accidentalmente otro folio por una solicitud mal formada. El folio
        // lleva diagonal y llega en la ruta como "E-020485%2F26" (ASP.NET Core
        // no lo decodifica): se normaliza antes de comparar, ver CasetaFolio.
        string folio = CasetaFolio.Normalizar(casetaId);
        if (folio.Length == 0 || !CasetaFolio.Coinciden(casetaId, request.Casetaid))
        {
            return BadRequest(new CasetaEntradaGuardarResult
            {
                Success = false,
                Message = "El folio de caseta no coincide con el registro a modificar."
            });
        }

        request.Casetaid = folio;
        request.Actualizado = true;
        try
        {
            var result = await _casetaService.GuardarEntradaCasetaAsync(request, ObtenerUsuario(), ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al actualizar entrada caseta {CasetaId}", casetaId);
            return StatusCode(500, new CasetaEntradaGuardarResult { Success = false, Message = "Error al actualizar: " + ex.Message });
        }
    }

    [HttpPost("entrada/{casetaId}/fotos")]
    [RequiereModulo(MobileModuleIds.Gate, "Agregar")]
    public async Task<IActionResult> SubirFotosEntradaCaseta(
        string casetaId,
        [FromBody] CasetaEntradaFotosRequest request,
        CancellationToken ct)
    {
        try
        {
            var result = await _casetaService.SubirFotosEntradaCasetaAsync(
                casetaId,
                request,
                ObtenerUsuario(),
                ct);
            if (!result.Success)
            {
                _logger.LogWarning(
                    "Subida de fotos de entrada caseta rechazada. CasetaId={CasetaId}, Motivo={Message}",
                    casetaId,
                    result.Message);
                return BadRequest(result);
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al subir fotos de entrada caseta {CasetaId}", casetaId);
            return StatusCode(500, new CasetaEntradaFotosResult
            {
                Success = false,
                Message = "Error al subir fotos de caseta."
            });
        }
    }

    [HttpGet("entrada/buscar-por-caja/{nequipo}")]
    [RequiereModulo(MobileModuleIds.Gate, "Lectura")]
    public async Task<IActionResult> BuscarEntradaPorCaja(string nequipo, CancellationToken ct)
    {
        try
        {
            var items = await _casetaService.BuscarEntradasPorCajaAsync(nequipo, ct);
            return Ok(new { success = true, items });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al buscar entrada de caseta por caja {Nequipo}", nequipo);
            return StatusCode(500, new { success = false, message = "Error al buscar la caja." });
        }
    }

    /// <summary>
    /// Reenvío manual del correo de una entrada de caseta ya guardada, para el caso
    /// de "hice la entrada pero no me llegó el correo". Con soloValidacion=true (el
    /// default) manda a desarrollo3 para revisar cómo se ve antes de mandarlo a los
    /// destinatarios reales con soloValidacion=false.
    /// </summary>
    [HttpPost("entrada/{casetaId}/reenviar-correo")]
    [RequiereModulo(MobileModuleIds.Gate, "Modificar")]
    public async Task<IActionResult> ReenviarCorreoEntrada(
        string casetaId, [FromQuery] bool soloValidacion = true, CancellationToken ct = default)
    {
        try
        {
            var (success, mensaje) = await _casetaService.ReenviarCorreoEntradaAsync(
                casetaId, soloValidacion, ObtenerUsuario(), ct);
            if (!success)
                return BadRequest(new { success = false, message = mensaje });

            return Ok(new { success = true, message = mensaje });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al reenviar correo de entrada caseta {CasetaId}", casetaId);
            return StatusCode(500, new { success = false, message = "Error al reenviar el correo." });
        }
    }

    [HttpPost("entrada/ocr-placa")]
    [RequiereModulo(MobileModuleIds.Gate, "Lectura")]
    public async Task<IActionResult> LeerPlacaEntrada([FromBody] CasetaEntradaPlateOcrRequest request, CancellationToken ct)
    {
        try
        {
            var result = await _casetaService.LeerPlacaEntradaAsync(request, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al leer placa de entrada caseta");
            return Ok(new CasetaEntradaPlateOcrResult
            {
                Success = false,
                Message = "No se pudo leer la placa en este momento."
            });
        }
    }

    [HttpPost("entrada/ocr-caja")]
    [RequiereModulo(MobileModuleIds.Gate, "Lectura")]
    public async Task<IActionResult> LeerCajaEntrada([FromBody] CasetaEntradaBoxOcrRequest request, CancellationToken ct)
    {
        try
        {
            var result = await _casetaService.LeerCajaEntradaAsync(request, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al leer caja de entrada caseta");
            return Ok(new CasetaEntradaBoxOcrResult
            {
                Success = false,
                Message = "No se pudo leer la caja en este momento."
            });
        }
    }

    /// <summary>
    /// Convierte el ?fecha=YYYY-MM-DD del panel. Devuelve null cuando viene
    /// vacio, cuando no parsea, o cuando es el dia de hoy -- en los tres casos
    /// el comportamiento correcto es el de "hoy en vivo", no una consulta
    /// historica. Fechas futuras tambien caen a null: no hay nada que consultar.
    /// </summary>
    private static DateTime? ParsearFechaConsulta(string? fecha)
    {
        if (string.IsNullOrWhiteSpace(fecha))
            return null;

        if (!DateTime.TryParseExact(fecha.Trim(), "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
            return null;

        return parsed.Date >= DateTime.Today ? null : parsed.Date;
    }

    [HttpGet("entrada/conteos-hoy")]
    public async Task<IActionResult> GetConteosEntradaCasetaHoy([FromQuery] string? fecha = null)
    {
        try
        {
            var result = await _casetaService.ObtenerConteosCasetaHoyAsync(ParsearFechaConsulta(fecha));
            return Ok(new { success = true, data = result });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener conteos caseta");
            return Ok(new { success = false, message = ex.Message });
        }
    }

    [HttpGet("entrada/volumen-hora")]
    public async Task<IActionResult> GetVolumenPorHora([FromQuery] string? fecha = null)
    {
        try
        {
            var items = await _casetaService.ObtenerVolumenPorHoraAsync(ParsearFechaConsulta(fecha));
            return Ok(new { success = true, items = items });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener volumen por hora de caseta");
            return Ok(new { success = false, message = ex.Message });
        }
    }

    [HttpGet("pendientes")]
    [RequiereModulo(MobileModuleIds.GateRelationExit, "Lectura", true)]
    public async Task<IActionResult> GetPendientes([FromQuery] string? fecha = null)
    {
        try
        {
            DateTime? consulta = ParsearFechaConsulta(fecha);
            var pendientes = await _casetaService.GetPendientesAsync(consulta);
            return Ok(new PendientesListResult
            {
                Success = true,
                Pendientes = pendientes,
                EsHistorico = consulta is not null,
                Fecha = consulta?.ToString("yyyy-MM-dd")
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener pendientes de caseta");
            return Ok(new PendientesListResult { Success = false, Message = "Error: " + ex.Message });
        }
    }

    [HttpGet("{relno}")]
    [RequiereModulo(MobileModuleIds.GateRelationExit, "Lectura", true)]
    public async Task<IActionResult> GetDetalle(string relno)
    {
        try
        {
            var result = await _casetaService.GetDetalleAsync(relno);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener detalle de caseta {Relno}", relno);
            return Ok(new CasetaResult { Success = false, Message = "Error: " + ex.Message });
        }
    }

    [HttpPost("{relno}/confirmar")]
    [RequiereModulo(MobileModuleIds.GateRelationExit, "Modificar", true)]
    public async Task<IActionResult> ConfirmarSalida(string relno, [FromBody] ConfirmarSalidaRequest? request, CancellationToken ct)
    {
        try
        {
            string usuario = ObtenerUsuario();
            string observaciones = request?.Observaciones ?? "";
            var result = await _casetaService.ConfirmarSalidaAsync(relno, usuario, observaciones, request?.FotosBase64, request?.TotalFotos, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al confirmar salida caseta {Relno}", relno);
            return Ok(new ConfirmarResult { Success = false, Message = "Error: " + ex.Message });
        }
    }

    /// <summary>
    /// Sube un lote pequeño de fotos de la salida ANTES de confirmarla. La app las manda de pocas en pocas (como ya hace la
    /// entrada) para que se vea el avance y una red lenta no deje colgada la salida; la confirmación solo verifica que estén.
    /// Repetir un lote es seguro: reemplaza las fotos de esas posiciones.
    /// </summary>
    [HttpPost("{relno}/salida-fotos")]
    [RequiereModulo(MobileModuleIds.GateRelationExit, "Agregar", true)]
    public async Task<IActionResult> SubirFotosSalida(string relno, [FromBody] SubirFotosSalidaRequest? request, CancellationToken ct)
    {
        try
        {
            var result = await _casetaService.SubirFotosSalidaAsync(
                CasetaFolio.Normalizar(relno), request?.IndiceInicial ?? 1, request?.FotosBase64, ObtenerUsuario(), ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al subir fotos de salida caseta {Relno}", relno);
            return Ok(new ConfirmarResult { Success = false, Message = "Error al subir las fotos de salida." });
        }
    }

    // ── Pre-registro (camiones sin caja): validacion y uso en garita ──────
    // Rutas api/Caseta/preregistro/... -- mismo modulo Gate que el resto de
    // caseta, el guardia ya tiene acceso. El JWT lo emite este mismo esquema
    // por defecto (nunca el esquema "Portal" del chofer/linea, que aqui no
    // pasaria la autenticacion: audiencia distinta).

    [HttpGet("preregistro/validar")]
    public async Task<IActionResult> ValidarPreregistro([FromQuery] string codigo)
    {
        if (string.IsNullOrWhiteSpace(codigo))
            return BadRequest(new PreregistroValidarResponse { Success = false, Estatus = PreregistroEstatus.NoEncontrado, Message = "Codigo vacio." });

        try
        {
            var result = await _preregistroService.ValidarAsync(codigo);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validando pre-registro. Codigo: {Codigo}", codigo);
            return StatusCode(500, new PreregistroValidarResponse
            {
                Success = false,
                Estatus = PreregistroEstatus.NoEncontrado,
                Message = "No fue posible validar el pre-registro. Intenta nuevamente."
            });
        }
    }

    // Mismo permiso que "entrada": cerrar el folio es el segundo paso de la
    // misma accion de guardar desde QR, no una edicion posterior. Con
    // "Modificar" una garita con solo Agregar+Lectura guardaba la entrada
    // bien pero este paso fallaba con 403 silencioso y el pre-registro se
    // quedaba "Vigente" para siempre.
    [HttpPost("preregistro/{folio}/usar")]
    [RequiereModulo(MobileModuleIds.Gate, "Agregar")]
    public async Task<IActionResult> UsarPreregistro(string folio, [FromBody] PreregistroUsarRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var result = await _preregistroService.UsarAsync(folio, request, ObtenerUsuario());
            if (!result.Success)
                return BadRequest(result);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marcando como usado el pre-registro {Folio}", folio);
            return StatusCode(500, new PreregistroUsarResponse
            {
                Success = false,
                Message = "No fue posible cerrar el pre-registro. La entrada puede reintentarse."
            });
        }
    }

    /// <summary>Camiones en camino (pre-registros vigentes con ubicacion), para Centro de Caseta y la web.</summary>
    [HttpGet("preregistro/en-camino")]
    public async Task<IActionResult> PreregistroEnCamino([FromQuery] string? idBodega)
    {
        try
        {
            return Ok(await _preregistroService.EnCaminoAsync(idBodega));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obteniendo camiones en camino");
            return StatusCode(500, new
            {
                message = "No fue posible consultar los camiones en camino. Intenta nuevamente."
            });
        }
    }

    /// <summary>
    /// Coordenadas de la bodega/planta para centrar el mapa de "Camiones en
    /// camino" y pintar el destino.
    /// </summary>
    [HttpGet("bodega-coordenadas/{idBodega}")]
    public async Task<IActionResult> BodegaCoordenadas(string idBodega, CancellationToken ct)
    {
        var bodega = (await _preregistroService.ListarBodegasAsync(false, ct))
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
