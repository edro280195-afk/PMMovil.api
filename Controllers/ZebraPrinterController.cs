using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using PMMovil.Api.Attributes;
using PMMovil.Api.Models;
using PMMovil.Api.Services;

namespace PMMovil.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class ZebraPrinterController : ControllerBase
{
    private readonly ZebraPrinterCatalogService _printerCatalogService;
    private readonly ILogger<ZebraPrinterController> _logger;

    public ZebraPrinterController(
        ZebraPrinterCatalogService printerCatalogService,
        ILogger<ZebraPrinterController> logger)
    {
        _printerCatalogService = printerCatalogService;
        _logger = logger;
    }

    [HttpGet]
    [RequiereModulo(MobileModuleIds.WarehouseOperations)]
    public async Task<IActionResult> GetActivePrinters(CancellationToken ct)
    {
        try
        {
            var result = await _printerCatalogService.GetActivePrintersAsync(ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener impresoras Zebra");
            return StatusCode(500, new ZebraPrinterListResult
            {
                Success = false,
                Message = "No se pudieron cargar las impresoras Zebra: " + ex.Message
            });
        }
    }

    [HttpGet("all")]
    [RequiereModulo(MobileModuleIds.SystemsMonitor)]
    public async Task<IActionResult> GetAllPrinters(CancellationToken ct)
    {
        try
        {
            var result = await _printerCatalogService.GetPrintersAsync(activeOnly: false, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al obtener catalogo Zebra");
            return StatusCode(500, new ZebraPrinterListResult
            {
                Success = false,
                Message = "No se pudo cargar el catalogo Zebra: " + ex.Message
            });
        }
    }

    [HttpPost]
    [RequiereModulo(MobileModuleIds.SystemsMonitor, "Agregar")]
    public async Task<IActionResult> CreatePrinter([FromBody] ZebraPrinterSaveRequest request, CancellationToken ct)
    {
        request.Id = 0;
        return await SavePrinter(request, ct);
    }

    [HttpPut("{id:int}")]
    [RequiereModulo(MobileModuleIds.SystemsMonitor, "Modificar")]
    public async Task<IActionResult> UpdatePrinter(int id, [FromBody] ZebraPrinterSaveRequest request, CancellationToken ct)
    {
        request.Id = id;
        return await SavePrinter(request, ct);
    }

    private async Task<IActionResult> SavePrinter(ZebraPrinterSaveRequest request, CancellationToken ct)
    {
        try
        {
            var result = await _printerCatalogService.SavePrinterAsync(request, ct);
            return result.Success ? Ok(result) : BadRequest(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al guardar impresora Zebra {PrinterId}", request.Id);
            return StatusCode(500, new ZebraPrinterSaveResult
            {
                Success = false,
                Message = "No se pudo guardar la impresora Zebra: " + ex.Message
            });
        }
    }
}
