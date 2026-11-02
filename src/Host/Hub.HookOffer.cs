using System.Text.Json.Nodes;

namespace TaskHub;

/// <summary>The card that offers an agent's hooks: an agent is used on this PC, its hooks are off and the user has not chosen yet.
/// This only reads; the agent's files are written after a click on the card, through SettingsBridge, never over HTTP.</summary>
sealed partial class Hub
{
    readonly object hookOfferGate = new();
    JsonArray hookOffer = new();   // under Lock
    bool hookOfferTg;              // under Lock: the panel says that these notifications are mirrored to Telegram

    /// <summary>Never from dev / test hubs, like Integrations.Ensure (tests ask for it).</summary>
    static bool HookOfferHere => Install.IsInstalledCopy || Install.IsPackaged || Environment.GetEnvironmentVariable("TASKHUB_TEST_HOOK_OFFER") == "1";

    /// <summary>Reads the agents' files: not under Lock. Called every minute and after a choice.</summary>
    public void RefreshHookOffer()
    {
        if (!HookOfferHere) return;
        try
        {
            lock (hookOfferGate)
            {
                var offer = Integrations.HookOffer(S);
                var rows = new JsonArray(offer.Select(a => (JsonNode)new JsonObject
                {
                    ["agent"] = Integrations.ArgName(a, Integrations.Kind.Hooks), ["name"] = Integrations.Name(a),
                }).ToArray());
                bool tg = (Notifier as Notifier)?.Telegram?.Linked == true;
                lock (Lock)
                    if (hookOfferTg != tg || hookOffer.ToJsonString() != rows.ToJsonString())
                    {
                        hookOffer = rows; hookOfferTg = tg;
                        Bump();
                    }
                TellHookOffer(offer);
            }
        }
        catch (Exception e) { Log.W("hooks offer: " + e.Message); }
    }

    /// <summary>The panel is usually hidden: one notification per agent, ever. Muted: it waits, the card is there anyway.</summary>
    void TellHookOffer(List<Integrations.Agent> offer)
    {
        if (Muted || Notifier == null) return;
        var told = (S.Get("hooks_offer_told") as JsonArray ?? new JsonArray()).Select(x => J.Str(x)).ToList();
        var fresh = offer.Where(a => !told.Contains(Integrations.ArgName(a, Integrations.Kind.Hooks))).ToList();
        if (fresh.Count == 0) return;
        Notifier.LocalToast(Settings.AppId, L.T("hooks_offer_toast", string.Join(", ", fresh.Select(Integrations.Name))));
        told.AddRange(fresh.Select(a => Integrations.ArgName(a, Integrations.Kind.Hooks)));
        S.Set(("hooks_offer_told", new JsonArray(told.Select(x => (JsonNode)x).ToArray())));
    }
}
