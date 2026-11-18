namespace LllmFacade.Domain;

public sealed class AdminUser
{
    public Guid Id { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public bool MustChangePassword { get; set; }
    public Guid SecurityStamp { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PasswordChangedAt { get; set; }
}
