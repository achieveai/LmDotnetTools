using System.Globalization;
using System.IO.Compression;
using System.Text;
using LmStreaming.Sample.FileBrowser;

namespace LmStreaming.Sample.Tests.FileBrowser;

/// <summary>
/// Covers <see cref="SpreadsheetPreviewReader"/>: the cell-to-string projection, the row/column/sheet caps
/// and their truncation flags, and the untrusted-input posture (a corrupt or hostile workbook becomes a
/// non-previewable reason, never an exception, and a formula's CACHED value is what surfaces — nothing is
/// evaluated, no external link is followed, no DTD is resolved).
/// </summary>
/// <remarks>
/// Every workbook here is built in-test by <c>XlsxBuilder</c>, which writes a minimal but real OOXML package
/// with <see cref="ZipArchive"/>. ExcelDataReader is a reader only — it cannot write — and a committed binary
/// fixture would be an opaque blob nobody can review, so the bytes are produced from XML that is readable in
/// the diff.
/// </remarks>
public class SpreadsheetPreviewReaderTests
{
    [Fact]
    public void Read_ASingleSheetWorkbook_ReturnsItsRowsAsStrings()
    {
        var bytes = XlsxBuilder.Build(
            (
                "Sheet1",
                """
                <row r="1"><c r="A1" t="inlineStr"><is><t>Name</t></is></c><c r="B1" t="inlineStr"><is><t>Qty</t></is></c></row>
                <row r="2"><c r="A2" t="inlineStr"><is><t>apple</t></is></c><c r="B2"><v>3</v></c></row>
                <row r="3"><c r="A3" t="inlineStr"><is><t>pear</t></is></c><c r="B3"><v>12</v></c></row>
                """
            )
        );

        var result = SpreadsheetPreviewReader.Read(bytes);

        result.Previewable.Should().BeTrue();
        result.Reason.Should().BeNull();
        result.Text.Should().BeNull("a spreadsheet preview carries no text body");
        result.LineCount.Should().BeNull();
        result.Table.Should().NotBeNull();
        var sheet = result.Table!.Sheets.Should().ContainSingle().Subject;
        sheet.Name.Should().Be("Sheet1");
        sheet.Truncated.Should().BeFalse();
        sheet.Rows.Should().HaveCount(3);
        sheet.Rows[0].Should().Equal("Name", "Qty");
        sheet.Rows[1].Should().Equal("apple", "3");
        sheet.Rows[2].Should().Equal("pear", "12");
    }

    [Fact]
    public void Read_AFormulaCell_SurfacesTheCachedValueAndNeverEvaluatesTheFormula()
    {
        // The cached <v> deliberately DISAGREES with what <f> would compute (2*3 = 6, cached 999).
        // An evaluating reader would return 6; asserting 999 is what makes "not evaluated" observable.
        var bytes = XlsxBuilder.Build(
            (
                "Calc",
                """
                <row r="1"><c r="A1"><v>2</v></c><c r="B1"><v>3</v></c><c r="C1"><f>A1*B1</f><v>999</v></c></row>
                """
            )
        );

        var result = SpreadsheetPreviewReader.Read(bytes);

        result.Previewable.Should().BeTrue();
        result.Table!.Sheets[0].Rows[0].Should().Equal("2", "3", "999");
    }

    [Fact]
    public void Read_MultipleSheets_ReturnsThemInWorkbookOrder()
    {
        var bytes = XlsxBuilder.Build(
            ("First", """<row r="1"><c r="A1" t="inlineStr"><is><t>one</t></is></c></row>"""),
            ("Second", """<row r="1"><c r="A1" t="inlineStr"><is><t>two</t></is></c></row>"""),
            ("Third", """<row r="1"><c r="A1" t="inlineStr"><is><t>three</t></is></c></row>""")
        );

        var result = SpreadsheetPreviewReader.Read(bytes);

        result.Table!.Sheets.Select(s => s.Name).Should().Equal("First", "Second", "Third");
        result.Table.Sheets.Select(s => s.Rows[0][0]).Should().Equal("one", "two", "three");
        result.Table.Truncated.Should().BeFalse();
    }

    [Fact]
    public void Read_MoreRowsThanTheLineCap_KeepsTheCapAndFlagsTheSheetTruncated()
    {
        var rows = string.Concat(
            Enumerable
                .Range(1, FileBrowserLimits.PreviewLineCap + 25)
                .Select(i =>
                    string.Create(CultureInfo.InvariantCulture, $"""<row r="{i}"><c r="A{i}"><v>{i}</v></c></row>""")
                )
        );
        var bytes = XlsxBuilder.Build(("Big", rows));

        var result = SpreadsheetPreviewReader.Read(bytes);

        var sheet = result.Table!.Sheets[0];
        sheet.Rows.Should().HaveCount(FileBrowserLimits.PreviewLineCap);
        sheet.Truncated.Should().BeTrue();
        sheet
            .Rows[FileBrowserLimits.PreviewLineCap - 1][0]
            .Should()
            .Be(FileBrowserLimits.PreviewLineCap.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Read_MoreColumnsThanTheColumnCap_KeepsTheCapAndFlagsTheSheetTruncated()
    {
        var cells = string.Concat(
            Enumerable
                .Range(1, FileBrowserLimits.PreviewColumnCap + 10)
                .Select(i =>
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"""<c r="{XlsxBuilder.ColumnRef(i)}1"><v>{i}</v></c>"""
                    )
                )
        );
        var bytes = XlsxBuilder.Build(("Wide", $"""<row r="1">{cells}</row>"""));

        var result = SpreadsheetPreviewReader.Read(bytes);

        var sheet = result.Table!.Sheets[0];
        sheet.Rows[0].Should().HaveCount(FileBrowserLimits.PreviewColumnCap);
        sheet.Truncated.Should().BeTrue();
    }

    [Fact]
    public void Read_MoreSheetsThanTheSheetCap_KeepsTheCapAndFlagsTheTableTruncated()
    {
        var sheets = Enumerable
            .Range(1, FileBrowserLimits.PreviewSheetCap + 3)
            .Select(i =>
                ($"S{i.ToString(CultureInfo.InvariantCulture)}", """<row r="1"><c r="A1"><v>1</v></c></row>""")
            )
            .ToArray();
        var bytes = XlsxBuilder.Build(sheets);

        var result = SpreadsheetPreviewReader.Read(bytes);

        result.Table!.Sheets.Should().HaveCount(FileBrowserLimits.PreviewSheetCap);
        result.Table.Truncated.Should().BeTrue();
    }

    [Fact]
    public void Read_APackageThatExpandsPastTheExpandedByteCap_IsNotPreviewableRatherThanInflatingItAll()
    {
        // Deflate collapses a run of identical bytes by roughly 1000:1, so passing the COMPRESSED cap says
        // nothing about the work of opening the file. The padding part below is a perfectly valid zip entry
        // that inflates past the expanded budget while the package on disk stays a few dozen KiB, and the
        // workbook parts beside it are the same valid ones every other test here uses — so a refusal is
        // attributable to the expansion and to nothing else.
        var bytes = XlsxBuilder.BuildWithPadding(
            FileBrowserLimits.SpreadsheetExpandedByteCap + (16L * 1024 * 1024),
            ("Sheet1", """<row r="1"><c r="A1" t="inlineStr"><is><t>bait</t></is></c></row>""")
        );

        bytes
            .LongLength.Should()
            .BeLessThan(
                FileBrowserLimits.SpreadsheetPreviewByteCap,
                "the package has to be one the compressed cap already lets through, or this proves nothing"
            );

        var result = SpreadsheetPreviewReader.Read(bytes);

        result.Previewable.Should().BeFalse();
        result.Reason.Should().Be(SpreadsheetPreviewReader.ExpandedTooLargeReason);
        result.Table.Should().BeNull();
    }

    [Fact]
    public void Read_APaddedPackageThatStaysUnderTheExpandedByteCap_StillPreviews()
    {
        // The control for the case above. Without it, that refusal would be indistinguishable from
        // "BuildWithPadding cannot produce a readable package at all" — the expanded-byte check runs BEFORE
        // the parser, so a package this helper had corrupted would be refused with the very same reason and
        // prove nothing. Same helper, same padding entry, only the size differs.
        var bytes = XlsxBuilder.BuildWithPadding(
            1024 * 1024,
            ("Sheet1", """<row r="1"><c r="A1" t="inlineStr"><is><t>bait</t></is></c></row>""")
        );

        var result = SpreadsheetPreviewReader.Read(bytes);

        result.Previewable.Should().BeTrue();
        result.Table!.Sheets[0].Rows[0].Should().Equal("bait");
    }

    [Fact]
    public void Read_ACellLongerThanTheCellCharCap_KeepsTheCapAndFlagsTheSheetTruncated()
    {
        // One cell, sized so that neither the row nor the column cap can be what cuts it.
        var giant = new string('x', FileBrowserLimits.PreviewCellCharCap + 500);
        var bytes = XlsxBuilder.Build(
            ("Long", $"""<row r="1"><c r="A1" t="inlineStr"><is><t>{giant}</t></is></c></row>""")
        );

        var result = SpreadsheetPreviewReader.Read(bytes);

        result.Previewable.Should().BeTrue();
        var sheet = result.Table!.Sheets[0];
        sheet.Rows[0][0].Should().HaveLength(FileBrowserLimits.PreviewCellCharCap);
        sheet.Rows[0][0].Should().Be(giant[..FileBrowserLimits.PreviewCellCharCap]);
        sheet.Truncated.Should().BeTrue();
    }

    [Fact]
    public void Read_ManyModestCellsPastTheOutputCharCap_StopsAtTheCapAndFlagsTheTableTruncated()
    {
        // Every individual cell is well under the per-cell cap and the sheet is well under the row and
        // column caps, so the ONLY budget that can stop this is the aggregate one.
        const int cellChars = 1_000;
        const int cellsPerRow = 10;
        var rowCount = (int)(FileBrowserLimits.PreviewOutputCharCap / (cellChars * cellsPerRow)) + 50;
        rowCount.Should().BeLessThan(FileBrowserLimits.PreviewLineCap, "the row cap must not be what cuts this");

        var cell = new string('y', cellChars);
        var rows = string.Concat(
            Enumerable
                .Range(1, rowCount)
                .Select(r =>
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"""<row r="{r}">{RowCells(r, cellsPerRow, cell)}</row>"""
                    )
                )
        );
        var bytes = XlsxBuilder.Build(("Bulk", rows));

        var result = SpreadsheetPreviewReader.Read(bytes);

        result.Previewable.Should().BeTrue();
        var kept = result.Table!.Sheets.SelectMany(s => s.Rows).SelectMany(r => r).Sum(c => (long)c.Length);
        kept.Should().BeLessThanOrEqualTo(FileBrowserLimits.PreviewOutputCharCap);
        kept.Should()
            .BeGreaterThan(
                FileBrowserLimits.PreviewOutputCharCap - (cellChars * cellsPerRow),
                "the budget should be spent, not abandoned early"
            );
        result.Table.Truncated.Should().BeTrue();
        result.Table.Sheets[0].Truncated.Should().BeTrue();
        result.Table.Sheets[0].Rows.Should().HaveCountLessThan(rowCount);

        static string RowCells(int row, int count, string value) =>
            string.Concat(
                Enumerable
                    .Range(1, count)
                    .Select(c =>
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"""<c r="{XlsxBuilder.ColumnRef(c)}{row}" t="inlineStr"><is><t>{value}</t></is></c>"""
                        )
                    )
            );
    }

    [Fact]
    public void Read_ARowWithGapsAndTrailingBlanks_PadsTheGapsAndDropsTheTrailingBlanks()
    {
        // Row 2 has A2 and C2 present, B2 absent (a gap Excel simply omits) and D2 an explicit blank.
        var bytes = XlsxBuilder.Build(
            (
                "Sparse",
                """
                <row r="1"><c r="A1" t="inlineStr"><is><t>a</t></is></c><c r="B1" t="inlineStr"><is><t>b</t></is></c><c r="C1" t="inlineStr"><is><t>c</t></is></c><c r="D1" t="inlineStr"><is><t>d</t></is></c></row>
                <row r="2"><c r="A2" t="inlineStr"><is><t>x</t></is></c><c r="C2" t="inlineStr"><is><t>z</t></is></c><c r="D2" t="inlineStr"><is><t></t></is></c></row>
                """
            )
        );

        var result = SpreadsheetPreviewReader.Read(bytes);

        result.Table!.Sheets[0].Rows[1].Should().Equal("x", "", "z");
    }

    [Fact]
    public void Read_BytesThatAreNotAWorkbook_IsNotPreviewableRatherThanThrowing()
    {
        var result = SpreadsheetPreviewReader.Read(Encoding.UTF8.GetBytes("this is plainly not a workbook"));

        result.Previewable.Should().BeFalse();
        result.Reason.Should().Be(SpreadsheetPreviewReader.CorruptReason);
        result.Table.Should().BeNull();
    }

    [Fact]
    public void Read_AZipWhoseWorkbookPartsAreMissing_IsNotPreviewableRatherThanThrowing()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = new StreamWriter(zip.CreateEntry("readme.txt").Open(), Encoding.UTF8);
            entry.Write("a zip, but not an OOXML package");
        }

        var result = SpreadsheetPreviewReader.Read(buffer.ToArray());

        result.Previewable.Should().BeFalse();
        result.Reason.Should().Be(SpreadsheetPreviewReader.CorruptReason);
    }

    [Fact]
    public void Read_ATruncatedWorkbook_IsNotPreviewableRatherThanThrowing()
    {
        var whole = XlsxBuilder.Build(("Sheet1", """<row r="1"><c r="A1"><v>1</v></c></row>"""));

        byte[] half = [.. whole.Take(whole.Length / 2)];

        var result = SpreadsheetPreviewReader.Read(half);

        result.Previewable.Should().BeFalse();
        result.Reason.Should().Be(SpreadsheetPreviewReader.CorruptReason);
    }

    [Fact]
    public void Read_ASheetWithoutADoctype_Parses()
    {
        // The control for the XXE case below. Without it, a refusal there would be indistinguishable from
        // "BuildRawSheet cannot produce a readable package at all", and that test would prove nothing.
        var sheetXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
            <row r="1"><c r="A1" t="inlineStr"><is><t>plain</t></is></c></row>
            </sheetData></worksheet>
            """;

        var result = SpreadsheetPreviewReader.Read(XlsxBuilder.BuildRawSheet("Sheet1", sheetXml));

        result.Previewable.Should().BeTrue();
        result.Table!.Sheets[0].Rows[0].Should().Equal("plain");
    }

    [Fact]
    public void Read_ASheetDeclaringAnExternalEntity_IsRefusedAndNeverResolvesIt()
    {
        // A classic XXE. The ONLY difference from the control above is the DOCTYPE line, so the outcome
        // here is attributable to the DTD and nothing else. Observed: the parser refuses the DTD outright,
        // which surfaces as the ordinary non-previewable reason. The probe-value guard is kept alongside
        // because "refused" and "read but not expanded" are both acceptable — silently expanding is not.
        var secret = Path.Combine(Path.GetTempPath(), $"xxe-probe-{Guid.NewGuid():N}.txt");
        File.WriteAllText(secret, "TOP-SECRET-PROBE-VALUE");
        try
        {
            var uri = "file:///" + secret.Replace('\\', '/');
            var sheetXml = $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE worksheet [ <!ENTITY xxe SYSTEM "{uri}"> ]>
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>&xxe;</t></is></c></row>
                </sheetData></worksheet>
                """;

            var result = SpreadsheetPreviewReader.Read(XlsxBuilder.BuildRawSheet("Sheet1", sheetXml));

            result.Previewable.Should().BeFalse();
            result.Reason.Should().Be(SpreadsheetPreviewReader.CorruptReason);
            var rendered = result.Table is null
                ? string.Empty
                : string.Join(" | ", result.Table.Sheets.SelectMany(s => s.Rows).SelectMany(r => r));
            rendered.Should().NotContain("TOP-SECRET-PROBE-VALUE");
        }
        finally
        {
            File.Delete(secret);
        }
    }

    /// <summary>
    /// Writes a minimal, valid OOXML spreadsheet package (inline strings, no styles, no shared strings).
    /// Internal rather than private because <c>FileBrowserControllerTests</c> needs real workbook bytes to
    /// drive the preview endpoint, and a second copy of this would be a second thing to keep correct.
    /// </summary>
    internal static class XlsxBuilder
    {
        public static byte[] Build(params (string Name, string RowsXml)[] sheets)
        {
            var parts = sheets
                .Select(s =>
                    (
                        s.Name,
                        Xml: $"""
                        <?xml version="1.0" encoding="UTF-8"?>
                        <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>{s.RowsXml}</sheetData></worksheet>
                        """
                    )
                )
                .ToArray();
            return Package(parts);
        }

        public static byte[] BuildRawSheet(string name, string sheetXml) => Package([(name, sheetXml)]);

        /// <summary>
        /// The same package as <see cref="Build"/> plus one extra part of <paramref name="paddingBytes"/>
        /// zero bytes. Zeros are the point: deflate stores them in almost nothing, so the package stays tiny
        /// on disk while an unbounded reader would inflate all of them. The part is not named in
        /// <c>[Content_Types].xml</c> or any relationship, so the workbook itself is unchanged.
        /// </summary>
        public static byte[] BuildWithPadding(long paddingBytes, params (string Name, string RowsXml)[] sheets)
        {
            var package = Build(sheets);

            using var buffer = new MemoryStream();
            buffer.Write(package, 0, package.Length);
            buffer.Position = 0;
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Update, leaveOpen: true))
            {
                using var padding = zip.CreateEntry("docProps/padding.bin", CompressionLevel.Optimal).Open();
                var chunk = new byte[64 * 1024];
                for (var written = 0L; written < paddingBytes; written += chunk.Length)
                {
                    padding.Write(chunk, 0, (int)Math.Min(chunk.Length, paddingBytes - written));
                }
            }

            return buffer.ToArray();
        }

        /// <summary>The A1-style column letters for a 1-based column index (1 -&gt; A, 27 -&gt; AA).</summary>
        public static string ColumnRef(int index)
        {
            var letters = string.Empty;
            while (index > 0)
            {
                var remainder = (index - 1) % 26;
                letters = (char)('A' + remainder) + letters;
                index = (index - 1) / 26;
            }

            return letters;
        }

        private static byte[] Package((string Name, string Xml)[] sheets)
        {
            var overrides = string.Concat(
                sheets.Select(
                    (_, i) =>
                        $"""<Override PartName="/xl/worksheets/sheet{i + 1}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>"""
                )
            );
            var sheetElements = string.Concat(
                sheets.Select((s, i) => $"""<sheet name="{s.Name}" sheetId="{i + 1}" r:id="rId{i + 1}"/>""")
            );
            var sheetRels = string.Concat(
                sheets.Select(
                    (_, i) =>
                        $"""<Relationship Id="rId{i + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{i + 1}.xml"/>"""
                )
            );

            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                Write(
                    zip,
                    "[Content_Types].xml",
                    $"""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                    <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                    <Default Extension="xml" ContentType="application/xml"/>
                    <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                    {overrides}</Types>
                    """
                );
                Write(
                    zip,
                    "_rels/.rels",
                    """
                    <?xml version="1.0" encoding="UTF-8"?>
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                    <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                    </Relationships>
                    """
                );
                Write(
                    zip,
                    "xl/workbook.xml",
                    $"""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                    <sheets>{sheetElements}</sheets></workbook>
                    """
                );
                Write(
                    zip,
                    "xl/_rels/workbook.xml.rels",
                    $"""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                    {sheetRels}</Relationships>
                    """
                );
                for (var i = 0; i < sheets.Length; i++)
                {
                    Write(zip, $"xl/worksheets/sheet{i + 1}.xml", sheets[i].Xml);
                }
            }

            return buffer.ToArray();
        }

        private static void Write(ZipArchive zip, string path, string content)
        {
            using var stream = zip.CreateEntry(path).Open();
            var bytes = Encoding.UTF8.GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
        }
    }
}
