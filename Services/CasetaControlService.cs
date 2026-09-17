using System.Data;
using Dapper;
using PMMovil.Api.Models;

namespace PMMovil.Api.Services;

public partial class CasetaService
{
    /// <summary>Consulta una visita o relación, incluidas las que ya salieron.</summary>
    public async Task<CasetaFicha?> ObtenerFichaControlAsync(string? casetaId, string? relno, CancellationToken ct)
    {
        using IDbConnection conn = _db.CreateConnection();
        var ficha = await conn.QueryFirstOrDefaultAsync<CasetaFicha>(new CommandDefinition("""
            SELECT casetaid AS CasetaId, relacion AS Relno,
                   CASE WHEN YEAR(farribo) >= 2000 THEN farribo END AS Arribo,
                   CASE WHEN YEAR(fsalida) >= 2000 THEN fsalida END AS Salida,
                   ususalida AS UsuarioSalida
            FROM caseta
            WHERE (@casetaId <> '' AND casetaid = @casetaId)
               OR (@casetaId = '' AND relacion = @relno)
            ORDER BY farribo DESC, idcaseta DESC LIMIT 1
            """, new { casetaId = casetaId?.Trim() ?? "", relno = relno?.Trim() ?? "" }, cancellationToken: ct));
        ficha ??= new CasetaFicha { Relno = relno?.Trim() };
        if (!string.IsNullOrWhiteSpace(ficha.CasetaId))
        {
            var entry = await ObtenerEntradaCasetaAsync(ficha.CasetaId, false);
            if (!entry.Success) return null;
            ficha.Entrada = entry.Datos;
        }
        if (!string.IsNullOrWhiteSpace(ficha.Relno))
        {
            var (table, _) = ResolverTabla(ficha.Relno);
            var row = await conn.QueryFirstOrDefaultAsync<CasetaDetalleDbRow>(new CommandDefinition($"""
                SELECT r.*, r.estatus_carga AS EstatusCarga, t.cnombre
                FROM {table} r LEFT JOIN tranfers t ON r.cidtransfer = t.cidtransfer
                WHERE r.relno = @relno
                """, new { relno = ficha.Relno }, cancellationToken: ct));
            if (row is not null)
            {
                ficha.Relacion = new CasetaDetalle { Relno = row.Relno, Caja = row.Caja ?? "",
                    Estatus = row.STATUS, EstatusCarga = row.EstatusCarga,
                    Transfer = row.Cnombre ?? "", Tractor = row.Num_tractor ?? "",
                    Operador = row.Op_transfer ?? "", Gafete = row.Gafete_op ?? "",
                    Sellos = JoinSellos(row.Sellos, row.Sellos2, row.Sellos3, row.Sellos4),
                    Cliente = ficha.Entrada?.NombreCliente ?? "", IdCliente = ficha.Entrada?.IdCliente ?? "" };
                ficha.PuedeConfirmar = row.EstatusCarga == "AUTORIZADA" && string.IsNullOrWhiteSpace(row.Usuedit) && ficha.Salida is null;
                ficha.Motivo = !string.IsNullOrWhiteSpace(row.Usuedit) ? "La relación está siendo editada."
                    : !ficha.PuedeConfirmar ? "La relación no está pendiente de liberación." : "";
            }
        }
        if (ficha.Entrada is null && ficha.Relacion is null) return null;
        if (ficha.Relacion is null) ficha.Motivo = "Sin relación de salida asignada.";
        ficha.Fotos = (await conn.QueryAsync<GaritaFoto>(new CommandDefinition("""
            SELECT creferencia AS Referencia, nlineno AS Indice,
                   CASE WHEN ntipodocto = 106 THEN 'entrada' ELSE 'salida' END AS Etapa
            FROM fotos
            WHERE (creferencia = @CasetaId AND ntipodocto = 106)
               OR (creferencia = @Relno AND ntipodocto = 101)
            ORDER BY ntipodocto DESC, nlineno
            """, new { ficha.CasetaId, ficha.Relno }, cancellationToken: ct))).AsList();
        return ficha;
    }

    /// <summary>Resuelve únicamente evidencia de caseta registrada en la base.</summary>
    public async Task<string?> ObtenerRutaFotoControlAsync(string referencia, int indice, string etapa, CancellationToken ct)
    {
        if (indice < 1 || (etapa != "entrada" && etapa != "salida")) return null;
        using IDbConnection conn = _db.CreateConnection();
        var extension = await conn.QueryFirstOrDefaultAsync<string>(new CommandDefinition("""
            SELECT cextension FROM fotos
            WHERE creferencia = @referencia AND nlineno = @indice AND ntipodocto = @tipo LIMIT 1
            """, new { referencia, indice, tipo = etapa == "entrada" ? 106 : 101 }, cancellationToken: ct));
        return extension is null ? null : _photoStorage.FindReadablePhotoPath(referencia, indice, extension);
    }
}
