namespace PlottR0._2.Domain;

public class AuditLog
{
    public Guid Id { get; set; }

    public DateTime Timestamp { get; set; }

    public string Action { get; set; } = string.Empty;

    public string EntityType { get; set; } = string.Empty;

    public string? EntityId { get; set; }

    public Guid? ActorUserId { get; set; }

    public string? ActorName { get; set; }

    public string? Details { get; set; }
}
