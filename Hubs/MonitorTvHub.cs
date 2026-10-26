using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

using PMMovil.Api.Services;

namespace PMMovil.Api.Hubs;

/// <summary>
/// Hub de solo emisión para monitores públicos. Cada conexión debe presentar
/// un ticket efímero, de un solo uso, ligado a un enlace de Monitor TV válido.
/// </summary>
[AllowAnonymous]
public sealed class MonitorTvHub : Hub
{
    public const string Group = "MONITOR_TV_OPERACION";

    private readonly IMonitorTvLinkService _linkService;

    public MonitorTvHub(IMonitorTvLinkService linkService)
    {
        _linkService = linkService;
    }

    public override async Task OnConnectedAsync()
    {
        string ticket = Context.GetHttpContext()?.Request.Query["access_token"].ToString() ?? string.Empty;
        bool valido = await _linkService.ConsumirTicketTiempoRealAsync(ticket, Context.ConnectionAborted);
        if (!valido)
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, Group, Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }
}
