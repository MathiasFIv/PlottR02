namespace PlottR0._2.Domain;

public class Plot : AuditableEntity
{
    public Guid Id { get; set; }
    public int PlotNumber { get; set; }
    public int? TreatmentNumber { get; set; }
    public List<Subsample> Subsamples { get; set; } = [];
}
