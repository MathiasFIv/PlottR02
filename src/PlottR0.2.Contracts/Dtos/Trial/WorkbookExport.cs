namespace PlottR0._2.Contracts.Dtos.Trial;

/// <summary>
/// Describes the workbook served for a trial export/download: the absolute path of
/// the file on disk and the download file name (<see cref="Path.GetFileName(string)"/>
/// of <see cref="FilePath"/>).
/// </summary>
public sealed record WorkbookExport(string FilePath, string FileName);