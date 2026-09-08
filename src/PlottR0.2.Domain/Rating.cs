namespace PlottR0._2.Domain;

public class Rating : AuditableEntity
{
    public Guid Id { get; set; }
    public int Value { get; set; }
    public Guid TraitId { get; set; }
}
