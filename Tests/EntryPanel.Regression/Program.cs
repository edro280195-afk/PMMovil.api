using ClosedXML.Excel;

using Dapper;

using PMMovil.Api.Models;
using PMMovil.Api.Services;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
void Equal<T>(T expected, T actual, string message)
    => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message} (esperado: {expected}, real: {actual})");

DateTime today = new(2026, 11, 20, 13, 30, 0);

EntryPanelFilter Normalize(
    string? from = null, string? to = null, string? clients = null, string? handlings = null, string? receivers = null,
    string? carriers = null, string? type = null, string? photos = null, bool damaged = false, string? text = null,
    string? sort = null, int page = 1, int pageSize = 0)
    => EntryPanelRules.NormalizeFilter(today, from, to, clients, handlings, receivers, carriers, type, photos, damaged, text, sort, page, pageSize);

EntryPanelFilter defaults = Normalize();
Equal(new DateTime(2026, 11, 14), defaults.From, "Sin fechas, el periodo son los últimos 7 días");
Equal(new DateTime(2026, 11, 21), defaults.ToExclusive, "El límite superior es el día siguiente a hoy");
Equal("C", defaults.Type, "Sin tipo se muestran las entradas de bodega (C)");
Equal("", defaults.Photos, "Sin filtro de fotos se muestran todas");
Equal("date_desc", defaults.Sort, "Sin orden, lo más reciente primero");
Equal(1, defaults.Page, "La página inicial es la 1");
Equal(50, defaults.PageSize, "El tamaño de página por omisión es 50");

EntryPanelFilter swapped = Normalize(from: "2026-11-10", to: "2026-11-03");
Equal(new DateTime(2026, 11, 3), swapped.From, "Las fechas invertidas se acomodan");
Equal(new DateTime(2026, 11, 11), swapped.ToExclusive, "La fecha final incluye todo el día");

EntryPanelFilter invalid = Normalize(from: "ayer", to: "2026-13-45");
Equal(new DateTime(2026, 11, 14), invalid.From, "Una fecha inválida se ignora y vuelve a la ventana por omisión");

EntryPanelFilter huge = Normalize(from: "2020-01-01", to: "2026-11-20");
Check((huge.ToExclusive - huge.From).TotalDays <= EntryPanelRules.MaxRangeDays, "El periodo máximo se recorta");
Equal(new DateTime(2026, 11, 21), huge.ToExclusive, "El recorte conserva la fecha final");

Equal("", Normalize(type: "todas").Type, "Todas incluye C y D");
Equal("D", Normalize(type: "d").Type, "El tipo D se acepta en minúscula");
Equal("C", Normalize(type: "X; DROP TABLE traf").Type, "Un tipo desconocido vuelve a C");
Equal("without", Normalize(photos: " Without ").Photos, "El filtro de fotos se normaliza");
Equal("", Normalize(photos: "1 OR 1=1").Photos, "Un filtro de fotos desconocido se ignora");
Equal("incomplete", Normalize(photos: "incomplete").Photos, "Por completar es un filtro válido");

Equal(10, Normalize(pageSize: 3).PageSize, "El tamaño mínimo de página es 10");
Equal(200, Normalize(pageSize: 5000).PageSize, "El tamaño máximo de página es 200");
Equal(1, Normalize(page: -4).Page, "La página nunca baja de 1");

Equal("date_asc", Normalize(sort: "DATE_ASC").Sort, "El orden acepta mayúsculas");
Equal("date_desc", Normalize(sort: "fcreatrafico; DROP").Sort, "Un orden desconocido vuelve al predeterminado");
Check(EntryPanelRules.OrderByClause("client; DELETE").StartsWith("t.fcreatrafico DESC"), "El orden nunca se arma con texto del usuario");

IReadOnlyList<string> list = EntryPanelRules.SplitList(" A1, a1 ,B2,,  ,C3 ");
Equal(3, list.Count, "La lista quita vacíos y repetidos sin importar mayúsculas");
Equal("A1", list[0], "La lista conserva el primer valor");
string longValue = new('x', 100);
Equal(EntryPanelRules.MaxItemLength, EntryPanelRules.SplitList(longValue)[0].Length, "Cada valor se recorta");
Equal(EntryPanelRules.MaxListItems, EntryPanelRules.SplitList(string.Join(",", Enumerable.Range(0, 500).Select(n => "k" + n))).Count, "La lista tiene tope");
Equal(0, EntryPanelRules.SplitList(null).Count, "Sin texto, lista vacía");

Equal(EntryPanelRules.MaxTextLength, Normalize(text: new string('a', 200)).Text!.Length, "La búsqueda tiene tope");
Equal<string?>(null, Normalize(text: "   ").Text, "Una búsqueda en blanco se descarta");

(string whereAll, DynamicParameters paramsAll) = EntryPanelRules.BuildWhere(
    Normalize(from: "2026-11-01", to: "2026-11-20", clients: "AC01,BD02", handlings: "M", receivers: "JORGE MEDINA", carriers: "T5", photos: "without", damaged: true, text: "50%_off'; DROP TABLE traf;--"));
Check(whereAll.Contains("t.tipop = @type"), "El tipo va como parámetro");
Check(whereAll.Contains("TRIM(t.cclienteidtraf) IN @clients"), "Los clientes van como lista de parámetros");
Check(whereAll.Contains("TRIM(t.cidmanejo) IN @handlings"), "Los manejos van como lista de parámetros");
Check(whereAll.Contains("TRIM(t.crecibio) IN @receivers"), "Quien recibió va como lista de parámetros");
Check(whereAll.Contains("TRIM(t.cidtransportraf) IN @carriers"), "Los transportes van como lista de parámetros");
Check(whereAll.Contains(EntryPanelRules.ArrivalCount + " = 0"), "Sin fotos exige cero fotos de arribo");
Check(whereAll.Contains(EntryPanelRules.DamagedFlag + " = 1"), "Solo dañadas filtra por daño");
Check(!whereAll.Contains("DROP") && !whereAll.Contains("AC01") && !whereAll.Contains("JORGE"), "Ningún texto del usuario se mete dentro del SQL");
Equal("%50\\%\\_off'; DROP TABLE traf;--%", paramsAll.Get<string>("text"), "La búsqueda escapa los comodines del LIKE");
Equal(new DateTime(2026, 11, 21), paramsAll.Get<DateTime>("toExclusive"), "El rango termina el día siguiente");

(string whereFacet, _) = EntryPanelRules.BuildWhere(Normalize(clients: "AC01", handlings: "M"), exclude: "clients");
Check(!whereFacet.Contains("@clients"), "Una faceta no se filtra por sí misma");
Check(whereFacet.Contains("@handlings"), "Una faceta sí respeta los demás filtros");

(string whereBoth, _) = EntryPanelRules.BuildWhere(Normalize(type: "todas"));
Check(whereBoth.Contains("t.tipop IN ('C', 'D')"), "Todas incluye las entradas C y D");
(string whereIncomplete, _) = EntryPanelRules.BuildWhere(Normalize(photos: "incomplete"));
Check(whereIncomplete.Contains(EntryPanelRules.DamageCount), "Por completar considera las fotos de daño");
Check(!EntryPanelRules.FromClause.Contains("GROUP BY"), "Las fotos se cuentan por fila, sin recorrer toda la tabla");
Check(EntryPanelRules.ArrivalCount.Contains("pa.creferencia = t.creferencia"), "El conteo de fotos usa el índice de la referencia");

EntryPanelFilter recent = Normalize(from: "2026-11-18", to: "2026-11-20");
(DateTime windowFrom, DateTime windowTo) = EntryPanelRules.HistogramWindow(recent, today, 0);
Equal(new DateTime(2026, 10, 7), windowFrom, "La ventana del histograma son 45 días terminando hoy");
Equal(new DateTime(2026, 11, 21), windowTo, "La ventana incluye hoy");
EntryPanelFilter old = Normalize(from: "2026-03-01", to: "2026-03-31");
(DateTime oldFrom, DateTime oldTo) = EntryPanelRules.HistogramWindow(old, today, 45);
Equal(new DateTime(2026, 3, 1), oldFrom, "Un periodo antiguo amplía la ventana hasta su inicio");
Equal(new DateTime(2026, 11, 21), oldTo, "La ventana siempre llega hasta hoy");
Check((oldTo - oldFrom).TotalDays <= EntryPanelRules.MaxRangeDays, "La ventana no supera el máximo");
(DateTime shortFrom, DateTime shortTo) = EntryPanelRules.HistogramWindow(recent, today, 3);
Equal(7, (int)(shortTo - shortFrom).TotalDays, "La ventana mínima es de 7 días");

Check(EntryPanelRules.IsIncomplete(0, false, 0), "Sin fotos de arribo está incompleta");
Check(EntryPanelRules.IsIncomplete(3, true, 0), "Dañada sin fotos del daño está incompleta");
Check(!EntryPanelRules.IsIncomplete(3, true, 2), "Con fotos de arribo y de daño está completa");
Check(!EntryPanelRules.IsIncomplete(1, false, 0), "Sin daño no se piden fotos de daño");
Equal(0, EntryPanelRules.PendingItems(2, false, 0, "").Count, "Una entrada completa no tiene pendientes");
Equal(
    "arrival_photos,damage_photos,damage_description",
    string.Join(",", EntryPanelRules.PendingItems(0, true, 0, " ").Select(item => item.Code)),
    "Los pendientes salen en orden");
Equal(EntryPanelRules.DamagePhotoType, EntryPanelRules.DocTypeFor("damage"), "El tipo de daño es el 103");
Equal(EntryPanelRules.ArrivalPhotoType, EntryPanelRules.DocTypeFor("cualquier cosa"), "Lo demás es arribo (100)");
Equal("damage", EntryPanelRules.KindFor(103), "103 es daño");
Equal("arrival", EntryPanelRules.KindFor(100), "100 es arribo");

Equal("62 bultos", EntryPanelRules.FormatPackages(62.000m), "Los bultos enteros no llevan decimales");
Equal("1 bulto", EntryPanelRules.FormatPackages(1m), "Un bulto va en singular");
Equal("2.5 bultos", EntryPanelRules.FormatPackages(2.5m), "Los decimales se conservan");
Equal("0 bultos", EntryPanelRules.FormatPackages(0m), "Cero bultos va en plural");

var row = new EntryRow { Reference = "01C263619810000", ClientName = "ACME DE MEXICO", Packages = 62, ReceivedBy = "JORGE MEDINA" };
(string doneTitle, string doneBody, string doneSeverity) = EntryPanelRules.CapturedNotification(row, Array.Empty<EntryPending>());
Equal("Entrada capturada", doneTitle, "Completa se anuncia como capturada");
Check(doneBody.StartsWith("ACME DE MEXICO · 62 bultos · JORGE MEDINA"), "El aviso resume cliente, bultos y quién recibió");
Equal("info", doneSeverity, "Completa es informativa");
(string todoTitle, string todoBody, _) = EntryPanelRules.CapturedNotification(row, EntryPanelRules.PendingItems(0, false, 0, ""));
Equal("Entrada por completar", todoTitle, "Con pendientes se anuncia por completar");
Check(todoBody.Contains("Falta: fotos de arribo."), "El aviso dice qué falta");
row.Damaged = true;
(_, _, string damagedSeverity) = EntryPanelRules.CapturedNotification(row, EntryPanelRules.PendingItems(0, true, 0, ""));
Equal("warning", damagedSeverity, "Dañada con pendientes avisa con más prioridad");
row.ClientName = new string('Z', 80);
(_, string longBody, _) = EntryPanelRules.CapturedNotification(row, Array.Empty<EntryPending>());
Check(longBody.Split('\n')[0].Length < 80, "Un cliente largo se recorta en el aviso");
Equal("/panel/entradas/01C263619810000", EntryPanelRules.EntryUrl(" 01C263619810000 "), "La dirección de la entrada es la del panel");

Check(NotificationRoutes.IsInternalUrl("/panel/monitor"), "Una ruta interna es válida");
Check(!NotificationRoutes.IsInternalUrl("//evil.example/x"), "Una dirección sin protocolo hacia otro sitio no es válida");
Check(!NotificationRoutes.IsInternalUrl("https://evil.example"), "Una dirección absoluta no es válida");
Check(!NotificationRoutes.IsInternalUrl("javascript:alert(1)"), "javascript: no es válida");
Check(!NotificationRoutes.IsInternalUrl("/\\evil.example"), "El truco de la diagonal invertida no es válido");
Check(!NotificationRoutes.IsInternalUrl("/panel/\nmonitor"), "Los caracteres de control no son válidos");
Check(!NotificationRoutes.IsInternalUrl(""), "Vacía no es válida");

Equal("/panel/monitor", NotificationRoutes.WebUrlFor("solicitud_desbloqueo", null), "El permiso solicitado abre el monitor");
Equal("/panel/monitor", NotificationRoutes.WebUrlFor("revision_asignada", null), "La revisión asignada abre el monitor");
Equal("/panel/validacion-reportes", NotificationRoutes.WebUrlFor("reporte_pendiente_validar", null), "El reporte pendiente abre la validación");
Equal("/panel/preregistro", NotificationRoutes.WebUrlFor("preregistro-arribo", null), "El arribo abre los pre-registros");
Equal("/panel/entradas/01C1", NotificationRoutes.WebUrlFor("entrada-capturada", new Dictionary<string, string> { ["reference"] = "01C1" }), "La entrada capturada abre su ficha");
Equal("/panel/entradas", NotificationRoutes.WebUrlFor("entrada-capturada", null), "Sin referencia abre la lista");
Equal("/panel/monitor", NotificationRoutes.WebUrlFor("otro", new Dictionary<string, string> { ["crefAgrupada"] = "X" }), "Con carpeta agrupada abre el monitor");
Equal("/home", NotificationRoutes.WebUrlFor("desconocido", null), "Lo desconocido abre el inicio");
Equal("/panel/mapa", NotificationRoutes.WebUrlFor("x", new Dictionary<string, string> { ["url"] = "/panel/mapa" }), "Una ruta explícita interna se respeta");
Equal("/home", NotificationRoutes.WebUrlFor("x", new Dictionary<string, string> { ["url"] = "https://evil.example" }), "Una ruta explícita externa se descarta");

Equal("critical", NotificationRoutes.SeverityFor("salida-bloqueo"), "El bloqueo es crítico");
Equal("critical", NotificationRoutes.SeverityFor("CARGA_BLOQUEO"), "El bloqueo de carga es crítico");
Equal("info", NotificationRoutes.SeverityFor("salida-desbloqueo"), "El desbloqueo es informativo");
Equal("warning", NotificationRoutes.SeverityFor("solicitud_desbloqueo"), "La solicitud de permiso pide atención");
Equal("info", NotificationRoutes.SeverityFor("revision_asignada"), "La revisión asignada es informativa");
Equal(3600, NotificationRoutes.TimeToLiveSeconds("info"), "Lo informativo caduca en una hora");
Equal(14400, NotificationRoutes.TimeToLiveSeconds("critical"), "Lo crítico caduca en cuatro horas");

DateTime stamp = new(2026, 11, 20, 19, 0, 0, DateTimeKind.Utc);
Dictionary<string, string> payload = NotificationRoutes.Standardize("Permiso", "Texto", new Dictionary<string, string> { ["type"] = "solicitud_desbloqueo", ["cnofact"] = "2986432", ["extra"] = "1" }, stamp);
Equal("Permiso", payload["title"], "El aviso lleva el título");
Equal("Texto", payload["body"], "El aviso lleva el texto");
Equal("solicitud_desbloqueo", payload["type"], "El aviso lleva el tipo");
Equal("/panel/monitor", payload["url"], "El aviso lleva su dirección en el web");
Equal("warning", payload["severity"], "El aviso lleva su gravedad");
Equal("solicitud_desbloqueo:2986432", payload["tag"], "La etiqueta agrupa por tipo y documento");
Equal("2986432", payload["cnofact"], "Los datos originales se conservan");
Equal("1", payload["extra"], "Los datos extra se conservan");
Equal(stamp.ToString("o"), payload["ts"], "El aviso lleva su hora");
Dictionary<string, string> plain = NotificationRoutes.Standardize("A", "B", null, stamp);
Equal("general", plain["type"], "Sin tipo es general");
Equal("general", plain["tag"], "Sin documento la etiqueta es el tipo");
Dictionary<string, string> keepTag = NotificationRoutes.Standardize("A", "B", new Dictionary<string, string> { ["type"] = "t", ["tag"] = "mia", ["severity"] = "critical", ["url"] = "/panel/x" }, stamp);
Equal("mia", keepTag["tag"], "Una etiqueta explícita se respeta");
Equal("critical", keepTag["severity"], "Una gravedad explícita se respeta");
Equal("/panel/x", keepTag["url"], "Una dirección explícita se respeta");

DateTime now = new(2026, 11, 20, 19, 0, 0, DateTimeKind.Utc);
Check(EntryRealtimePublisher.ShouldNotify("REF-A", now), "El primer aviso de una entrada sale");
Check(!EntryRealtimePublisher.ShouldNotify("REF-A", now.AddMinutes(5)), "Un reintento de la app no repite el aviso");
Check(EntryRealtimePublisher.ShouldNotify("REF-B", now.AddMinutes(5)), "Otra entrada sí avisa");
Check(EntryRealtimePublisher.ShouldNotify("REF-A", now.AddMinutes(45)), "Pasada la ventana vuelve a avisar");
Check(!EntryRealtimePublisher.ShouldNotify("  ", now), "Sin referencia no se avisa");
EntryRealtimePublisher.ForgetNotification("REF-B");
Check(EntryRealtimePublisher.ShouldNotify("REF-B", now.AddMinutes(6)), "Olvidar una entrada permite avisar de nuevo");

var exportRows = new List<EntryRow>
{
    new()
    {
        Reference = "01C263619810000", Type = "C", CreatedAt = new DateTime(2026, 11, 20, 13, 3, 37), ClientId = "AC01", ClientName = "ACME DE MEXICO",
        HandlingId = "D", HandlingName = "DIRECTO", CarrierId = "T5", CarrierName = "TRANSPORTES NORTE", Waybill = "G-100", Container = "H14172",
        Packages = 62, ReceivedBy = "JORGE MEDINA", CreatedBy = "jmedina", Damaged = true, ArrivalPhotos = 3, DamagePhotos = 0, Incomplete = true
    },
    new()
    {
        Reference = "01C263619800000", Type = "C", CreatedAt = new DateTime(2026, 11, 20, 12, 51, 28), ClientId = "BD02", ClientName = "",
        Packages = 26.5m, ReceivedBy = "RODOLFO NIÑO", CreatedBy = "rnino", ArrivalPhotos = 2
    },
    new() { Reference = "01D260018430000", Type = "D", CreatedAt = null, ClientName = "SIN FECHA", ReceivedBy = "GLADYS RODRIGUEZ", CreatedBy = "gladys" }
};
EntryPanelFilter exportFilter = Normalize(from: "2026-11-01", to: "2026-11-20", clients: "AC01", type: "todas", damaged: true);
byte[] workbookBytes = EntryPanelExport.Build(exportRows, exportFilter, today, truncated: true);
Check(workbookBytes.Length > 1000, "El archivo de Excel tiene contenido");
Equal((byte)'P', workbookBytes[0], "Un .xlsx es un zip (empieza con PK)");
Equal((byte)'K', workbookBytes[1], "Un .xlsx es un zip (empieza con PK)");
using (var stream = new MemoryStream(workbookBytes))
using (var workbook = new XLWorkbook(stream))
{
    IXLWorksheet sheet = workbook.Worksheet("Entradas");
    Equal("Referencia", sheet.Cell(1, 1).GetString(), "La primera columna es la referencia");
    string[] headers = Enumerable.Range(1, 16).Select(column => sheet.Cell(1, column).GetString()).ToArray();
    Check(!headers.Any(header => header.Contains("Peso", StringComparison.OrdinalIgnoreCase)), "El Excel no lleva peso");
    Equal(16, headers.Length, "El Excel tiene 16 columnas");
    Equal("01C263619810000", sheet.Cell(2, 1).GetString(), "La primera fila trae la referencia");
    Equal(new DateTime(2026, 11, 20), sheet.Cell(2, 2).GetDateTime(), "La fecha es una fecha real de Excel");
    Equal(new TimeSpan(13, 3, 37), sheet.Cell(2, 3).GetDateTime().TimeOfDay, "La hora es una hora real de Excel");
    Equal("ACME DE MEXICO", sheet.Cell(2, 5).GetString(), "Se exporta el nombre del cliente");
    Equal("BD02", sheet.Cell(3, 5).GetString(), "Sin nombre se exporta la clave del cliente");
    Equal(62.0, sheet.Cell(2, 10).GetDouble(), "Los bultos son un número");
    Equal(26.5, sheet.Cell(3, 10).GetDouble(), "Los bultos conservan decimales");
    Equal("Sí", sheet.Cell(2, 13).GetString(), "La dañada se marca con Sí");
    Equal("Por completar", sheet.Cell(2, 16).GetString(), "Una entrada con pendientes se marca por completar");
    Equal("Completa", sheet.Cell(3, 16).GetString(), "Una entrada sin pendientes se marca completa");
    Equal("Otra entrada (D)", sheet.Cell(4, 4).GetString(), "El tipo D se explica");
    Equal("", sheet.Cell(4, 2).GetString(), "Sin fecha la celda queda vacía");
    Equal(4, sheet.LastRowUsed()!.RowNumber(), "El archivo trae una fila por entrada más el encabezado");
    Check(sheet.AutoFilter.IsEnabled, "El archivo trae filtro automático");
    Equal(1, sheet.SheetView.SplitRow, "El encabezado queda fijo");
    IXLWorksheet filters = workbook.Worksheet("Filtros");
    Check(Enumerable.Range(1, 20).Any(line => filters.Cell(line, 2).GetString().Contains("AC01")), "La hoja de filtros enumera los clientes");
    Check(Enumerable.Range(1, 20).Any(line => filters.Cell(line, 1).GetString() == "Aviso"), "La hoja de filtros avisa cuando se recortó el resultado");
    Check(Enumerable.Range(1, 20).Any(line => filters.Cell(line, 2).GetString() == "01/11/2026 al 20/11/2026"), "La hoja de filtros muestra el periodo");
}
Equal("entradas-20261120-1330.xlsx", EntryPanelExport.FileName(today), "El archivo se nombra con la fecha y la hora");
byte[] emptyBytes = EntryPanelExport.Build(Array.Empty<EntryRow>(), Normalize(), today, truncated: false);
using (var stream = new MemoryStream(emptyBytes))
using (var workbook = new XLWorkbook(stream))
{
    Equal("Referencia", workbook.Worksheet("Entradas").Cell(1, 1).GetString(), "Un resultado vacío también trae encabezados");
}

Console.WriteLine($"EntryPanel.Regression: {checks} verificaciones correctas.");
