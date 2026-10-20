namespace PlottR0._2.Contracts.Dtos.Trial;

public class VoiceCellEditRequest
{
    public int Plot { get; set; }

    public int Subsample { get; set; }

    public int TraitOrdinal { get; set; }

    public bool Overwrite { get; set; } = true;
}
