namespace TaskHub;

/// <summary>/api/worktime/*: the work hours module, behind the API's one token like the rest. Turning it on and its settings aren't here:
/// money and the start date change only in Settings. The isolated hubs of the tests have a route for that.</summary>
sealed partial class Api
{
    void WorktimeRoutes()
    {
        const string DateId = @"(\d{4}-\d{2}-\d{2})", NumId = "([0-9]{1,15})";
        R("GET", "/api/worktime/status", (c, q, g) => V(Work.Status()));
        R("POST", "/api/worktime/start", async (c, q, g) => { await Body(c); return Work.Start("api"); });
        R("POST", "/api/worktime/pause", async (c, q, g) => { await Body(c); return Work.Pause(); });
        R("POST", "/api/worktime/resume", async (c, q, g) => { await Body(c); return Work.Resume("api"); });
        R("POST", "/api/worktime/finish", async (c, q, g) => { await Body(c); return Work.Finish(); });
        R("POST", "/api/worktime/sessions", async (c, q, g) => Work.AddSession(await Body(c)));
        R("POST", "/api/worktime/intervals", async (c, q, g) => Work.AddInterval(await Body(c)));
        R("GET", "/api/worktime/intervals", (c, q, g) => V(Work.ListIntervals(q)));
        R("PUT", $"/api/worktime/intervals/{NumId}", async (c, q, g) => Work.EditInterval(long.Parse(g[0]), await Body(c)));
        R("DELETE", $"/api/worktime/intervals/{NumId}", (c, q, g) => V(Work.DeleteInterval(long.Parse(g[0]))));
        R("GET", "/api/worktime/days", (c, q, g) => V(Work.ListDays(q)));
        R("PUT", "/api/worktime/days", async (c, q, g) => Work.SetDays(q, await Body(c)));
        R("GET", $"/api/worktime/days/{DateId}/history", (c, q, g) => V(Work.History(g[0])));
        if (Environment.GetEnvironmentVariable("TASKHUB_TEST_HOME") is { Length: > 0 })
            R("POST", "/api/worktime/setup", async (c, q, g) => Work.Setup(await Body(c)));
    }
}
