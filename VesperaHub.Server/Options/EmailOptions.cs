namespace VesperaHub.Server.Options;

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "Vespera Hub";
    public string PublicUrl { get; set; } = "https://localhost:5001";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(SmtpHost) &&
        !string.IsNullOrWhiteSpace(FromAddress);
}
