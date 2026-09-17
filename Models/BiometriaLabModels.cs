namespace PMMovil.Api.Models;

/// <summary>
/// DTOs del laboratorio biometrico facial. Banco de pruebas temporal: sirve
/// para medir si el reconocimiento facial aguanta las condiciones de bodega
/// antes de comprometer un diseno definitivo. Ver Scripts/bio_lab_schema.sql.
/// </summary>
public class BioLabSujeto
{
    public int Id { get; set; }
    public string ClaveUsuario { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string? Notas { get; set; }
    public bool Activo { get; set; } = true;
    public int Plantillas { get; set; }
}

public class BioLabSujetoRequest
{
    public string ClaveUsuario { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string? Notas { get; set; }
}

/// <summary>
/// Condiciones en las que se tomo una captura. Se repiten en enrolamiento y en
/// reconocimiento porque comparar ambas es justamente el punto del laboratorio.
/// </summary>
public class BioLabCondiciones
{
    public float? LuzLux { get; set; }
    public float? Luma { get; set; }
    public float? Nitidez { get; set; }
    public int? RostroPx { get; set; }
    public float? ScoreDeteccion { get; set; }
    public float? Yaw { get; set; }
    public float? Roll { get; set; }
    public string? Dispositivo { get; set; }
    public string? Camara { get; set; }
}

public class BioLabPlantillaRequest : BioLabCondiciones
{
    public int SujetoId { get; set; }
    /// <summary>128 floats little-endian (512 bytes) en Base64, tal como los saca SFace.</summary>
    public string VectorBase64 { get; set; } = "";
    public string Modelo { get; set; } = "sface-2021dec";
    public int Dims { get; set; } = 128;
}

/// <summary>Una plantilla tal como viaja al dispositivo para el matching local.</summary>
public class BioLabPlantilla
{
    public long Id { get; set; }
    public int SujetoId { get; set; }
    public string ClaveUsuario { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string VectorBase64 { get; set; } = "";
    public int Dims { get; set; }
    public string Modelo { get; set; } = "";
}

/// <summary>
/// El banco completo. Es lo que la app descarga para comparar en el propio
/// dispositivo: 512 bytes por plantilla, asi que aunque fueran 300 usuarios
/// con 5 muestras cada uno seguirian siendo menos de 800 KB.
/// </summary>
public class BioLabBanco
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public List<BioLabPlantilla> Plantillas { get; set; } = new();
    public List<BioLabSujeto> Sujetos { get; set; } = new();
}

public class BioLabPruebaRequest : BioLabCondiciones
{
    public int? SujetoEsperadoId { get; set; }
    public int? SujetoTop1Id { get; set; }
    public float? SimTop1 { get; set; }
    public int? SujetoTop2Id { get; set; }
    public float? SimTop2 { get; set; }
    public float? Umbral { get; set; }
    public string? Veredicto { get; set; }
    public int? MsDeteccion { get; set; }
    public int? MsEmbedding { get; set; }
    public int? MsTotal { get; set; }
    public string? Escenario { get; set; }
    public bool PantallaFlash { get; set; }
    public int? Exposicion { get; set; }
}

/// <summary>
/// Corte estadistico por tramo de luz. Es la respuesta directa a "¿a que nivel
/// de oscuridad deja de servir?": para cada tramo, cuantas acerto y con cuanto
/// margen contra el impostor mas parecido.
/// </summary>
public class BioLabTramoLuz
{
    public string Tramo { get; set; } = "";
    public int Total { get; set; }
    public int Aciertos { get; set; }
    public int FalsosPositivos { get; set; }
    public int Rechazos { get; set; }
    public int SinRostro { get; set; }
    public double? SimPromedio { get; set; }
    public double? SimMinima { get; set; }
    public double? MargenPromedio { get; set; }
}

public class BioLabResumen
{
    public bool Success { get; set; }
    public int TotalPruebas { get; set; }
    public int Aciertos { get; set; }
    public int FalsosPositivos { get; set; }
    public int Rechazos { get; set; }
    public int SinRostro { get; set; }
    public double? MsPromedio { get; set; }
    public List<BioLabTramoLuz> PorLuz { get; set; } = new();
    public List<BioLabTramoLuz> PorEscenario { get; set; } = new();
}

public class BioLabResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public long Id { get; set; }
}
