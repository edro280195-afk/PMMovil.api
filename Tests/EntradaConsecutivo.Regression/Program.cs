using System.Text.Json;

using PMMovil.Api.Models;
using PMMovil.Api.Services;

// Regresiones del consecutivo de Entrada Bodega (19-nov-2026): el número se tomaba al PEDIRLO (GET Entrada/consecutivo) y no
// al guardar, así que toda captura que no llegaba a guardarse dejaba un hueco en la numeración. Caseta no lo sufre porque
// toma su folio dentro de la transacción del INSERT. Sin servidor ni bases de datos: las reglas puras son las mismas que
// usa el servicio, y el almacén de abajo replica en memoria lo que el servicio hace con SQL.
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
void Equal<T>(T expected, T actual, string message)
    => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message} (esperado: {expected}, real: {actual})");

// ── La referencia ───────────────────────────────────────────────────────────────────────────────────
Equal("01C263618970000", EntradaConsecutivo.ArmarReferencia("01C26", 361897, "0000"), "01C + año + consecutivo + 0000");
Equal("26", EntradaConsecutivo.Anio2(new DateTime(2026, 11, 19)), "El año va en 2 dígitos");
Equal("07", EntradaConsecutivo.Anio2(new DateTime(2007, 1, 1)), "El año de un dígito se rellena con cero");
Equal("01C26", EntradaConsecutivo.PrefijoPorOmision(new DateTime(2026, 11, 19)), "Prefijo por omisión");

// ── Los tramos que acompañan al consecutivo: lo que mande la app manda, lo que falte se completa ─────────
PartesReferencia vacias = EntradaConsecutivo.Partes(null, "", "  ", null, null, new DateTime(2026, 11, 19));
Equal("01C26", vacias.Prefijo, "Sin tramos 1 a 3 se completa 01C + año");
Equal("0000", vacias.Sufijo, "Sin tramos 5 y 6 se completa 00 + 00");

PartesReferencia deLaApp = EntradaConsecutivo.Partes("01", "C", "25", "00", "00", new DateTime(2026, 1, 1));
Equal("01C25", deLaApp.Prefijo, "El año con el que empezó la captura se respeta aunque el guardado sea de otro año");

PartesReferencia conEspacios = EntradaConsecutivo.Partes(" 01 ", " C ", " 26 ", " 00 ", " 00 ", new DateTime(2026, 11, 19));
Equal("01C26", conEspacios.Prefijo, "Los espacios de los extremos no cuentan");

// ── Buscar el primer número libre ───────────────────────────────────────────────────────────────────
var ocupadas = new HashSet<string>();
Task<bool> Existe(string referencia) => Task.FromResult(ocupadas.Contains(referencia));

AsignacionConsecutivo? libre = await EntradaConsecutivo.BuscarLibreAsync(361898, "01C26", "0000", Existe);
Equal(361898, libre!.Numero, "Con todo libre se usa el del contador");
Equal(0, libre.Saltos, "Sin ocupados no se brinca nada");
Equal(1, libre.Incremento, "Se consume un solo número");
Equal("01C263618980000", libre.Referencia, "La referencia queda armada");

ocupadas.Add("01C263618980000");
ocupadas.Add("01C263618990000");
ocupadas.Add("01C263619000000");
libre = await EntradaConsecutivo.BuscarLibreAsync(361898, "01C26", "0000", Existe);
Equal(361901, libre!.Numero, "Se brinca los números que otro sistema ya tomó");
Equal(3, libre.Saltos, "Cuenta los que se brincó");
Equal(4, libre.Incremento, "El contador pasa también por encima de los brincados");

AsignacionConsecutivo? otroAnio = await EntradaConsecutivo.BuscarLibreAsync(361898, "01C27", "0000", Existe);
Equal(361898, otroAnio!.Numero, "Un número ocupado en otro año no estorba");

AsignacionConsecutivo? agotado = await EntradaConsecutivo.BuscarLibreAsync(1, "01C26", "0000", _ => Task.FromResult(true), maxSaltos: 5);
Check(agotado == null, "Sin ningún número libre en el tope de intentos se rinde en lugar de ciclarse");

// ── El flujo completo, con el contador y traf en memoria ────────────────────────────────────────────
// Las apps instaladas piden el número al empezar y guardan al final: lo que se pierde es lo que no se guarda.
{
    var viejo = new Almacen { Contador = 361898 };
    var apartados = new List<int>();
    for (int i = 0; i < 5; i++) apartados.Add(await viejo.ApartarAsync());
    viejo.GuardarApartado(apartados[0]);
    viejo.GuardarApartado(apartados[3]);

    Equal(361903, viejo.Contador, "Apartar al pedir: cinco solicitudes mueven el contador cinco números");
    Equal(2, viejo.Traf.Count, "Pero solo dos se guardaron");

    var perdidos = apartados.Where(n => n != apartados[0] && n != apartados[3]).ToList();
    Check(perdidos.SequenceEqual(new[] { 361899, 361900, 361902 }), "Los tres que no se guardaron se perdieron");
    Check(viejo.Huecos().SequenceEqual(new[] { 361899, 361900 }),
        "Dos se ven como huecos entre entradas guardadas (el tercero aparece como brinco en la siguiente)");
}

// Con vista previa y asignación al guardar, lo que no se guarda no cuesta nada.
{
    var nuevo = new Almacen { Contador = 361898 };
    for (int i = 0; i < 5; i++) await nuevo.VistaAsync();
    Equal(361898, nuevo.Contador, "La vista previa no mueve el contador");

    string primera = await nuevo.GuardarAsignandoAsync();
    string segunda = await nuevo.GuardarAsignandoAsync();
    Equal("01C263618980000", primera, "El primer guardado toma el número del contador");
    Equal("01C263618990000", segunda, "El segundo, el siguiente");
    Equal(361900, nuevo.Contador, "El contador avanzó solo por lo que se guardó");
    Check(nuevo.Huecos().Count == 0, "No queda ningún hueco");
}

// Dos capturas simultáneas ven el mismo número de vista previa; al guardar cada una recibe uno distinto.
{
    var almacen = new Almacen { Contador = 361898 };
    int vistaA = await almacen.VistaAsync();
    int vistaB = await almacen.VistaAsync();
    Equal(vistaA, vistaB, "Las dos capturas ven el mismo número de vista previa");

    string guardadaA = await almacen.GuardarAsignandoAsync();
    string guardadaB = await almacen.GuardarAsignandoAsync();
    Check(guardadaA != guardadaB, "Pero cada una se guarda con un número distinto");
    Equal("01C263618980000", guardadaA, "La que guarda primero se queda con el que vio");
    Equal("01C263618990000", guardadaB, "La otra recibe el siguiente, y la app lo adopta de la respuesta");
}

// Un guardado que falla se revierte con todo y contador: el número queda libre para la siguiente captura.
{
    var almacen = new Almacen { Contador = 361898 };
    await almacen.GuardarAsignandoAsync(falla: true);
    Equal(361898, almacen.Contador, "Si el guardado falla el contador no se mueve");
    Equal(0, almacen.Traf.Count, "Y no queda ninguna entrada a medias");

    string despues = await almacen.GuardarAsignandoAsync();
    Equal("01C263618980000", despues, "La siguiente captura recibe el mismo número que no se pudo usar");
}

// Números que otro sistema (legacy) o una captura manual ya tomaron sin pasar por el contador.
{
    var almacen = new Almacen { Contador = 361898 };
    almacen.Traf.Add("01C263618980000");
    almacen.Traf.Add("01C263618990000");

    int vista = await almacen.VistaAsync();
    Equal(361900, vista, "La vista previa ya muestra el primero libre");
    Equal(361898, almacen.Contador, "Y sigue sin mover el contador");

    string guardada = await almacen.GuardarAsignandoAsync();
    Equal("01C263619000000", guardada, "El guardado se brinca los ocupados");
    Equal(361901, almacen.Contador, "El contador queda después del número usado");
}

// ── Lo que viaja por el cable: las apps instaladas no deben notar nada ──────────────────────────────────
var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);

string respuestaGuardar = JsonSerializer.Serialize(new ResultadoOperacion { Success = true, Message = "ok", RecepcionId = 361898 }, web);
Check(!respuestaGuardar.Contains("referencia", StringComparison.OrdinalIgnoreCase),
    "Sin referencia asignada la respuesta no lleva el campo (Salida usa el mismo tipo y no debe cambiar)");
Check(!respuestaGuardar.Contains("ref4", StringComparison.OrdinalIgnoreCase), "Tampoco ref4");

string respuestaEntrada = JsonSerializer.Serialize(new ResultadoOperacion
{
    Success = true,
    Message = "ok",
    RecepcionId = 361898,
    Referencia = "01C263618980000",
    Ref4 = "361898"
}, web);
Check(respuestaEntrada.Contains("\"referencia\":\"01C263618980000\""), "Al guardar una entrada la respuesta trae la referencia definitiva");
Check(respuestaEntrada.Contains("\"ref4\":\"361898\""), "Y el consecutivo");

string consecutivoApartado = JsonSerializer.Serialize(new ConsecutivoResult { Success = true, Numero = "361898" }, web);
Check(consecutivoApartado.Contains("\"tentativo\":false"), "Una respuesta sin vista previa dice que el número ya es suyo");
string consecutivoVista = JsonSerializer.Serialize(new ConsecutivoResult { Success = true, Numero = "361898", Tentativo = true }, web);
Check(consecutivoVista.Contains("\"tentativo\":true"), "La vista previa avisa que no se consumió");

GuardarRecepcionRequest? viejoRequest = JsonSerializer.Deserialize<GuardarRecepcionRequest>("{\"ref4\":\"361898\"}", web);
Check(viejoRequest!.ConsecutivoAlGuardar == null, "Una app instalada no manda la bandera: el número viene apartado");
GuardarRecepcionRequest? nuevoRequest = JsonSerializer.Deserialize<GuardarRecepcionRequest>("{\"ref4\":\"361898\",\"consecutivoAlGuardar\":true}", web);
Check(nuevoRequest!.ConsecutivoAlGuardar == true, "La app nueva pide que el servidor asigne el número al guardar");

Console.WriteLine($"EntradaConsecutivo.Regression: {checks} comprobaciones correctas.");

/// <summary>
/// El contador (consecutivo.numero del renglón '01C') y traf.creferencia en memoria, con las mismas operaciones que hace
/// el servicio: apartar al pedir (apps instaladas), vista previa y guardar asignando el número dentro de la transacción.
/// </summary>
sealed class Almacen
{
    public int Contador { get; set; }
    public HashSet<string> Traf { get; } = new();
    public DateTime Ahora { get; } = new(2026, 11, 19, 14, 0, 0);

    private string Prefijo => EntradaConsecutivo.PrefijoPorOmision(Ahora);

    private Task<bool> Existe(string referencia) => Task.FromResult(Traf.Contains(referencia));

    /// <summary>GET Entrada/consecutivo sin parámetros: el contador avanza en ese instante.</summary>
    public async Task<int> ApartarAsync()
    {
        AsignacionConsecutivo libre = (await EntradaConsecutivo.BuscarLibreAsync(
            Contador, Prefijo, EntradaConsecutivo.SufijoPorOmision, Existe))!;
        Contador += libre.Incremento;
        return libre.Numero;
    }

    /// <summary>GET Entrada/consecutivo?vista=true: solo lee.</summary>
    public async Task<int> VistaAsync()
        => (await EntradaConsecutivo.BuscarLibreAsync(Contador, Prefijo, EntradaConsecutivo.SufijoPorOmision, Existe))!.Numero;

    /// <summary>POST Entrada de una app instalada: guarda con el número que ya había apartado.</summary>
    public void GuardarApartado(int numero)
        => Traf.Add(EntradaConsecutivo.ArmarReferencia(Prefijo, numero, EntradaConsecutivo.SufijoPorOmision));

    /// <summary>
    /// POST Entrada con ConsecutivoAlGuardar: dentro de la transacción toma el número, avanza el contador e inserta. Con
    /// <paramref name="falla"/> el resto del guardado truena y la transacción se revierte completa.
    /// </summary>
    public async Task<string> GuardarAsignandoAsync(bool falla = false)
    {
        int contadorAntes = Contador;
        var trafAntes = new HashSet<string>(Traf);
        try
        {
            AsignacionConsecutivo libre = (await EntradaConsecutivo.BuscarLibreAsync(
                Contador, Prefijo, EntradaConsecutivo.SufijoPorOmision, Existe))!;
            Contador += libre.Incremento;
            Traf.Add(libre.Referencia);

            if (falla) throw new InvalidOperationException("falló un INSERT posterior");
            return libre.Referencia;
        }
        catch (InvalidOperationException)
        {
            // ROLLBACK: el contador y traf vuelven a como estaban.
            Contador = contadorAntes;
            Traf.Clear();
            foreach (string referencia in trafAntes) Traf.Add(referencia);
            return string.Empty;
        }
    }

    /// <summary>Los números por debajo del último guardado que nunca se guardaron.</summary>
    public List<int> Huecos()
    {
        var numeros = Traf
            .Select(r => int.Parse(r[5..^4]))
            .OrderBy(n => n)
            .ToList();
        var huecos = new List<int>();
        for (int i = 1; i < numeros.Count; i++)
        {
            for (int n = numeros[i - 1] + 1; n < numeros[i]; n++) huecos.Add(n);
        }

        return huecos;
    }
}
