namespace PMMovil.Api.Services;

/// <summary>Una etiqueta ya escaneada, con lo mínimo que necesita la revisión de ráfagas.</summary>
public sealed record EtiquetaRafaga(string Texto, string? Manejo, DateTime? TimeScan, string? Historial);

/// <summary>Una etiqueta cargada, con lo mínimo que necesita la revisión de tiempo final.</summary>
public sealed record EtiquetaTiempo(string? Manejo, DateTime? TimeScan);

/// <summary>Una etiqueta que le toca a la relación según tráfico/agrupa, con la relación a la que está asignada hoy (vacía si ninguna).</summary>
public sealed record EtiquetaDeRelacion(string? Texto, string? Relacion);

/// <summary>
/// Reglas puras de la carga de mercancía (Salida de bodega). Replican el legacy
/// (<c>Reportes.Business\Relacion.cs</c>: RevisaTiempo, RevisaTiempoReal, Autorizar…) sin tocar la base,
/// para poder probarlas con la consola de regresión.
/// </summary>
public static class SalidaReglas
{
    /// <summary>Máximo de segundos entre dos escaneos para considerarlos parte de la misma ráfaga.</summary>
    public const double SegundosVentanaRafaga = 20;

    /// <summary>Promedio mínimo de segundos por bulto en la revisión final.</summary>
    public const double SegundosMinimosPorBulto = 45;

    public const string ManejoPaqueteMultiple = "PAQ/M";

    /// <summary>
    /// Estado del legacy cuando no se pueden sumar los bultos de la relación (sin partidas o sin agrupar). El supervisor
    /// no puede autorizar mientras exista (RelacionBodega.aspx.cs: Estatus.Contains("ERROR")).
    /// </summary>
    public const string EstatusInformacionIncorrecta = "ERROR EN INFORMACION (revisar si esta agrupado)";

    /// <summary>Clientes exentos de la revisión de ráfaga (legacy <c>RevisaTiempoReal</c>, ERV 2025-07-16). Con ellos el umbral sube de 1 a 3 bultos.</summary>
    public static readonly IReadOnlyList<string> ClientesExentosRafaga =
        new[] { "2944", "CL7283", "CL7027", "CL7331", "CL7597", "CL7467", "2901" };

    /// <summary>Clientes exentos de la revisión final de tiempo (legacy <c>RevisaTiempo</c>; ojo: aquí NO está CL7467).</summary>
    public static readonly IReadOnlyList<string> ClientesExentosTiempoFinal =
        new[] { "2944", "CL7283", "CL7027", "CL7331", "CL7597", "2901" };

    private static readonly string[] EstadosCargaCerrada = { "CARGADA", "AUTORIZADA", "SALIDA", "DEMASIADO RAPIDO" };
    private static readonly string[] EstadosBloqueo = { "BLOQUEO", "BLOQUEO REIMPRESION" };

    /// <summary>True si alguno de los ids de cliente (separados por coma) está en la lista. Igualdad exacta, no por texto.</summary>
    public static bool EsClienteExento(string? idsCliente, IEnumerable<string> exentos)
    {
        if (string.IsNullOrWhiteSpace(idsCliente)) return false;
        var ids = idsCliente.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return ids.Any(id => exentos.Contains(id, StringComparer.OrdinalIgnoreCase));
    }

    public static bool EsEstadoCargaCerrada(string? estatusCarga)
        => EstadosCargaCerrada.Contains((estatusCarga ?? "").Trim(), StringComparer.OrdinalIgnoreCase);

    public static bool EsEstadoBloqueo(string? estatusCarga)
        => EstadosBloqueo.Contains((estatusCarga ?? "").Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Estados de mercancía con los que el cargador puede trabajar. El legacy (<c>SalidaBodega.aspx.cs:138-156</c>) esconde
    /// escáner y finalizar con cualquier otro: "INFORMACION INCORRECTA…", "ERROR…", "RELACIÓN OCUPADA", "PRECARGA", etc.
    /// </summary>
    public static bool EstadoPermiteCaptura(string? estatusMercancia)
        => (estatusMercancia ?? "").Trim() is "COMPLETA" or "DIFERENCIA EN BULTOS-ETIQUETAS" or "NO DISP ETIQUETAS EN MCIA";

    /// <summary>
    /// ¿Un bulto de la relación cuenta como ya cargado? Igual que el legacy (<c>GetEtiqEntradas</c>, <c>Relacion.cs:1101-1142</c>
    /// y <c>SalidaBodega.contar()</c>): basta con que tenga hora de escaneo, sin importar a qué relación esté asignado.
    /// Un bulto cargado «sin relación» (entrada directa o transbordo, que el cargador escanea antes de que exista la relación)
    /// queda con <c>etiqueta_mcia.relacion = ''</c>; cuando esa entrada se agrupa después en una relación, esos bultos ya están
    /// cargados y no hay que volver a escanearlos (el legacy los cuenta y, al pausar o finalizar, les asigna la relación).
    /// Medido en producción el 17-nov-2026 con la 01I26106890: 30 de 37 con hora de escaneo, de los cuales 27 tenían la
    /// relación vacía; el API solo contaba los 3 asignados.
    /// </summary>
    /// <param name="tieneHoraDeEscaneo">La etiqueta tiene <c>time_scan</c> con fecha real (no 0000-00-00).</param>
    public static bool BultoCuentaComoCargado(bool tieneHoraDeEscaneo) => tieneHoraDeEscaneo;

    /// <summary>
    /// ¿El bulto ya escaneado pertenece a OTRA relación? Es la única condición en que el legacy avisa
    /// (<c>Relacion.cs:1125-1128</c>): bulto con hora de escaneo, con relación asignada y distinta de la que se carga. El estado
    /// que resulta es «INFORMACION INCORRECTA EN RELACION, NOTIFICAR A ENCARGADO,ENTRADA DE OTRA RELACION AGRUPADA &lt;entrada&gt;».
    /// Un bulto sin hora de escaneo asignado a otra relación NO es error para el legacy: sale como pendiente y al escanearlo
    /// se reasigna.
    /// </summary>
    public static bool BultoEsDeOtraRelacion(string? relacionDelBulto, string? relno, bool tieneHoraDeEscaneo)
    {
        if (!tieneHoraDeEscaneo) return false;

        string asignada = (relacionDelBulto ?? "").Trim();
        return asignada.Length > 0 && !string.Equals(asignada, (relno ?? "").Trim(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Textos de las etiquetas sobre las que actúa el supervisor (legacy <c>GetEtiqenRelacion</c>, <c>Relacion.cs:1400-1454</c>):
    /// las asignadas a la relación MÁS las que le tocan por tráfico y todavía no están asignadas a ninguna
    /// (<c>relacion = ''</c>, p. ej. bultos de transbordo cargados «sin relación»). «Eliminar cargado» y «Retornar a espera»
    /// del legacy recorren esa lista completa; el API solo tocaba las asignadas. Sin repetidos y en orden de aparición.
    /// </summary>
    public static IReadOnlyList<string> TextosVistaSupervisor(
        IEnumerable<string?> asignadasALaRelacion,
        IEnumerable<EtiquetaDeRelacion> deLaRelacionPorTrafico)
    {
        var resultado = new List<string>();
        var vistos = new HashSet<string>(StringComparer.Ordinal);

        void Agregar(string? texto)
        {
            string limpio = (texto ?? "").Trim();
            if (limpio.Length > 0 && vistos.Add(limpio)) resultado.Add(limpio);
        }

        foreach (var texto in asignadasALaRelacion) Agregar(texto);
        foreach (var etiqueta in deLaRelacionPorTrafico)
        {
            if (string.IsNullOrWhiteSpace(etiqueta.Relacion)) Agregar(etiqueta.Texto);
        }

        return resultado;
    }

    /// <summary>
    /// Un historial de etiqueta pide bloqueo si nunca se desbloqueó o si, después del último desbloqueo, se volvió a escanear.
    /// (El legacy tiene aquí un error de índice que lo vuelve más laxo; esta es la intención del código.)
    /// </summary>
    public static bool RequiereBloqueoPorHistorial(string? historial)
    {
        if (string.IsNullOrWhiteSpace(historial)) return true;
        if (!historial.Contains("DESBLOQUEO")) return true;

        var partes = historial.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string ultimo = partes.LastOrDefault() ?? historial;
        return ultimo.Contains("ESCANEO");
    }

    /// <summary>
    /// Revisión de ráfaga (legacy <c>RevisaTiempoReal</c>): recorre las etiquetas por hora de escaneo y, si hay más de una
    /// (más de tres en clientes exentos) dentro de la misma ráfaga de 20 s, devuelve las que hay que bloquear.
    /// Devuelve null si todo va bien.
    /// </summary>
    public static IReadOnlyList<EtiquetaRafaga>? DetectarRafaga(IEnumerable<EtiquetaRafaga> etiquetasPorHora, string? idsCliente)
    {
        bool exento = EsClienteExento(idsCliente, ClientesExentosRafaga);
        var enBloque = new List<EtiquetaRafaga>();
        DateTime? inicioBloque = null;
        int count = 0;

        foreach (var et in etiquetasPorHora)
        {
            if (et.Manejo == "T" && !string.IsNullOrEmpty(et.Historial)) continue;
            if (et.Manejo == ManejoPaqueteMultiple) continue;
            if (!et.TimeScan.HasValue || et.TimeScan.Value.Year <= 2010) continue;

            count++;
            if (inicioBloque == null)
            {
                inicioBloque = et.TimeScan.Value;
                count = 1;
                enBloque.Clear();
            }
            else
            {
                double segundos = (et.TimeScan.Value - inicioBloque.Value).TotalSeconds;
                if (segundos > SegundosVentanaRafaga)
                {
                    inicioBloque = null;
                    count = 1;
                    enBloque.Clear();
                }
                else
                {
                    inicioBloque = et.TimeScan.Value;
                }
            }
            enBloque.Add(et);

            bool umbral = (count > 3 && exento) || (count > 1 && !exento && inicioBloque != null);
            if (umbral && enBloque.Any(e => RequiereBloqueoPorHistorial(e.Historial)))
            {
                return enBloque.ToList();
            }
        }

        return null;
    }

    /// <summary>
    /// Revisión final de tiempo (legacy <c>RevisaTiempo</c>, <c>Relacion.cs:369-407</c>). Se replica LITERAL: solo marca
    /// "DEMASIADO RAPIDO" cuando hay exactamente UN bulto que no sea PAQ/M y el promedio es menor a 45 s
    /// (cambio de ERV del 17-jul-2025). Si el negocio confirma que era <c>count &gt; 1</c>, se cambia aquí.
    /// </summary>
    public static bool EsCargaDemasiadoRapida(IReadOnlyCollection<EtiquetaTiempo> etiquetas, string? idsCliente)
        => EsCargaDemasiadoRapida(etiquetas, idsCliente, out _, out _, out _);

    public static bool EsCargaDemasiadoRapida(
        IReadOnlyCollection<EtiquetaTiempo> etiquetas,
        string? idsCliente,
        out DateTime inicio,
        out DateTime fin,
        out int cantidad)
    {
        inicio = DateTime.MaxValue;
        fin = DateTime.MinValue;
        cantidad = 0;

        foreach (var et in etiquetas)
        {
            if (et.Manejo != ManejoPaqueteMultiple) cantidad++;
            if (!et.TimeScan.HasValue || et.TimeScan.Value.Year <= 2000) continue;
            if (et.TimeScan.Value < inicio) inicio = et.TimeScan.Value;
            if (et.TimeScan.Value > fin) fin = et.TimeScan.Value;
        }

        if (cantidad == 0 || fin < inicio) return false;

        double promedio = (fin - inicio).TotalSeconds / cantidad;
        return promedio < SegundosMinimosPorBulto
            && cantidad == 1
            && !EsClienteExento(idsCliente, ClientesExentosTiempoFinal);
    }

    /// <summary>
    /// Motivo por el que un supervisor NO puede autorizar la relación (misma lógica que el botón del legacy,
    /// <c>RelacionBodega.aspx.cs:189-247</c>). Null si puede.
    /// </summary>
    public static string? MotivoNoSePuedeAutorizar(
        string? estatus,
        string? estatusCarga,
        string? usuarioCargo,
        string usuario,
        bool ocupada,
        bool hayEtiquetasAjenas,
        bool hayEtiquetasConError,
        bool informacionIncorrecta = false)
    {
        string st = (estatus ?? "").Trim().ToUpperInvariant();
        string carga = (estatusCarga ?? "").Trim().ToUpperInvariant();
        string cargo = (usuarioCargo ?? "").Trim();
        bool noRequerido = string.Equals(cargo, "NO REQUERIDO", StringComparison.OrdinalIgnoreCase);

        if (!noRequerido && cargo.Length > 0 && string.Equals(cargo, usuario.Trim(), StringComparison.OrdinalIgnoreCase))
            return "NECESITA AUTORIZAR OTRO ENCARGADO";
        if (ocupada) return "RELACION OCUPADA";
        if (hayEtiquetasAjenas) return "ERROR, retorne relacion, posible descargo";
        if (informacionIncorrecta) return EstatusInformacionIncorrecta;
        if (hayEtiquetasConError) return "INFORMACION INCORRECTA EN RELACION, NOTIFICAR A ENCARGADO, ENTRADA DE OTRA RELACION AGRUPADA";
        if (st == "EN PROCESO") return "EN PROCESO";
        if (carga is "EN ESPERA" or "CARGANDO") return carga;
        if (carga.Length == 0 && !noRequerido) return "NO HA SIDO CARGADA";
        if (EsEstadoBloqueo(carga)) return "RELACION BLOQUEADA";
        if (carga == "SALIDA") return "LA RELACION YA SALIO";
        return null;
    }

    /// <summary>Motivo por el que no se puede retornar a espera (el legacy deshabilita el botón con bloqueo).</summary>
    public static string? MotivoNoSePuedeRetornar(string? estatusCarga)
        => EsEstadoBloqueo(estatusCarga) ? "RELACION BLOQUEADA, DESBLOQUEALA PRIMERO" : null;

    /// <summary>
    /// Llena una plantilla de correo de la tabla <c>reportes.config_correo</c>: cada <c>[clave]</c> se cambia por su valor
    /// (el legacy hacía lo mismo con [relacion], [usuario], [caja], [inicio], [fin], [cantidad] y [tiempo]).
    /// En el cuerpo HTML los valores van codificados para que un usuario o una caja con símbolos no rompa el correo.
    /// </summary>
    public static string AplicarPlantilla(string plantilla, IReadOnlyDictionary<string, string> valores, bool html)
    {
        string resultado = plantilla ?? "";
        foreach (var par in valores)
        {
            string valor = par.Value ?? "";
            if (html) valor = System.Net.WebUtility.HtmlEncode(valor);
            resultado = resultado.Replace("[" + par.Key + "]", valor, StringComparison.OrdinalIgnoreCase);
        }
        return resultado;
    }

    /// <summary>Escapa los comodines de LIKE (%, _ y \) de lo que teclea el usuario.</summary>
    public static string EscaparLike(string? texto)
        => (texto ?? "").Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>Recorta un texto al largo de su columna (el legacy recortaba a 50 antes de insertar).</summary>
    public static string Recortar(string? texto, int max)
    {
        string valor = texto ?? "";
        return valor.Length <= max ? valor : valor[..max];
    }
}
