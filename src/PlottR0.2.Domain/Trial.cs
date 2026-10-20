namespace PlottR0._2.Domain;

public class Trial : AuditableEntity
{
    public Guid Id { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Status { get; set; } = "Created";
    public string? SourceWorkbookPath { get; set; }

    public List<TrialAssignment> Assignments { get; set; } = [];

    public User? CreatedByUser { get; set; }
}
