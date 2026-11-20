namespace VesperaHub.Server.Options;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "VesperaHub";
    public string Audience { get; set; } = "VesperaHub";
    public string Key { get; set; } = "";
    public int LifetimeHours { get; set; } = 8;
}
