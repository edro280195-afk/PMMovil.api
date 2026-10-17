using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

/// <summary>Reglas puras compartidas por el API y las pruebas sin base de datos.</summary>
public static class LabelCaptureRules
{
    public static string Normalize(string? value) => value?.Trim() ?? "";

    public static string Fingerprint(ConfirmLabelCaptureRequest request, string photoHash)
    {
        string payload = JsonSerializer.Serialize(request with { PhotoBase64 = "" }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload + photoHash)));
    }

    public static string AppendLots(string? current, string incoming)
    {
        List<string> lots = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string item in Regex.Split($"{current ?? ""},{incoming}", @"[,;\r\n]+"))
        {
            string lot = item.Trim();
            if (lot.Length > 0 && seen.Add(lot)) lots.Add(lot);
        }
        return string.Join(", ", lots);
    }

    public static bool SnapshotsMatch(LabelCaptureSnapshot expected, LabelCaptureSnapshot current) =>
        Normalize(expected.NumLote) == Normalize(current.NumLote) &&
        Normalize(expected.NumSer) == Normalize(current.NumSer) &&
        Normalize(expected.Marca) == Normalize(current.Marca) &&
        Normalize(expected.Modelo) == Normalize(current.Modelo) &&
        Normalize(expected.CPaisOrigen) == Normalize(current.CPaisOrigen) &&
        expected.Revisada == current.Revisada &&
        Normalize(expected.HazmatUn) == Normalize(current.HazmatUn) &&
        Normalize(expected.HazmatClase) == Normalize(current.HazmatClase) &&
        Normalize(expected.HazmatGrupo) == Normalize(current.HazmatGrupo);

    public static LabelCaptureSnapshot Apply(
        LabelCaptureSnapshot current, LabelCaptureFields fields, LabelCaptureHazmat? hazmat, bool markReviewed) =>
        current with
        {
            NumLote = fields.NumLote is null ? current.NumLote : AppendLots(current.NumLote, fields.NumLote),
            NumSer = fields.NumSer?.Trim() ?? current.NumSer,
            Marca = fields.Marca?.Trim() ?? current.Marca,
            Modelo = fields.Modelo?.Trim() ?? current.Modelo,
            CPaisOrigen = fields.CPaisOrigen?.Trim().ToUpperInvariant() ?? current.CPaisOrigen,
            Revisada = current.Revisada || markReviewed,
            HazmatUn = hazmat?.Un is null ? current.HazmatUn : NormalizeUn(hazmat.Un),
            HazmatClase = hazmat?.Clase?.Trim() ?? current.HazmatClase,
            HazmatGrupo = hazmat?.Grupo?.Trim().ToUpperInvariant() ?? current.HazmatGrupo
        };

    public static string NormalizeUn(string value) =>
        Regex.Replace(value.Trim().ToUpperInvariant(), @"^UN\s*", "");

    public static void ValidateHazmat(string? un, string? clase, string? grupo)
    {
        if (un is not null && !Regex.IsMatch(NormalizeUn(un), @"^[0-9]{4}$"))
            throw new ArgumentException("El número UN debe contener cuatro dígitos.");
        if (clase is not null && !Regex.IsMatch(clase.Trim(), @"^[1-9](?:\.[1-9])?$"))
            throw new ArgumentException("La clase Hazmat no tiene un formato válido.");
        if (grupo is not null && !new[] { "I", "II", "III" }.Contains(grupo.Trim().ToUpperInvariant()))
            throw new ArgumentException("El grupo de embalaje debe ser I, II o III.");
    }

    public static void Validate(ConfirmLabelCaptureRequest request)
    {
        if (request.CaptureId == Guid.Empty || request.IdRevdet <= 0 ||
            !Regex.IsMatch(request.CrefAgrupada?.Trim() ?? "", @"^[A-Za-z0-9_-]{1,30}$"))
            throw new ArgumentException("Falta una captura, referencia o partida válida.");
        if (request.ExpectedSnapshot is null || request.Fields is null)
            throw new ArgumentException("Falta la comparación con los datos originales de la partida.");
        if (request.MarkReviewed && !request.PhysicalReviewConfirmed)
            throw new ArgumentException("Confirma la revisión física antes de marcar la partida como revisada.");
        foreach (string? field in new[] { request.Fields.NumLote, request.Fields.NumSer, request.Fields.Marca,
                     request.Fields.Modelo, request.Fields.CPaisOrigen })
        {
            if (field is not null && (string.IsNullOrWhiteSpace(field) || field.Length > 1024 || field.Any(char.IsControl)))
                throw new ArgumentException("Los datos aceptados no pueden estar vacíos ni contener caracteres de control.");
        }
        if (request.Fields.CPaisOrigen is { } country && !Regex.IsMatch(country.Trim(), @"^[A-Za-z]{3}$"))
            throw new ArgumentException("Selecciona un país válido del catálogo.");
        ValidateHazmat(request.Hazmat?.Un, request.Hazmat?.Clase, request.Hazmat?.Grupo);
        if (request.RawText?.Length > 24000 || request.RawCodes?.Count > 100 ||
            request.RawCodes?.Any(code => code is null || code.Length > 1024) == true || request.Observations?.Count > 64 ||
            request.Observations?.Any(item => item is null || string.IsNullOrWhiteSpace(item.Field) ||
                item.Field.Length > 64 || item.Value is null || item.Value.Length > 1024 ||
                item.RawValue?.Length > 2048 || string.IsNullOrWhiteSpace(item.Source) || item.Source.Length > 64) == true)
            throw new ArgumentException("La lectura excede el tamaño permitido para la auditoría.");
    }
}
