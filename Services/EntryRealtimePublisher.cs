using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.SignalR;

using PMMovil.Api.Hubs;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

public sealed class EntryRealtimePublisher
{
    public const string CapturedEvent = "onEntradaCapturada";
    public const string UpdatedEvent = "onEntradaActualizada";
    public const string PhotosEvent = "onEntradaFotos";
    public const string CapturedNotificationType = "entrada-capturada";

    private static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(30);
    private static readonly ConcurrentDictionary<string, DateTime> RecentNotifications = new(StringComparer.OrdinalIgnoreCase);

    private readonly IHubContext<WarehouseHub> _hub;
    private readonly EntryPanelService _panel;
    private readonly IRealtimeNotificationService _notifications;
    private readonly ILogger<EntryRealtimePublisher> _logger;

    public EntryRealtimePublisher(
        IHubContext<WarehouseHub> hub,
        EntryPanelService panel,
        IRealtimeNotificationService notifications,
        ILogger<EntryRealtimePublisher> logger)
    {
        _hub = hub;
        _panel = panel;
        _notifications = notifications;
        _logger = logger;
    }

    public static bool ShouldNotify(string reference, DateTime now)
    {
        string key = reference.Trim();
        if (key.Length == 0) return false;

        foreach (KeyValuePair<string, DateTime> item in RecentNotifications)
        {
            if (now - item.Value > RepeatWindow) RecentNotifications.TryRemove(item.Key, out _);
        }

        if (RecentNotifications.TryGetValue(key, out DateTime last) && now - last <= RepeatWindow) return false;
        RecentNotifications[key] = now;
        return true;
    }

    public static void ForgetNotification(string reference)
    {
        RecentNotifications.TryRemove(reference.Trim(), out _);
    }

    public async Task PublishSavedAsync(string reference, string actor, bool created)
    {
        if (string.IsNullOrWhiteSpace(reference)) return;
        try
        {
            EntryRow? row = await _panel.GetRowAsync(reference, CancellationToken.None);
            if (row is null) return;
            await _hub.Clients.Group(WarehouseHub.EntriesGroup).SendAsync(
                created ? CapturedEvent : UpdatedEvent,
                new EntryRealtimeEvent
                {
                    Reference = row.Reference,
                    Actor = actor,
                    At = DateTime.Now.ToString("o"),
                    Kind = created ? "created" : "updated",
                    Entry = row
                });
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "No se pudo avisar por SignalR la entrada {Reference}.", reference);
        }
    }

    public async Task PublishPhotosAsync(string reference, string actor, string action, int count)
    {
        if (string.IsNullOrWhiteSpace(reference)) return;
        try
        {
            (int arrival, int damage) = await _panel.CountPhotosAsync(reference, CancellationToken.None);
            await _hub.Clients.Group(WarehouseHub.EntriesGroup).SendAsync(
                PhotosEvent,
                new EntryPhotosEvent
                {
                    Reference = reference.Trim(),
                    Actor = actor,
                    At = DateTime.Now.ToString("o"),
                    Action = action,
                    Count = count,
                    ArrivalPhotos = arrival,
                    DamagePhotos = damage
                });
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "No se pudo avisar por SignalR las fotos de {Reference}.", reference);
        }
    }

    public async Task PublishCaptureFinishedAsync(string reference, string actor, bool isEdit)
    {
        if (string.IsNullOrWhiteSpace(reference)) return;
        try
        {
            EntryDetailResult? detail = await _panel.GetDetailAsync(reference, CancellationToken.None);
            if (detail is null) return;

            await _hub.Clients.Group(WarehouseHub.EntriesGroup).SendAsync(
                UpdatedEvent,
                new EntryRealtimeEvent
                {
                    Reference = detail.Entry.Reference,
                    Actor = actor,
                    At = DateTime.Now.ToString("o"),
                    Kind = isEdit ? "updated" : "completed-capture",
                    Entry = detail.Entry
                });

            if (isEdit || !ShouldNotify(detail.Entry.Reference, DateTime.UtcNow)) return;

            (string title, string body, string severity) = EntryPanelRules.CapturedNotification(detail.Entry, detail.Pending);
            IReadOnlyList<string> recipients = await _panel.CompletionRecipientsAsync(CancellationToken.None);
            string[] targets = recipients
                .Where(user => !string.Equals(user, actor, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var data = new Dictionary<string, string>
            {
                ["type"] = CapturedNotificationType,
                ["reference"] = detail.Entry.Reference,
                ["url"] = EntryPanelRules.EntryUrl(detail.Entry.Reference),
                ["severity"] = severity,
                ["pending"] = string.Join(",", detail.Pending.Select(item => item.Code))
            };

            await Task.WhenAll(targets.Select(user => SendSafeAsync(user, title, body, data)));
        }
        catch (Exception exception)
        {
            ForgetNotification(reference);
            _logger.LogWarning(exception, "No se pudo avisar la captura terminada de {Reference}.", reference);
        }
    }

    private async Task SendSafeAsync(string user, string title, string body, IDictionary<string, string> data)
    {
        try
        {
            await _notifications.SendToUserOrInboxAsync(user, title, body, new Dictionary<string, string>(data));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "No se pudo avisar a {User} la entrada capturada.", user);
        }
    }
}
