using System;

namespace PMMovil.Api.Services;

/// <summary>Qué hacer cuando un revisador toca «Tomar carpeta».</summary>
public enum DecisionToma
{
    /// <summary>Nadie la tiene: se registra la toma.</summary>
    Nueva,

    /// <summary>El mismo revisador ya la tenía tomada: se conserva la hora original, no se reinicia el reloj.</summary>
    YaEraTuya,

    /// <summary>El revisador ya la está revisando: tomarla no tiene sentido.</summary>
    EnRevisionPropia,

    /// <summary>Otra persona la está revisando ahora mismo: no se le quita.</summary>
    EnRevisionPorOtro,

    /// <summary>La entrada ya se revisó: no hay nada que tomar.</summary>
    YaRevisada,

    /// <summary>Otra persona la tomó antes: hay que avisar quién y preguntar si se la quiere quedar.</summary>
    PedirConfirmacion,

    /// <summary>Otra persona la tenía y el revisador confirmó: pasa a su nombre.</summary>
    Transferir
}

/// <summary>
/// Reglas de «Tomar carpeta» sin base de datos. La toma es el momento en que el revisador agarra la carpeta física:
/// hoy la app solo sabe cuándo abre la entrada, y al abrirla arranca el cronómetro, así que quien agarraba la carpeta a las
/// 9:30 y la revisaba a las 17:00 no dejaba rastro de las 9:30 y nadie sabía quién traía la entrada.
/// </summary>
public static class CarpetaTomaRules
{
    public const int AvisoMinPorOmision = 60;
    public const int CriticoMinPorOmision = 180;

    public const string NivelOk = "OK";
    public const string NivelAviso = "AVISO";
    public const string NivelCritico = "CRITICO";

    /// <summary>
    /// Qué tan grave es el tiempo que lleva una carpeta tomada sin que se inicie la revisión. Los umbrales los fija el
    /// supervisor (monitor_config); si vienen vacíos o incoherentes se usan los de siempre.
    /// </summary>
    public static string Nivel(long segundosSinIniciar, int avisoMin, int criticoMin)
    {
        (int aviso, int critico) = NormalizarUmbrales(avisoMin, criticoMin);
        long minutos = Math.Max(0, segundosSinIniciar) / 60;
        if (minutos >= critico) return NivelCritico;
        if (minutos >= aviso) return NivelAviso;
        return NivelOk;
    }

    /// <summary>Un aviso en cero o un crítico por debajo del aviso no tienen sentido: se vuelve a los valores por omisión.</summary>
    public static (int Aviso, int Critico) NormalizarUmbrales(int? avisoMin, int? criticoMin)
    {
        int aviso = avisoMin is > 0 ? avisoMin.Value : AvisoMinPorOmision;
        int critico = criticoMin is > 0 ? criticoMin.Value : CriticoMinPorOmision;
        if (critico <= aviso)
        {
            return (AvisoMinPorOmision, CriticoMinPorOmision);
        }

        return (aviso, critico);
    }

    /// <summary>Orden de las reglas: lo definitivo primero (ya revisada, en revisión), luego quién la tiene.</summary>
    public static DecisionToma Decidir(
        bool yaRevisada,
        bool enRevisionPropia,
        bool enRevisionPorOtro,
        bool esMiaActiva,
        bool tieneOtroTitular,
        bool confirmar)
    {
        if (yaRevisada) return DecisionToma.YaRevisada;
        if (enRevisionPropia) return DecisionToma.EnRevisionPropia;
        if (enRevisionPorOtro) return DecisionToma.EnRevisionPorOtro;
        if (esMiaActiva) return DecisionToma.YaEraTuya;
        if (tieneOtroTitular) return confirmar ? DecisionToma.Transferir : DecisionToma.PedirConfirmacion;
        return DecisionToma.Nueva;
    }

    /// <summary>Código estable que la app interpreta (el mensaje es solo para mostrar).</summary>
    public static string Codigo(DecisionToma decision) => decision switch
    {
        DecisionToma.Nueva => "TOMADA",
        DecisionToma.Transferir => "TOMADA",
        DecisionToma.YaEraTuya => "YA_ERA_TUYA",
        DecisionToma.EnRevisionPropia => "EN_REVISION_PROPIA",
        DecisionToma.EnRevisionPorOtro => "EN_REVISION_POR_OTRO",
        DecisionToma.YaRevisada => "YA_REVISADA",
        DecisionToma.PedirConfirmacion => "TOMADA_POR_OTRO",
        _ => "DESCONOCIDO"
    };

    /// <summary>Si la decisión deja a la carpeta a nombre del revisador (la app lo muestra como éxito).</summary>
    public static bool Exito(DecisionToma decision) =>
        decision is DecisionToma.Nueva or DecisionToma.Transferir or DecisionToma.YaEraTuya;

    /// <summary>«hace 3 h 20 min» / «hace 12 min» / «hace un momento».</summary>
    public static string Hace(long segundos)
    {
        long minutos = Math.Max(0, segundos) / 60;
        if (minutos < 1) return "hace un momento";
        if (minutos < 60) return $"hace {minutos} min";
        long horas = minutos / 60;
        long resto = minutos % 60;
        if (horas < 24) return resto == 0 ? $"hace {horas} h" : $"hace {horas} h {resto} min";
        long dias = horas / 24;
        return dias == 1 ? "hace 1 día" : $"hace {dias} días";
    }
}
