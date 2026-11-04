namespace PMMovil.Api.Models;

public class CargarRelacionResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public RelacionInfo? Relacion { get; set; }
    public List<EtiquetaInfo> Etiquetas { get; set; } = new();
    public string EstatusCarga { get; set; } = string.Empty;
    public int TotBultos { get; set; }
    public string VersionHoja { get; set; } = string.Empty;
}

public class RelacionInfo
{
    public string Relno { get; set; } = string.Empty;
    public string Estatus { get; set; } = string.Empty;
    public string EstatusCarga { get; set; } = string.Empty;
    public string Manejo { get; set; } = string.Empty;
    public string Cliente { get; set; } = string.Empty;
    public string IdCliente { get; set; } = string.Empty;
    public string CidTransfer { get; set; } = string.Empty;
    public string OpTransfer { get; set; } = string.Empty;
    public string Caja { get; set; } = string.Empty;
    public string UsucargaMcia { get; set; } = string.Empty;
    public string UsuAutoriza { get; set; } = string.Empty;
    public string Sellos { get; set; } = string.Empty;
    public string NumTractor { get; set; } = string.Empty;
    public string GafeteOp { get; set; } = string.Empty;
    public string Planta { get; set; } = string.Empty;
    public string VersionHoja { get; set; } = string.Empty;
}

public class EtiquetaInfo
{
    public int Id { get; set; }
    public string TextoEtiqueta { get; set; } = string.Empty;
    public string TextoPagina { get; set; } = string.Empty;
    public string Creferencia { get; set; } = string.Empty;
    public int Nlineno { get; set; }
    public int Total { get; set; }
    public bool Escaneado { get; set; }
    public string Estatus { get; set; } = string.Empty;
    public string Usucarga { get; set; } = string.Empty;
    public DateTime? TimeScan { get; set; }
    public string Manejo { get; set; } = string.Empty;
    public string Historial { get; set; } = string.Empty;
    /// <summary>La etiqueta es una reimpresión (etiqueta_mcia.reimpresa = 1): al escanearla la relación se bloquea.</summary>
    public bool Reimpresa { get; set; }
}

public class ScanLabelRequest
{
    public string Relno { get; set; } = string.Empty;
    public string TextoEtiqueta { get; set; } = string.Empty;
    public string AutorizacionToken { get; set; } = string.Empty;
    public string Usuario { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
}

public class ScanLabelResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Tipo { get; set; } = string.Empty;
    public string Relno { get; set; } = string.Empty;
    public string? EntradaCref { get; set; }
    public string EstatusCarga { get; set; } = string.Empty;
    public string BloqueoMotivo { get; set; } = string.Empty;
    public int EtiquetasBloqueadas { get; set; }
    public bool RequiereCasetaQr { get; set; } = true;
}

public class ValidarCasetaQrRequest
{
    public string Relno { get; set; } = string.Empty;
    public string Entrada { get; set; } = string.Empty;
    public string CasetaId { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
}

public class ValidarCasetaQrResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Tipo { get; set; } = string.Empty;
    public string Relno { get; set; } = string.Empty;
    public string CasetaId { get; set; } = string.Empty;
    public string AutorizacionToken { get; set; } = string.Empty;
    public int ExpiraEnSegundos { get; set; }
    public RelacionInfo? Relacion { get; set; }
    public List<EtiquetaInfo> Etiquetas { get; set; } = new();
    public string EstatusCarga { get; set; } = string.Empty;
    public int TotBultos { get; set; }
    public string VersionHoja { get; set; } = string.Empty;
}

public class FinalizarRequest
{
    public string Usuario { get; set; } = string.Empty;
}

public class PausaRequest
{
    public string Usuario { get; set; } = string.Empty;
}

public class CancelarRequest
{
    public string Usuario { get; set; } = string.Empty;
}

public class SolicitarAutorizacionRequest
{
    public string Usuario { get; set; } = string.Empty;
}

public class DescargarRequest
{
    public string Relno { get; set; } = string.Empty;
    public string TextoEtiqueta { get; set; } = string.Empty;
    public string Usuario { get; set; } = string.Empty;
}

public class FinalizarDescargaRequest
{
    public string Relno { get; set; } = string.Empty;
    public string Usuario { get; set; } = string.Empty;
}

public class SinRelacionRequest
{
    public string Entrada { get; set; } = string.Empty;
    public string TextoEtiqueta { get; set; } = string.Empty;
    public string AutorizacionToken { get; set; } = string.Empty;
    public string Usuario { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string Accion { get; set; } = "cargar";
}

public class ErrorEvitadoRequest
{
    public string Relno { get; set; } = string.Empty;
    public string TextoEtiqueta { get; set; } = string.Empty;
    public string Usuario { get; set; } = string.Empty;
}

public class SalidaDbRelation
{
    public string Relno { get; set; } = string.Empty;
    public string Estatus { get; set; } = string.Empty;
    public string EstatusCarga { get; set; } = string.Empty;
    public string Usucargamcia { get; set; } = string.Empty;
    public string Usuedit { get; set; } = string.Empty;
    public string VersionHoja { get; set; } = string.Empty;
    public string Manejo { get; set; } = string.Empty;
    public string Cidtransfer { get; set; } = string.Empty;
    public string OpTransfer { get; set; } = string.Empty;
    public string Caja { get; set; } = string.Empty;
    public string Cliente { get; set; } = string.Empty;
    public string Idcliente { get; set; } = string.Empty;
    public string UsucargamciaNombre { get; set; } = string.Empty;
    public string Usuautoriza { get; set; } = string.Empty;
    public string Sellos { get; set; } = string.Empty;
    public string NumTractor { get; set; } = string.Empty;
    public string GafeteOp { get; set; } = string.Empty;
    public string Planta { get; set; } = string.Empty;
    public DateTime InicioCarga { get; set; }
    public DateTime FinCarga { get; set; }
    public DateTime AutorizaCarga { get; set; }
    public string Fcruce { get; set; } = string.Empty;
}

public class PendienteAutorizarItem
{
    public string Relno { get; set; } = string.Empty;
    public string Caja { get; set; } = string.Empty;
    public string Usucargamcia { get; set; } = string.Empty;
    public string EstatusCarga { get; set; } = string.Empty;
    public string Planta { get; set; } = string.Empty;
    public string VersionHoja { get; set; } = string.Empty;
    public string Cliente { get; set; } = string.Empty;
    public int TotBultos { get; set; }
    public int EtiquetasCount { get; set; }
}

public class EtiquetaDbRow
{
    public int IdEtiquetaMcia { get; set; }
    public string Textoetiqueta { get; set; } = string.Empty;
    public string Creferencia { get; set; } = string.Empty;
    public int Nlineno { get; set; }
    public int Total { get; set; }
    public DateTime? TimeScan { get; set; }
    public string Estatus { get; set; } = string.Empty;
    public string Usucarga { get; set; } = string.Empty;
    public string Relacion { get; set; } = string.Empty;
    public string Cidmanejo { get; set; } = string.Empty;
    public string Historial { get; set; } = string.Empty;
    public bool Reimpresa { get; set; }
}

public class TotBultosDbRow
{
    public string Bultos { get; set; } = "0";
    public string Manejo { get; set; } = string.Empty;
}
