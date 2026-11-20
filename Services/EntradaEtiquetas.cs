using System.Data;
using System.Globalization;
using System.Text;
using Dapper;

namespace PMMovil.Api.Services;

/// <summary>
/// Registro de los bultos de una entrada de bodega en <c>etiqueta_mcia</c>: UNA FILA POR BULTO, con el texto
/// <c>REFERENCIA n-total</c> (por ejemplo <c>01C263619470000 14-14</c>). Es exactamente lo que lleva el QR de cada etiqueta
/// que imprime la app (<c>ZebraZplBuilder.fromEntradaForm</c>: <c>"$reference $index-$totalPackages"</c>) y lo que busca el
/// escaneo de Cargar mercancía (<c>SalidaService</c>, por <c>textoetiqueta</c>). Sin estas filas una relación no tiene
/// bultos que cargar.
///
/// Es lo que el legacy hace al generar la entrada. La app guardaba <c>traf</c>, <c>traf3</c> y <c>trafdet</c> pero nunca
/// estas filas, así que las entradas hechas desde la app no se podían cargar (19-nov-2026).
///
/// Cómo escribe el legacy cada fila (medido en producción, SELECT sobre <c>pmmtraf</c>):
/// <list type="bullet">
///   <item><c>creferencia</c> la referencia de la entrada; <c>textoetiqueta</c> = <c>referencia + " " + n + "-" + total</c>.</item>
///   <item><c>nlineno</c> es el NÚMERO DEL BULTO (1..total), no la línea de <c>trafdet</c>: una entrada de 4 líneas
///         (3 + 4 + 5 + 4) tiene 16 filas numeradas del 1 al 16, todas con <c>total = 16</c>.</item>
///   <item><c>total</c> es el total de bultos de la entrada: la suma de <c>trafdet.ncantidad</c> redondeada, mínimo 1 (lo mismo
///         que calcula la app al imprimir). La etiqueta de CARPETA (QR de la referencia sola) no lleva fila.</item>
///   <item><c>usucrea</c>/<c>usumodi</c> el usuario (varchar(15)); <c>fcrea</c>/<c>fmodi</c> el momento del registro.</item>
///   <item><c>relacion</c>, <c>estatus</c>, <c>historial</c>, <c>usucarga</c>, <c>usu_inv</c> y <c>desblq_etiq</c> vacíos (cadena
///         vacía, no NULL); <c>reimpresa</c> 0; <c>time_scan</c> y <c>time_inv</c> se omiten y quedan en su valor por
///         omisión (<c>0000-00-00 00:00:00</c>), como en las filas del legacy.</item>
///   <item>No hay filas repetidas del mismo texto en la historia reciente: por eso el registro es idempotente (solo agrega
///         los que faltan).</item>
/// </list>
/// </summary>
public static class EntradaEtiquetas
{
    /// <summary>Tamaño de <c>etiqueta_mcia.usucrea</c> y <c>usumodi</c> (varchar(15)); en modo estricto un usuario más largo rechazaría el INSERT.</summary>
    public const int MaxUsuario = 15;

    /// <summary>
    /// Tope de bultos que se registran por entrada. Lo más grande que se ha capturado este año son 500; esto es solo para que
    /// un cero de más al teclear (o un 1,000,000) no inunde la tabla ni deje la transacción colgada.
    /// </summary>
    public const int MaxBultos = 10_000;

    /// <summary>Filas por INSERT: un solo viaje a la base por cada tanda en lugar de uno por bulto.</summary>
    public const int FilasPorLote = 200;

    /// <summary>
    /// Manejos (<c>traf.cidmanejo</c>) a los que el legacy NO les registra etiquetas: PIPA (0 de 126 entradas con detalle del 15-sep
    /// al 18-nov; el Cargar mercancía ya los trata como exentos del conteo de bultos) y EXPO (1 de 38; la 01C263619590000 pasó a
    /// EXPO el 19-nov y su etiqueta ya no existe). Registrarles etiquetas dejaría bultos «pendientes» que no
    /// existen físicamente y bloquearía la carga.
    /// </summary>
    public static readonly IReadOnlySet<string> ManejosSinEtiqueta =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PIPA", "EXPO" };

    /// <summary><c>false</c> para los manejos que el legacy no etiqueta (<see cref="ManejosSinEtiqueta"/>); <c>true</c> para todos los demás.</summary>
    public static bool LlevaEtiquetas(string? manejo)
        => !ManejosSinEtiqueta.Contains((manejo ?? string.Empty).Trim());

    /// <summary>
    /// Total de bultos de la entrada, igual que lo calcula la app al imprimir
    /// (<c>maxOf(1, detalles.sumOf { it.cantidad }.roundToInt())</c>): la suma de las cantidades redondeada (.5 hacia arriba)
    /// y como mínimo 1. Un resultado desmedido se topa en <see cref="int.MaxValue"/> para no desbordar; luego
    /// <see cref="MensajeTotalInvalido"/> lo rechaza.
    /// </summary>
    public static int TotalBultos(IEnumerable<decimal> cantidades)
    {
        decimal suma = 0m;
        foreach (decimal cantidad in cantidades)
        {
            suma += cantidad;
            if (suma > int.MaxValue) return int.MaxValue;
        }

        decimal redondeada = Math.Round(suma, MidpointRounding.AwayFromZero);
        if (redondeada >= int.MaxValue) return int.MaxValue;
        return Math.Max(1, (int)redondeada);
    }

    /// <summary>Mensaje para el usuario si el total no se puede registrar; <c>null</c> si está bien.</summary>
    public static string? MensajeTotalInvalido(int total)
        => total > MaxBultos
            ? $"El total de bultos ({total.ToString("N0", CultureInfo.InvariantCulture)}) supera el máximo que se puede registrar " +
              $"({MaxBultos.ToString("N0", CultureInfo.InvariantCulture)}). Revisa las cantidades de la mercancía."
            : null;

    /// <summary>El texto de la etiqueta de un bulto: <c>01C263619470000 14-14</c> (un espacio entre la referencia y el número).</summary>
    public static string Texto(string referencia, int ordinal, int total)
        => string.Concat(
            referencia, " ",
            ordinal.ToString(CultureInfo.InvariantCulture), "-",
            total.ToString(CultureInfo.InvariantCulture));

    /// <summary>El usuario como cabe en la tabla: sin espacios en los extremos y de 15 caracteres como máximo.</summary>
    public static string UsuarioParaEtiqueta(string? usuario)
    {
        string limpio = (usuario ?? string.Empty).Trim();
        return limpio.Length <= MaxUsuario ? limpio : limpio[..MaxUsuario];
    }

    /// <summary>
    /// Los números de bulto (1..<paramref name="total"/>) que todavía no tienen fila, dado lo que ya hay registrado para la
    /// referencia. Compara sin distinguir mayúsculas ni espacios finales, como lo hace la columna (latin1_swedish_ci). Así un
    /// reintento, o una entrada que el legacy ya etiquetó, no duplica filas.
    /// </summary>
    public static List<int> Faltantes(string referencia, int total, IEnumerable<string?> existentes)
    {
        var yaRegistrados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? texto in existentes)
        {
            if (!string.IsNullOrWhiteSpace(texto)) yaRegistrados.Add(texto.Trim());
        }

        var faltan = new List<int>();
        for (int ordinal = 1; ordinal <= total; ordinal++)
        {
            if (!yaRegistrados.Contains(Texto(referencia, ordinal, total))) faltan.Add(ordinal);
        }

        return faltan;
    }

    /// <summary>
    /// El INSERT de <paramref name="filas"/> bultos. Parámetros compartidos: <c>@referencia</c>, <c>@total</c>, <c>@usuario</c>;
    /// por fila: <c>@n0</c>, <c>@t0</c>, <c>@n1</c>, <c>@t1</c>… (número y texto). Las columnas que el legacy deja vacías van
    /// como literales; <c>time_scan</c> y <c>time_inv</c> no se nombran: quedan en su valor por omisión.
    /// </summary>
    public static string ArmarInsert(int filas)
    {
        if (filas < 1) throw new ArgumentOutOfRangeException(nameof(filas), "Debe haber al menos una fila.");

        var sql = new StringBuilder(
            "INSERT INTO etiqueta_mcia " +
            "(creferencia, nlineno, total, textoetiqueta, usucrea, fcrea, usumodi, fmodi, " +
            "relacion, estatus, historial, usucarga, usu_inv, reimpresa, desblq_etiq) VALUES ");

        for (int i = 0; i < filas; i++)
        {
            if (i > 0) sql.Append(", ");
            sql.Append("(@referencia, @n").Append(i).Append(", @total, @t").Append(i)
               .Append(", @usuario, NOW(), @usuario, NOW(), '', '', '', '', '', 0, '')");
        }

        return sql.ToString();
    }

    /// <summary>
    /// Registra en <c>etiqueta_mcia</c> los bultos de la entrada <paramref name="referencia"/> que todavía no tengan fila, dentro
    /// de la transacción <paramref name="tran"/> del guardado: si algo falla después, también se revierten, y si fallan ellos
    /// se revierte la entrada (una entrada sin sus bultos no se puede cargar). Regresa cuántas filas agregó.
    /// </summary>
    public static async Task<int> RegistrarAsync(
        IDbConnection conn,
        IDbTransaction tran,
        string referencia,
        int total,
        string? usuario)
    {
        if (string.IsNullOrWhiteSpace(referencia))
            throw new ArgumentException("La referencia es obligatoria.", nameof(referencia));
        if (total < 1)
            throw new ArgumentOutOfRangeException(nameof(total), "El total de bultos debe ser al menos 1.");
        if (MensajeTotalInvalido(total) is { } mensaje)
            throw new InvalidOperationException(mensaje);

        IEnumerable<string> existentes = await conn.QueryAsync<string>(
            "SELECT textoetiqueta FROM etiqueta_mcia WHERE creferencia = @referencia",
            new { referencia },
            transaction: tran);

        List<int> faltan = Faltantes(referencia, total, existentes);
        if (faltan.Count == 0) return 0;

        string usuarioFila = UsuarioParaEtiqueta(usuario);

        foreach (int[] lote in faltan.Chunk(FilasPorLote))
        {
            var parametros = new DynamicParameters();
            parametros.Add("referencia", referencia, DbType.String);
            parametros.Add("total", total, DbType.Int32);
            parametros.Add("usuario", usuarioFila, DbType.String);
            for (int i = 0; i < lote.Length; i++)
            {
                parametros.Add("n" + i, lote[i], DbType.Int32);
                parametros.Add("t" + i, Texto(referencia, lote[i], total), DbType.String);
            }

            await conn.ExecuteAsync(ArmarInsert(lote.Length), parametros, transaction: tran);
        }

        return faltan.Count;
    }
}
