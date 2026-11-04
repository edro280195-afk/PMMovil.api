namespace PMMovil.Api.Services;

/// <summary>
/// El folio de caseta lleva diagonal (<c>E-020485/26</c>), así que la app lo manda en la ruta como <c>E-020485%2F26</c>.
/// ASP.NET Core NO decodifica el %2F al entregar el valor de ruta: el controlador recibe <c>E-020485%2F26</c> y, si lo compara
/// tal cual con el folio del cuerpo, jamás coinciden (era el aviso «El folio de caseta no coincide con el registro a
/// modificar» en cada actualización). Todo folio que llegue por la ruta debe pasar por aquí antes de usarse.
/// </summary>
public static class CasetaFolio
{
    /// <summary>
    /// Deja el folio como se escribe: sin espacios en los extremos y con las codificaciones de URL deshechas. Hasta dos
    /// vueltas porque el valor puede pasar por dos capas de URL antes de llegar (proxy, IIS); más no, para no alterar un
    /// carácter literal que sea parte del folio.
    /// </summary>
    public static string Normalizar(string? folio)
    {
        string valor = (folio ?? string.Empty).Trim();
        if (valor.Length == 0) return valor;

        for (int i = 0; i < 2; i++)
        {
            string decodificado;
            try
            {
                decodificado = Uri.UnescapeDataString(valor);
            }
            catch (UriFormatException)
            {
                break;
            }

            if (decodificado == valor) break;
            valor = decodificado;
        }

        return valor.Trim();
    }

    /// <summary>
    /// La ruta es la identidad del registro que se abrió para editar; el cuerpo solo la confirma. Un cuerpo sin folio no
    /// contradice nada. Mayúsculas y minúsculas no cuentan.
    /// </summary>
    public static bool Coinciden(string? ruta, string? cuerpo)
    {
        if (string.IsNullOrWhiteSpace(cuerpo)) return true;
        return string.Equals(Normalizar(ruta), Normalizar(cuerpo), StringComparison.OrdinalIgnoreCase);
    }
}
