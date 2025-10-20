namespace AuthService.Options;

public class HashingOptions
{
    public int MemorySize { get; set; } = 65536;
    public int Iterations { get; set; } = 3;
    public int DegreeOfParallelism { get; set; } = 4;
}
