using System;
using System.Collections.Generic;
using System.Linq;

namespace PMMovil.Api.Services;

public static class NotificationRoutes
{
    public const string DefaultUrl = "/home";
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Critical = "critical";

    private static readonly string[] ReferenceKeys =
    {
        "reference", "crefAgrupada", "cnofact", "relno", "folio", "eventoId"
    };

    public static bool IsInternalUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        string value = url.Trim();
        return value.StartsWith('/')
            && !value.StartsWith("//", StringComparison.Ordinal)
            && !value.StartsWith("/\\", StringComparison.Ordinal)
            && !value.Contains("://", StringComparison.Ordinal)
            && !value.Any(char.IsControl);
    }

    public static string WebUrlFor(string? type, IReadOnlyDictionary<string, string>? data)
    {
        if (data is not null && data.TryGetValue("url", out string? explicitUrl) && IsInternalUrl(explicitUrl))
            return explicitUrl!.Trim();

        string normalized = (type ?? string.Empty).Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "solicitud_desbloqueo":
            case "desbloqueo_aprobado":
            case "desbloqueo_rechazado":
            case "revision_asignada":
            case "permiso_revocado":
                return "/panel/monitor";
            case "reporte_pendiente_validar":
            case "reporte_decidido":
                return "/panel/validacion-reportes";
            case "preregistro-arribo":
                return "/panel/preregistro";
            case "entrada-capturada":
                return data is not null && data.TryGetValue("reference", out string? reference) && !string.IsNullOrWhiteSpace(reference)
                    ? EntryPanelRules.EntryUrl(reference)
                    : "/panel/entradas";
        }

        return data is not null && data.TryGetValue("crefAgrupada", out string? cref) && !string.IsNullOrWhiteSpace(cref)
            ? "/panel/monitor"
            : DefaultUrl;
    }

    public static string SeverityFor(string? type)
    {
        string normalized = (type ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Contains("bloqueo", StringComparison.Ordinal) && !normalized.Contains("desbloqueo", StringComparison.Ordinal))
            return Critical;
        return normalized switch
        {
            "solicitud_desbloqueo" => Warning,
            "desbloqueo_rechazado" => Warning,
            "permiso_revocado" => Warning,
            "reporte_pendiente_validar" => Warning,
            _ => Info
        };
    }

    public static string TagFor(string type, IReadOnlyDictionary<string, string> data)
    {
        foreach (string key in ReferenceKeys)
        {
            if (data.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
                return $"{type}:{value.Trim()}";
        }

        return type;
    }

    public static int TimeToLiveSeconds(string severity)
    {
        return severity == Info ? 3600 : 14400;
    }

    public static Dictionary<string, string> Standardize(
        string title,
        string body,
        IDictionary<string, string>? data,
        DateTime utcNow)
    {
        var payload = data is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(data);

        string type = payload.TryGetValue("type", out string? rawType) && !string.IsNullOrWhiteSpace(rawType)
            ? rawType.Trim()
            : "general";
        payload["type"] = type;
        payload["title"] = title;
        payload["body"] = body;

        if (!payload.TryGetValue("url", out string? url) || !IsInternalUrl(url))
            payload["url"] = WebUrlFor(type, payload);
        if (!payload.TryGetValue("severity", out string? severity) || string.IsNullOrWhiteSpace(severity))
            payload["severity"] = SeverityFor(type);
        if (!payload.TryGetValue("tag", out string? tag) || string.IsNullOrWhiteSpace(tag))
            payload["tag"] = TagFor(type, payload);
        payload["ts"] = utcNow.ToString("o");
        return payload;
    }
}
