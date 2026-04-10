using System;

namespace PMMovil.Api.Models
{
    /// <summary>
    /// Representa una entrada de bodega pendiente de revision
    /// </summary>
    public class EntradaPendiente
    {
        public string CrefAgrupada { get; set; } = string.Empty;
        public string ClienteId { get; set; } = string.Empty;
        public string NombreCliente { get; set; } = string.Empty;
        public string dFecha { get; set; } = string.Empty;
        public int TotalFacturas { get; set; }
        public int FacturasCompletas { get; set; }
        public bool TodasRelacionCompleta { get; set; }
        public string? InicioRevision { get; set; }
        public string? FinRevision { get; set; }
        /// <summary>Alguien abrió la revisión y tiene el timer corriendo (finiciorevision activo).</summary>
        public string? UsuarioRevisando { get; set; }
        /// <summary>Alguien tomó el documento pero aún no inició el timer de revisión.</summary>
        public string? UsuarioTomoDocumento { get; set; }
    }

    /// <summary>
    /// Representa una factura individual dentro de una entrada
    /// </summary>
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
        public string UsuEdit { get; set; } = string.Empty;
    }

    /// <summary>
    /// Representa una partida o producto individual de una factura
    /// </summary>
    public class PartidaRevision
    {
        public int IdRevdet { get; set; }
        public string Cnofact { get; set; } = string.Empty;
        public string CnoParte { get; set; } = string.Empty;
        public string cDescripcion { get; set; } = string.Empty;
        public string Marca { get; set; } = string.Empty;
        public string Modelo { get; set; } = string.Empty;
        public string NumSer { get; set; } = string.Empty;
        public double nCantFactura { get; set; }
        public string cPaisOrigen { get; set; } = string.Empty;
        public bool Revisada { get; set; }
    }

    /// <summary>
    /// Representa los datos de una foto capturada en bodega
    /// </summary>
    public class FotoRevision
    {
        public int IdFotos { get; set; }
        public int nLineNo { get; set; }
        public string cExtension { get; set; } = string.Empty;
        public string UsuCrea { get; set; } = string.Empty;
        public string fCrea { get; set; } = string.Empty;
    }

    /// <summary>
    /// Item simple para catálogos, puede ser reutilizada.
    /// </summary>
    public class CatalogoItem
    {
        public string Codigo { get; set; } = string.Empty;
        public string Descrip { get; set; } = string.Empty;
    }
}
