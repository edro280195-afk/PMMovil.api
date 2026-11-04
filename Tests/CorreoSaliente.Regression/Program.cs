using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using MimeKit;

using PMMovil.Api.Services;

// Regresiones de la cola durable de correo. Sin SMTP, sin base de datos: el transporte es falso y el reloj es manual.
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
void Equal<T>(T expected, T actual, string message)
    => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message} (esperado: {expected}, real: {actual})");

static MimeMessage Mensaje()
{
    var mensaje = new MimeMessage();
    mensaje.From.Add(new MailboxAddress("Notificaciones PMM", "notificacionespmm@imexnotify.com"));
    mensaje.To.Add(MailboxAddress.Parse("nldoperacion@grupopmm.com"));
    mensaje.Cc.Add(MailboxAddress.Parse("calidad@grupopmm.com"));
    mensaje.Bcc.Add(MailboxAddress.Parse("desarrollo3@grupopmm.com"));
    mensaje.Subject = "Entrada Caseta: B1 MAQUILA | Caja: 698623 | IMPORTACION | COLOCACION/VACIO";
    mensaje.Body = new BodyBuilder { HtmlBody = "<html><body><p>Recibió: gduran — camión ñandú</p></body></html>" }.ToMessageBody();
    return mensaje;
}

static string CarpetaTemporal() => Path.Combine(Path.GetTempPath(), "cola-correo-" + Guid.NewGuid().ToString("N"));

var carpetas = new List<string>();
ColaDeCorreo NuevaCola(TransporteFalso transporte, Func<DateTime> reloj)
{
    string carpeta = CarpetaTemporal();
    carpetas.Add(carpeta);
    return new ColaDeCorreo(transporte, carpeta, NullLogger<ColaDeCorreo>.Instance, reloj);
}

DateTime ahora = new(2026, 10, 28, 16, 34, 0);
DateTime Reloj() => ahora;

// ── Sale a la primera ────────────────────────────────────────────────────────
{
    var transporte = new TransporteFalso();
    ColaDeCorreo cola = NuevaCola(transporte, Reloj);

    Check(await cola.EncolarAsync(Mensaje(), "entrada-caseta", "E-020485/26"), "Encolar guarda el correo en disco");
    Equal(1, cola.Pendientes, "El correo espera su turno");
    Equal(1, await cola.ProcesarPendientesAsync(default), "Sale en cuanto el trabajador pasa");
    Equal(0, cola.Pendientes, "Ya no queda pendiente");
    Equal(0, cola.Fallidos, "Nada falló");

    MimeMessage enviado = transporte.Enviados.Single();
    Equal("Entrada Caseta: B1 MAQUILA | Caja: 698623 | IMPORTACION | COLOCACION/VACIO", enviado.Subject, "El asunto llega igual");
    Equal("nldoperacion@grupopmm.com", enviado.To.Mailboxes.Single().Address, "El Para llega igual");
    Equal("calidad@grupopmm.com", enviado.Cc.Mailboxes.Single().Address, "El Cc llega igual");
    Equal("desarrollo3@grupopmm.com", enviado.Bcc.Mailboxes.Single().Address, "La copia oculta se conserva al pasar por disco");
    Check(enviado.HtmlBody!.Contains("ñandú"), "Los acentos sobreviven");
}

// ── Falla, espera y reintenta ────────────────────────────────────────────────
{
    ahora = new DateTime(2026, 10, 28, 16, 34, 0);
    var transporte = new TransporteFalso { FallosRestantes = 2 };
    ColaDeCorreo cola = NuevaCola(transporte, Reloj);
    await cola.EncolarAsync(Mensaje(), "entrada-caseta", "E-020485/26");

    Equal(0, await cola.ProcesarPendientesAsync(default), "El primer intento falla");
    Equal(1, transporte.Llamadas, "Se intentó una vez");
    Equal(1, cola.Pendientes, "El correo no se pierde: sigue pendiente");

    ahora = ahora.AddSeconds(10);
    await cola.ProcesarPendientesAsync(default);
    Equal(1, transporte.Llamadas, "Antes de que se cumpla la espera no se vuelve a intentar");

    ahora = ahora.AddSeconds(30);
    Equal(0, await cola.ProcesarPendientesAsync(default), "El segundo intento también falla");
    Equal(2, transporte.Llamadas, "Pasada la espera de 30 s se reintenta");

    ahora = ahora.AddSeconds(30);
    await cola.ProcesarPendientesAsync(default);
    Equal(2, transporte.Llamadas, "La segunda espera es de un minuto, no de 30 s");

    ahora = ahora.AddMinutes(1);
    Equal(1, await cola.ProcesarPendientesAsync(default), "El tercer intento sale");
    Equal(0, cola.Pendientes, "Ya no queda pendiente");
    Equal(1, transporte.Enviados.Count, "Salió una sola vez");
}

// ── Se rinde después de los intentos, sin perder el archivo ──────────────────
{
    ahora = new DateTime(2026, 10, 28, 16, 34, 0);
    var transporte = new TransporteFalso { FallosRestantes = 1000 };
    ColaDeCorreo cola = NuevaCola(transporte, Reloj);
    await cola.EncolarAsync(Mensaje(), "entrada-caseta", "E-020485/26");

    for (int i = 0; i < ColaDeCorreo.MaxIntentos; i++)
    {
        ahora = ahora.AddHours(5);
        await cola.ProcesarPendientesAsync(default);
    }

    Equal(ColaDeCorreo.MaxIntentos, transporte.Llamadas, "Se intentó el máximo de veces");
    Equal(0, cola.Pendientes, "Ya no se reintenta");
    Equal(1, cola.Fallidos, "Queda apartado para que una persona lo mande a mano");

    ahora = ahora.AddHours(5);
    await cola.ProcesarPendientesAsync(default);
    Equal(ColaDeCorreo.MaxIntentos, transporte.Llamadas, "Lo apartado no se vuelve a intentar");
}

// ── Candado: dos procesos no mandan el mismo correo ──────────────────────────
{
    ahora = new DateTime(2026, 10, 28, 16, 34, 0);
    var transporte = new TransporteFalso();
    string carpeta = CarpetaTemporal();
    carpetas.Add(carpeta);
    var cola = new ColaDeCorreo(transporte, carpeta, NullLogger<ColaDeCorreo>.Instance, Reloj);
    await cola.EncolarAsync(Mensaje(), "entrada-caseta", "E-020485/26");

    string json = Directory.GetFiles(Path.Combine(carpeta, "pendientes"), "*.json").Single();
    string candado = Path.ChangeExtension(json, ".lock");
    File.WriteAllText(candado, string.Empty);
    File.SetLastWriteTime(candado, ahora);

    Equal(0, await cola.ProcesarPendientesAsync(default), "Con el candado de otro proceso vigente se deja pasar");
    Equal(0, transporte.Llamadas, "No se intentó mandar");

    File.SetLastWriteTime(candado, ahora.AddHours(-1));
    Equal(1, await cola.ProcesarPendientesAsync(default), "Un candado viejo (proceso muerto) no bloquea el correo");
    Check(!File.Exists(candado), "El candado se suelta");
}

// ── Un archivo de control dañado no detiene la cola ──────────────────────────
{
    ahora = new DateTime(2026, 10, 28, 16, 34, 0);
    var transporte = new TransporteFalso();
    string carpeta = CarpetaTemporal();
    carpetas.Add(carpeta);
    var cola = new ColaDeCorreo(transporte, carpeta, NullLogger<ColaDeCorreo>.Instance, Reloj);
    File.WriteAllText(Path.Combine(carpeta, "pendientes", "0000-roto.json"), "{ esto no es json");
    await cola.EncolarAsync(Mensaje(), "entrada-caseta", "E-020485/26");

    Equal(1, await cola.ProcesarPendientesAsync(default), "El correo sano sale aunque haya uno dañado");
    Equal(1, cola.Fallidos, "El dañado se aparta");
}

// ── SmtpService: si el servidor SMTP no contesta, el correo pasa a la cola en vez de perderse ──────
{
    IConfiguration configuracion = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Smtp:Host"] = "127.0.0.1",
        ["Smtp:Port"] = "1", // nadie escucha: la conexión se rechaza al instante
        ["Smtp:From"] = "notificacionespmm@imexnotify.com"
    }).Build();

    var activa = new ColaFalsa { Activa = true };
    var smtp = new SmtpService(configuracion, NullLogger<SmtpService>.Instance, new EntornoFalso(), activa);
    bool salio = await smtp.EnviarAsync(["imexoperacion@grupopmm.com"], "Revision de mercancía - prueba", "<p>hola</p>");

    Check(!salio, "Sin servidor SMTP el envío directo no sale");
    Equal(1, activa.Mensajes.Count, "El correo pasa a la cola de reintentos");
    Equal("Revision de mercancía - prueba", activa.Mensajes[0].Subject, "Lo que se encola es el mensaje armado");
    Equal("imexoperacion@grupopmm.com", activa.Mensajes[0].To.Mailboxes.Single().Address, "Con su destinatario");
    Check(activa.Mensajes[0].Bcc.Mailboxes.Any(), "Y con las copias ocultas automáticas de producción");

    // Sin trabajador de cola (modos admin de línea de comandos) el fallo se reporta y no se encola nada.
    var inactiva = new ColaFalsa { Activa = false };
    var smtpAdmin = new SmtpService(configuracion, NullLogger<SmtpService>.Instance, new EntornoFalso(), inactiva);
    Check(!await smtpAdmin.EnviarAsync(["imexoperacion@grupopmm.com"], "Prueba admin", "<p>hola</p>"), "Sin servidor SMTP no sale");
    Equal(0, inactiva.Mensajes.Count, "Sin trabajador no se deja en una cola que nadie vacía");

    // La confirmación de una salida no espera al SMTP: con la cola activa, "preferirCola" guarda el mensaje y responde al instante.
    var cola = new ColaFalsa { Activa = true };
    var smtpRapido = new SmtpService(configuracion, NullLogger<SmtpService>.Instance, new EntornoFalso(), cola);
    var reloj = System.Diagnostics.Stopwatch.StartNew();
    bool aceptado = await smtpRapido.EnviarAsync(["imexoperacion@grupopmm.com"], "Salida bodega Imex, caja: ABC-123", "<p>salió</p>", preferirCola: true);
    reloj.Stop();
    Check(aceptado, "Con la cola activa el correo se acepta sin esperar al SMTP");
    Equal(1, cola.Mensajes.Count, "Y queda en la cola");
    Check(reloj.ElapsedMilliseconds < 1000, $"Sin tocar la red: tardó {reloj.ElapsedMilliseconds} ms");

    // Sin cola activa (línea de comandos) "preferirCola" no cambia nada: se intenta directo y el fallo se reporta.
    var sinCola = new ColaFalsa { Activa = false };
    var smtpDirecto = new SmtpService(configuracion, NullLogger<SmtpService>.Instance, new EntornoFalso(), sinCola);
    Check(!await smtpDirecto.EnviarAsync(["imexoperacion@grupopmm.com"], "Salida directa", "<p>salió</p>", preferirCola: true), "Sin cola va directo y, sin servidor, falla");
    Equal(0, sinCola.Mensajes.Count, "Sin trabajador no se encola");

    // Un envío sin destinatarios no es un fallo de red: no se encola.
    Check(!await smtp.EnviarAsync([], "Sin nadie", "<p>hola</p>"), "Sin destinatarios no sale");
    Equal(1, activa.Mensajes.Count, "Y no ensucia la cola");
}

foreach (string carpeta in carpetas)
{
    try { Directory.Delete(carpeta, true); } catch (IOException) { }
}

// ── El aviso de una caja no se desvia a desarrollo por un pre-registro de tractocamion ──────────────
// El mismo tractor que deja una caja hoy suele tener un pre-registro VIGENTE para volver por otra.
Check(!ArriboPreregistro.PuedeCoincidirPorUnidad("CAJA"), "Una entrada con caja no busca pareja entre los pre-registros");
Check(!ArriboPreregistro.PuedeCoincidirPorUnidad("caja"), "Mayusculas y minusculas no cuentan");
Check(!ArriboPreregistro.PuedeCoincidirPorUnidad(""), "Sin tipo se trata como caja (el valor por omision del API)");
Check(!ArriboPreregistro.PuedeCoincidirPorUnidad(null), "Un tipo nulo tampoco busca pareja");
Check(ArriboPreregistro.PuedeCoincidirPorUnidad("TRACTOCAMION"), "El tractocamion sin caja si puede ser el arribo del pre-registro");
Check(ArriboPreregistro.PuedeCoincidirPorUnidad(" tractocamion "), "Con espacios y en minusculas tambien");

// ── Avisos de entrada que esperan a las fotos: el respaldo ─────────────────────────────────────────
// La tableta sube las fotos DESPUES de guardar y el correo sale al llegar la ultima. Si esa cadena se rompe (sin red, una
// foto que no se procesa, app cerrada) el aviso no debe perderse: el servidor lo anota y lo manda por su cuenta.
{
    DateTime t0 = new(2026, 11, 2, 9, 0, 0);
    DateTime reloj = t0;
    var carpetaDiferidos = Path.Combine(Path.GetTempPath(), "diferido-" + Guid.NewGuid().ToString("N"));
    carpetas.Add(carpetaDiferidos);
    var almacen = new AlmacenCorreoEntradaDiferido(carpetaDiferidos, NullLogger<AlmacenCorreoEntradaDiferido>.Instance, ahora: () => reloj);

    // Registrar y leer: el folio lleva diagonal y la ruta lo entrega codificado.
    almacen.Registrar("E-020485/26", actualizado: true, usuario: "gduran", indicesEsperados: [5, 3, 4, 4, 0, -2]);
    var anotado = almacen.Leer("E-020485/26");
    Check(anotado != null, "El aviso anotado se puede leer");
    Equal("E-020485/26", anotado!.CasetaId, "Conserva el folio real, con diagonal");
    Check(anotado.Actualizado, "Recuerda que era una ACTUALIZACION (lo decide el servidor, no la tableta)");
    Equal("3,4,5", string.Join(",", anotado.IndicesEsperados), "Posiciones esperadas: sin repetidas, sin ceros, en orden");
    Check(almacen.Leer("E-020485%2F26") != null, "Tambien se encuentra con el folio como llega por la ruta");
    Equal(1, almacen.Pendientes, "Hay un aviso pendiente");

    // Respaldo por tiempo: 5 minutos sin actividad.
    reloj = t0.AddMinutes(4).AddSeconds(59);
    Equal(0, almacen.Vencidos().Count, "A los 4:59 todavia no toca");
    reloj = t0.AddMinutes(5);
    Equal(1, almacen.Vencidos().Count, "A los 5 minutos sin fotos nuevas el respaldo ya puede avisar");

    // Mientras sigan llegando fotos se espera, pero hay un tope de 30 minutos desde el guardado.
    reloj = t0.AddMinutes(4);
    almacen.RegistrarActividad("E-020485/26");
    reloj = t0.AddMinutes(8).AddSeconds(59);
    Equal(0, almacen.Vencidos().Count, "Con actividad a los 4 min, a los 8:59 todavia no toca");
    reloj = t0.AddMinutes(9);
    Equal(1, almacen.Vencidos().Count, "Y a los 9 min (5 despues de la ultima actividad) si");
    reloj = t0.AddMinutes(29);
    almacen.RegistrarActividad("E-020485/26");
    reloj = t0.AddMinutes(30);
    Equal(1, almacen.Vencidos().Count, "Aunque lleguen fotos sin parar, el aviso sale a los 30 minutos del guardado");

    // Solo uno se queda con el derecho de avisar: el ultimo lote o el respaldo.
    var primero = almacen.Reclamar("E-020485/26");
    var segundo = almacen.Reclamar("E-020485%2F26");
    Check(primero != null, "El primero que reclama se lleva el aviso");
    Check(segundo == null, "El segundo no recibe nada: no hay correo repetido");
    Check(almacen.Leer("E-020485/26") == null, "Reclamado ya no figura como pendiente");
    Check(!almacen.YaAvisado("E-020485/26"), "Mientras se manda aun no esta avisado");

    // Si salio, queda anotado: un ultimo lote tardio no lo repite.
    almacen.MarcarAvisado(primero!, MotivoCorreoDiferido.Respaldo);
    Check(almacen.YaAvisado("E-020485/26"), "Avisado queda anotado");
    Check(almacen.YaAvisado("E-020485%2F26"), "Tambien con el folio de la ruta");
    almacen.Registrar("E-020485/26", actualizado: false, usuario: "gduran", indicesEsperados: []);
    Check(!almacen.YaAvisado("E-020485/26"), "Una edicion nueva del mismo folio abre un aviso nuevo");

    // Si no pudo salir, vuelve a la lista y se reintenta; tras 5 intentos se aparta y pide atencion.
    var reclamado = almacen.Reclamar("E-020485/26")!;
    almacen.Devolver(reclamado, "SMTP caido");
    var devuelto = almacen.Leer("E-020485/26");
    Check(devuelto != null, "Un aviso que no salio vuelve a la lista");
    Equal(1, devuelto!.Intentos, "Cuenta el intento");
    Equal("SMTP caido", devuelto.UltimoError ?? "", "Guarda por que fallo");
    Check(devuelto.Vence > reloj, "Se reintenta mas tarde, no en el acto");
    for (int i = 1; i < AlmacenCorreoEntradaDiferido.MaxIntentos; i++)
    {
        var otra = almacen.Reclamar("E-020485/26");
        Check(otra != null, "Sigue disponible para reintentar");
        almacen.Devolver(otra!, "SMTP caido");
    }
    Check(almacen.Leer("E-020485/26") == null, "Tras agotar los intentos ya no se reintenta solo");
    Equal(0, almacen.Pendientes, "Y no queda como pendiente");
    Equal(1, Directory.GetFiles(Path.Combine(carpetaDiferidos, "fallidos"), "*.json").Length, "Queda apartado en fallidos para atencion humana");

    // Un archivo dañado no tira al respaldo: se aparta.
    File.WriteAllText(Path.Combine(carpetaDiferidos, "pendientes", "E-000001_26.json"), "{ no es json");
    Equal(0, almacen.Vencidos().Count, "Un archivo dañado no se manda ni lanza");
    Equal(2, Directory.GetFiles(Path.Combine(carpetaDiferidos, "fallidos"), "*.json").Length, "El dañado queda en fallidos");

    // Un reclamo que quedo a medias (el proceso murio al mandar) vuelve a la lista al reiniciar: mejor repetido que perdido.
    almacen.Registrar("E-020500/26", false, "gduran", [1]);
    Check(almacen.Reclamar("E-020500/26") != null, "Se reclama");
    var almacenReiniciado = new AlmacenCorreoEntradaDiferido(
        carpetaDiferidos, NullLogger<AlmacenCorreoEntradaDiferido>.Instance, ahora: () => DateTime.Now.AddMinutes(11));
    Check(almacenReiniciado.Leer("E-020500/26") != null, "Al reiniciar, el aviso que se quedo reclamado vuelve a la lista");

    // La decision de cuando sale (logica pura).
    Equal(MotivoCorreoDiferido.FotosCompletas, CorreoEntradaDiferido.Decidir(true, false, true, false), "Llegaron todas las anunciadas: sale sin esperar la bandera");
    Equal(MotivoCorreoDiferido.Finalizar, CorreoEntradaDiferido.Decidir(true, true, false, false), "El ultimo lote de la tableta sigue valiendo");
    Equal(MotivoCorreoDiferido.Ninguno, CorreoEntradaDiferido.Decidir(true, false, false, false), "Con fotos por llegar y sin ultimo lote, todavia no");
    Equal(MotivoCorreoDiferido.FinalizarSinRegistro, CorreoEntradaDiferido.Decidir(false, true, false, false), "Sin registro (app anterior) el ultimo lote avisa como siempre");
    Equal(MotivoCorreoDiferido.Ninguno, CorreoEntradaDiferido.Decidir(false, true, false, true), "Si el respaldo ya aviso, el ultimo lote tardio NO repite el correo");
    Equal(MotivoCorreoDiferido.Ninguno, CorreoEntradaDiferido.Decidir(false, false, false, false), "Un lote cualquiera sin registro no avisa");

    Check(CorreoEntradaDiferido.Completas([3, 4, 5], [1, 2, 3, 4, 5]), "Estan todas las anunciadas");
    Check(!CorreoEntradaDiferido.Completas([3, 4, 5], [1, 2, 3, 5]), "Falta la 4");
    Check(!CorreoEntradaDiferido.Completas([], [1, 2, 3]), "Sin anuncio nunca se completa por conteo");
}

Console.WriteLine($"OK: {checks} verificaciones de la cola de correo.");

sealed class ColaFalsa : ICorreoSalienteCola
{
    public bool Activa { get; set; }
    public List<MimeMessage> Mensajes { get; } = [];

    public Task<bool> EncolarAsync(MimeMessage mensaje, string tipo, string referencia, CancellationToken ct = default)
    {
        Mensajes.Add(mensaje);
        return Task.FromResult(true);
    }
}

sealed class EntornoFalso : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Production;
    public string ApplicationName { get; set; } = "Pruebas";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

sealed class TransporteFalso : ITransporteCorreo
{
    public int Llamadas { get; private set; }
    public int FallosRestantes { get; set; }
    public List<MimeMessage> Enviados { get; } = [];

    public Task EnviarAsync(MimeMessage mensaje, CancellationToken ct)
    {
        Llamadas++;
        if (FallosRestantes > 0)
        {
            FallosRestantes--;
            throw new IOException("Mailgun no contesta");
        }

        Enviados.Add(mensaje);
        return Task.CompletedTask;
    }
}
