using System.Text;
using System.Text.Json;

namespace WorkClock;

/// <summary>
/// Thin wrapper around the attendance portal's location-lookup endpoint,
/// discovered in the browser's Network tab.
/// </summary>
public class AttendanceClient
{
    private readonly HttpClient _client;

    public AttendanceClient(HttpClient client)
    {
        _client = client;
    }

    /// <param name="employeeId">e.g. "210"</param>
    /// <param name="date">format dd/MM/yyyy, e.g. "25/08/2026"</param>
    /// <returns>
    /// (success, rawJson). success is false if the session looks expired
    /// (e.g. we got redirected to the login page instead of JSON).
    /// </returns>
    public async Task<(bool Success, string RawJson)> GetAttendanceAsync(
        string employeeId, string date, string userType = "Employee")
    {
        var payload = new
        {
            employeeID = employeeId,
            employeeName = "",
            selectedDate = date,
            userType
        };

        var content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/attendance/QuickFindLocation", content);
        var body = await response.Content.ReadAsStringAsync();

        // If the session expired, the portal/Azure AD will usually return an
        // HTML login page instead of JSON. Detect that here.
        var looksLikeLogin = !response.IsSuccessStatusCode
            || body.TrimStart().StartsWith("<");

        return (!looksLikeLogin, body);
    }
}
