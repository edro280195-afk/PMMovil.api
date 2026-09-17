using System;
using System.Collections.Generic;

namespace PMMovil.Api.Models;

/// <summary>
/// Una plantilla facial tal como la manda el dispositivo: el vector de SFace
/// en base64 mas la pose y la calidad con que se capturo.
///
/// El vector son 128 floats little-endian = 512 bytes exactos. El mismo
/// formato que ya usa el laboratorio, para que las mediciones sigan
/// comparandose entre si.
/// </summary>
public class FacePlantillaDto
{
    public string VectorBase64 { get; set; } = string.Empty;
    public int Dims { get; set; } = 128;

    /// <summary>-1 = frontal; 0..23 = sector del anillo de enrolamiento.</summary>
    public int PoseSector { get; set; } = -1;

    public float? Yaw { get; set; }
    public float? Pitch { get; set; }
    public float? Roll { get; set; }

    public float? Luma { get; set; }
    public float? Nitidez { get; set; }
    public float? ScoreDeteccion { get; set; }
    public int? RostroPx { get; set; }
}

/// <summary>
/// Alta de rostro completa, en un solo viaje.
///
/// El dispositivo hace las dos vueltas del anillo por su cuenta (tiene los
/// modelos y no necesita al servidor para comparar sus propias capturas) y
/// manda hasta aca las plantillas ya seleccionadas.
/// </summary>
public class FaceEnrolamientoRequest
{
    /// <summary>
    /// Texto del aviso que la persona acepto. Sin esto el alta se rechaza:
    /// es dato biometrico de un empleado y el consentimiento se sella junto
    /// con el registro.
    /// </summary>
    public string? AceptacionTexto { get; set; }

    /// <summary>
    /// Trazo de la firma en PNG, base64. Evidencia de que quien acepto el
    /// aviso fue la persona y no alguien con su equipo desbloqueado.
    /// </summary>
    public string? FirmaPngBase64 { get; set; }

    /// <summary>
    /// Como se llama esta apariencia: "Con lentes", "Sin barba". La ve la
    /// persona en su lista, asi que si llega vacia el servidor pone una.
    /// </summary>
    public string? Etiqueta { get; set; }

    /// <summary>
    /// true = borrar todas las apariencias anteriores y dejar solo esta.
    ///
    /// El default es AGREGAR. Reemplazar existe para cuando la persona quiere
    /// empezar de cero (bajo mucho de peso, cambio radical), no para el caso
    /// comun de "hoy vengo con lentes".
    /// </summary>
    public bool Reemplazar { get; set; }

    public string? Dispositivo { get; set; }
    public string? Planta { get; set; }
    public string? Modelo { get; set; }

    public List<FacePlantillaDto> Plantillas { get; set; } = new();
}

/// <summary>
/// Una apariencia dada de alta: un juego de plantillas capturado en una sola
/// sesion frente al anillo. Es lo que la persona ve y borra en Ajustes.
/// </summary>
public class FaceAparienciaDto
{
    public long Id { get; set; }
    public string? Etiqueta { get; set; }
    public int Plantillas { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? Dispositivo { get; set; }

    /// <summary>Si quedo guardado el trazo de la firma junto con el aviso.</summary>
    public bool Firmado { get; set; }
}

/// <summary>
/// Intento de entrar con la cara. Va sin autenticacion -- es justamente lo
/// que se esta pidiendo -- asi que solo lleva el vector y las condiciones en
/// que se tomo. Nunca lleva un nombre de usuario: quien decide la identidad
/// es el servidor.
/// </summary>
public class FaceLoginRequest
{
    public string VectorBase64 { get; set; } = string.Empty;
    public int Dims { get; set; } = 128;
    public string? Modelo { get; set; }

    /// <summary>Planta del equipo, para acotar la galeria. Opcional.</summary>
    public string? Planta { get; set; }

    public string? Dispositivo { get; set; }

    /// <summary>1 o 2 dentro de la misma presencia de la persona frente a la camara.</summary>
    public int Intento { get; set; } = 1;

    public float? LuzLux { get; set; }
    public float? Luma { get; set; }
    public float? Nitidez { get; set; }
    public float? ScoreDeteccion { get; set; }
    public float? Yaw { get; set; }
    public float? Pitch { get; set; }
    public float? Roll { get; set; }
    public int? RostroPx { get; set; }
    public int? MsDispositivo { get; set; }
}

/// <summary>Respuesta generica de las operaciones de rostro.</summary>
public class FaceResult
{
    public bool Success { get; set; }
    public long Id { get; set; }
    public int Plantillas { get; set; }
    public string? Message { get; set; }
}

/// <summary>Si la persona autenticada ya tiene su rostro dado de alta.</summary>
public class FaceEstadoResponse
{
    public bool Success { get; set; } = true;

    /// <summary>false cuando face_config.activo = 0 (todavia sin umbral medido).</summary>
    public bool Disponible { get; set; }

    public bool Enrolado { get; set; }

    /// <summary>Suma de plantillas activas de TODAS las apariencias.</summary>
    public int Plantillas { get; set; }

    public DateTime? EnroladoEl { get; set; }
    public string? Dispositivo { get; set; }
    public string? Message { get; set; }

    /// <summary>Las apariencias activas, de la mas reciente a la mas vieja.</summary>
    public List<FaceAparienciaDto> Apariencias { get; set; } = new();

    /// <summary>
    /// Cuantas caben en total (face_config.max_apariencias). La app lo usa para
    /// saber cuando dejar de ofrecer "Agregar apariencia" en vez de dejar que
    /// la persona capture 26 tomas y recibir el rechazo hasta el final.
    /// </summary>
    public int MaxApariencias { get; set; }
}

/// <summary>
/// Parametros vivos del reconocimiento. Salen de la tabla face_config para
/// poder moverlos sin volver a desplegar el API: el umbral se ajusta con lo
/// que diga la bitacora, no con lo que se creyo el dia del despliegue.
/// </summary>
public class FaceConfig
{
    public float Umbral { get; set; } = 0.45f;
    public float MargenMinimo { get; set; } = 0.06f;
    public bool GaleriaPorPlanta { get; set; } = true;
    public int MaxIntentos { get; set; } = 2;
    public bool Activo { get; set; }

    /// <summary>
    /// Apariencias activas por persona. Cada una suma ~26 vectores a la
    /// galeria, y en 1:N mas vectores por persona es mas superficie para un
    /// falso positivo: por eso tiene tope y por eso vive en la tabla.
    /// </summary>
    public int MaxApariencias { get; set; } = 3;
}
