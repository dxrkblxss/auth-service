namespace AuthService.Options;

public class RefreshTokenOptions
{
    public int DaysValid { get; set; } = 7;
    public int TokenLengthBytes { get; set; } = 64;
}