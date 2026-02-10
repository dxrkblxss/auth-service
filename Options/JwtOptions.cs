namespace AuthService.Options;

public class JwtOptions
{
    public string Key { get; set; } = string.Empty;
    public string Issuer { get; set; } = "AuthService";
    public string Audience { get; set; } = "AuthClient";
    public int AccessTokenLifetimeMinutes { get; set; } = 15;
}
