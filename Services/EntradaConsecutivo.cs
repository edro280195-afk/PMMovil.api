using System.Globalization;

namespace PMMovil.Api.Services;

/// <summary>
/// Reglas puras del consecutivo de Entrada Bodega: el renglón <c>formulario = '01C'</c> de la tabla <c>consecutivo</c>.
/// La referencia de una entrada es <c>01C</c> + año de 2 dígitos + consecutivo + <c>0000</c> (por ejemplo
/// <c>01C263618970000</c>), y <c>consecutivo.numero</c> guarda el SIGUIENTE número por usar.
///
/// Por qué existe una vista previa: antes el número se tomaba al pedirlo (<c>GET Entrada/consecutivo</c>) y el contador
/// avanzaba en ese instante, aunque la entrada se guardara hasta minutos después o nunca (captura abandonada, red que se
/// cae con la respuesta ya dada, un reintento, una prueba). Cada uno de esos casos dejaba un hueco en la numeración.
/// Caseta no tiene ese problema porque toma su folio DENTRO de la transacción que inserta el registro: si no se guarda, el
/// contador no se mueve. Entrada ahora puede hacer lo mismo: la app muestra un número de vista previa (sin consumirlo) y
/// el servidor asigna el definitivo al guardar (ver <c>EntradaService.GuardarRecepcionAsync</c>).
/// </summary>
public static class EntradaConsecutivo
{
    /// <summary>Renglón de <c>consecutivo</c> que lleva la numeración de las entradas de bodega.</summary>
    public const string Formulario = "01C";

    /// <summary>Cuántos números ocupados se brincan, como máximo, antes de rendirse.</summary>
    public const int MaxSaltos = 1000;

    /// <summary>Lo que va después del consecutivo cuando la app no manda tramos 5 y 6 (<c>00</c> + <c>00</c>).</summary>
    public const string SufijoPorOmision = "0000";

    /// <summary>Año de 2 dígitos: 2026 → <c>26</c>.</summary>
    public static string Anio2(DateTime fecha)
        => (fecha.Year % 100).ToString("D2", CultureInfo.InvariantCulture);

    /// <summary>Lo que va antes del consecutivo cuando la app no manda los tramos 1 a 3 (<c>01</c> + <c>C</c> + año).</summary>
    public static string PrefijoPorOmision(DateTime fecha) => "01C" + Anio2(fecha);

    /// <summary>La referencia completa que se guarda en <c>traf.creferencia</c>.</summary>
    public static string ArmarReferencia(string prefijo, int numero, string sufijo)
        => string.Concat(prefijo, numero.ToString(CultureInfo.InvariantCulture), sufijo);

    /// <summary>
    /// Los cinco tramos de la referencia que NO son el consecutivo, con lo que falte completado como siempre
    /// (<c>01</c>, <c>C</c>, año, <c>00</c>, <c>00</c>). El tramo 3 es el año: si la captura empezó el 31 de diciembre y se
    /// guarda el 1 de enero, la app trae el año con el que empezó y se respeta (es la referencia que verá el usuario).
    /// </summary>
    public static PartesReferencia Partes(string? ref1, string? ref2, string? ref3, string? ref5, string? ref6, DateTime ahora)
        => new(
            Ref1: Limpiar(ref1, "01"),
            Ref2: Limpiar(ref2, "C"),
            Ref3: Limpiar(ref3, Anio2(ahora)),
            Ref5: Limpiar(ref5, "00"),
            Ref6: Limpiar(ref6, "00"));

    /// <summary>
    /// El primer número, desde <paramref name="desde"/>, cuya referencia todavía no existe en <c>traf</c>. Se brinca los
    /// ocupados porque el sistema legacy (o una captura manual con número tecleado) puede haber tomado números sin pasar
    /// por el contador. Devuelve <c>null</c> si en <paramref name="maxSaltos"/> intentos no hay ninguno libre.
    /// </summary>
    public static async Task<AsignacionConsecutivo?> BuscarLibreAsync(
        int desde,
        string prefijo,
        string sufijo,
        Func<string, Task<bool>> existe,
        int maxSaltos = MaxSaltos)
    {
        for (int saltos = 0; saltos < maxSaltos; saltos++)
        {
            int numero = desde + saltos;
            string referencia = ArmarReferencia(prefijo, numero, sufijo);
            if (!await existe(referencia))
            {
                return new AsignacionConsecutivo(numero, saltos, referencia);
            }
        }

        return null;
    }

    private static string Limpiar(string? valor, string porOmision)
        => string.IsNullOrWhiteSpace(valor) ? porOmision : valor.Trim();
}

/// <summary>Los tramos de la referencia que acompañan al consecutivo (el tramo 4).</summary>
public sealed record PartesReferencia(string Ref1, string Ref2, string Ref3, string Ref5, string Ref6)
{
    /// <summary>Lo que va antes del consecutivo: <c>01C26</c>.</summary>
    public string Prefijo => Ref1 + Ref2 + Ref3;

    /// <summary>Lo que va después del consecutivo: <c>0000</c>.</summary>
    public string Sufijo => Ref5 + Ref6;
}

/// <summary>El número que le toca a una entrada y cuántos ocupados se brincó para llegar a él.</summary>
public sealed record AsignacionConsecutivo(int Numero, int Saltos, string Referencia)
{
    /// <summary>
    /// Cuánto hay que sumarle al contador al consumir este número: el que se usó más los que se brincó, para que el
    /// siguiente no vuelva a toparse con ellos.
    /// </summary>
    public int Incremento => 1 + Saltos;
}
