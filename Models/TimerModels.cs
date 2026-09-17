namespace PMMovil.Api.Models;

public class FinalizarTimerRequest
{
    public string Creferencia { get; set; } = string.Empty;
    public int StatusFinal { get; set; } = 1;
    public bool EsFinal { get; set; } = true;
}
