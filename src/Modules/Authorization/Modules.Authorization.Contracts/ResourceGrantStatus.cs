namespace FSH.Modules.Authorization.Contracts;

public enum ResourceGrantStatus
{
    Pending = 1,
    Active = 2,
    RevocationPending = 3,
    Revoked = 4,
}
