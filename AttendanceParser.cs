using System.Text.Json;
using HtmlAgilityPack;

namespace WorkClock;

public record AttendanceEntry(string Date, string Time, string Mode, string Location);

public record AttendanceSummary(
    string EmployeeName,
    string Status,
    string FirstIn,
    string LastOut,
    string TotalTimeInside,
    List<AttendanceEntry> Entries);

/// <summary>
/// The attendance portal's location-lookup endpoint returns JSON where the
/// useful data is actually embedded as an HTML string inside a field. This
/// pulls the real values out of that HTML so you get clean data to work with.
/// </summary>
public static class AttendanceParser
{
    public static AttendanceSummary Parse(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var result = doc.RootElement.GetProperty("SearchResult");

        var locationHtml = result.GetProperty("EmployeeLocationContent").GetString() ?? "";
        var entryHtml = result.GetProperty("AttendanceListEntryContent").GetString() ?? "";

        var (name, status, firstIn, lastOut, totalTime) = ParseLocationHtml(locationHtml);
        var entries = ParseEntryHtml(entryHtml);

        return new AttendanceSummary(name, status, firstIn, lastOut, totalTime, entries);
    }

    private static (string Name, string Status, string FirstIn, string LastOut, string TotalTime)
        ParseLocationHtml(string html)
    {
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(html);

        var nameCell = doc.DocumentNode.SelectSingleNode("//td[@class='QuickListEmpName']");
        var nameAndStatus = nameCell?.InnerText.Trim() ?? "";

        // Format is usually "Name ~ Status (Details)"
        var parts = nameAndStatus.Split('~', 2);
        var name = parts[0].Trim();
        var status = parts.Length > 1 ? parts[1].Trim() : "";

        string GetValue(string className) =>
            doc.DocumentNode.SelectSingleNode($"//td[contains(@class,'{className}')]")
                ?.InnerText.Trim() ?? "";

        return (
            name,
            status,
            GetValue("TD_FirstIn_Value"),
            GetValue("TD_LastOut_Value"),
            GetValue("TD_TotalTimeInSide_Value")
        );
    }

    private static List<AttendanceEntry> ParseEntryHtml(string html)
    {
        var entries = new List<AttendanceEntry>();
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(html);

        // Each attendance row is a <tr> containing Date/Time/Mode/Location cells.
        var rows = doc.DocumentNode.SelectNodes("//tr[td[contains(@class,'TD_Date_Text')]]");
        if (rows is null) return entries;

        foreach (var row in rows)
        {
            string GetValue(string className) =>
                row.SelectSingleNode($".//td[contains(@class,'{className}')]")
                    ?.InnerText.Trim() ?? "";

            entries.Add(new AttendanceEntry(
                GetValue("TD_Date_Value"),
                GetValue("TD_Time_Value"),
                GetValue("TD_Mode_Value"),
                GetValue("TD_Location_Value")
            ));
        }

        return entries;
    }
}
