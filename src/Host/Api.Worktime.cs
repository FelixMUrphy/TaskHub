using System.Text.Json.Nodes;

namespace TaskHub;

/// <summary>/api/worktime/*: the work hours module, behind the API's one token like the rest. Turning it on and its settings aren't here:
/// money and the start date change only in Settings. The isolated hubs of the tests have a route for that.</summary>
sealed partial class Api
{
    static bool Preview(Dictionary<string, string> q) => q.TryGetValue("preview", out var v) && v == "1";

    void WorktimeRoutes()
    {
        const string DateId = @"(\d{4}-\d{2}-\d{2})", NumId = "([0-9]{1,15})";
        R("GET", "/api/worktime/status", (c, q, g) => V(Work.Status()));
        R("POST", "/api/worktime/start", async (c, q, g) => Work.Start(await Body(c)));
        R("POST", "/api/worktime/pause", async (c, q, g) => { await Body(c); return Work.Pause(); });
        R("POST", "/api/worktime/resume", async (c, q, g) => Work.Resume(Worktime.Source(await Body(c))));
        R("POST", "/api/worktime/finish", async (c, q, g) => { await Body(c); return Work.Finish(); });
        R("POST", "/api/worktime/sessions", async (c, q, g) => Work.AddSession(await Body(c), Preview(q)));
        R("POST", "/api/worktime/intervals", async (c, q, g) => Work.AddInterval(await Body(c), Preview(q)));
        R("GET", "/api/worktime/intervals", (c, q, g) => V(Work.ListIntervals(q)));
        R("PUT", $"/api/worktime/intervals/{NumId}", async (c, q, g) => Work.EditInterval(long.Parse(g[0]), await Body(c), Preview(q)));
        R("DELETE", $"/api/worktime/intervals/{NumId}", (c, q, g) => V(Work.DeleteInterval(long.Parse(g[0]), Preview(q))));
        R("GET", "/api/worktime/days", (c, q, g) => V(Work.ListDays(q)));
        R("PUT", "/api/worktime/days", async (c, q, g) => Work.SetDays(q, await Body(c), Preview(q)));
        R("PUT", "/api/worktime/days/hours", async (c, q, g) => Work.SetHours(await Body(c), Preview(q)));
        R("POST", $"/api/worktime/days/{DateId}/undo", async (c, q, g) => { await Body(c); return Work.Undo(g[0], Preview(q)); });
        R("GET", $"/api/worktime/days/{DateId}", (c, q, g) => V(Work.Day(g[0])));
        R("GET", "/api/worktime/breakdown", (c, q, g) => V(Work.Breakdown(q)));
        R("GET", $"/api/worktime/days/{DateId}/history", (c, q, g) => V(Work.History(g[0])));
        const string MonthId = @"(\d{4}-\d{2})";
        R("GET", $"/api/worktime/months/{MonthId}", (c, q, g) => V(Work.Month(g[0])));
        R("POST", $"/api/worktime/months/{MonthId}/close", async (c, q, g) => Work.CloseMonth(g[0], await Body(c), Preview(q)));
        R("POST", $"/api/worktime/months/{MonthId}/reopen", async (c, q, g) => { await Body(c); return Work.ReopenMonth(g[0], Preview(q)); });
        R("GET", "/api/worktime/holidays", async (c, q, g) => await Work.Holidays(q));
        R("POST", $"/api/worktime/holidays/{MonthId}/confirm", async (c, q, g) => Work.ConfirmHolidays(g[0], await Body(c), Preview(q)));
        if (Environment.GetEnvironmentVariable("TASKHUB_TEST_HOME") is { Length: > 0 })
        {
            R("POST", "/api/worktime/setup", async (c, q, g) => Work.Setup(await Body(c)));
            R("POST", "/api/test/worktime-page", async (c, q, g) =>
            {
                var b = await Body(c);
                try { Work.PageSet(b); } catch (ApiError e) { return new JsonObject { ["error"] = e.Ui ?? e.Message }; }
                return Work.PageView();
            });
            // one reminder check at the hub's clock + offset seconds, with the given idle time and lock state
            R("POST", "/api/test/worktime-remind", async (c, q, g) =>
            {
                var b = await Body(c);
                return Work.RemindTick(Worktime.Clock + (long)(J.Num(b["offset"]) ?? 0), J.Num(b["idle"]) ?? 0, J.Truthy(b["locked"]));
            });
        }
    }
}
