namespace FSH.Modules.Authorization.Contracts;

public enum ResourceGrantStatus
{
    None = 0,
    Pending = 1,
    Active = 2,
    RevocationPending = 3,
    Revoked = 4,
}
