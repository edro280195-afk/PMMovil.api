using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

using PMMovil.Api.Attributes;
using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

[Authorize]
[RequiereModulo(MobileModuleIds.WarehouseOperations)]
[ApiController]
[Route("api/[controller]")]
public class EntradaController : ControllerBase
{
    private readonly EntradaService _entradaService;
    private readonly IEntradaShareService _shareService;
    private readonly EntradaAvisoCapturaService _avisoCapturaService;
    private readonly IModulePermissionService _permissionService;
    private readonly ILogger<EntradaController> _logger;

    public EntradaController(
        EntradaService entradaService,
        IEntradaShareService shareService,
        EntradaAvisoCapturaService avisoCapturaService,
        IModulePermissionService permissionService,
        ILogger<EntradaController> logger)
    {
        _entradaService = entradaService;
        _shareService = shareService;
        _avisoCapturaService = avisoCapturaService;
        _permissionService = permissionService;
        _logger = logger;
    }

    // Aviso de PRUEBA al terminar la captura en el teléfono: un correo que llega SOLO a desarrollo3 (ver
    // EntradaAvisoCaptura). PMMovil no manda la notificación de entrada al cliente: la manda el sistema PMM (VFP).
    // Se llama desde la app, en segundo plano, cuando las fotos ya terminaron de subir.
    [HttpPost("{cref}/aviso-captura")]
    public async Task<IActionResult> AvisoCaptura(string cref, [FromQuery] bool edicion = false)
    {
        try
        {
            string usuario = base.User.Identity?.Name ?? "Desconocido";
            (bool ok, string mensaje) = await _avisoCapturaService.EnviarAsync(cref, usuario, edicion, $"{Request.Scheme}://{Request.Host}");
            return Ok(new { success = ok, message = mensaje });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al mandar el aviso de prueba de la entrada {Cref}", cref);
            return StatusCode(500, new { success = false, message = "Error al mandar el aviso de prueba" });
        }
    }

    [HttpGet("consecutivo")]
    public async Task<IActionResult> ObtenerConsecutivo()
    {
        try
        {
            var result = await _entradaService.ObtenerConsecutivoAsync();
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener consecutivo");
            return StatusCode(500, new { success = false, message = "Error al obtener consecutivo: " + ex.Message });
        }
    }

    [HttpGet("catalogos")]
    public async Task<IActionResult> ObtenerCatalogos()
    {
        try
        {
            var result = await _entradaService.ObtenerCatalogosAsync();
            return Ok(new { success = true, catalogos = result });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener catálogos");
            return StatusCode(500, new { success = false, message = "Error al obtener catálogos" });
        }
    }

    [HttpGet("cliente/{codigo}")]
    public async Task<IActionResult> BuscarCliente(string codigo)
    {
        try
        {
            var result = await _entradaService.BuscarClienteAsync(codigo);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al buscar cliente {Codigo}", codigo);
            return Ok(new { success = false, codigo = "", nombre = "" });
        }
    }

    [HttpGet("transporte/{codigo}")]
    public async Task<IActionResult> BuscarTransporte(string codigo)
    {
        try
        {
            var result = await _entradaService.BuscarTransporteAsync(codigo);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al buscar transporte {Codigo}", codigo);
            return Ok(new { success = false, codigo = "", nombre = "" });
        }
    }

    [HttpGet("{cref}")]
    public async Task<IActionResult> BuscarRef(string cref)
    {
        try
        {
            var result = await _entradaService.BuscarRefAsync(cref);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al buscar referencia {Cref}", cref);
            return Ok(new { success = false, message = "Error al buscar: " + ex.Message });
        }
    }

    // Genera el link público de "Seguimiento de entrada" (ver
    // EntradaShareService / PublicoController) -- vigente 72h, sin sesión de
    // PMMovil para abrirlo.
    [HttpPost("{cref}/compartir")]
    public async Task<IActionResult> Compartir(string cref)
    {
        try
        {
            string usuario = base.User.Identity?.Name ?? "Desconocido";
            (string Token, DateTime Expira)? resultado = await _shareService.GenerarLinkAsync(cref, usuario);
            if (resultado == null)
            {
                return NotFound(new EntradaShareLinkResponse { Success = false, Message = "Referencia no encontrada" });
            }

            string url = $"{Request.Scheme}://{Request.Host}/api/publico/entrada/{resultado.Value.Token}";
            return Ok(new EntradaShareLinkResponse
            {
                Success = true,
                Url = url,
                Expira = resultado.Value.Expira
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al generar link de seguimiento para {Cref}", cref);
            return StatusCode(500, new EntradaShareLinkResponse { Success = false, Message = "Error al generar el enlace" });
        }
    }

    [HttpGet("caseta/{codigo}")]
    public async Task<IActionResult> BuscarPorCaseta(string codigo)
    {
        try
        {
            var result = await _entradaService.BuscarPorCasetaAsync(codigo);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al buscar en caseta: {Codigo}", codigo);
            return Ok(new CasetaScanResult { Success = false, Message = "Error al consultar caseta: " + ex.Message });
        }
    }

    [HttpGet("cajas-disponibles")]
    public async Task<IActionResult> BuscarCajas()
    {
        try
        {
            var result = await _entradaService.BuscarCajasAsync();
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al buscar cajas");
            return Ok(new { success = false, message = ex.Message });
        }
    }

    [HttpGet("clientes")]
    public async Task<IActionResult> ListarClientes([FromQuery] string? filtro)
    {
        try
        {
            var result = await _entradaService.ListarClientesAsync(filtro);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al listar clientes");
            return Ok(new { success = false, message = ex.Message });
        }
    }

    [HttpGet("transportes")]
    public async Task<IActionResult> ListarTransportes([FromQuery] string? filtro)
    {
        try
        {
            var result = await _entradaService.ListarTransportesAsync(filtro);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al listar transportes");
            return Ok(new { success = false, message = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> GuardarRecepcion([FromBody] GuardarRecepcionRequest datos)
    {
        try
        {
            string usuarioActual = User.Identity?.Name ?? "sistema";
            var resultado = await _entradaService.GuardarRecepcionAsync(datos, usuarioActual);

            if (!resultado.Success)
                return Ok(resultado);

            return Ok(resultado);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al guardar recepción");
            return StatusCode(500, new { success = false, message = "Error al guardar: " + ex.Message });
        }
    }

    [HttpPut("{cref}")]
    public async Task<IActionResult> ActualizarRecepcion(string cref, [FromBody] GuardarRecepcionRequest datos)
    {
        try
        {
            string usuarioActual = User.Identity?.Name ?? "sistema";
            var resultado = await _entradaService.ActualizarRecepcionAsync(cref, datos, usuarioActual);

            if (!resultado.Success)
                return Ok(resultado);

            return Ok(resultado);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al actualizar recepción {Cref}", cref);
            return StatusCode(500, new { success = false, message = "Error al actualizar: " + ex.Message });
        }
    }

    [HttpPost("reimpresion")]
    [RequiereModulo(MobileModuleIds.WarehouseOperations, "Modificar")]
    public async Task<IActionResult> NotificarReimpresion([FromBody] NotificarReimpresionRequest req)
    {
        try
        {
            string usuarioActual = User.Identity?.Name ?? "sistema";
            var result = await _entradaService.NotificarReimpresionAsync(req.Referencia, req.Motivo, usuarioActual);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al notificar reimpresión");
            return StatusCode(500, new { success = false, message = "Error enviando correo: " + ex.Message });
        }
    }

    [HttpPost("fotos")]
    [RequiereModulo(MobileModuleIds.WarehouseOperations, "Lectura")]
    public async Task<IActionResult> GuardarFotosEntrada([FromBody] GuardarFotosEntradaRequest req)
    {
        string? username = User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(username)
            || !await EntradaFotoPermisos.PuedeSubirAsync(_permissionService, username, HttpContext.RequestAborted))
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(req.Referencia))
            return BadRequest(new { success = false, message = "La referencia es obligatoria." });

        if (req.Fotos == null || req.Fotos.Count == 0)
            return Ok(new { success = true, message = "Sin fotos que guardar." });

        try
        {
            var result = await _entradaService.GuardarFotosEntradaAsync(req.Referencia, req.Fotos, username);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al guardar fotos de entrada para {Ref}", req.Referencia);
            return StatusCode(500, new { success = false, message = "Error al guardar fotos: " + ex.Message });
        }
    }

    [HttpPost("fotos-danio")]
    [RequiereModulo(MobileModuleIds.WarehouseOperations, "Lectura")]
    public async Task<IActionResult> GuardarFotosDanioEntrada([FromBody] GuardarFotosEntradaRequest req)
    {
        string? username = User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(username)
            || !await EntradaFotoPermisos.PuedeSubirAsync(_permissionService, username, HttpContext.RequestAborted))
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(req.Referencia))
            return BadRequest(new { success = false, message = "La referencia es obligatoria." });

        if (req.Fotos == null || req.Fotos.Count == 0)
            return Ok(new { success = true, message = "Sin fotos que guardar." });

        try
        {
            var result = await _entradaService.GuardarFotosDanioEntradaAsync(req.Referencia, req.Fotos, username);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al guardar fotos de daño para {Ref}", req.Referencia);
            return StatusCode(500, new { success = false, message = "Error al guardar fotos de daño: " + ex.Message });
        }
    }

    /// <summary>
    /// Igual que <see cref="GuardarFotosEntrada"/>, pero las fotos llegan como archivos (multipart: <c>referencia</c> y los
    /// archivos <c>fotos</c>) y no como texto base64, que las infla un 33 % por el internet de la oficina.
    /// </summary>
    [HttpPost("fotos/archivos")]
    [RequiereModulo(MobileModuleIds.WarehouseOperations, "Lectura")]
    public async Task<IActionResult> GuardarFotosEntradaArchivos([FromForm] string? referencia, [FromForm] List<IFormFile>? fotos)
    {
        return await GuardarFotosArchivosAsync(referencia, fotos, danio: false);
    }

    /// <summary>Igual que <see cref="GuardarFotosDanioEntrada"/>, con las fotos como archivos (multipart).</summary>
    [HttpPost("fotos-danio/archivos")]
    [RequiereModulo(MobileModuleIds.WarehouseOperations, "Lectura")]
    public async Task<IActionResult> GuardarFotosDanioEntradaArchivos([FromForm] string? referencia, [FromForm] List<IFormFile>? fotos)
    {
        return await GuardarFotosArchivosAsync(referencia, fotos, danio: true);
    }

    private async Task<IActionResult> GuardarFotosArchivosAsync(string? referencia, List<IFormFile>? fotos, bool danio)
    {
        string? username = User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(username)
            || !await EntradaFotoPermisos.PuedeSubirAsync(_permissionService, username, HttpContext.RequestAborted))
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(referencia))
            return BadRequest(new { success = false, message = "La referencia es obligatoria." });

        (List<byte[]> recibidas, string? error) = await FotosRecibidas.LeerAsync(fotos, HttpContext.RequestAborted);
        if (error != null)
            return BadRequest(new { success = false, message = error });

        if (recibidas.Count == 0)
            return Ok(new { success = true, message = "Sin fotos que guardar." });

        try
        {
            object result = danio
                ? await _entradaService.GuardarFotosDanioEntradaArchivosAsync(referencia, recibidas, username)
                : await _entradaService.GuardarFotosEntradaArchivosAsync(referencia, recibidas, username);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al guardar fotos{Tipo} de entrada para {Ref}", danio ? " de daño" : "", referencia);
            return StatusCode(500, new { success = false, message = (danio ? "Error al guardar fotos de daño: " : "Error al guardar fotos: ") + ex.Message });
        }
    }

    [HttpGet("frecuentes")]
    public async Task<IActionResult> ObtenerFrecuentes([FromQuery] string tipo, [FromQuery] int limite = 8, [FromQuery] int dias = 90)
    {
        try
        {
            var items = await _entradaService.ObtenerFrecuentesEntradaAsync(tipo ?? "", limite, dias);
            return Ok(new { success = true, items });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener frecuentes de entrada bodega");
            return Ok(new { success = false, message = "Error al obtener frecuentes: " + ex.Message });
        }
    }
}

public class NotificarReimpresionRequest
{
    public string Referencia { get; set; } = string.Empty;
    public string Motivo { get; set; } = string.Empty;
}

public class GuardarFotosEntradaRequest
{
    public string Referencia { get; set; } = string.Empty;
    public List<string> Fotos { get; set; } = new();
}
