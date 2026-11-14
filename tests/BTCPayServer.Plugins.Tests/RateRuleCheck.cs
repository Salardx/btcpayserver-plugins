using BTCPayServer.Rating;

namespace BTCPayServer.Plugins.Tests;

/// <summary>Runs a plugin's default rate rules through BTCPay Server's own rule engine, with the rates a test supplies.</summary>
internal static class RateRuleCheck
{
    /// <summary>
    /// The rate of <paramref name="pair"/> under the given rules, with every other pair read from Kraken as a store's
    /// catch-all rule would. Fails when the rules do not parse or need a rate the test did not supply.
    /// </summary>
    public static decimal Evaluate(IEnumerable<string> rules, string pair, params (string Exchange, string Pair, decimal Rate)[] rates)
    {
        var script = string.Join(";\n", rules.Append("X_X = kraken(X_X)")) + ";";
        Assert.True(RateRules.TryParse(script, out var parsed), script);
        var rule = parsed.GetRuleFor(CurrencyPair.Parse(pair));
        foreach (var (exchange, quoted, rate) in rates)
            rule.ExchangeRates.SetRate(exchange, CurrencyPair.Parse(quoted), new BidAsk(rate));
        Assert.True(rule.Reevaluate(), $"{pair}: {string.Join(", ", rule.Errors)} in {rule}");
        return rule.BidAsk!.Bid;
    }
}
