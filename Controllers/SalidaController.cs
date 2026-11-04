using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using PMMovil.Api.Attributes;
using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

[Authorize]
[RequiereModulo(MobileModuleIds.WarehouseExit)]
[ApiController]
[Route("api/[controller]")]
public class SalidaController : ControllerBase
{
    private readonly SalidaService _salidaService;
    private readonly ILogger<SalidaController> _logger;

    public SalidaController(SalidaService salidaService, ILogger<SalidaController> logger)
    {
        _salidaService = salidaService;
        _logger = logger;
    }

    private string ObtenerUsuario() => User.Identity?.Name ?? "sistema";

    [HttpGet("pendientes-autorizar")]
    [RequiereModulo(MobileModuleIds.WarehouseRelation, "Lectura", true)]
    public async Task<IActionResult> GetPendientesAutorizar([FromQuery] string? busqueda = "")
    {
        try
        {
            var pendientes = await _salidaService.GetPendientesAutorizarAsync(busqueda ?? "");
            return Ok(new { success = true, pendientes });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener pendientes de autorización");
            return Ok(new { success = false, pendientes = new List<object>(), message = ex.Message });
        }
    }

    [HttpGet("{relno}/supervisor")]
    [RequiereModulo(MobileModuleIds.WarehouseRelation, "Lectura", true)]
    public async Task<IActionResult> GetDetalleSupervisor(string relno)
    {
        try
        {
            var result = await _salidaService.GetDetalleSupervisorAsync(relno);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al consultar detalle supervisor {Relno}", relno);
            return Ok(new CargarRelacionResult { Success = false, Message = "Error: " + ex.Message });
        }
    }

    [HttpGet("{relno}")]
    public async Task<IActionResult> CargarRelacion(string relno, [FromQuery] string? version)
    {
        try
        {
            var result = await _salidaService.CargarRelacionAsync(relno, version ?? "", ObtenerUsuario());
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al cargar relación {Relno}", relno);
            return Ok(new CargarRelacionResult { Success = false, Message = "Error: " + ex.Message });
        }
    }

    [HttpGet("{relno}/etiquetas")]
    public async Task<IActionResult> GetEtiquetas(string relno)
    {
        try
        {
            var result = await _salidaService.GetDetalleSupervisorAsync(relno);
            return Ok(new { success = result.Success, etiquetas = result.Etiquetas });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener etiquetas de {Relno}", relno);
            return Ok(new { success = false, message = ex.Message });
        }
    }

    [HttpGet("{relno}/descarga")]
    public async Task<IActionResult> GetEtiquetasDescarga(string relno)
    {
        try
        {
            var etiquetas = await _salidaService.GetEtiquetasDescargaAsync(relno);
            return Ok(new { success = true, etiquetas });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener etiquetas descarga");
            return Ok(new { success = false, message = ex.Message });
        }
    }

    [HttpGet("{relno}/status")]
    public async Task<IActionResult> GetStatus(string relno)
    {
        try
        {
            var info = await _salidaService.GetStatusAsync(relno);
            return Ok(new { success = info != null, relacion = info });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener status");
            return Ok(new { success = false, message = ex.Message });
        }
    }

    [HttpPost("scan")]
    [RequiereModulo(MobileModuleIds.WarehouseExit, "Modificar")]
    public async Task<IActionResult> ScanLabel([FromBody] ScanLabelRequest request)
    {
        try
        {
            request.Usuario = ObtenerUsuario();
            request.IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
            var result = await _salidaService.ScanLabelAsync(request);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al escanear etiqueta");
            return Ok(new ScanLabelResult { Success = false, Message = "Error: " + ex.Message });
        }
    }

    [HttpPost("validar-caseta")]
    [RequiereModulo(MobileModuleIds.WarehouseExit, "Modificar")]
    public async Task<IActionResult> ValidarCaseta([FromBody] ValidarCasetaQrRequest request)
    {
        try
        {
            request.IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
            var result = await _salidaService.ValidarCasetaQrAsync(request, ObtenerUsuario());
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al validar QR de caseta {CasetaId}", request.CasetaId);
            return Ok(new ValidarCasetaQrResult { Success = false, Message = "Error: " + ex.Message, Tipo = "ERROR" });
        }
    }

    [HttpPost("{relno}/finalizar")]
    [RequiereModulo(MobileModuleIds.WarehouseExit, "Modificar")]
    public async Task<IActionResult> Finalizar(string relno)
    {
        try
        {
            var result = await _salidaService.FinalizarAsync(relno, ObtenerUsuario());
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al finalizar {Relno}", relno);
            return Ok(new ResultadoOperacion { Success = false, Message = "Error: " + ex.Message });
        }
    }

    [HttpPost("{relno}/pausa")]
    [RequiereModulo(MobileModuleIds.WarehouseExit, "Modificar")]
    public async Task<IActionResult> Pausar(string relno)
    {
        try
        {
            var result = await _salidaService.PausarAsync(relno, ObtenerUsuario());
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al pausar {Relno}", relno);
            return Ok(new ResultadoOperacion { Success = false, Message = "Error: " + ex.Message });
        }
    }

    [HttpPost("{relno}/cancelar")]
    [RequiereModulo(MobileModuleIds.WarehouseExit, "Modificar")]
    public async Task<IActionResult> Cancelar(string relno, [FromQuery] string? restaurar = null)
    {
        try
        {
            var result = await _salidaService.CancelarAsync(relno, ObtenerUsuario(), restaurar);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al cancelar {Relno}", relno);
            return Ok(new ResultadoOperacion { Success = false, Message = "Error: " + ex.Message });
        }
    }

    [HttpPost("{relno}/solicitar-autorizacion")]
    [RequiereModulo(MobileModuleIds.WarehouseExit, "Modificar")]
    public async Task<IActionResult> SolicitarAutorizacion(string relno)
    {
        try
        {
            var result = await _salidaService.SolicitarAutorizacionAsync(relno, ObtenerUsuario());
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al solicitar autorización {Relno}", relno);
            return Ok(new ResultadoOperacion { Success = false, Message = "Error: " + ex.Message });
        }
    }

    [HttpPost("descargar")]
    [RequiereModulo(MobileModuleIds.WarehouseExit, "Modificar")]
    public async Task<IActionResult> Descargar([FromBody] DescargarRequest request)
    {
        try
        {
            request.Usuario = ObtenerUsuario();
            var result = await _salidaService.DescargarLabelAsync(request);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al descargar etiqueta");
            return Ok(new ResultadoOperacion { Success = false, Message = "Error: " + ex.Message });
        }
    }

    [HttpPost("{relno}/finalizar-descarga")]
    [RequiereModulo(MobileModuleIds.WarehouseExit, "Modificar")]
    public async Task<IActionResult> FinalizarDescarga(string relno)
    {
        try
        {
            var result = await _salidaService.FinalizarDescargaAsync(relno, ObtenerUsuario());
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al finalizar descarga");
            return Ok(new ResultadoOperacion { Success = false, Message = "Error: " + ex.Message });
        }
    }

    [HttpGet("sin-relacion/{entrada}")]
    public async Task<IActionResult> CargarSinRelacion(string entrada)
    {
        try
        {
            var result = await _salidaService.CargarSinRelacionAsync(entrada, ObtenerUsuario());
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al cargar SR {Entrada}", entrada);
            return Ok(new CargarRelacionResult { Success = false, Message = "Error: " + ex.Message });
        }
    }

    [HttpPost("sin-relacion/scan")]
    [RequiereModulo(MobileModuleIds.WarehouseExit, "Modificar")]
    public async Task<IActionResult> ScanSinRelacion([FromBody] SinRelacionRequest request)
    {
        try
        {
            request.Usuario = ObtenerUsuario();
            request.IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
            var result = await _salidaService.ScanSinRelacionAsync(request);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al escanear SR");
            return Ok(new ScanLabelResult { Success = false, Message = "Error: " + ex.Message });
        }
    }

    [HttpPost("sin-relacion/{entrada}/finalizar")]
    [RequiereModulo(MobileModuleIds.WarehouseExit, "Modificar")]
    public async Task<IActionResult> FinalizarSinRelacion(string entrada)
    {
        try
        {
            var result = await _salidaService.FinalizarSinRelacionAsync(entrada, ObtenerUsuario());
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al finalizar SR {Entrada}", entrada);
            return Ok(new ResultadoOperacion { Success = false, Message = "Error: " + ex.Message });
        }
    }

    [HttpPost("{relno}/autorizar")]
    [RequiereModulo(MobileModuleIds.WarehouseRelation, "Modificar", true)]
    public async Task<IActionResult> Autorizar(string relno)
    {
        try
        {
            var result = await _salidaService.AutorizarAsync(relno, ObtenerUsuario());
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al autorizar {Relno}", relno);
            return Ok(new ResultadoOperacion { Success = false, Message = "Error: " + ex.Message });
        }
    }

    [HttpPost("{relno}/eliminar-cargado")]
    [RequiereModulo(MobileModuleIds.WarehouseRelation, "Borrar", true)]
    public async Task<IActionResult> EliminarCargado(string relno)
    {
        try
        {
            var result = await _salidaService.EliminarCargadoAsync(relno, ObtenerUsuario());
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al eliminar cargado {Relno}", relno);
            return Ok(new ResultadoOperacion { Success = false, Message = "Error: " + ex.Message });
        }
    }

    [HttpPost("{relno}/bloquear")]
    [RequiereModulo(MobileModuleIds.WarehouseExit, "Modificar")]
    public async Task<IActionResult> Bloquear(string relno)
    {
        try
        {
            var result = await _salidaService.BloquearAsync(relno, ObtenerUsuario());
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al bloquear {Relno}", relno);
            return Ok(new ResultadoOperacion { Success = false, Message = "Error: " + ex.Message });
        }
    }

    [HttpPost("{relno}/desbloquear")]
    [RequiereModulo(MobileModuleIds.WarehouseRelation, "Modificar", true)]
    public async Task<IActionResult> Desbloquear(string relno)
    {
        try
        {
            var result = await _salidaService.DesbloquearAsync(relno, ObtenerUsuario());
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al desbloquear {Relno}", relno);
            return Ok(new ResultadoOperacion { Success = false, Message = "Error: " + ex.Message });
        }
    }

    [HttpPost("{relno}/retornar-espera")]
    [RequiereModulo(MobileModuleIds.WarehouseRelation, "Modificar", true)]
    public async Task<IActionResult> RetornarAEspera(string relno)
    {
        try
        {
            var result = await _salidaService.RetornarAEsperaAsync(relno, ObtenerUsuario());
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al retornar a espera {Relno}", relno);
            return Ok(new ResultadoOperacion { Success = false, Message = "Error: " + ex.Message });
        }
    }

    [HttpPost("error-evitado")]
    public async Task<IActionResult> RegistrarErrorEvitado([FromBody] ErrorEvitadoRequest request)
    {
        try
        {
            request.Usuario = ObtenerUsuario();
            var result = await _salidaService.RegistrarErrorEvitadoAsync(request);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al registrar error evitado");
            return Ok(new ResultadoOperacion { Success = false, Message = "Error: " + ex.Message });
        }
    }
}
