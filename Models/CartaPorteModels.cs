namespace PMMovil.Api.Models;

public class CartaPorteDetalle
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public TransferInfo? Transfer { get; set; }
    public UbicacionInfo? UbicacionOrigen { get; set; }
    public UbicacionInfo? UbicacionDestino { get; set; }
    public TraficoInfo? Trafico { get; set; }
    public List<PartidaInfo> Partidas { get; set; } = new();
    public UsuarioCartaPorte? Usuario { get; set; }
}

public class TransferInfo
{
    public string CidTransfer { get; set; } = string.Empty;
    public string Nombre { get; set; } = string.Empty;
    public string Rfc { get; set; } = string.Empty;
    public string Calle { get; set; } = string.Empty;
    public string NumExt { get; set; } = string.Empty;
    public string NumInt { get; set; } = string.Empty;
    public string CveColonia { get; set; } = string.Empty;
    public string Colonia { get; set; } = string.Empty;
    public string CodPostal { get; set; } = string.Empty;
    public string CveLocalidad { get; set; } = string.Empty;
    public string Localidad { get; set; } = string.Empty;
    public string CveMunicipio { get; set; } = string.Empty;
    public string Municipio { get; set; } = string.Empty;
    public string CveEstado { get; set; } = string.Empty;
    public string Estado { get; set; } = string.Empty;
    public string Contacto { get; set; } = string.Empty;
    public string Telefono { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

public class UbicacionInfo
{
    public string CidUbicacion { get; set; } = string.Empty;
    public string Nombre { get; set; } = string.Empty;
    public string Rfc { get; set; } = string.Empty;
    public string Calle { get; set; } = string.Empty;
    public string NumExt { get; set; } = string.Empty;
    public string NumInt { get; set; } = string.Empty;
    public string CveColonia { get; set; } = string.Empty;
    public string Colonia { get; set; } = string.Empty;
    public string CodPostal { get; set; } = string.Empty;
    public string CveLocalidad { get; set; } = string.Empty;
    public string Localidad { get; set; } = string.Empty;
    public string CveMunicipio { get; set; } = string.Empty;
    public string Municipio { get; set; } = string.Empty;
    public string CveEstado { get; set; } = string.Empty;
    public string Estado { get; set; } = string.Empty;
    public string Contacto { get; set; } = string.Empty;
    public string Telefono { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

public class TraficoInfo
{
    public string Creferencia { get; set; } = string.Empty;
    public string Cclienteidtraf { get; set; } = string.Empty;
    public string Cliente { get; set; } = string.Empty;
    public string Rfc { get; set; } = string.Empty;
    public string Regimen { get; set; } = string.Empty;
    public string Bodega { get; set; } = string.Empty;
    public string Tipop { get; set; } = string.Empty;
    public string Caduana { get; set; } = string.Empty;
    public string Nnopatente { get; set; } = string.Empty;
    public string Pedimento { get; set; } = string.Empty;
    public string Ctamex { get; set; } = string.Empty;
    public string Ctaame { get; set; } = string.Empty;
    public string Ctatipo { get; set; } = string.Empty;
    public string Transfer { get; set; } = string.Empty;
    public string UbicaOrigen { get; set; } = string.Empty;
    public string UbicaDestino { get; set; } = string.Empty;
    public string Placas { get; set; } = string.Empty;
    public string Vehiculo { get; set; } = string.Empty;
    public string Cnocaja { get; set; } = string.Empty;
    public string Cidmanejo { get; set; } = string.Empty;
    public DateTime FechaCruce { get; set; }
}

public class PartidaInfo
{
    public string BienesTrans { get; set; } = string.Empty;
    public string DescBienes { get; set; } = string.Empty;
    public double Cantidad { get; set; }
    public string Unidad { get; set; } = string.Empty;
    public string UnidadSat { get; set; } = string.Empty;
    public string Embalaje { get; set; } = string.Empty;
    public string DescEmbalaje { get; set; } = string.Empty;
    public double Peso { get; set; }
    public string Fraccion { get; set; } = string.Empty;
    public string Uuid { get; set; } = string.Empty;
    public string Hazmat { get; set; } = string.Empty;
    public string HazmatSat { get; set; } = string.Empty;
    public double ValorMerc { get; set; }
    public string Moneda { get; set; } = string.Empty;
    public string TipoMateria { get; set; } = string.Empty;
}

public class UsuarioCartaPorte
{
    public string IdUsuario { get; set; } = string.Empty;
    public string Nombre { get; set; } = string.Empty;
    public string Correo { get; set; } = string.Empty;
}

public class TransferirResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? ArchivoGenerado { get; set; }
}
