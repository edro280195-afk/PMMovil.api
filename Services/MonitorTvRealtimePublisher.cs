using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.SignalR;

using PMMovil.Api.Hubs;

namespace PMMovil.Api.Services;

public sealed class MonitorTvRealtimePublisher
{
    private readonly IHubContext<MonitorTvHub> _hub;

    public MonitorTvRealtimePublisher(IHubContext<MonitorTvHub> hub)
    {
        _hub = hub;
    }

    public Task RevisionActualizadaAsync(
        string creferencia,
        string usuario,
        string entidad,
        string? oficinaOrigen,
        CancellationToken cancellationToken = default)
    {
        return _hub.Clients.Group(MonitorTvHub.Group).SendAsync("onRevisionActualizada", new
        {
            creferencia,
            usuario,
            entidad,
            oficinaOrigen,
            fecha = DateTime.UtcNow.ToString("O")
        }, cancellationToken);
    }

    public Task RevisionCompletadaAsync(
        string creferencia,
        string usuario,
        CancellationToken cancellationToken = default,
        long? eventId = null)
    {
        return _hub.Clients.Group(MonitorTvHub.Group).SendAsync("onRevisionCompletada", new
        {
            creferencia,
            usuario,
            eventId,
            fecha = DateTime.UtcNow.ToString("O")
        }, cancellationToken);
    }

    public Task RevisionEstadoCambiadoAsync(
        string creferencia,
        string? usuario,
        string estado,
        int? estatusFinal,
        CancellationToken cancellationToken = default,
        long? eventId = null)
    {
        return _hub.Clients.Group(MonitorTvHub.Group).SendAsync("onRevisionEstadoCambiado", new
        {
            creferencia,
            usuario,
            estado,
            fecha = DateTime.UtcNow.ToString("O"),
            estatusFinal,
            eventId
        }, cancellationToken);
    }

    public Task DiscrepanciaReportadaAsync(
        string creferencia,
        string usuario,
        string cnofact,
        string detalle,
        CancellationToken cancellationToken = default,
        long? eventId = null)
    {
        return _hub.Clients.Group(MonitorTvHub.Group).SendAsync("onDiscrepanciaReportada", new
        {
            creferencia,
            usuario,
            cnofact,
            motivo = detalle,
            eventId,
            fecha = DateTime.UtcNow.ToString("O")
        }, cancellationToken);
    }

    /// <summary>Alguien tomó una carpeta (la TV agrega su renglón a «Carpetas tomadas sin iniciar»).</summary>
    public Task CarpetaTomadaAsync(
        string creferencia,
        string usuario,
        string tomadaEn,
        CancellationToken cancellationToken = default)
    {
        return _hub.Clients.Group(MonitorTvHub.Group).SendAsync("onCarpetaTomada", new
        {
            creferencia,
            usuario,
            tomadaEn
        }, cancellationToken);
    }

    /// <summary>El revisador devolvió la carpeta sin revisarla.</summary>
    public Task CarpetaSoltadaAsync(
        string creferencia,
        string usuario,
        CancellationToken cancellationToken = default)
    {
        return _hub.Clients.Group(MonitorTvHub.Group).SendAsync("onCarpetaSoltada", new
        {
            creferencia,
            usuario
        }, cancellationToken);
    }
}
