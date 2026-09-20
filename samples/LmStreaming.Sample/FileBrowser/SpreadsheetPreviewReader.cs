using System.Globalization;
using System.Text;
using ExcelDataReader;

namespace LmStreaming.Sample.FileBrowser;

/// <summary>
/// Turns the bytes of an OOXML workbook (<c>.xlsx</c>/<c>.xlsm</c>) into the same
/// <see cref="TablePreviewDto"/> shape the client already renders for a delimited text file, so one viewer
/// serves both sources.
/// </summary>
/// <remarks>
/// <para>
/// The workbook is UNTRUSTED input — a person or an agent put it in the workspace — so this deliberately
/// does the least that produces a readable table:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="ExcelReaderFactory.CreateOpenXmlReader(Stream, ExcelReaderConfiguration)"/> is called by name
/// rather than the sniffing <c>CreateReader</c>, so bytes that are not an OOXML package can never be routed
/// into the legacy BIFF (<c>.xls</c>) parser — a code path this preview does not want to expose at all.
/// </description></item>
/// <item><description>
/// The reader is forward-only over the worksheet parts. It reads the CACHED value of a formula cell and
/// never evaluates the formula, never opens <c>externalLink*</c> parts, and never touches drawings, images,
/// or embedded objects.
/// </description></item>
/// <item><description>
/// No password is configured, so an encrypted workbook simply fails to open and is reported as
/// <see cref="CorruptReason"/> rather than prompting for or guessing anything.
/// </description></item>
/// <item><description>
/// Output is bounded before it is built: <see cref="FileBrowserLimits.PreviewSheetCap"/> sheets,
/// <see cref="FileBrowserLimits.PreviewLineCap"/> rows per sheet, and
/// <see cref="FileBrowserLimits.PreviewColumnCap"/> columns per row, each dropped excess flagged.
/// </description></item>
/// </list>
/// </remarks>
public static class SpreadsheetPreviewReader
{
    /// <summary>The non-previewable reason returned for a workbook that cannot be opened or read.</summary>
    public const string CorruptReason = "corrupt_spreadsheet";

    static SpreadsheetPreviewReader()
    {
        // ExcelReaderConfiguration's constructor initialises FallbackEncoding to Encoding.GetEncoding(1252),
        // a codepage .NET Core does not carry, so WITHOUT this every CreateOpenXmlReader call throws
        // NotSupportedException before it looks at a single byte — including for a perfectly valid workbook,
        // which the catch below would then mislabel as corrupt. Registering the real provider (rather than a
        // hand-rolled one that aliases 1252 onto Latin-1) keeps the process honest for anything else that
        // asks for a legacy codepage. The call is idempotent, and this runs once on first use.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// Reads <paramref name="bytes"/> as a workbook and returns its capped per-sheet rows, or reports the
    /// file non-previewable with <see cref="CorruptReason"/>. This never throws for bad input: a malformed,
    /// encrypted, or hostile workbook is a preview outcome, not a server error.
    /// </summary>
    public static PreviewResultDto Read(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = ExcelReaderFactory.CreateOpenXmlReader(stream);
            return new PreviewResultDto(true, null, null, null, ReadWorkbook(reader));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Deliberately broad. The input is untrusted and the parser is third-party, so the set of
            // exception types a bad workbook can produce is not knowable from here — and the contract the
            // endpoint owes its caller is "a reason", never a 500. OutOfMemoryException stays fatal because
            // swallowing it would leave the process in a state this method cannot reason about.
            return new PreviewResultDto(false, CorruptReason, null, null, null);
        }
    }

    private static TablePreviewDto ReadWorkbook(IExcelDataReader reader)
    {
        var sheets = new List<SheetPreviewDto>();
        var moreSheets = false;

        do
        {
            if (sheets.Count == FileBrowserLimits.PreviewSheetCap)
            {
                moreSheets = true;
                break;
            }

            sheets.Add(ReadSheet(reader));
        } while (reader.NextResult());

        return new TablePreviewDto(sheets, moreSheets);
    }

    private static SheetPreviewDto ReadSheet(IExcelDataReader reader)
    {
        // FieldCount is the sheet's widest row, so an over-wide sheet is known before a single row is built.
        var columns = Math.Min(reader.FieldCount, FileBrowserLimits.PreviewColumnCap);
        var truncated = reader.FieldCount > FileBrowserLimits.PreviewColumnCap;
        var rows = new List<IReadOnlyList<string>>();

        while (reader.Read())
        {
            if (rows.Count == FileBrowserLimits.PreviewLineCap)
            {
                // One row past the cap exists, so the sheet really is cut short — say so rather than
                // letting a sheet of exactly the cap look complete.
                truncated = true;
                break;
            }

            rows.Add(ReadRow(reader, columns));
        }

        if (!truncated)
        {
            TrimTrailingEmptyRows(rows);
        }

        return new SheetPreviewDto(reader.Name ?? string.Empty, rows, truncated);
    }

    private static string[] ReadRow(IExcelDataReader reader, int columns)
    {
        var cells = new string[columns];
        var lastNonEmpty = -1;
        for (var i = 0; i < columns; i++)
        {
            cells[i] = Format(reader.GetValue(i));
            if (cells[i].Length > 0)
            {
                lastNonEmpty = i;
            }
        }

        // Trailing blanks are an artifact of the sheet's declared width, not content: a row of 3 values in a
        // 40-column sheet should render as 3 cells. Interior gaps stay, so column alignment survives.
        return lastNonEmpty == cells.Length - 1 ? cells : cells[..(lastNonEmpty + 1)];
    }

    private static void TrimTrailingEmptyRows(List<IReadOnlyList<string>> rows)
    {
        var last = rows.Count - 1;
        while (last >= 0 && rows[last].Count == 0)
        {
            last--;
        }

        if (last < rows.Count - 1)
        {
            rows.RemoveRange(last + 1, rows.Count - last - 1);
        }
    }

    /// <summary>Renders one cell as the string the viewer shows. Culture-invariant so the client can parse numbers back.</summary>
    private static string Format(object? value) =>
        value switch
        {
            null => string.Empty,
            string s => s,
            // A date-formatted cell arrives as DateTime; ISO-8601 keeps it sortable as text and unambiguous.
            DateTime d => d.TimeOfDay == TimeSpan.Zero
                ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : d.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
            TimeSpan t => t.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
            bool b => b ? "TRUE" : "FALSE",
            double n => n.ToString(CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
}
