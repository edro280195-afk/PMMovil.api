using System.Text.RegularExpressions;

using PMMovil.Api.Models;
using PMMovil.Api.Services;

// Regresiones de la entrada de bodega: lo que se guarda en traf.crecibio (5-nov-2026) y las fotos de daño con su enlace
// público (6-nov-2026). Sin servidor ni bases de datos.
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
void Equal<T>(T expected, T actual, string message)
    => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message} (esperado: {expected}, real: {actual})");

// ── El nombre, no la clave ─────────────────────────────────────────────────────────────────────────
// usuarios: claveusuario = frodriguez, nombre = FRANCISCO JAVIE, apaterno = RODRIGUEZ.
Equal("FRANCISCO JAVIE RODRIGUEZ", EntradaService.NombreRecibio("FRANCISCO JAVIE RODRIGUEZ", "frodriguez", "frodriguez"),
    "Con nombre en usuarios se guarda el nombre y no la clave");
Equal("GLADYS RODRIGUEZ", EntradaService.NombreRecibio("Gladys Rodriguez", "", "gladys"),
    "Se guarda en mayúsculas como las entradas del legacy");
Equal("HUMBERTO SANCHEZ", EntradaService.NombreRecibio("  Humberto   Sanchez ", null, "bhsanchez"),
    "Los espacios sobrantes se limpian");

// ── Respaldos cuando no hay nombre ─────────────────────────────────────────────────────────────────
Equal("JORGE MEDINA", EntradaService.NombreRecibio(null, "Jorge Medina", "jmedina"),
    "Sin nombre en usuarios se respeta lo que mandó la app");
Equal("JMEDINA", EntradaService.NombreRecibio("", "   ", "jmedina"),
    "Sin nada más, la clave: nunca queda vacío");
Equal("", EntradaService.NombreRecibio(null, null, null), "Sin usuario tampoco se inventa un nombre");

// ── traf.crecibio es varchar(30): con el modo estricto de MySQL un nombre más largo haría fallar el guardado ──
string largo = EntradaService.NombreRecibio("MARIA GUADALUPE FERNANDA RODRIGUEZ SANCHEZ", null, "mgfrs");
Check(largo.Length <= EntradaService.LongitudMaximaRecibio, "El nombre se recorta a la longitud de la columna");
Check(!largo.EndsWith(' '), "El recorte no deja un espacio al final");
Equal("MARIA GUADALUPE FERNANDA RODRI", largo, "Se recorta por la derecha");

// ── Mercancía dañada: la app sube las fotos del daño DESPUÉS (cola), no dentro del guardado (6-nov-2026) ──
// Antes el guardado exigía FotosDanio y rechazaba toda recepción dañada con «agrega al menos una foto del daño»
// aunque la foto sí estuviera tomada. Aquí solo se exige la descripción; la foto obligatoria la exige la app.
Equal<string?>(null, EntradaService.MensajeDanioInvalido(true, "Caja aplastada en una esquina"),
    "Dañada con descripción es válida: las fotos llegan después por la cola, no dentro del guardado");
Equal<string?>("Si declaras mercancía dañada, describe el daño.", EntradaService.MensajeDanioInvalido(true, "   "),
    "Dañada sin descripción sigue rechazándose");
Equal<string?>("Si declaras mercancía dañada, describe el daño.", EntradaService.MensajeDanioInvalido(true, null),
    "Dañada con descripción nula también se rechaza");
Equal<string?>(null, EntradaService.MensajeDanioInvalido(false, null), "Sin daño no se exige nada");

// ── Candado «declara daño, avisa N» (6-nov-2026) ───────────────────────────────────────────────────
const string SinFotos = "Si declaras mercancía dañada, agrega al menos una foto del daño.";
Equal<string?>(SinFotos, EntradaService.MensajeDanioInvalido(true, "Caja aplastada", fotosInline: 0, fotosPendientes: 0),
    "Declara daño y avisa cero fotos: se rechaza");
Equal<string?>(null, EntradaService.MensajeDanioInvalido(true, "Caja aplastada", fotosInline: 0, fotosPendientes: 2),
    "Declara daño y avisa que sube 2 por la cola: pasa");
Equal<string?>(null, EntradaService.MensajeDanioInvalido(true, "Caja aplastada", fotosInline: 1, fotosPendientes: 0),
    "Declara daño con una foto dentro del guardado: pasa");
Equal<string?>(null, EntradaService.MensajeDanioInvalido(true, "Caja aplastada", fotosInline: 0, fotosPendientes: null),
    "Cliente viejo que no avisa nada no se rompe");
Equal<string?>("Si declaras mercancía dañada, describe el daño.",
    EntradaService.MensajeDanioInvalido(true, "  ", fotosInline: 0, fotosPendientes: 3),
    "Sin descripción se rechaza antes que lo de las fotos");
Equal<string?>(null, EntradaService.MensajeDanioInvalido(false, null, fotosInline: 0, fotosPendientes: 0),
    "Sin daño declarado el aviso en cero es normal");

// ── Cuántas fotos de daño espera el servidor y cuántas faltan ──────────────────────────────────────
Equal<int?>(3, EntradaDanioSeguimiento.EsperadasAlGuardar(true, fotosInline: 0, fotosPendientes: 3), "Entrada nueva: espera lo que avisa");
Equal<int?>(2, EntradaDanioSeguimiento.EsperadasAlGuardar(true, fotosInline: 2, fotosPendientes: null), "Cliente viejo con fotos dentro: espera esas");
Equal<int?>(null, EntradaDanioSeguimiento.EsperadasAlGuardar(true, fotosInline: 0, fotosPendientes: null), "Sin dato no se promete nada");
Equal<int?>(null, EntradaDanioSeguimiento.EsperadasAlGuardar(false, fotosInline: 0, fotosPendientes: 3), "Sin daño no se espera nada");
Equal<int?>(5, EntradaDanioSeguimiento.EsperadasAlGuardar(true, 0, 2, yaGuardadas: 3), "Al editar suma lo que ya hay en fotos");
Equal(2, EntradaDanioSeguimiento.Faltantes(true, 4, 2), "Esperaba 4 y llegaron 2: faltan 2");
Equal(0, EntradaDanioSeguimiento.Faltantes(true, 2, 5), "Si llegaron de más no faltan (nunca negativo)");
Equal(0, EntradaDanioSeguimiento.Faltantes(true, null, 0), "Sin dato no hay «faltan»");
Equal(0, EntradaDanioSeguimiento.Faltantes(false, 4, 0), "Sin daño no hay «faltan»");

// ── Página pública de la entrada con fotos de daño (6-nov-2026) ───────────────────────────────────
const string FotoBase = "/api/publico/entrada/tok/foto";
const string DanioBase = "/api/publico/entrada/tok/foto-danio";
const string ZipUrl = "/api/publico/entrada/tok/zip";

EntradaShareResumen Muestra(bool danada, int evidencia, int danio, int pendientes = 0, string descripcion = "Caja aplastada en una esquina")
{
    var r = new EntradaShareResumen
    {
        CrefAgrupada = "01C263551100000", NombreCliente = "CLIENTE DE EJEMPLO", NombreTransportista = "TRANSPORTES DEMO",
        Unidad = "CAJA 000-000", Ubicacion = "Bodega 1", FechaLlegada = "6 nov, 10:42",
        GeneradoEn = DateTime.Now.AddMinutes(-12), Expira = DateTime.Now.AddHours(60),
        Danada = danada, DanadaDescripcion = descripcion, FotosDanioPendientes = pendientes
    };
    for (int i = 1; i <= evidencia; i++) r.Fotos.Add(new EntradaShareFoto { NLineNo = i, Caption = $"Foto {i}", Hora = "10:5" + i });
    for (int i = 1; i <= danio; i++) r.FotosDanio.Add(new EntradaShareFoto { NLineNo = i, Caption = $"Daño {i}", Hora = "11:0" + i });
    return r;
}
// El CSS de la página ya trae el selector .panel--dmg: se busca el ELEMENTO, no la cadena suelta.
const string SeccionDanio = "class=\"panel panel--dmg\"";
int Tiles(string html) => Regex.Matches(html, @"class=""tile""").Count;
// HtmlEncode vuelve «ñ», «í» o «·» entidades (&#241;…): el navegador las muestra bien, pero un Contains literal no. Las
// comprobaciones de TEXTO van sobre el HTML decodificado; las de seguridad (inyección) siempre sobre el HTML crudo.
string Txt(string html) => System.Net.WebUtility.HtmlDecode(html);

// Con daño y fotos: sección propia ARRIBA de la evidencia, con las rutas de cada grupo.
string conDanio = EntradaShareHtmlRenderer.RenderResumen(Muestra(true, 4, 3), FotoBase, DanioBase, ZipUrl);
Check(conDanio.Contains(SeccionDanio), "Con daño sale la sección «Mercancía dañada»");
Check(Txt(conDanio).Contains("Mercancía dañada"), "La etiqueta «Mercancía dañada» está en la página");
Check(conDanio.IndexOf(SeccionDanio, StringComparison.Ordinal) < Txt(conDanio).IndexOf("Evidencia de la entrada", StringComparison.Ordinal),
    "El daño va antes que la evidencia");
Equal(7, Tiles(conDanio), "3 fotos de daño + 4 de evidencia");
Check(conDanio.Contains("/foto-danio/2\""), "Las fotos de daño usan su propia ruta");
Check(conDanio.Contains("/foto/3\""), "Las de evidencia siguen en la ruta de siempre");
Check(Txt(conDanio).Contains("3 fotos del daño"), "Cuenta de fotos del daño");
Check(Txt(conDanio).Contains("ZIP con carpetas Daño y Evidencia"), "El ZIP anuncia sus dos carpetas");
Check(Txt(conDanio).Contains("7 · 3 de daño"), "El resumen cuenta el total y las de daño");
Check(conDanio.Contains(@"""g"":""d"""), "El visor sabe cuáles fotos son de daño");
Check(Regex.Matches(conDanio, "</script>").Count == 2, "Solo los dos scripts de la plantilla cierran (nada inyectado)");

// Sin daño: la página de siempre, sin nada de daño.
string sinDanio = EntradaShareHtmlRenderer.RenderResumen(Muestra(false, 5, 0), FotoBase, DanioBase, ZipUrl);
Check(!sinDanio.Contains(SeccionDanio), "Sin daño no hay sección de daño");
Check(!sinDanio.Contains("foto-danio"), "Sin daño no hay ni una ruta de daño");
Check(Txt(sinDanio).Contains("Fotos de la entrada"), "Sin daño el panel se llama como siempre");
Check(Txt(sinDanio).Contains("ZIP con todas las fotos"), "Sin daño el ZIP es uno solo");
Equal(5, Tiles(sinDanio), "5 fotos de evidencia");

// Lo más importante de privacidad: una entrada NO dañada nunca muestra fotos de daño, aunque el resumen las trajera
// (el tipo 103 también es la discrepancia de Revisión).
string fuga = EntradaShareHtmlRenderer.RenderResumen(Muestra(false, 2, 3), FotoBase, DanioBase, ZipUrl);
Check(!fuga.Contains("foto-danio"), "Sin daño declarado no sale ninguna foto 103, aunque venga en el resumen");
Equal(2, Tiles(fuga), "Solo la evidencia");

// La descripción es texto libre escrito por bodega: se escapa, y se puede ocultar.
string malicioso = EntradaShareHtmlRenderer.RenderResumen(
    Muestra(true, 1, 1, descripcion: "<script>alert(1)</script><img src=x onerror=alert(2)>"), FotoBase, DanioBase, ZipUrl);
Check(!malicioso.Contains("<script>alert"), "La descripción no inyecta un script");
Check(!malicioso.Contains("<img src=x"), "La descripción no inyecta HTML");
Check(malicioso.Contains("&lt;script&gt;alert(1)&lt;/script&gt;"), "La descripción se muestra como texto");
Check(Regex.Matches(malicioso, "</script>").Count == 2, "Ni la descripción cierra el script de la plantilla");
string sinTexto = EntradaShareHtmlRenderer.RenderResumen(Muestra(true, 1, 2), FotoBase, DanioBase, ZipUrl, mostrarDescripcionDanio: false);
Check(!Txt(sinTexto).Contains("Caja aplastada en una esquina"), "Con el interruptor apagado no sale la descripción");
Check(sinTexto.Contains(SeccionDanio) && Tiles(sinTexto) == 3, "Apagar la descripción deja las fotos y la etiqueta");

// Fotos por llegar: «faltan N», casillas de espera y recarga automática.
string subiendo = EntradaShareHtmlRenderer.RenderResumen(Muestra(true, 3, 2, pendientes: 2), FotoBase, DanioBase, ZipUrl);
Check(Txt(subiendo).Contains("Faltan 2 fotos del daño por llegar"), "Avisa cuántas faltan");
Equal(2, Regex.Matches(subiendo, "tile--wait\"").Count, "Una casilla de espera por foto que falta");
Check(Txt(subiendo).Contains("2 de 4 fotos del daño"), "El encabezado dice cuántas han llegado de cuántas");
Check(subiendo.Contains("var PENDING=2;"), "La página sabe que debe recargarse sola");
Equal(5, Tiles(subiendo), "Las casillas de espera no cuentan como fotos del visor");
string una = EntradaShareHtmlRenderer.RenderResumen(Muestra(true, 0, 1, pendientes: 1), FotoBase, DanioBase, ZipUrl);
Check(Txt(una).Contains("Falta 1 foto del daño por llegar"), "Singular cuando falta una");
Check(!conDanio.Contains("id=\"pendNote\""), "Con todas las fotos no hay aviso");
Check(subiendo.Contains("id=\"pendNote\""), "Con fotos por llegar sí hay aviso");

// Declaró daño y no hay ni una foto ni se espera ninguna: se dice claro.
string vacio = EntradaShareHtmlRenderer.RenderResumen(Muestra(true, 2, 0), FotoBase, DanioBase, ZipUrl);
Check(Txt(vacio).Contains("las fotos del daño todavía no llegan"), "Daño declarado sin fotos lo dice");

// Sin ninguna foto no hay botón de descarga; y el enlace vencido no muestra nada de la entrada.
Check(!EntradaShareHtmlRenderer.RenderResumen(Muestra(false, 0, 0), FotoBase, DanioBase, ZipUrl).Contains("Descargar todas las fotos"),
    "Sin fotos no hay descarga");
string vencido = EntradaShareHtmlRenderer.RenderExpirado();
Check(Txt(vencido).Contains("Este enlace ya no está disponible") && !vencido.Contains("Mercancía"), "El enlace vencido solo dice que venció");

// ── Aviso de PRUEBA de la entrada: solo a desarrollo3 (6-nov-2026) ────────────────────────────────
// PMMovil no manda la notificación de entrada al cliente (la manda Gladys desde el sistema PMM / VFP). Este aviso es solo
// para ver cómo quedó la captura y cómo se vería la notificación, y tiene que llegarle ÚNICAMENTE a desarrollo3.
Equal("desarrollo3@grupopmm.com", EntradaAvisoCaptura.DestinatarioDePrueba(null), "Sin configurar: desarrollo3");
Equal("desarrollo3@grupopmm.com", EntradaAvisoCaptura.DestinatarioDePrueba("   "), "Vacío: desarrollo3");
Equal("otro.interno@grupopmm.com", EntradaAvisoCaptura.DestinatarioDePrueba(" otro.interno@grupopmm.com "), "Una dirección interna válida se respeta");
Equal("desarrollo3@grupopmm.com", EntradaAvisoCaptura.DestinatarioDePrueba("cliente@empresa.com"), "Un correo externo se ignora: nunca sale a un cliente");
Equal("desarrollo3@grupopmm.com", EntradaAvisoCaptura.DestinatarioDePrueba("a@grupopmm.com,cliente@empresa.com"), "Varias direcciones se ignoran");
Equal("desarrollo3@grupopmm.com", EntradaAvisoCaptura.DestinatarioDePrueba("a@grupopmm.com;b@grupopmm.com"), "Dos direcciones con punto y coma se ignoran");
Equal("desarrollo3@grupopmm.com", EntradaAvisoCaptura.DestinatarioDePrueba("x@evil.com@grupopmm.com"), "Dos arrobas se ignoran");
Equal("desarrollo3@grupopmm.com", EntradaAvisoCaptura.DestinatarioDePrueba("@grupopmm.com"), "Sin usuario se ignora");

// El envío: UN destinatario, sin Cc, sin Bcc y SIN las copias ocultas automáticas del SmtpService (en producción agregan a
// desarrollo@ y carmen.barragan@).
var smtp = new FakeSmtp();
bool salio = await EntradaAvisoCaptura.EnviarSoloAAsync(smtp, "desarrollo3@grupopmm.com", "asunto", "<b>hola</b>");
Check(salio && smtp.Llamadas == 1, "Se hizo un solo envío");
Equal("desarrollo3@grupopmm.com", string.Join(",", smtp.Destinatarios), "El único destinatario es desarrollo3");
Check(smtp.Copias == null || !smtp.Copias.Any(), "Sin copias");
Check(smtp.CopiasOcultas == null || !smtp.CopiasOcultas.Any(), "Sin copias ocultas");
Check(!smtp.IncluirCopiasOcultasAutomaticas, "Las copias ocultas AUTOMÁTICAS del SmtpService están apagadas");
Check(smtp.PreferirCola, "Pide la cola durable");
Check(smtp.Adjuntos == null, "Sin adjuntos");

// El contenido.
EntradaAvisoDatos Captura(bool danada = true, string cliente = "CLIENTE DE EJEMPLO, S.A.", string obs = "Descargó sin novedad")
{
    var d = new EntradaAvisoDatos
    {
        Referencia = "01C263551100000", Cliente = cliente, Transportista = "TRANSPORTES DEMO", Caja = "CAJA 000-000", Guia = "BL123", Manejo = "1",
        Planta = "MAQUILA", Recibio = "FRANCISCO JAVIE RODRIGUEZ", Observaciones = obs, FechaArribo = new DateTime(2026, 11, 6, 10, 42, 0),
        PesoLbs = 1000, PesoKgs = 453.6m, Hazmat = true, ClaseHazmat = "3", UnHazmat = "1993",
        Danada = danada, DescripcionDanio = "Caja aplastada en una esquina", InBond = false, MaderaConSello = 2, Aserrin = 1,
        FotosEvidencia = 7, FotosDanio = 3
    };
    d.Detalles.Add(new EntradaAvisoDetalle(5, "A", "TARIMAS DE PRUEBA", "", "A-1"));
    d.Detalles.Add(new EntradaAvisoDetalle(2.5m, "B", "CAJAS DE PRUEBA", "", "B-2"));
    return d;
}
DateTime ahora = new(2026, 11, 6, 11, 5, 0);
var (asunto, html) = EntradaAvisoCaptura.Construir(Captura(), "desarrollo3@grupopmm.com", "https://x.test/api/publico/entrada/abc", ahora.AddHours(72), esEdicion: false, ahora);
string texto = System.Net.WebUtility.HtmlDecode(html);
Equal("[PRUEBA] [01C263551100000] Recepción en Bodega - CLIENTE DE EJEMPLO, S.A.", asunto, "Asunto: marcado como prueba");
Check(texto.Contains("CORREO DE PRUEBA") && texto.Contains("Llega únicamente a desarrollo3@grupopmm.com"), "Avisa que es de prueba y a quién llega");
Check(texto.Contains("la envía Gladys desde el sistema PMM"), "Explica quién manda la notificación de verdad");
Check(texto.Contains("1 · ASÍ SE VERÍA LA NOTIFICACIÓN") && texto.Contains("2 · CÓMO QUEDÓ LA CAPTURA"), "Trae las dos partes");
Check(texto.Contains("Total Piezas") && texto.Contains("7.5"), "Total de piezas = suma de las cantidades (5 + 2.5)");
Check(texto.Contains("TARIMAS DE PRUEBA") && texto.Contains("CAJAS DE PRUEBA"), "Trae el detalle de la mercancía");
Check(texto.Contains("Bodega") && texto.Contains("MAQUILA"), "Bodega = la planta capturada, no un texto fijo");
Check(texto.Contains("Sí · clase 3 · UN 1993"), "HAZMAT con clase y UN");
Check(texto.Contains("SÍ — Caja aplastada en una esquina"), "Daño con su descripción");
Check(texto.Contains("Madera con sello: 2 · Aserrín: 1"), "Control de madera");
Check(texto.Contains("7 de evidencia · 3 del daño"), "Cuenta de fotos de evidencia y de daño");
Check(html.Contains("href='https://x.test/api/publico/entrada/abc'") && texto.Contains("Ver fotos y seguimiento de la entrada"), "Botón con el enlace de las fotos");
Check(texto.Contains("Enlace vigente hasta 09/11/2026 11:05"), "Dice hasta cuándo vale el enlace");

var (_, sinEnlace) = EntradaAvisoCaptura.Construir(Captura(danada: false), "desarrollo3@grupopmm.com", null, null, true, ahora);
Check(System.Net.WebUtility.HtmlDecode(sinEnlace).Contains("No se pudo generar el enlace de las fotos"), "Sin enlace el aviso sale igual y lo dice");
Check(!sinEnlace.Contains("href='http"), "Sin enlace no hay botón");
Check(!System.Net.WebUtility.HtmlDecode(sinEnlace).Contains("del daño"), "Sin daño no se cuentan fotos de daño");
Check(EntradaAvisoCaptura.Construir(Captura(), "d@grupopmm.com", null, null, true, ahora).Asunto.Contains("(actualizada)"), "Una edición se marca en el asunto");

// Lo que escribe bodega es texto libre: se escapa.
var (_, correoMalicioso) = EntradaAvisoCaptura.Construir(
    Captura(cliente: "<script>alert(1)</script> S.A.", obs: "<img src=x onerror=alert(2)>"), "desarrollo3@grupopmm.com", "https://x.test/?a=1&b=\"2\"", null, false, ahora);
Check(!correoMalicioso.Contains("<script>alert") && !correoMalicioso.Contains("<img src=x"), "El texto libre no inyecta HTML en el correo");
Check(correoMalicioso.Contains("&lt;script&gt;alert(1)&lt;/script&gt;"), "Se muestra como texto");
Check(!correoMalicioso.Contains("href='https://x.test/?a=1&b=\""), "El enlace también se escapa");

// No repetir: el mismo aviso no sale dos veces en 10 minutos (doble toque, o el teléfono reintenta si perdió la respuesta).
EntradaAvisoCaptura.OlvidarEnviados();
Check(!EntradaAvisoCaptura.YaEnviadoReciente("01C263551100000", false, ahora), "La primera vez sí sale");
Check(EntradaAvisoCaptura.YaEnviadoReciente("01C263551100000", false, ahora.AddMinutes(3)), "La repetición a los 3 min no sale");
Check(!EntradaAvisoCaptura.YaEnviadoReciente("01C263551100000", true, ahora.AddMinutes(3)), "Una edición es otro aviso");
Check(!EntradaAvisoCaptura.YaEnviadoReciente("01C263551100001", false, ahora.AddMinutes(3)), "Otra entrada es otro aviso");
Check(!EntradaAvisoCaptura.YaEnviadoReciente("01C263551100000", false, ahora.AddMinutes(11)), "Pasada la ventana vuelve a poder salir");

Console.WriteLine($"Entrada.Regression: {checks} comprobaciones correctas.");


/// <summary>SMTP de mentira: solo registra con qué argumentos se le pidió mandar el correo.</summary>
sealed class FakeSmtp : ISmtpService
{
    public int Llamadas { get; private set; }
    public List<string> Destinatarios { get; private set; } = new();
    public IEnumerable<string>? Copias { get; private set; }
    public IEnumerable<string>? CopiasOcultas { get; private set; }
    public IEnumerable<EmailAttachment>? Adjuntos { get; private set; }
    public bool IncluirCopiasOcultasAutomaticas { get; private set; } = true;
    public bool PreferirCola { get; private set; }

    public Task<bool> EnviarAsync(
        IEnumerable<string> destinatarios, string asunto, string cuerpoHtml,
        IEnumerable<EmailAttachment>? adjuntos = null, IEnumerable<string>? copias = null, IEnumerable<string>? copiasOcultas = null,
        bool incluirCopiasOcultasAutomaticas = true, CancellationToken ct = default, bool preferirCola = false)
    {
        Llamadas++;
        Destinatarios = destinatarios.ToList();
        Copias = copias;
        CopiasOcultas = copiasOcultas;
        Adjuntos = adjuntos;
        IncluirCopiasOcultasAutomaticas = incluirCopiasOcultasAutomaticas;
        PreferirCola = preferirCola;
        return Task.FromResult(true);
    }
}
