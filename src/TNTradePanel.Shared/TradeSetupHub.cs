namespace TNTradePanel.Shared
{
    /// <summary>
    /// Plugin ↔ indicator bus. Quantower loads scripts' Shared DLL in a separate ALC,
    /// so command and setup travel via AppDomain.SetData strings, not static events.
    /// </summary>
    public static class TradeSetupHub
    {
        public const string DrawLinesCommand = "DrawLines";
        public const string DrawBeCommand = "DrawBe";
        public const string ClearLinesCommand = "ClearLines";
        public const string OrientCommand = "Orient";
        public const string SetupCommand = "Setup";

        private const string SeqKey = "TNTradePanel.Hub.Seq";
        private const string CmdKey = "TNTradePanel.Hub.Cmd";
        private const string PayloadKey = "TNTradePanel.Hub.Payload";
        private const string ListenersKey = "TNTradePanel.Hub.Listeners";

        public static readonly object Sync = new object();

        public static TradeSetup Current { get; } = new TradeSetup();

        public static int LineListeners
        {
            get => ToInt(AppDomain.CurrentDomain.GetData(ListenersKey));
            set => AppDomain.CurrentDomain.SetData(ListenersKey, value);
        }

        public static void AddListener()
        {
            lock (Sync)
                LineListeners = LineListeners + 1;
        }

        public static void RemoveListener()
        {
            lock (Sync)
                LineListeners = Math.Max(0, LineListeners - 1);
        }

        public static void RequestDrawLines() => Post(DrawLinesCommand);

        public static void RequestDrawBe() => Post(DrawBeCommand);

        public static void RequestClearLines() => Post(ClearLinesCommand);

        public static void RequestOrient() => Post(OrientCommand);

        public static void NotifySetupChanged() => Post(SetupCommand);

        public static bool TryPull(ref int lastSeq, out string command)
        {
            command = null;
            int seq;
            string payload;
            lock (Sync)
            {
                seq = ToInt(AppDomain.CurrentDomain.GetData(SeqKey));
                if (seq == lastSeq)
                    return false;
                command = AppDomain.CurrentDomain.GetData(CmdKey) as string;
                payload = AppDomain.CurrentDomain.GetData(PayloadKey) as string;
                lastSeq = seq;
            }

            TradeSetup parsed = Parse(payload);
            lock (Sync)
                Copy(parsed, Current);
            return true;
        }

        private static void Post(string command)
        {
            string payload;
            lock (Sync)
                payload = Format(Current);

            lock (Sync)
            {
                AppDomain.CurrentDomain.SetData(PayloadKey, payload);
                AppDomain.CurrentDomain.SetData(CmdKey, command ?? SetupCommand);
                AppDomain.CurrentDomain.SetData(SeqKey, ToInt(AppDomain.CurrentDomain.GetData(SeqKey)) + 1);
            }
        }

        private static void Copy(TradeSetup from, TradeSetup to)
        {
            if (from == null || to == null)
                return;
            to.LossLimitUsd = from.LossLimitUsd;
            to.Entry = from.Entry;
            to.StopLoss = from.StopLoss;
            to.TakeProfit = from.TakeProfit;
            to.BreakEven = from.BreakEven;
            to.ShowEntry = from.ShowEntry;
            to.ShowStop = from.ShowStop;
            to.ShowTakeProfit = from.ShowTakeProfit;
            to.ShowBreakEven = from.ShowBreakEven;
            to.UseEntryLine = from.UseEntryLine;
            to.EntryColorArgb = from.EntryColorArgb;
            to.StopColorArgb = from.StopColorArgb;
            to.TakeProfitColorArgb = from.TakeProfitColorArgb;
            to.BreakEvenColorArgb = from.BreakEvenColorArgb;
            to.LineWidth = from.LineWidth;
            to.LineStyle = from.LineStyle;
            to.StopLocked = from.StopLocked;
            to.LockedStopFloor = from.LockedStopFloor;
            to.LockedStopTicks = from.LockedStopTicks;
            to.DrawDirection = from.DrawDirection;
            to.Dragging = from.Dragging;
        }

        private static string Format(TradeSetup setup)
        {
            if (setup == null)
                return string.Empty;
            return string.Join("|",
                "L=" + setup.LossLimitUsd.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "E=" + FormatNum(setup.Entry),
                "S=" + FormatNum(setup.StopLoss),
                "T=" + FormatNum(setup.TakeProfit),
                "B=" + FormatNum(setup.BreakEven),
                "sE=" + (setup.ShowEntry ? "1" : "0"),
                "sS=" + (setup.ShowStop ? "1" : "0"),
                "sT=" + (setup.ShowTakeProfit ? "1" : "0"),
                "sB=" + (setup.ShowBreakEven ? "1" : "0"),
                "uE=" + (setup.UseEntryLine ? "1" : "0"),
                "cE=" + setup.EntryColorArgb.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "cS=" + setup.StopColorArgb.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "cT=" + setup.TakeProfitColorArgb.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "cB=" + setup.BreakEvenColorArgb.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "w=" + setup.LineWidth.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "ls=" + setup.LineStyle.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "lk=" + (setup.StopLocked ? "1" : "0"),
                "lf=" + FormatNum(setup.LockedStopFloor),
                "lt=" + setup.LockedStopTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "dd=" + setup.DrawDirection.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "dg=" + (setup.Dragging ? "1" : "0"));
        }

        private static TradeSetup Parse(string payload)
        {
            var setup = new TradeSetup();
            if (string.IsNullOrEmpty(payload))
                return setup;

            foreach (string part in payload.Split('|'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0)
                    continue;
                string key = part.Substring(0, eq);
                string value = part.Substring(eq + 1);
                switch (key)
                {
                    case "L":
                        if (double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double limit))
                            setup.LossLimitUsd = limit;
                        break;
                    case "E":
                        setup.Entry = ParseNum(value);
                        break;
                    case "S":
                        setup.StopLoss = ParseNum(value);
                        break;
                    case "T":
                        setup.TakeProfit = ParseNum(value);
                        break;
                    case "B":
                        setup.BreakEven = ParseNum(value);
                        break;
                    case "sE":
                        setup.ShowEntry = value == "1";
                        break;
                    case "sS":
                        setup.ShowStop = value == "1";
                        break;
                    case "sT":
                        setup.ShowTakeProfit = value == "1";
                        break;
                    case "sB":
                        setup.ShowBreakEven = value == "1";
                        break;
                    case "uE":
                        setup.UseEntryLine = value == "1";
                        break;
                    case "cE":
                        if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int ce))
                            setup.EntryColorArgb = ce;
                        break;
                    case "cS":
                        if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int cs))
                            setup.StopColorArgb = cs;
                        break;
                    case "cT":
                        if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int ct))
                            setup.TakeProfitColorArgb = ct;
                        break;
                    case "cB":
                        if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int cb))
                            setup.BreakEvenColorArgb = cb;
                        break;
                    case "w":
                        if (float.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float width))
                            setup.LineWidth = width;
                        break;
                    case "ls":
                        if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int style))
                            setup.LineStyle = style;
                        break;
                    case "lk":
                        setup.StopLocked = value == "1";
                        break;
                    case "lf":
                        setup.LockedStopFloor = ParseNum(value);
                        break;
                    case "lt":
                        if (double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double ticks))
                            setup.LockedStopTicks = ticks;
                        break;
                    case "dd":
                        if (int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int draw))
                            setup.DrawDirection = draw >= 0 ? 1 : -1;
                        break;
                    case "dg":
                        setup.Dragging = value == "1";
                        break;
                }
            }

            return setup;
        }

        private static string FormatNum(double? value)
        {
            return value.HasValue ? value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
        }

        private static double? ParseNum(string value)
        {
            if (string.IsNullOrEmpty(value))
                return null;
            if (double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double number))
                return number;
            return null;
        }

        private static int ToInt(object value)
        {
            if (value is int i)
                return i;
            if (value == null)
                return 0;
            int.TryParse(value.ToString(), out int parsed);
            return parsed;
        }
    }
}
