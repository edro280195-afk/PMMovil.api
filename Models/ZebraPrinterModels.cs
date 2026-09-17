namespace PMMovil.Api.Models;

public class ZebraPrinterDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 9100;
    public string? Location { get; set; }
    public string? TerminalName { get; set; }
    public int ConnectTimeoutMillis { get; set; } = 4000;
    public int WriteTimeoutMillis { get; set; } = 8000;
    public decimal? PrintDarkness { get; set; }
    public decimal? PrintSpeedIps { get; set; }
    public string? PrintMethod { get; set; }
    public string? MediaType { get; set; }
    public int? LabelTop { get; set; }
    public int? LeftPosition { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
}

public class ZebraPrinterListResult
{
    public bool Success { get; set; } = true;
    public string Message { get; set; } = string.Empty;
    public List<ZebraPrinterDto> Items { get; set; } = new();
}

public class ZebraPrinterSaveRequest
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 9100;
    public string? Location { get; set; }
    public string? TerminalName { get; set; }
    public int ConnectTimeoutMillis { get; set; } = 4000;
    public int WriteTimeoutMillis { get; set; } = 8000;
    public decimal? PrintDarkness { get; set; }
    public decimal? PrintSpeedIps { get; set; }
    public string? PrintMethod { get; set; }
    public string? MediaType { get; set; }
    public int? LabelTop { get; set; }
    public int? LeftPosition { get; set; }
    public bool IsActive { get; set; } = true;
}

public class ZebraPrinterSaveResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public ZebraPrinterDto? Item { get; set; }
}
