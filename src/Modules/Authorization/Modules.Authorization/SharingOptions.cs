namespace FSH.Modules.Authorization;

public sealed class SharingOptions
{
    public const string SectionName = "Authorization:Sharing";
    public int GrantRateLimitPermitLimit { get; set; } = 30;
    public int GrantRateLimitWindowSeconds { get; set; } = 60;
}

public static class SharingRateLimits
{
    public const string GrantMutations = "authorization-grant-mutations";
}
