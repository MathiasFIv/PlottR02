namespace PlottR0._2.Domain;

public record Trait
{
    public int Ordinal { get; init; }
    public PestCode PestCode { get; init; }
    public PartRated PartRated { get; init; }
}
