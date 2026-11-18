using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PMMovil.Api.Models
{
    public class EntradaPendiente
    {
        public string CrefAgrupada { get; set; } = string.Empty;
        public string ClienteId { get; set; } = string.Empty;
        public string NombreCliente { get; set; } = string.Empty;
        public string dFecha { get; set; } = string.Empty;
        public int TotalFacturas { get; set; }
        public int FacturasCompletas { get; set; }
        public int TotalPartidas { get; set; }
        public int PartidasRevisadas { get; set; }
        public bool TodasRelacionCompleta { get; set; }
        public string? InicioRevision { get; set; }
        public string? FinRevision { get; set; }
        public string? UsuarioRevisando { get; set; }
        public string? UsuarioTomoDocumento { get; set; }
        public int? AsignPrioridad { get; set; }
        public bool? EsAsignada { get; set; }
        public int? IdAsignacion { get; set; }

        /// <summary>
        /// Quién trae la carpeta (la tomó con «Tomar carpeta» y todavía no la termina). Responde a «¿quién trae la 123?».
        /// </summary>
        public string? TomadaPor { get; set; }

        /// <summary>Cuándo la tomó (ISO, hora del servidor).</summary>
        public string? TomadaDesde { get; set; }

        /// <summary>Segundos desde la toma, medidos por el servidor.</summary>
        public long? SegundosDesdeToma { get; set; }
    }

    /// <summary>Una coincidencia de la búsqueda rápida: la entrada con su estado real y si salió por tolerancia a un error de tecleo.</summary>
    public class EntradaBusquedaHit
    {
        public EntradaPendiente Item { get; set; } = new();
        public bool Aproximada { get; set; }
    }

    /// <summary>Respuesta de <c>GET Revision/buscar-rapido</c>: las mejores coincidencias y cuántas hubo en total.</summary>
    public class BusquedaRapidaResultado
    {
        public List<EntradaBusquedaHit> Hits { get; set; } = new();
        public int Total { get; set; }
        public bool Tolerante { get; set; }
    }

    public class FacturaRevision
    {
        public string Cnofact { get; set; } = string.Empty;
        public string Creferencia { get; set; } = string.Empty;
        public string CrefAgrupada { get; set; } = string.Empty;
        public string dFecha { get; set; } = string.Empty;
        public string Cpedido { get; set; } = string.Empty;
        public string Bodegafact { get; set; } = string.Empty;
        public string Cclienteid { get; set; } = string.Empty;
        public string NombreCliente { get; set; } = string.Empty;
        public string NombreProveedor { get; set; } = string.Empty;
        public double nPesoLbs { get; set; }
        public double nPesoKgs { get; set; }
        public int TotBultos { get; set; }
        public string Tipoop { get; set; } = string.Empty;
        public string Marcas { get; set; } = string.Empty;
        public string Numeros { get; set; } = string.Empty;
        public string Obsrev { get; set; } = string.Empty;
        public string Discrepancias { get; set; } = string.Empty;
        public string Usurev { get; set; } = string.Empty;
        public int StatusRev { get; set; }
        public int TotBultosDoc { get; set; }
        public string Usumodi { get; set; } = string.Empty;
        public string fModi { get; set; } = string.Empty;
        public bool RelacionCompleta { get; set; }
        public string Obsrev2 { get; set; } = string.Empty;
        [JsonPropertyName("usuedit")]
        public string UsuEdit { get; set; } = string.Empty;
    }

    public class PartidaRevision
    {
        public int IdRevdet { get; set; }
        public string Cnofact { get; set; } = string.Empty;
        public string CnoParte { get; set; } = string.Empty;
        public string ParteCte { get; set; } = string.Empty;
        public bool Hazmat { get; set; }
        public string CptCveHazmat { get; set; } = string.Empty;
        public string CDescripcion { get; set; } = string.Empty;
        public string Marca { get; set; } = string.Empty;
        public string Modelo { get; set; } = string.Empty;
        public string NumSer { get; set; } = string.Empty;
        public string NumLote { get; set; } = string.Empty;
        public double NCantFactura { get; set; }
        public double NCantOut { get; set; }
        public string CPaisOrigen { get; set; } = string.Empty;
        public string CptCveTmateria { get; set; } = string.Empty;
        public string CptDescTmateria { get; set; } = string.Empty;
        public string TipoMaterial { get; set; } = string.Empty;
        public bool Revisada { get; set; }
        public string Obsrevpartida { get; set; } = string.Empty;
        public string Mnotapartida { get; set; } = string.Empty;
        public double NPeso { get; set; }
        // Datos de clasificación que el formulario VFP muestra junto con la
        // captura física. Se envían para que Android conserve el mismo contexto.
        public double CantidadComercial { get; set; }
        public string UnidadComercial { get; set; } = string.Empty;
        public string UnidadFactura { get; set; } = string.Empty;
        public double CantidadTarifa { get; set; }
        public string UnidadTarifa { get; set; } = string.Empty;
        public double PesoUnitario { get; set; }
        public double ValorUnitario { get; set; }
        public double ValorAgregado { get; set; }
        public double ValorPartidaMe { get; set; }
        public double FactorEquivalencia { get; set; }
        /// <summary>Solo para decidir en el front si se muestra el campo especial de peso en libras (clientes GGD 14-08-17). No es editable desde Revisión.</summary>
        public string Cclienteid { get; set; } = string.Empty;
    }

    public class FotoRevision
    {
        public int IdFotos { get; set; }
        public int nLineNo { get; set; }
        public string cExtension { get; set; } = string.Empty;
        public string UsuCrea { get; set; } = string.Empty;
        public string fCrea { get; set; } = string.Empty;
        /// <summary>107 = evidencia normal de revisión, 103 = evidencia de discrepancia (mismo tipo que daño de Entrada Bodega).</summary>
        public int NTipoDocto { get; set; } = 107;

        /// <summary><c>fcrea</c> en segundos Unix (lo llena la consulta); con él se arma <see cref="Version"/>.</summary>
        [JsonIgnore]
        public long FcreaUnix { get; set; }

        /// <summary>
        /// Versión de la foto para la dirección (<c>&amp;v=</c>): con ella la tableta la guarda para siempre y no la vuelve a
        /// pedir (ver <see cref="PMMovil.Api.Services.FotoCache"/>). La app nueva la usa también para saber que el servidor
        /// ya sirve miniatura y vista: uno anterior no la manda.
        /// </summary>
        public string Version => PMMovil.Api.Services.FotoCache.Version(IdFotos, FcreaUnix);
    }

    public class CatalogoItem
    {
        public string Codigo { get; set; } = string.Empty;
        public string Descrip { get; set; } = string.Empty;
    }

    /// <summary>
    /// Unidad comercial del catálogo legacy. Codigo es el valor visible
    /// (cidunidad), mientras CodigoInterno es el valor que PMM guarda en UMC
    /// (umcint); por ejemplo PIES -> LF.
    /// </summary>
    public class UnidadComercial
    {
        public string Codigo { get; set; } = string.Empty;
        public string Nombre { get; set; } = string.Empty;
        public string CodigoInterno { get; set; } = string.Empty;
    }

    public class PaisFrecuente
    {
        public string Codigo { get; set; } = string.Empty;
        public string Descrip { get; set; } = string.Empty;
        public int Usos { get; set; }
    }

    /// <summary>
    /// Fila de "fotos" resuelta para servir/borrar una imagen (FotoController.VerFoto /
    /// EliminarFoto). Antes se leía con QueryFirstOrDefaultAsync&lt;object&gt; (dynamic),
    /// que en este proyecto ya causó fallos silenciosos por mayúsculas/guion bajo
    /// (ver bugs de Caseta y Finalizar) -- Dapper mapea a una clase tipada sin
    /// distinguir mayúsculas/minúsculas, así que este modelo evita esa trampa.
    /// </summary>
    public class FotoVerInfo
    {
        /// <summary>La fila de <c>fotos</c>: identifica la copia local en el almacén (ver ClientePhotoStorage).</summary>
        public long IdFotos { get; set; }

        /// <summary><c>fcrea</c> en segundos Unix, para la versión de la foto (cuando la consulta lo trae).</summary>
        public long FcreaUnix { get; set; }

        public string Creferencia { get; set; } = string.Empty;
        public string CExtension { get; set; } = string.Empty;
        public int NLineNo { get; set; }
        public int NTipoDocto { get; set; }
        public string Cidcte { get; set; } = string.Empty;
    }
}
