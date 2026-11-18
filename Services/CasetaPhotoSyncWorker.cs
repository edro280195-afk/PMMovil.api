namespace PMMovil.Api.Services;

/// <summary>
/// Pasa al 23 las fotos de caseta que llegaron (en cuanto llegan, no cada tantos segundos) y limpia el almacén caliente
/// de lo que ya cumplió su tiempo. Ver <see cref="CasetaPhotoStorage"/> y <see cref="AlmacenFotosWorker"/>.
/// </summary>
public sealed class CasetaPhotoSyncWorker : AlmacenFotosWorker
{
    public CasetaPhotoSyncWorker(CasetaPhotoStorage storage, IConfiguration config, ILogger<CasetaPhotoSyncWorker> logger)
        : base(storage, config, "CasetaPhotoSettings", "Caseta", logger)
    {
    }
}
