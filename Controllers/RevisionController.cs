using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Dapper;
using PMMovil.Api.Data;
using PMMovil.Api.Models;
using System.Data;
using Microsoft.AspNetCore.SignalR;


namespace PMMovil.Api.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class RevisionController : ControllerBase
    {
        private readonly BaseRepository _db;
        private readonly Microsoft.AspNetCore.SignalR.IHubContext<PMMovil.Api.Hubs.WarehouseHub> _hub;
        private readonly ILogger<RevisionController> _logger;

        public RevisionController(BaseRepository db, Microsoft.AspNetCore.SignalR.IHubContext<PMMovil.Api.Hubs.WarehouseHub> hub, ILogger<RevisionController> logger)
        {
            _db = db;
            _hub = hub;
            _logger = logger;
        }


        /// <summary>
        /// Obtiene la hora actual del servidor para sincronizar relojes entre clientes (R13).
        /// </summary>
        [HttpGet("server-time")]
        public IActionResult GetServerTime()
        {
            return Ok(new { success = true, serverTime = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") });
        }

        /// <summary>
        /// Obtiene el listado de entradas que están pendientes de ser revisadas en bodega.
        /// </summary>
        /// <param name="filtro">Texto opcional para buscar por referencia o cliente solamente</param>
        [HttpGet("pendientes")]
        public async Task<IActionResult> GetEntradasPendientes([FromQuery] string? filtro = "")
        {
            try
            {
                using var conn = _db.CreateConnection();
                
                // --- EXPLICACIÓN DE LA CONSULTA ---
                // Esta consulta es "inteligente" y funciona en 5 pasos para no alentar la base de datos:
                // 1. Primero, busca las referencias. Si no hay filtro, solo toma las 500 más nuevas.
                // 2. Tambien busca referencias que alguien este revisando en este momento (sin importar la fecha).
                // 3. Junta todo y cuenta cuántas facturas tiene cada grupo y cuántas de esas ya están terminadas.
                // 4. Revisa si hay relaciones pendientes (si falta papelería, req: Lupita).
                // 5. Finalmente, trae quién es el ultimo que estuvo revisando esa entrada y si ya termino o no.
                // Al final, nos entrega una lista limpia con todo lo necesario para la pantalla principal

                // Estrategia: carga inteligente por fecha + sesiones activas siempre incluidas.
                // Sin filtro → solo últimos 7 días (evita traer 2000 registros históricos).
                // Con filtro → búsqueda completa sin restricción de fecha.
                bool tieneFiltro = !string.IsNullOrWhiteSpace(filtro);

                string sqlIds = @"
                    SELECT f.cref_agrupada
                    FROM fact f
                    LEFT JOIN clientes cl ON cl.cidcte = f.cclienteid AND cl.inactivo = 0
                    WHERE f.cref_agrupada != '' AND f.cref_agrupada IS NOT NULL
                      AND (
                        @tieneFiltro = 1
                        OR f.dfecha >= DATE_SUB(NOW(), INTERVAL 20 DAY)
                        OR YEAR(f.dfecha) < 2000
                      )
                      AND (@filtro = ''
                           OR f.cref_agrupada LIKE @filtroLike
                           OR f.cclienteid LIKE @filtroLike
                           OR cl.cnombre LIKE @filtroLike)
                    GROUP BY f.cref_agrupada
                    ORDER BY MAX(f.dfecha) DESC
                    LIMIT @limitValue";

                // Sesiones activas: siempre se incluyen sin importar la fecha de la entrada.
                string sqlActive = "SELECT DISTINCT creferencia FROM traftiemposrevision WHERE ffinrevision IS NULL OR ffinrevision < '2000-01-01'";

                var idsGeneral = await conn.QueryAsync<string>(sqlIds, new {
                    filtro       = filtro ?? "",
                    filtroLike   = $"%{filtro}%",
                    tieneFiltro  = tieneFiltro ? 1 : 0,
                    limitValue   = tieneFiltro ? 999999 : 200
                });
                var idsActivos = await conn.QueryAsync<string>(sqlActive);

                var finalIds = idsGeneral.Union(idsActivos).Distinct().ToList();
                if (!finalIds.Any()) return Ok(new { success = true, items = new List<EntradaPendiente>(), truncado = false });

                // 3. Hidratación por pasos (Split-Query approach) para evitar Timeouts.
                
                // 3a. Datos base (Facturas + Cliente)
                string sqlBase = @"
                    SELECT 
                        f.cref_agrupada AS CrefAgrupada,
                        MAX(f.cclienteid) AS ClienteId,
                        MAX(IF(f.dfecha < '2000-01-01', NULL, f.dfecha)) AS dFecha,
                        COUNT(*) AS TotalFacturas,
                        SUM(CASE WHEN f.status_rev = 1 THEN 1 ELSE 0 END) AS FacturasCompletas,
                        MAX(cl.cnombre) AS NombreCliente
                    FROM fact f
                    LEFT JOIN clientes cl ON cl.cidcte = f.cclienteid
                    WHERE f.cref_agrupada IN @finalIds
                    GROUP BY f.cref_agrupada";

                var baseData = (await conn.QueryAsync<EntradaPendiente>(sqlBase, new { finalIds })).ToList();

                // 3b. Sesiones de Usuario (Última sesión de cronómetro)
                string sqlSessions = @"
                    SELECT 
                        t1.creferencia AS CrefAgrupada, 
                        IF(t1.finiciorevision < '2000-01-01', NULL, t1.finiciorevision) AS finiciorevision,
                        IF(t1.ffinrevision < '2000-01-01', NULL, t1.ffinrevision) AS ffinrevision,
                        t1.usuario, 
                        IF(t1.ftomadocumento < '2000-01-01', NULL, t1.ftomadocumento) AS ftomadocumento
                    FROM traftiemposrevision t1
                    INNER JOIN (
                        SELECT creferencia, MAX(idtiemposrevision) as maxid 
                        FROM traftiemposrevision 
                        WHERE creferencia IN @finalIds
                        GROUP BY creferencia
                    ) t2 ON t1.idtiemposrevision = t2.maxid";

                var sessionData = (await conn.QueryAsync(sqlSessions, new { finalIds }))
                    .ToDictionary(x => (string)x.CrefAgrupada, x => x);

                // 3c. Estatus de Relación (Papelería completa)
                string sqlRel = @"
                    SELECT tf2.CREF_ORIGEN AS CrefAgrupada, 
                           SUM(CASE WHEN COALESCE(r2.STATUS,'') != 'COMPLETA' AND r2.relno IS NOT NULL THEN 1 ELSE 0 END) AS Pendientes
                    FROM traf2 tf2
                    INNER JOIN relacion r2 ON tf2.nrelacion = r2.relno
                    WHERE tf2.CREF_ORIGEN IN @finalIds
                    GROUP BY tf2.CREF_ORIGEN";

                var relData = (await conn.QueryAsync(sqlRel, new { finalIds }))
                    .ToDictionary(x => (string)x.CrefAgrupada, x => (int)(long)x.Pendientes);

                // 4. Combinar en memoria (Map)
                foreach(var item in baseData)
                {
                    // Mapear Sesión
                    if (sessionData.TryGetValue(item.CrefAgrupada, out dynamic s))
                    {
                        DateTime? inicio = s.finiciorevision as DateTime?;
                        DateTime? fin    = s.ffinrevision as DateTime?;
                        DateTime? toma   = s.ftomadocumento as DateTime?;

                        item.InicioRevision = inicio?.ToString("yyyy/MM/dd HH:mm:ss");
                        item.FinRevision    = fin?.ToString("yyyy/MM/dd HH:mm:ss");
                        item.UsuarioRevisando = (item.InicioRevision != null && item.FinRevision == null) ? (string)s.usuario : null;
                        
                        // Si no hay cronómetro activo, ver si fue el que tomó el documento
                        if (item.UsuarioRevisando == null && toma != null)
                        {
                            item.UsuarioTomoDocumento = (string)s.usuario;
                        }
                    }

                    // Mapear Relaciones
                    if (relData.TryGetValue(item.CrefAgrupada, out int pendientes))
                    {
                        item.TodasRelacionCompleta = (pendientes == 0);
                    }
                    else
                    {
                        item.TodasRelacionCompleta = true; // Si no hay traf2/relacion, asumimos OK o ignoramos
                    }
                }

                // Ordenar por actividad reciente
                var sorted = baseData.OrderByDescending(x => x.InicioRevision ?? "0000").ThenByDescending(x => x.dFecha).ToList();

                // truncado = true cuando se aplica filtro de fecha (sin búsqueda activa)
                // → el frontend mostrará "Últimos 7 días, busca para ver más"
                return Ok(new { success = true, items = sorted, truncado = !tieneFiltro });

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en GetEntradasPendientes");
                return StatusCode(500, new { success = false, message = "Error interno del servidor" });
            }
        }

        /// <summary>
        /// Obtiene SOLO las entradas que el usuario actual tiene activas (timer corriendo o documento tomado).
        /// Consulta ultrarrápida — devuelve 1-10 filas máximo, sin escaneos completos de tablas.
        /// </summary>
        [HttpGet("mis-activas")]
        public async Task<IActionResult> GetMisActivas()
        {
            try
            {
                string username = User.Identity?.Name ?? "";
                if (string.IsNullOrEmpty(username))
                    return Ok(new { success = true, items = new List<EntradaPendiente>() });

                using var conn = _db.CreateConnection();

                // Paso 1: Las referencias donde la ÚLTIMA sesión de este usuario sigue abierta
                string sqlRefs = @"
                    SELECT t1.creferencia, 
                           IF(t1.finiciorevision < '2000-01-01', NULL, t1.finiciorevision) AS finiciorevision,
                           IF(t1.ffinrevision < '2000-01-01', NULL, t1.ffinrevision) AS ffinrevision,
                           IF(t1.ftomadocumento < '2000-01-01', NULL, t1.ftomadocumento) AS ftomadocumento
                    FROM traftiemposrevision t1
                    INNER JOIN (
                        SELECT creferencia, MAX(idtiemposrevision) AS maxid
                        FROM traftiemposrevision
                        WHERE usuario = @username
                        GROUP BY creferencia
                    ) latest ON t1.idtiemposrevision = latest.maxid
                    WHERE t1.ffinrevision IS NULL OR YEAR(t1.ffinrevision) < 2000
                    ORDER BY t1.finiciorevision DESC
                    LIMIT 10";

                var sesiones = (await conn.QueryAsync(sqlRefs, new { username })).ToList();
                if (!sesiones.Any())
                    return Ok(new { success = true, items = new List<EntradaPendiente>() });

                var refs = sesiones.Select(s => (string)s.creferencia).ToList();

                // Paso 2: Hidratar con datos de facturas y cliente
                string sqlBase = @"
                    SELECT
                        f.cref_agrupada AS CrefAgrupada,
                        MAX(f.cclienteid) AS ClienteId,
                        MAX(cl.cnombre) AS NombreCliente,
                        COUNT(DISTINCT f.cnofact) AS TotalFacturas,
                        SUM(CASE WHEN f.status_rev = 1 THEN 1 ELSE 0 END) AS FacturasCompletas
                    FROM fact f
                    LEFT JOIN clientes cl ON cl.cidcte = f.cclienteid AND cl.inactivo = 0
                    WHERE f.cref_agrupada IN @refs
                    GROUP BY f.cref_agrupada";

                var items = (await conn.QueryAsync<EntradaPendiente>(sqlBase, new { refs })).ToList();

                // Paso 3: Mapear estado de sesión a cada entrada
                var sesionDict = sesiones.ToDictionary(s => (string)s.creferencia, s => s);
                foreach (var item in items)
                {
                    if (!sesionDict.TryGetValue(item.CrefAgrupada, out dynamic s)) continue;

                    DateTime? inicio = s.finiciorevision as DateTime?;
                    DateTime? fin    = s.ffinrevision as DateTime?;
                    DateTime? toma   = s.ftomadocumento as DateTime?;

                    bool timerActivo = inicio != null && (fin == null || fin.Value.Year < 2000);
                    item.InicioRevision       = inicio?.ToString("yyyy/MM/dd HH:mm:ss");
                    item.FinRevision          = fin?.ToString("yyyy/MM/dd HH:mm:ss");
                    item.UsuarioRevisando     = timerActivo ? username : null;
                    item.UsuarioTomoDocumento = !timerActivo ? username : null;
                }

                return Ok(new { success = true, items });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en GetMisActivas");
                return StatusCode(500, new { success = false, message = "Error interno del servidor" });
            }
        }

        /// <summary>
        /// Obtiene las facturas asociadas a una referencia agrupada.
        /// </summary>
        [HttpGet("facturas/{cref_agrupada}")]
        public async Task<IActionResult> GetFacturasEntrada(string cref_agrupada)
        {
            try
            {
                using var conn = _db.CreateConnection();
                
                // Consulta directa de las facturas que pertenecen a una referencia específica.
                string sql = @"
                    SELECT cnofact, cclienteid, tipoop,
                           COALESCE(status_rev, 0)   AS StatusRev,
                           COALESCE(totbultos, 0)    AS TotBultos,
                           COALESCE(npesolbs, 0)     AS nPesoLbs,
                           COALESCE(npesokgs, 0)     AS nPesoKgs,
                           COALESCE(nsubtotal, 0)    AS nSubtotal,
                           COALESCE(nvalorme, 0)     AS nValorme,
                           COALESCE(marcas,'')       AS Marcas,
                           COALESCE(numeros,'')      AS Numeros,
                           COALESCE(usuedit,'')      AS UsuEdit
                    FROM fact
                    WHERE cref_agrupada = @cref
                    ORDER BY cnofact";

                var items = await conn.QueryAsync<FacturaRevision>(sql, new { cref = cref_agrupada });
                return Ok(new { success = true, items });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en GetFacturasEntrada para {cref}", cref_agrupada);
                return StatusCode(500, new { success = false, message = "Error al obtener facturas" });
            }
        }

        /// <summary>
        /// Obtiene el detalle completo de una factura específica.
        /// </summary>
        [HttpGet("factura/{cnofact}")]
        public async Task<IActionResult> GetRevisionFactura(string cnofact)
        {
            try
            {
                using var conn = _db.CreateConnection();
                
                // Esta consulta trae toda la información de la cabecera de la factura,
                // incluyendo el nombre del cliente y si ya se puede considerar 'completa' por el sistema legal.
                string sql = @"
                    SELECT f.creferencia, f.cref_agrupada AS CrefAgrupada, f.cnofact,
                           DATE_FORMAT(f.dfecha, '%d/%m/%Y')        AS dFecha,
                           COALESCE(f.cpedido,'')                   AS Cpedido,
                           COALESCE(f.bodegafact,'')                AS Bodegafact,
                           f.cidfacturo, f.cclienteid AS Cclienteid,
                           (SELECT cnombre FROM clientes
                            WHERE cidcte = f.cclienteid AND inactivo = 0
                            LIMIT 1)                                AS NombreCliente,
                           (SELECT p.cnombre FROM provs p
                            WHERE p.cidprov = (
                                SELECT rd.cprovid FROM revdet rd
                                WHERE rd.cnofact = @cnofact LIMIT 1
                            ) AND p.inactivo = 0
                            LIMIT 1)                                AS NombreProveedor,
                           COALESCE(f.npesolbs,0)                  AS nPesoLbs,
                           COALESCE(f.npesokgs,0)                  AS nPesoKgs,
                           COALESCE(f.totbultos,0)                 AS TotBultos,
                           COALESCE(f.tipoop,'')                   AS Tipoop,
                           COALESCE(f.marcas,'')                   AS Marcas,
                           COALESCE(f.numeros,'')                  AS Numeros,
                           COALESCE(f.obsrev,'')                   AS Obsrev,
                           COALESCE(f.discrepancias,'')            AS Discrepancias,
                           COALESCE(f.usurev,'')                   AS Usurev,
                           COALESCE(f.status_rev, 0)               AS StatusRev,
                           COALESCE(f.totbultosdoc, 0)             AS TotBultosDoc,
                           COALESCE(f.usumodi,'')                  AS Usumodi,
                           DATE_FORMAT(f.fmodi, '%d/%m/%Y %H:%i')  AS fModi,
                           COALESCE(f.obsrev2,'')                  AS Obsrev2,
                           -- Revisa si todos los trámites legales (relaciones) están cerrados
                           (EXISTS (
                               SELECT 1 FROM traf2 t2
                               WHERE t2.CREF_ORIGEN = f.cref_agrupada
                               LIMIT 1
                           ) AND NOT EXISTS (
                               SELECT 1 FROM traf2 t2
                               INNER JOIN relacion r ON t2.nrelacion = r.relno
                               WHERE t2.CREF_ORIGEN = f.cref_agrupada
                                 AND COALESCE(r.STATUS, '') != 'COMPLETA'
                               LIMIT 1
                           ))                                      AS RelacionCompleta,
                           COALESCE(f.usuedit,'')                  AS UsuEdit
                    FROM fact f
                    WHERE f.cnofact = @cnofact
                    LIMIT 1";

                var data = await conn.QueryFirstOrDefaultAsync<FacturaRevision>(sql, new { cnofact });
                
                if (data == null) return NotFound(new { success = false, message = "Factura no encontrada" });
                
                return Ok(new { success = true, data });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en GetRevisionFactura para {factura}", cnofact);
                return StatusCode(500, new { success = false, message = "Error al obtener detalle de factura" });
            }
        }

        /// <summary>
        /// Obtiene todas las partidas (productos) de una entrada completa.
        /// </summary>
        [HttpGet("partidas/{cref_agrupada}")]
        public async Task<IActionResult> GetPartidasEntrada(string cref_agrupada)
        {
            try
            {
                using var conn = _db.CreateConnection();
                
                // Traemos todos los productos de todas las facturas que pertenecen a este grupo.
                string sql = @"
                    SELECT rd.idrevdet AS IdRevdet, rd.cnofact, rd.cnoparte AS CnoParte, rd.cdescripcion,
                           rd.marca, rd.modelo, rd.numser, rd.ncantfactura,
                           rd.cpaisorigen, rd.revisada
                    FROM revdet rd
                    INNER JOIN fact f ON f.cnofact = rd.cnofact
                    WHERE f.cref_agrupada = @cref
                    ORDER BY rd.cnofact, rd.idrevdet";

                var items = await conn.QueryAsync<PartidaRevision>(sql, new { cref = cref_agrupada });
                return Ok(new { success = true, items });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en GetPartidasEntrada para {cref}", cref_agrupada);
                return StatusCode(500, new { success = false, message = "Error al obtener partidas" });
            }
        }

        /// <summary>
        /// Guarda un campo específico de una factura (Auto-save).
        /// </summary>
        [HttpPatch("factura/campo")]
        public async Task<IActionResult> GuardarCampoFactura([FromBody] GuardarCampoFacturaDto payload)
        {
            try
            {
                string cnofact = payload.cnofact;
                string campo = payload.campo;
                string valor = payload.valor.ValueKind != System.Text.Json.JsonValueKind.Null ? payload.valor.ToString() : "";
                
                // Normalizar booleanos para MySQL (True/False -> 1/0)
                if (valor == "True") valor = "1";
                else if (valor == "False") valor = "0";

                string usuario = User.Identity?.Name ?? "Desconocido";

                // Lista de campos que permitimos editar directamente
                var allowedFields = new HashSet<string> { 
                    "marcas", "numeros", "obsrev", "discrepancias", 
                    "status_rev", "npesolbs", "npesokgs", "totbultos" 
                };

                if (!allowedFields.Contains(campo.ToLower()))
                    return BadRequest(new { success = false, message = "Campo no permitido" });

                using var conn = _db.CreateConnection();
                
                // Si cambiamos el estado de revisión, guardamos quién fue el revisador.
                string colUsurev = campo.ToLower() == "status_rev" ? ", usurev = @usuario" : "";
                string sql = $@"UPDATE fact SET `{campo}` = @valor{colUsurev}, 
                                usumodi = @usuario, fmodi = NOW() 
                                WHERE cnofact = @cnofact";

                int rows = await conn.ExecuteAsync(sql, new { valor, usuario, cnofact });
                return Ok(new { success = rows > 0 });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en GuardarCampoFactura");
                return StatusCode(500, new { success = false, message = "Error al guardar campo" });
            }
        }

        /// <summary>
        /// Guarda un campo específico de un producto/partida.
        /// </summary>
        [HttpPatch("partida/campo")]
        public async Task<IActionResult> GuardarCampoProducto([FromBody] GuardarCampoPartidaDto payload)
        {
            try
            {
                int id = payload.id;
                string campo = payload.campo;
                string valor = payload.valor.ValueKind != System.Text.Json.JsonValueKind.Null ? payload.valor.ToString() : "";

                // Normalizar booleanos para MySQL (True/False -> 1/0)
                if (valor == "True") valor = "1";
                else if (valor == "False") valor = "0";

                string usuario = User.Identity?.Name ?? "Desconocido";

                var allowedFields = new HashSet<string> { 
                    "marca", "modelo", "numser", "cpaisorigen", 
                    "ncantfactura", "revisada" 
                };

                if (!allowedFields.Contains(campo.ToLower()))
                    return BadRequest(new { success = false, message = "Campo no permitido" });

                using var conn = _db.CreateConnection();
                string sql = $@"UPDATE revdet SET `{campo}` = @valor, 
                                usumodi = @usuario, fmodi = NOW() 
                                WHERE idrevdet = @id";

                int rows = await conn.ExecuteAsync(sql, new { valor, usuario, id });
                return Ok(new { success = rows > 0 });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en GuardarCampoProducto");
                return StatusCode(500, new { success = false, message = "Error al guardar campo del producto" });
            }
        }

        /// <summary>
        /// Mueve las discrepancias actuales al campo de observaciones secundarias y autoriza la factura.
        /// </summary>
        [HttpPost("factura/aclarar-discrepancias")]
        public async Task<IActionResult> AclararDiscrepancias([FromBody] AccionFacturaDto payload)
        {
            try
            {
                string cnofact = payload.cnofact;
                string usuario = User.Identity?.Name ?? "Desconocido";

                using var conn = _db.CreateConnection();
                
                // Esta regla mueve lo que el revisador puso como 'Problema' (discrepancia) a una nota permanente
                // y limpia el campo de discrepancia para que la factura pueda seguir su proceso.
                string sql = @"
                    UPDATE fact
                    SET obsrev2 = IF(TRIM(COALESCE(obsrev2,'')) = '',
                            CONCAT(COALESCE(discrepancias,''), ' AUTORIZO: ', @usuario),
                            CONCAT(TRIM(obsrev2), ' | ', COALESCE(discrepancias,''), ' AUTORIZO: ', @usuario)),
                        discrepancias = '',
                        usumodi = @usuario,
                        fmodi = NOW()
                    WHERE cnofact = @cnofact";

                int rows = await conn.ExecuteAsync(sql, new { usuario, cnofact });
                return Ok(new { success = rows > 0 });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en AclararDiscrepancias");
                return StatusCode(500, new { success = false, message = "Error al aclarar discrepancias" });
            }
        }

        /// <summary>
        /// Revisa si los productos peligrosos (HazMat) tienen sus hojas de seguridad cargadas.
        /// </summary>
        [HttpGet("factura/validar-hazmat/{cnofact}")]
        public async Task<IActionResult> ValidarHojasSeguridad(string cnofact)
        {
            try
            {
                using var conn = _db.CreateConnection();
                
                // 1. Buscamos todas las partidas que están marcadas como HazMat (Peligrosas).
                //    También revisamos con un cruce a la tabla de productos si ya tienen constancia.
                string sqlPartes = @"
                    SELECT rd.idrevdet, rd.cnoparte, rd.lhazmat, p.idparte
                    FROM revdet rd
                    LEFT JOIN products p ON rd.cclienteid = p.cclienteid
                                        AND rd.cprovid    = p.cprovid
                                        AND rd.cnoparte   = p.cidproduct
                                        AND p.lconstancia = 1
                    WHERE rd.cnofact = @cnofact
                      AND (rd.lhazmat = 'S' OR rd.lhazmat = 1)";

                var partes = await conn.QueryAsync<dynamic>(sqlPartes, new { cnofact });
                var problemList = new List<object>();

                foreach (var p in partes)
                {
                    if (p.idparte == null)
                    {
                        problemList.Add(new { cnoparte = p.cnoparte, problema = "Producto no encontrado en el catálogo de seguridad" });
                        continue;
                    }

                    // 2. Para cada producto peligroso, confirmamos que tenga su hoja en Inglés (800) y Español (801).
                    string sqlDocs = "SELECT ntipodocto FROM documentos_partes WHERE id_parte = @idParte AND ntipodocto IN (800,801)";
                    var docs = await conn.QueryAsync<int>(sqlDocs, new { idParte = p.idparte });
                    
                    if (!docs.Contains(800))
                        problemList.Add(new { cnoparte = p.cnoparte, problema = "Falta Hoja de Seguridad en Inglés (800)" });
                    if (!docs.Contains(801))
                        problemList.Add(new { cnoparte = p.cnoparte, problema = "Falta Hoja de Seguridad en Español (801)" });
                }

                return Ok(new { success = true, problemas = problemList });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en ValidarHojasSeguridad");
                return StatusCode(500, new { success = false, message = "Error al validar seguridad HazMat" });
            }
        }

        /// <summary>
        /// Obtiene el catálogo de países para el llenado de partidas.
        /// </summary>
        [HttpGet("catalogos/paises")]
        public async Task<IActionResult> GetPaises()
        {
            try
            {
                using var conn = _db.CreateConnection();
                string sql = "SELECT Codigo, Descrip FROM view_catalogo3 WHERE UPPER(Tabla) = 'PAISES' ORDER BY Descrip";
                var items = await conn.QueryAsync<CatalogoItem>(sql);
                return Ok(new { success = true, items });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en GetPaises");
                return StatusCode(500, new { success = false, message = "Error al obtener catálogo de países" });
            }
        }

        /// <summary>
        /// Solicita al supervisor que desbloquee una factura para poder editarla.
        /// </summary>
        [HttpPost("solicitar-desbloqueo")]
        public async Task<IActionResult> SolicitarDesbloqueo([FromBody] SolicitarDesbloqueoDto payload)
        {
            try
            {
                string cnofact = payload.cnofact;
                string motivo = payload.motivo;
                string usuario = User.Identity?.Name ?? "Desconocido";

                using var conn = _db.CreateConnection();
                conn.Open();

                using var transaction = conn.BeginTransaction();
                try
                {
                    // Bug #3 fix: transacción + SELECT FOR UPDATE para que el check de duplicado
                    // y el INSERT sean atómicos — evita doble solicitud por doble-tap o carrera.
                    string sqlCheck = "SELECT COUNT(*) FROM rev_desbloqueos WHERE cnofact = @cnofact AND estatus = 0 FOR UPDATE";
                    int count = await conn.ExecuteScalarAsync<int>(sqlCheck, new { cnofact }, transaction);

                    if (count > 0)
                    {
                        transaction.Rollback();
                        return BadRequest(new { success = false, message = "Ya tienes una solicitud pendiente para esta factura" });
                    }

                    string sqlIns = "INSERT INTO rev_desbloqueos (cnofact, usuario_solicita, motivo, estatus) VALUES (@cnofact, @usuario, @motivo, 0); SELECT LAST_INSERT_ID();";
                    int nuevoId = await conn.ExecuteScalarAsync<int>(sqlIns, new { cnofact, usuario, motivo }, transaction);

                    transaction.Commit();

                    // Notificamos vía SignalR para que el monitor del supervisor se actualice al instante.
                    // PARIDAD: Se envían 4 argumentos separados (cnofact, usuario, motivo, id)
                    await _hub.Clients.All.SendAsync("onNuevaSolicitud", cnofact, usuario, motivo, nuevoId);

                    return Ok(new { success = true, id = nuevoId });
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en SolicitarDesbloqueo");
                return StatusCode(500, new { success = false, message = "Error al enviar solicitud de desbloqueo" });
            }
        }

        /// <summary>
        /// Revisa si el supervisor ya dio permiso para editar la factura.
        /// </summary>
        [HttpGet("verificar-desbloqueo/{cnofact}")]
        public async Task<IActionResult> VerificarDesbloqueo(string cnofact)
        {
            try
            {
                using var conn = _db.CreateConnection();
                
                // Buscamos si existe un desbloqueo activo (que no haya caducado).
                string sql = @"SELECT usuario_autoriza AS AutorizadoPor,
                                      DATE_FORMAT(fecha_expiracion, '%Y-%m-%dT%H:%i:%s') AS FechaExpiracion
                               FROM rev_desbloqueos
                               WHERE cnofact = @cnofact
                                 AND estatus = 1
                                 AND (fecha_expiracion IS NULL OR fecha_expiracion > NOW())
                               ORDER BY id_desbloqueo DESC LIMIT 1";

                var data = await conn.QueryFirstOrDefaultAsync<dynamic>(sql, new { cnofact });
                
                return Ok(new { 
                    success = true, 
                    autorizado = data != null, 
                    autorizadoPor = data?.AutorizadoPor,
                    fechaExpiracion = data?.FechaExpiracion
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en VerificarDesbloqueo");
                return StatusCode(500, new { success = false, message = "Error al verificar estatus de desbloqueo" });
            }
        }

        /// <summary>
        /// Valida que todas las facturas que se marcaron como revisadas tengan el nombre del revisador asignado.
        /// Esto es un paso previo indispensable antes de cerrar el cronómetro de la entrada.
        /// </summary>
        [HttpGet("validar-cierre/{creferencia}")]
        public async Task<IActionResult> ValidarEntradaAntesDeCerrar(string creferencia)
        {
            try
            {
                using var conn = _db.CreateConnection();
                
                // Buscamos facturas que digan que ya se revisaron pero que el campo de quien lo hizo esté vacío.
                string sql = @"
                    SELECT cnofact FROM fact
                    WHERE cref_agrupada = @cref
                      AND status_rev = 1
                      AND (usurev IS NULL OR TRIM(usurev) = '')";

                var facturasSinRevisor = await conn.QueryAsync<string>(sql, new { cref = creferencia });
                
                return Ok(new { success = true, facturasSinRevisor });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en ValidarEntradaAntesDeCerrar");
                return StatusCode(500, new { success = false, message = "Error al validar la entrada" });
            }
        }

        /// <summary>
        /// Limpia el nombre del revisador de una factura. 
        /// Útil cuando alguien más va a retomar la revisión.
        /// </summary>
        [HttpPost("factura/limpiar-revisador")]
        public async Task<IActionResult> LimpiarUsurev([FromBody] AccionFacturaDto payload)
        {
            try
            {
                string cnofact = payload.cnofact;
                string usuario = User.Identity?.Name ?? "Desconocido";

                using var conn = _db.CreateConnection();
                string sql = "UPDATE fact SET usurev = '', usumodi = @usuario, fmodi = NOW() WHERE cnofact = @cnofact";
                
                int rows = await conn.ExecuteAsync(sql, new { usuario, cnofact });
                return Ok(new { success = rows > 0 });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en LimpiarUsurev");
                return StatusCode(500, new { success = false, message = "Error al limpiar revisor" });
            }
        }

        /// <summary>
        /// Agrega un nuevo producto vacío a una factura.
        /// Útil cuando el revisor detecta mercancía no declarada.
        /// </summary>
        [HttpPost("partida/agregar")]
        public async Task<IActionResult> AgregarProducto([FromBody] AgregarProductoDto payload)
        {
            try
            {
                string cnofact = payload.cnofact;
                string cref = payload.cref;
                string usuario = User.Identity?.Name ?? "Desconocido";

                using var conn = _db.CreateConnection();
                
                // Insertamos un renglón básico en revdet. Luego el usuario lo llenará vía auto-save.
                string sql = @"
                    INSERT INTO revdet (cnofact, cref, marca, modelo, submodelo, numser, cpaisorigen, ncantfactura, usumodi, fmodi)
                    VALUES (@cnofact, @cref, '', '', '', '', '', 0, @usuario, NOW())";

                // Usamos Dapper para obtener el ID recién creado
                var id = await conn.ExecuteScalarAsync<int>($"{sql}; SELECT LAST_INSERT_ID();", new { cnofact, cref, usuario });
                
                return Ok(new { success = true, id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en AgregarProducto");
                return StatusCode(500, new { success = false, message = "Error al agregar producto" });
            }
        }


        /// <summary>
        /// Elimina un producto de la revisión.
        /// </summary>
        [HttpDelete("partida/{id}")]
        public async Task<IActionResult> EliminarProducto(int id)
        {
            try
            {
                using var conn = _db.CreateConnection();
                string sql = "DELETE FROM revdet WHERE idrevdet = @id";
                int rows = await conn.ExecuteAsync(sql, new { id });
                return Ok(new { success = rows > 0 });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en EliminarProducto");
                return StatusCode(500, new { success = false, message = "Error al eliminar producto" });
            }
        }

        /// <summary>
        /// Devuelve el "lugar" del usuario autenticado.
        /// Si lugar = '01' es bodega central; otros valores indican sucursal remota.
        /// </summary>
        [HttpGet("usuario/lugar")]
        public async Task<IActionResult> GetLugarUsuario()
        {
            try
            {
                string usuario = User.Identity?.Name ?? "";
                using var conn = _db.CreateReportesConnection();
                string sql = "SELECT COALESCE(lugar,'') FROM usuarios WHERE claveusuario = @clave LIMIT 1";
                var lugar = await conn.ExecuteScalarAsync<string>(sql, new { clave = usuario }) ?? "";
                return Ok(new { success = true, lugar });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en GetLugarUsuario");
                return StatusCode(500, new { success = false, message = "Error al obtener lugar del usuario" });
            }
        }

        /// <summary>
        /// Registra en el sistema que el revisor ya tiene los documentos físicos en su mano.
        /// Esto avisa automáticamente al supervisor en su tablero.
        /// </summary>
        [HttpPost("registrar-actividad/{creferencia}")]
        public async Task<IActionResult> RegistrarTomaDocumento(string creferencia)
        {
            try
            {
                string usuario = User.Identity?.Name ?? "Desconocido";
                using var conn = _db.CreateConnection();
                conn.Open();
                // Use a transaction with FOR UPDATE to prevent double-entry race conditions (Legacy Parity)
                using var transaction = conn.BeginTransaction();
                try
                {
                    // 1. Buscamos si ya hay un registro de tiempo abierto para esta referencia (Bloqueante)
                    string sqlCheck = @"
                        SELECT idtiemposrevision FROM traftiemposrevision 
                        WHERE creferencia = @cref 
                          AND (ffinrevision IS NULL OR YEAR(ffinrevision) < 2000) 
                        ORDER BY idtiemposrevision DESC LIMIT 1 FOR UPDATE";
                    
                    var lastId = await conn.ExecuteScalarAsync<int?>(sqlCheck, new { cref = creferencia }, transaction);

                    if (lastId.HasValue)
                    {
                        // Si ya existe, solo actualizamos la hora en que tomó el documento.
                        // PARIDAD: usuario = quien toma el documento
                        string sqlUpd = @"
                            UPDATE traftiemposrevision 
                            SET ftomadocumento = IF(ftomadocumento IS NULL OR YEAR(ftomadocumento) < 2000, NOW(), ftomadocumento), 
                                usuario = COALESCE(NULLIF(usuario, ''), @usuario)
                            WHERE idtiemposrevision = @id";
                        await conn.ExecuteAsync(sqlUpd, new { usuario, id = lastId.Value }, transaction);
                    }
                    else
                    {
                        // Si no existe, creamos un nuevo registro indicando que empezó con la toma de documentos.
                        // PARIDAD: usuario = quien toma el documento
                        string sqlIns = "INSERT INTO traftiemposrevision (creferencia, ftomadocumento, usuario, finiciorevision) VALUES (@cref, NOW(), @usuario, NULL)";
                        await conn.ExecuteAsync(sqlIns, new { cref = creferencia, usuario }, transaction);
                    }

                    transaction.Commit();

                    // Notificamos la actividad al monitor
                    await _hub.Clients.All.SendAsync("onActividadRecibida", new { creferencia, usuario });
                    return Ok(new { success = true });
                }
                catch (Exception)
                {
                    transaction.Rollback();
                    throw;
                }

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en RegistrarTomaDocumento");
                return StatusCode(500, new { success = false, message = "Error al registrar actividad" });
            }
        }
        /// <summary>
        /// Registra que un usuario está editando una factura específica para prevenir colisiones (R13).
        /// </summary>
        [HttpPost("factura/registrar-editor")]
        public async Task<IActionResult> RegistrarEditorFactura([FromBody] AccionFacturaDto payload)
        {
            try
            {
                string cnofact = payload.cnofact;
                string usuario = User.Identity?.Name ?? "Desconocido";

                using var conn = _db.CreateConnection();
                string sql = "UPDATE fact SET usuedit = @usuario WHERE cnofact = @cnofact";
                int rows = await conn.ExecuteAsync(sql, new { usuario, cnofact });
                
                // Opcional: Notificar a otros usuarios via SignalR
                await _hub.Clients.All.SendAsync("onFacturaEnUso", new { cnofact, usuario });

                return Ok(new { success = rows > 0 });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en RegistrarEditorFactura");
                return StatusCode(500, new { success = false, message = "Error al registrar editor" });
            }
        }

        /// <summary>
        /// Limpia el registro de quién está editando una factura.
        /// </summary>
        [HttpPost("factura/limpiar-editor")]
        public async Task<IActionResult> LimpiarEditorFactura([FromBody] AccionFacturaDto payload)
        {
            try
            {
                string cnofact = payload.cnofact;
                using var conn = _db.CreateConnection();
                string sql = "UPDATE fact SET usuedit = '' WHERE cnofact = @cnofact";
                int rows = await conn.ExecuteAsync(sql, new { cnofact });

                await _hub.Clients.All.SendAsync("onFacturaLiberada", new { cnofact });

                return Ok(new { success = rows > 0 });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en LimpiarEditorFactura");
                return StatusCode(500, new { success = false, message = "Error al limpiar editor" });
            }
        }

        /// <summary>
        /// Limpia todos los editores de una referencia completa (útil al salir de la revisión).
        /// </summary>
        [HttpPost("entrada/limpiar-editores/{cref}")]
        public async Task<IActionResult> LimpiarEditoresEntrada(string cref)
        {
            try
            {
                using var conn = _db.CreateConnection();
                string sql = "UPDATE fact SET usuedit = '' WHERE cref_agrupada = @cref";
                int rows = await conn.ExecuteAsync(sql, new { cref });
                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en LimpiarEditoresEntrada");
                return StatusCode(500, new { success = false, message = "Error al limpiar editores de la entrada" });
            }
        }
    }

    // =====================================================================
    // DTOs para solicitudes JSON (Reemplazo de dynamic para prevenir System.Text.Json element errors)
    // =====================================================================
    public class GuardarCampoFacturaDto
    {
        public string cnofact { get; set; } = "";
        public string campo { get; set; } = "";
        public System.Text.Json.JsonElement valor { get; set; }
    }

    public class GuardarCampoPartidaDto
    {
        public int id { get; set; }
        public string campo { get; set; } = "";
        public System.Text.Json.JsonElement valor { get; set; }
    }

    public class AccionFacturaDto
    {
        public string cnofact { get; set; } = "";
    }

    public class SolicitarDesbloqueoDto
    {
        public string cnofact { get; set; } = "";
        public string motivo { get; set; } = "";
    }

    public class AgregarProductoDto
    {
        public string cnofact { get; set; } = "";
        public string cref { get; set; } = "";
    }

}
