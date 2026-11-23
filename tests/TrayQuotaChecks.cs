using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace NhipPC
{
internal static partial class Checks
{
    static void TrayQuotaEdges()
    {
        var reading = QuotaReaders.StatusLine("antigravity", Json.Parse(
            @"{""quota"":{""gemini-5h"":{""remaining_fraction"":0.89},""3p-5h"":{""remaining_fraction"":0.12}," +
            @"""gemini-weekly"":{""remaining_fraction"":0.42},""tokens-weekly"":{""remaining_amount"":1234.5}," +
            @"""tokens"":{""remaining_amount"":12.5}}}"));
        var source = new ProviderState { Phase = Phase.Live, Snapshot = reading };
        Func<List<PinChoice>> choices = () => DisplayData.Choices(new HardwareFrame(),
                id => id == "antigravity" ? source : null).Where(c => c.Parent == "antigravity").ToList();
        var live = choices();
        var prefs = new Preferences { TrayItems = live.Select(c => c.Key).ToList(), TrayStyle = "bars" };
        var bars = DisplayData.TrayIcons(prefs, live).Single(c => c.Bars != null);
        Check(bars.Number == "12/42" && bars.Bars.Select(c => DisplayData.Duration(c.Key))
                  .SequenceEqual(new long[] { 18000, 604800 }),
              "Dual bars retain a weekly period when multiple five-hour quotas are pinned");
        using (var shortIcon = TrayIndicators.MakeBars(bars.Bars.Take(1).ToList(), false, Color.Blue))
        using (var weekIcon = TrayIndicators.MakeBars(bars.Bars.Skip(1).ToList(), false, Color.Blue))
        using (var shortBar = shortIcon.ToBitmap())
        using (var weekBar = weekIcon.ToBitmap())
            Check(Enumerable.Range(0, shortBar.Height).Count(y => shortBar.GetPixel(1, y).A > 0) >
                  Enumerable.Range(0, weekBar.Height).Count(y => weekBar.GetPixel(1, y).A > 0),
                  "A single weekly bar stays thinner than a single five-hour bar");
        foreach (var style in new[] { "numbers", "bars", "ring" })
        {
            prefs.TrayStyle = style;
            var quantities = DisplayData.TrayIcons(prefs, live).Where(c => c.IsBalance).ToList();
            Check(quantities.Count == 2 && quantities.All(c => c.Bars == null && !c.Percent.HasValue) &&
                  quantities.Single(c => c.Key.EndsWith("/tokens-weekly")).Number == "1,2k" &&
                  quantities.Single(c => c.Key.EndsWith("/tokens")).Number == "12,5",
                  "Fixed quantities keep their numeric icons and units in " + style);
        }
        UiText.SetLanguage("en");
        try
        {
            var amount = choices().Single(c => c.Key.EndsWith("/tokens"));
            Check(amount.Value == "Remaining 12.5" && amount.Number == "12.5",
                  "English uses a decimal point for quantities from Vietnamese caches");
        }
        finally { UiText.SetLanguage("vi"); }
        source.Phase = Phase.Error;
        prefs.TrayItems = new List<string> { "ai/antigravity/3p-5h", "ai/antigravity/gemini-weekly" };
        foreach (var style in new[] { "bars", "ring" })
        {
            prefs.TrayStyle = style;
            var cached = DisplayData.TrayIcons(prefs, choices()).Single();
            Check(cached.Bars.All(c => !c.Percent.HasValue && c.Number == "—") &&
                  cached.Tip.Contains("lần cuối 12%") && cached.Tip.Contains("lần cuối 42%"),
                  "Expired cache values are explicitly historical in the " + style + " tooltip");
        }
    }
}
}
