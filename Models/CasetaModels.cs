namespace PMMovil.Api.Models;

public class CasetaPendiente
{
    public string Relno { get; set; } = string.Empty;
    public string Caja { get; set; } = string.Empty;
    public string UsuAutoriza { get; set; } = string.Empty;
    public DateTime AutorizaCarga { get; set; }
    public string Cliente { get; set; } = string.Empty;

    // Datos del tractocamion que viene a recoger la caja. Los llena el legacy de
    // escritorio (frmopera_relacion.scx, seccion DATOS SALIDA); aqui solo se leen.
    // El panel web ya declaraba estos campos en su interfaz de TypeScript pero el
    // API nunca los mandaba, asi que las columnas Operador/Tractor/Estatus salian
    // siempre vacias ("Sin asignar", "Pendiente").
    public string CidTransfer { get; set; } = string.Empty;
    public string NumTractor { get; set; } = string.Empty;
    public string OpTransfer { get; set; } = string.Empty;
    public string GafeteOp { get; set; } = string.Empty;
    public string Sellos { get; set; } = string.Empty;
    public string Estatus { get; set; } = string.Empty;

    /// <summary>Timestamp del "un clic" de garita. Null mientras no ha salido.</summary>
    public DateTime? SalidaCaseta { get; set; }
}

public class CasetaDetalle
{
    public string Relno { get; set; } = string.Empty;
    public string Estatus { get; set; } = string.Empty;
    public string EstatusCarga { get; set; } = string.Empty;
    public string Transfer { get; set; } = string.Empty;
    public string Operador { get; set; } = string.Empty;
    public string Caja { get; set; } = string.Empty;
    public string Sellos { get; set; } = string.Empty;
    public string Tractor { get; set; } = string.Empty;
    public string Gafete { get; set; } = string.Empty;
    public string Cliente { get; set; } = string.Empty;
    public string IdCliente { get; set; } = string.Empty;
}

public class ConfirmarSalidaRequest
{
    public string Usuario { get; set; } = string.Empty;
    public string Observaciones { get; set; } = string.Empty;
    public List<string> FotosBase64 { get; set; } = new();

    /// <summary>
    /// Cuántas fotos de salida se subieron ANTES por lotes (<c>salida-fotos</c>). Con valor, la confirmación no recibe fotos:
    /// comprueba que estén todas y quita las que sobren de un intento anterior. Sin valor (apps viejas) las fotos vienen
    /// en <see cref="FotosBase64"/> como siempre.
    /// </summary>
    public int? TotalFotos { get; set; }
}

/// <summary>Un lote de fotos de salida (pocas por envío, para que una red lenta no deje la salida colgada).</summary>
public class SubirFotosSalidaRequest
{
    /// <summary>Posición (desde 1) de la primera foto del lote dentro de las fotos de la salida.</summary>
    public int IndiceInicial { get; set; } = 1;

    public List<string> FotosBase64 { get; set; } = new();
}

public class CasetaResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public CasetaDetalle? Detalle { get; set; }
}

public class PendientesListResult
{
    public bool Success { get; set; }
    public List<CasetaPendiente> Pendientes { get; set; } = new();
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// true cuando se pidio una fecha pasada: la lista deja de ser "lo que falta
    /// por salir" y pasa a ser "lo que se autorizo ese dia". El cliente cambia el
    /// rotulo de la tarjeta para no ensenar una cosa diciendo otra.
    /// </summary>
    public bool EsHistorico { get; set; }

    /// <summary>Fecha efectivamente consultada (YYYY-MM-DD), o null si es la cola en vivo.</summary>
    public string? Fecha { get; set; }
}

public class ConfirmarResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class CasetaEntradaCatalogItem
{
    public string Id { get; set; } = string.Empty;
    public string Nombre { get; set; } = string.Empty;
}

public class CasetaEntradaFrequentItem
{
    public string Id { get; set; } = string.Empty;
    public string Nombre { get; set; } = string.Empty;
    public int Uso { get; set; }
    public DateTime? UltimoUso { get; set; }
}

public class CasetaEntradaFrequentResult
{
    public bool Success { get; set; } = true;
    public string Message { get; set; } = string.Empty;
    public List<CasetaEntradaFrequentItem> Items { get; set; } = new();
}

public class CasetaEntradaCatalogosResult
{
    public bool Success { get; set; } = true;
    public string Message { get; set; } = string.Empty;
    public List<CasetaEntradaCatalogItem> Bodegas { get; set; } = new();
    public List<CasetaEntradaCatalogItem> TiposCaja { get; set; } = new();
    public List<CasetaEntradaCatalogItem> LineasCamion { get; set; } = new();
    public List<CasetaEntradaCatalogItem> LineasCaja { get; set; } = new();
    public List<CasetaEntradaCatalogItem> Clientes { get; set; } = new();
    public List<CasetaEntradaCatalogItem> Paises { get; set; } = new();
}

public class CasetaEntradaGuardarRequest
{
    /// <summary>
    /// Campo heredado para compatibilidad. La identidad que se guarda se toma del usuario autenticado.
    /// </summary>
    public string Usuario { get; set; } = string.Empty;
    /// <summary>
    /// Campo heredado para compatibilidad. El nombre del responsable se resuelve en el servidor desde su sesión.
    /// </summary>
    public string NombreUsuario { get; set; } = string.Empty;
    public string Planta { get; set; } = string.Empty;
    public string IdBodega { get; set; } = string.Empty;
    public string Bodega { get; set; } = string.Empty;
    public string Cliente { get; set; } = string.Empty;
    public string IdCliente { get; set; } = string.Empty;
    public string NombreCliente { get; set; } = string.Empty;
    public string Operacion { get; set; } = string.Empty;
    public string Movimiento { get; set; } = string.Empty;
    public string IdTipoCaja { get; set; } = string.Empty;
    public string TipoCaja { get; set; } = string.Empty;
    public string DescTipoCaja { get; set; } = string.Empty;
    public string Operador { get; set; } = string.Empty;
    public string NoCamion { get; set; } = string.Empty;
    public string LineaCamion { get; set; } = string.Empty;
    public string IdLineaCamion { get; set; } = string.Empty;
    public string NombreLineaCamion { get; set; } = string.Empty;
    public string NumCaja { get; set; } = string.Empty;
    public string LineaCaja { get; set; } = string.Empty;
    public string IdLineaCaja { get; set; } = string.Empty;
    public string NombreLineaCaja { get; set; } = string.Empty;
    public string IdPais { get; set; } = string.Empty;
    public string Pais { get; set; } = string.Empty;
    public string Placas { get; set; } = string.Empty;
    public string Sellos { get; set; } = string.Empty;
    public string Observaciones { get; set; } = string.Empty;
    public string Combustible { get; set; } = string.Empty;
    public string TemperaturaOculta { get; set; } = string.Empty;
    public string Pedimento { get; set; } = string.Empty;
    public string Guia { get; set; } = string.Empty;
    public List<string> Fotos { get; set; } = new();
    /// <summary>Índices de evidencia remota que se deben eliminar por petición explícita de la app.</summary>
    public List<int> IndicesFotosEliminar { get; set; } = new();
    /// <summary>
    /// La app movil puede guardar primero el folio y enviar la evidencia en
    /// segundo plano. En ese caso el correo se dispara al finalizar la cola.
    /// </summary>
    public bool DiferirCorreo { get; set; }

    /// <summary>
    /// Posiciones de las fotos nuevas que la app va a subir despues de guardar (con <see cref="DiferirCorreo"/>). Con
    /// ellas el servidor sabe cuando ya llegaron todas y manda el aviso solo, sin depender de que la tableta marque el
    /// ultimo lote. Vacio en apps anteriores: ahi decide el ultimo lote o el respaldo por tiempo.
    /// </summary>
    public List<int> IndicesFotosEsperadas { get; set; } = new();
    public bool RequiereImpo { get; set; }
    public string Casetaid { get; set; } = string.Empty;
    public string Idcaseta { get; set; } = string.Empty;
    public bool Actualizado { get; set; }

    /// <summary>
    /// true cuando el registro se precargo desde un pre-registro (camion sin
    /// caja, escaneado en garita). Es solo dato de procedencia/auditoria --
    /// ya NO decide que campos son obligatorios, eso lo hace TipoRegistro. No
    /// se persiste en `caseta`; la trazabilidad de que vino de un
    /// pre-registro vive en caseta_preregistro.casetaid.
    /// </summary>
    public bool OrigenPreRegistro { get; set; }

    /// <summary>
    /// Folio del pre-registro que se esta consumiendo al generar la entrada.
    /// Es opcional para conservar compatibilidad con clientes anteriores; si
    /// no llega, el API intenta vincularlo por unidad, placas, linea y bodega.
    /// </summary>
    public string PreregistroFolio { get; set; } = string.Empty;

    /// <summary>
    /// La app puede continuar con la entrada cuando el guardia confirma una
    /// discrepancia de placas. Se conserva para auditoria del cierre del QR.
    /// </summary>
    public bool PlacasNoCoinciden { get; set; }

    /// <summary>
    /// "CAJA" (default) o "TRACTOCAMION". Decide que campos exime la
    /// validacion server-side (ValidarEntradaCaseta): un tractocamion sin
    /// caja no tiene Cliente, Tipo de Caja, Linea de Caja ni Numero de Caja.
    /// Se persiste tal cual en caseta.tiporegistro.
    /// </summary>
    public string TipoRegistro { get; set; } = "CAJA";
}

public class CasetaEntradaFotoUpload
{
    public int Indice { get; set; }
    public string Base64Image { get; set; } = string.Empty;
}

public class CasetaEntradaFotosRequest
{
    public List<CasetaEntradaFotoUpload> Fotos { get; set; } = new();
    public bool Finalizar { get; set; }
    public bool Actualizado { get; set; }
}

public class CasetaEntradaFotosResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int FotosGuardadas { get; set; }
}

public class CasetaEntradaDetalle
{
    public int IdCaseta { get; set; }
    public string CasetaId { get; set; } = string.Empty;
    public string IdBodega { get; set; } = string.Empty;
    public string Bodega { get; set; } = string.Empty;
    public string TipoOp { get; set; } = string.Empty;
    public string TipoMov { get; set; } = string.Empty;
    public string TipoRegistro { get; set; } = "CAJA";
    public string IdUnidad { get; set; } = string.Empty;
    public string Tipouni { get; set; } = string.Empty;
    public string NumUnidad { get; set; } = string.Empty;
    public string IdTransporteTractor { get; set; } = string.Empty;
    public string Transpor { get; set; } = string.Empty;
    public string LineaCamion { get; set; } = string.Empty;
    public string Nequipo { get; set; } = string.Empty;
    public string IdTransporteCaja { get; set; } = string.Empty;
    public string Transporte { get; set; } = string.Empty;
    public string LineaCaja { get; set; } = string.Empty;
    public string IdPais { get; set; } = string.Empty;
    public string Pais { get; set; } = string.Empty;
    public string Placas { get; set; } = string.Empty;
    public string Sellos { get; set; } = string.Empty;
    public string Operador { get; set; } = string.Empty;
    public string IdCliente { get; set; } = string.Empty;
    public string NombreCliente { get; set; } = string.Empty;
    public string Cliente { get; set; } = string.Empty;
    public string Observaciones { get; set; } = string.Empty;
    public string Temperatura { get; set; } = string.Empty;
    public string Pedimento { get; set; } = string.Empty;
    public int Guias { get; set; }
    public string Combustible { get; set; } = string.Empty;
    public string Planta { get; set; } = string.Empty;
    public bool Actualizado { get; set; }
}

public class CasetaEntradaFotoInfo
{
    public int Indice { get; set; }
    public string NombreArchivo { get; set; } = string.Empty;

    /// <summary>
    /// Cambia cuando la foto se reemplaza. La app la pone en la dirección de la foto para guardarla sin volver a pedirla
    /// (ver <c>CasetaFotoCache</c>). Vacía en fotos sin fila en la base.
    /// </summary>
    public string Version { get; set; } = string.Empty;
}

/// <summary>Resultado de buscar una entrada de caseta por número de caja (nequipo),
/// usado para localizar el folio antes de reenviar su correo.</summary>
public class CasetaEntradaBusquedaPorCajaItem
{
    public string CasetaId { get; set; } = string.Empty;
    public string Nequipo { get; set; } = string.Empty;
    public string Planta { get; set; } = string.Empty;
    public string TipoMov { get; set; } = string.Empty;
    public string TipoOp { get; set; } = string.Empty;
    public string IdCliente { get; set; } = string.Empty;
    public string NombreCliente { get; set; } = string.Empty;
    public DateTime Farribo { get; set; }
}

public class CasetaEntradaObtenerResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public CasetaEntradaDetalle? Datos { get; set; }
    public List<string> FotosBase64 { get; set; } = new();
    public List<CasetaEntradaFotoInfo> Fotos { get; set; } = new();
}

public class CasetaEntradaGuardarResult
{
    public bool Success { get; set; }
    public bool Exito
    {
        get => Success;
        set => Success = value;
    }

    public string Message { get; set; } = string.Empty;
    public string Mensaje
    {
        get => Message;
        set => Message = value;
    }

    public string CasetaId { get; set; } = string.Empty;
    public List<string> CasetaIds { get; set; } = new();
}

public class CasetaEntradaBusquedaItem
{
    public string Casetaid { get; set; } = string.Empty;
    public string Nequipo { get; set; } = string.Empty;
    public string Placas { get; set; } = string.Empty;
    public string Nunidad { get; set; } = string.Empty;
    public string Transporu { get; set; } = string.Empty;
    public string Transpore { get; set; } = string.Empty;
    public string Cliente { get; set; } = string.Empty;
    public string IdCliente { get; set; } = string.Empty;
    public string TipoOp { get; set; } = string.Empty;
    public string TipoMov { get; set; } = string.Empty;
    public string TipoRegistro { get; set; } = "CAJA";
    public DateTime? Farribo { get; set; }
    public int Fotos { get; set; }
    public string MatchField { get; set; } = string.Empty;
    public int MatchScore { get; set; }
    public string MatchReason { get; set; } = string.Empty;
}

public class CasetaEntradaOcrCandidate
{
    public string Value { get; set; } = string.Empty;
    public double? Confidence { get; set; }
    public string Source { get; set; } = "server";
    public string Reason { get; set; } = string.Empty;
}

public class CasetaEntradaPlateOcrRequest
{
    public string Base64Image { get; set; } = string.Empty;
}

public class CasetaEntradaPlateOcrResult
{
    public bool Success { get; set; }
    public string Placa { get; set; } = string.Empty;
    public double? Confidence { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Source { get; set; } = "server";
    public List<CasetaEntradaOcrCandidate> Candidates { get; set; } = new();
}

public class CasetaEntradaBoxOcrRequest
{
    public string Base64Image { get; set; } = string.Empty;
}

public class CasetaEntradaBoxOcrResult
{
    public bool Success { get; set; }
    public string NumCaja { get; set; } = string.Empty;
    public double? Confidence { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Source { get; set; } = "server";
    public List<CasetaEntradaOcrCandidate> Candidates { get; set; } = new();
}
