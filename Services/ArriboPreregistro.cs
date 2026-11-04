namespace PMMovil.Api.Services;

/// <summary>
/// Cuándo una entrada de caseta puede ser el arribo de un pre-registro. Esa respuesta decide si el correo operativo sale
/// a nldoperacion/imexoperacion o se desvía únicamente a desarrollo (ver <c>CasetaService.ConstruirCorreoEntradaCasetaAsync</c>).
/// </summary>
public static class ArriboPreregistro
{
    /// <summary>
    /// El pre-registro es para un tractocamión que llega SIN caja a recoger una. Una entrada con caja (tipo CAJA) jamás
    /// es ese arribo, así que no se le debe buscar pareja por unidad/placas: el mismo tractor que deja una caja hoy
    /// suele tener un pre-registro VIGENTE para volver por otra, y con la búsqueda por unidad el aviso de la caja que dejó
    /// se desviaba a desarrollo sin que nadie lo notara, ni al corregir el registro después.
    /// </summary>
    public static bool PuedeCoincidirPorUnidad(string? tipoRegistro) =>
        string.Equals((tipoRegistro ?? string.Empty).Trim(), "TRACTOCAMION", StringComparison.OrdinalIgnoreCase);
}
