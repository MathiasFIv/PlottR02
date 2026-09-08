namespace PlottR0._2.Domain;

public class Subsample : AuditableEntity
{
    public Guid Id { get; set; }
    public int SubsampleIndex { get; set; }
    public List<Rating> Ratings { get; set; } = [];
}
