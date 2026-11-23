namespace FSH.Modules.Authorization;

public sealed class SpiceDbOptions
{
    public const string SectionName = "Authorization:SpiceDB";

    public string Endpoint { get; set; } = "http://localhost:50051";
    public string PresharedKey { get; set; } = string.Empty;
    public bool UseTls { get; set; }
    public int MaxRetries { get; set; } = 3;
    public int TimeoutSeconds { get; set; } = 10;
}
