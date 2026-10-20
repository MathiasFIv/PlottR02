using PlottR0._2.Contracts.Ratings;

namespace PlottR0._2.Contracts.Repositories;

public interface IRatingRepository
{
    /// <summary>
    /// Deliberately synchronous and without a CancellationToken: ClosedXML parses the whole file
    /// in memory and cannot be aborted part-way.
    /// </summary>
    SheetRegion ReadSheetRegion(string workbookPath);

    PolicyWritePlotColumnResult WritePlotColumnPreserving(
        string workbookPath,
        string outputDirectory,
        int plot,
        int traitOrdinal,
        IReadOnlyList<int> values,
        IReadOnlySet<int> subsampleIndices,
        bool scanForFreeCells);

    SingleCellWriteResult WriteSingleCell(
        string workbookPath,
        string outputDirectory,
        int plot,
        int subsample,
        int traitOrdinal,
        int value,
        bool allowOverwrite);
}
