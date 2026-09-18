using TradingPlatform.BusinessLayer;

namespace TNTradePanel.Shared
{
    public sealed class PositionSizeResult
    {
        public bool Ok;
        public string Error;
        public double Quantity;
        public int SlTicks;
        public double TickCost;
        public double RiskPerContract;
        public double RewardRisk;
    }

    public static class PositionSizing
    {
        public static PositionSizeResult Calculate(Symbol symbol, TradeSetup setup)
        {
            var result = new PositionSizeResult();
            if (symbol == null)
            {
                result.Error = "No linked symbol.";
                return result;
            }

            if (setup == null || !setup.StopLoss.HasValue)
            {
                result.Error = "SL line required.";
                return result;
            }

            if (setup.UseEntryLine && setup.ShowEntry && !setup.HasEntryAndStop)
            {
                result.Error = "Entry and SL required.";
                return result;
            }

            if (setup.LossLimitUsd <= 0)
            {
                result.Error = "Loss limit must be positive.";
                return result;
            }

            double tickSize = symbol.TickSize;
            if (tickSize <= 0)
            {
                result.Error = "Invalid TickSize.";
                return result;
            }

            int direction = InferDirection(symbol, setup);
            if (direction == 0)
            {
                result.Error = "SL/TP direction unclear.";
                return result;
            }

            double entry = setup.HasEntryLine
                ? setup.Entry.Value
                : MarketEntry(symbol, direction);
            double stop = setup.StopLoss.Value;
            double distance = Math.Abs(entry - stop);
            int slTicks = (int)Math.Round(distance / tickSize);
            if (slTicks <= 0)
            {
                result.Error = "SL is too close to Entry.";
                return result;
            }

            double tickCost;
            try
            {
                tickCost = symbol.GetTickCost(entry);
            }
            catch (Exception ex)
            {
                result.Error = "TickCost error: " + ex.Message;
                return result;
            }

            if (tickCost <= 0 || double.IsNaN(tickCost) || double.IsInfinity(tickCost))
            {
                result.Error = "Invalid TickCost.";
                return result;
            }

            double riskPerContract = slTicks * tickCost;
            double lotStep = symbol.LotStep > 0 ? symbol.LotStep : 1.0;
            double raw = setup.LossLimitUsd / riskPerContract;
            double quantity = Math.Floor(raw / lotStep) * lotStep;

            if (symbol.MaxLot > 0 && quantity > symbol.MaxLot)
                quantity = Math.Floor(symbol.MaxLot / lotStep) * lotStep;

            if (symbol.MinLot > 0 && quantity > 0 && quantity < symbol.MinLot)
            {
                result.Error = "Limit yields 0 contracts (risk would exceed MinLot).";
                result.SlTicks = slTicks;
                result.TickCost = tickCost;
                result.RiskPerContract = riskPerContract;
                return result;
            }

            if (setup.TakeProfit.HasValue && setup.ShowTakeProfit)
                result.RewardRisk = Math.Abs(setup.TakeProfit.Value - entry) / distance;

            result.SlTicks = slTicks;
            result.TickCost = tickCost;
            result.RiskPerContract = riskPerContract;
            result.Quantity = quantity;
            if (quantity <= 0)
            {
                result.Error = "Quantity: 0. Raise the limit or tighten SL.";
                return result;
            }

            result.Ok = true;
            return result;
        }

        /// <summary>
        /// Signed P/L at the SL level (long: stop−entry, short: entry−stop), qty × tick cost.
        /// </summary>
        public static double? EstimateStopUsd(Symbol symbol, double entry, double stop, double quantity, int direction)
        {
            if (symbol == null || quantity <= 0 || direction == 0)
                return null;
            double tick = symbol.TickSize;
            if (tick <= 0)
                return null;
            double tickCost;
            try
            {
                tickCost = symbol.GetTickCost(entry);
            }
            catch
            {
                return null;
            }

            if (tickCost <= 0 || double.IsNaN(tickCost) || double.IsInfinity(tickCost))
                return null;

            double ticks = (stop - entry) / tick;
            if (direction < 0)
                ticks = -ticks;
            return ticks * tickCost * quantity;
        }

        public static int InferDirection(Symbol symbol, TradeSetup setup)
        {
            if (setup == null)
                return 0;
            if (setup.HasEntryLine && setup.HasEntryAndStop)
                return setup.Direction;
            if (setup.StopLoss.HasValue && setup.TakeProfit.HasValue && setup.StopLoss.Value != setup.TakeProfit.Value)
                return setup.StopLoss.Value < setup.TakeProfit.Value ? 1 : -1;

            double last = 0;
            try
            {
                if (symbol != null)
                {
                    if (symbol.Last > 0)
                        last = symbol.Last;
                    else if (symbol.Bid > 0)
                        last = symbol.Bid;
                }
            }
            catch
            {
            }

            if (setup.StopLoss.HasValue && last > 0)
                return setup.StopLoss.Value < last ? 1 : -1;
            return 0;
        }

        public static double MarketEntry(Symbol symbol, int direction)
        {
            if (symbol == null)
                return 0;
            try
            {
                if (direction > 0 && symbol.Ask > 0)
                    return symbol.Ask;
                if (direction < 0 && symbol.Bid > 0)
                    return symbol.Bid;
                if (symbol.Last > 0)
                    return symbol.Last;
            }
            catch
            {
            }

            return symbol.Last;
        }

        public static double RoundToTick(Symbol symbol, double price)
        {
            if (symbol == null)
                return price;

            double tickSize = symbol.TickSize;
            if (tickSize <= 0)
                return price;

            return Math.Round(price / tickSize) * tickSize;
        }
    }
}
