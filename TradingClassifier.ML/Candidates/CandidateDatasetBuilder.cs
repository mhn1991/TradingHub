using System.Globalization;
using System.Text.Json;
using TradingClassifier.Dataset;
using TradingClassifier.Evaluation;
using TradingClassifier.Features;
using TradingClassifier.Models;

namespace TradingClassifier.ML.Candidates;

/// <summary>
/// V2 P1: turns a primary strategy's realised trades into one causal row per candidate.
/// <para>
/// One row per candidate event, not one per candle (§8.1). Features are snapshotted at the decision
/// timestamp by joining the classifier's own causal feature engine, because the run journal carries
/// almost no candidate context for this source (see PROJECT_STATE §3.18).
/// </para>
/// </summary>
public static class CandidateDatasetBuilder
{
    /// <summary>Reads candidates and their realised R straight from a run's simulation-result.json.</summary>
    public static IReadOnlyList<(TradingCandidate Candidate, CandidateOutcome Outcome)> LoadCandidates(
        string simulationResultPath,
        string? strategyId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(simulationResultPath);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(simulationResultPath));
        List<(TradingCandidate, CandidateOutcome)> result = [];
        int number = 0;

        foreach (JsonElement trades in FindTradeArrays(document.RootElement))
        {
            foreach (JsonElement trade in trades.EnumerateArray())
            {
                if (strategyId is not null &&
                    trade.TryGetProperty("strategyId", out JsonElement id) &&
                    !string.Equals(id.GetString(), strategyId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!trade.TryGetProperty("rMultiple", out JsonElement r) ||
                    r.ValueKind is JsonValueKind.Null ||
                    !trade.TryGetProperty("signalCreatedAt", out JsonElement signal) ||
                    !trade.TryGetProperty("signalPrice", out JsonElement signalPrice) ||
                    !trade.TryGetProperty("initialStopLossPrice", out JsonElement stop) ||
                    !trade.TryGetProperty("takeProfitPrice", out JsonElement target))
                {
                    continue;
                }

                TradingCandidate candidate = new(
                    ++number,
                    string.Equals(trade.GetProperty("side").GetString(), "Buy", StringComparison.OrdinalIgnoreCase)
                        ? TradeLabel.Buy
                        : TradeLabel.Sell,
                    signal.GetDateTimeOffset(),
                    trade.GetProperty("openedAt").GetDateTimeOffset(),
                    trade.GetProperty("closedAt").GetDateTimeOffset(),
                    signalPrice.GetDecimal(),
                    stop.GetDecimal(),
                    target.GetDecimal(),
                    trade.TryGetProperty("entrySession", out JsonElement session)
                        ? session.GetString() ?? "Unknown"
                        : "Unknown");

                // Excursions in units of the risk the candidate actually took.
                decimal riskDistance = Math.Abs(candidate.DecisionPrice - candidate.StopPrice);
                bool isBuy = candidate.Side == TradeLabel.Buy;
                decimal favourable = 0m, adverse = 0m;
                bool? favourableFirst = null;

                if (riskDistance > 0m &&
                    trade.TryGetProperty("entryPrice", out JsonElement entryElement) &&
                    trade.TryGetProperty("maximumFavourableExcursionPrice", out JsonElement mfeElement) &&
                    trade.TryGetProperty("maximumAdverseExcursionPrice", out JsonElement maeElement) &&
                    mfeElement.ValueKind == JsonValueKind.Number &&
                    maeElement.ValueKind == JsonValueKind.Number)
                {
                    decimal entryPrice = entryElement.GetDecimal();
                    decimal best = mfeElement.GetDecimal();
                    decimal worst = maeElement.GetDecimal();
                    favourable = ((isBuy ? best - entryPrice : entryPrice - best)) / riskDistance;
                    adverse = ((isBuy ? entryPrice - worst : worst - entryPrice)) / riskDistance;

                    if (trade.TryGetProperty("maximumFavourableExcursionAt", out JsonElement mfeAt) &&
                        trade.TryGetProperty("maximumAdverseExcursionAt", out JsonElement maeAt) &&
                        mfeAt.ValueKind == JsonValueKind.String &&
                        maeAt.ValueKind == JsonValueKind.String)
                    {
                        favourableFirst = mfeAt.GetDateTimeOffset() < maeAt.GetDateTimeOffset();
                    }
                }

                CandidateOutcome outcome = new(
                    r.GetDecimal(),
                    trade.TryGetProperty("netProfitLoss", out JsonElement net) ? net.GetDecimal() : 0m,
                    trade.TryGetProperty("exitReason", out JsonElement reason)
                        ? reason.GetString() ?? "Unknown"
                        : "Unknown",
                    favourable,
                    adverse,
                    // Filled at join time: the ATR at the decision bar comes from the causal
                    // feature row, which the loader does not have.
                    0m,
                    favourableFirst);

                result.Add((candidate, outcome));
            }
        }

        return [.. result.OrderBy(item => item.Item1.DecisionAt)];
    }

    private static IEnumerable<JsonElement> FindTradeArrays(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.NameEquals("trades") || property.NameEquals("completedTrades"))
                {
                    if (property.Value.ValueKind == JsonValueKind.Array &&
                        property.Value.GetArrayLength() > 0 &&
                        property.Value[0].TryGetProperty("rMultiple", out _))
                    {
                        yield return property.Value;
                        continue;
                    }
                }
                foreach (JsonElement found in FindTradeArrays(property.Value))
                    yield return found;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
                foreach (JsonElement found in FindTradeArrays(item))
                    yield return found;
        }
    }

    /// <summary>
    /// Joins each candidate to the latest causal feature row at or before its decision bar.
    /// <para>
    /// §6.1's predeclared groups: candidate geometry, then a small local causal state supplied by the
    /// classifier's own OHLC feature set. Geometry is expected to be constant for a fixed-ATR bracket;
    /// <see cref="CandidateDataset.ConstantFeatures"/> reports that rather than hiding it.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <c>includeGeometry</c> MUST be false when the target is stop placement: the geometry group
    /// contains <c>stop_distance_atr</c>, so training a stop model on it means training on the
    /// answer. Excluding it also makes the vector identical to what an agent can supply before it
    /// has chosen a stop.
    /// </remarks>
    public static CandidateDataset Build(
        IReadOnlyList<(TradingCandidate Candidate, CandidateOutcome Outcome)> candidates,
        ClassifierDataset local,
        TradingCostModel costs,
        bool includeGeometry = true)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(costs);

        string[] geometryNames =
        [
            "side_sign", "stop_distance_atr", "target_distance_atr", "planned_rr", "cost_r",
            "hour_sin", "hour_cos"
        ];
        List<string> names = includeGeometry
            ? [.. geometryNames, .. local.Schema.Names]
            : [.. local.Schema.Names];

        List<CandidateRow> rows = [];
        int unjoined = 0;
        int cursor = 0;

        foreach ((TradingCandidate candidate, CandidateOutcome outcome) in candidates)
        {
            // Rows are time-ordered, so walk a cursor instead of rescanning 1.2M rows per candidate.
            while (cursor + 1 < local.Rows.Count &&
                   local.Rows[cursor + 1].Features.Timestamp <= candidate.DecisionAt)
            {
                cursor++;
            }

            LabeledFeatureRow row = local.Rows[cursor];
            if (row.Features.Timestamp > candidate.DecisionAt || row.Features.LabelAtr <= 0m)
            {
                unjoined++;
                continue;
            }

            decimal atr = row.Features.LabelAtr;
            decimal risk = Math.Abs(candidate.DecisionPrice - candidate.StopPrice);
            if (risk <= 0m)
            {
                unjoined++;
                continue;
            }

            decimal reward = Math.Abs(candidate.TargetPrice - candidate.DecisionPrice);
            decimal costPerTrade = (costs.HalfSpread + costs.SlippagePerSide) * 2m + costs.CommissionPerTrade;
            double hour = candidate.DecisionAt.UtcDateTime.TimeOfDay.TotalHours;

            float[] features = includeGeometry
                ?
                [
                    candidate.Side == TradeLabel.Buy ? 1f : -1f,
                    (float)(risk / atr),
                    (float)(reward / atr),
                    (float)(reward / risk),
                    (float)(costPerTrade / risk),
                    (float)Math.Sin(2 * Math.PI * hour / 24.0),
                    (float)Math.Cos(2 * Math.PI * hour / 24.0),
                    .. row.Features.Values
                ]
                : [.. row.Features.Values];

            // Re-express adverse excursion in ATR now that the decision-bar ATR is known.
            decimal adverseAtr = 0m;
            if (atr > 0m)
                adverseAtr = outcome.MaximumAdverseR * risk / atr;

            rows.Add(new CandidateRow
            {
                Candidate = candidate,
                Outcome = outcome with { MaximumAdverseAtr = adverseAtr },
                Features = features
            });
        }

        return new CandidateDataset
        {
            Rows = rows,
            FeatureNames = names,
            Unjoined = unjoined
        };
    }
}
