using System;

namespace PMMovil.Api.Hubs;

public class PresenceState
{
    public string Usuario { get; set; } = string.Empty;

    public string Entry { get; set; } = string.Empty;

    public string Invoice { get; set; } = string.Empty;

    public string Item { get; set; } = string.Empty;

    public DateTime LastSeen { get; set; }
}
