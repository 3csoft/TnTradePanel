using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Chart;
using TradingPlatform.BusinessLayer.Native;
using TNTradePanel.Shared;

namespace TNTradePanel.Indicator
{
    public class TNTradePanelLines : global::TradingPlatform.BusinessLayer.Indicator
    {
        private const int HitPx = 7;
        private const double TpMultiple = 1.5;

        private enum DragTarget
        {
            None,
            Entry,
            Stop,
            TakeProfit,
            BreakEven
        }

        private DragTarget dragging;
        private bool mouseHooked;
        private int hubSeq;

        public TNTradePanelLines()
            : base()
        {
            this.Name = "TN Trade Panel Lines";
            this.Description = "Entry / SL / TP / BE lines for TN Trade Panel. Draggable levels.";
            this.SeparateWindow = false;
        }

        public override string ShortName => "TNTP Lines";

        protected override void OnInit()
        {
            TradeSetupHub.AddListener();
            this.HookMouse();
        }

        protected override void OnClear()
        {
            TradeSetupHub.RemoveListener();
            this.UnhookMouse();
            base.OnClear();
        }

        public override void Dispose()
        {
            this.UnhookMouse();
            base.Dispose();
        }

        private void HookMouse()
        {
            if (this.mouseHooked || this.CurrentChart == null)
                return;
            this.CurrentChart.MouseDown += this.OnMouseDown;
            this.CurrentChart.MouseUp += this.OnMouseUp;
            this.CurrentChart.MouseMove += this.OnMouseMove;
            this.mouseHooked = true;
        }

        private void UnhookMouse()
        {
            if (!this.mouseHooked || this.CurrentChart == null)
                return;
            this.CurrentChart.MouseDown -= this.OnMouseDown;
            this.CurrentChart.MouseUp -= this.OnMouseUp;
            this.CurrentChart.MouseMove -= this.OnMouseMove;
            this.mouseHooked = false;
        }

        private void ApplyDrawLines()
        {
            this.HookMouse();
            lock (TradeSetupHub.Sync)
            {
                TradeSetup setup = TradeSetupHub.Current;
                // BE is owned by the BE button; only Entry/TP count as setup lines here.
                bool extras = setup.ShowEntry || setup.ShowTakeProfit;
                bool hasPosition = this.FindOpenPosition() != null;

                if (extras)
                {
                    if (hasPosition)
                        setup.ClearUnlockedLines();
                    else
                        setup.ClearLines();
                    TradeSetupHub.NotifySetupChanged();
                    this.CurrentChart?.RedrawBuffer();
                    return;
                }

                this.PlaceDirectionLines(setup, keepExistingStop: hasPosition && setup.ShowStop);
            }

            TradeSetupHub.NotifySetupChanged();
            this.CurrentChart?.RedrawBuffer();
        }

        private void PlaceDirectionLines(TradeSetup setup, bool keepExistingStop)
        {
            double entry = this.GetMarketPrice();
            double atr = this.GetAtrOffset();
            bool useEntry = setup.UseEntryLine;
            int direction = setup.DrawDirection >= 0 ? 1 : -1;
            double sl = direction > 0 ? entry - atr : entry + atr;
            double tp = direction > 0 ? entry + atr * TpMultiple : entry - atr * TpMultiple;
            setup.Entry = useEntry ? PositionSizing.RoundToTick(this.Symbol, entry) : (double?)null;
            setup.TakeProfit = PositionSizing.RoundToTick(this.Symbol, tp);
            setup.ShowEntry = useEntry;
            setup.ShowTakeProfit = true;
            setup.ShowBreakEven = setup.ShowBreakEven && setup.BreakEven.HasValue;
            if (!keepExistingStop)
            {
                setup.StopLoss = PositionSizing.RoundToTick(this.Symbol, sl);
                setup.ShowStop = true;
                setup.StopLocked = false;
                setup.LockedStopFloor = null;
                setup.LockedStopTicks = 0;
            }
            else
            {
                setup.ShowStop = true;
            }
        }

        private void ApplyOrient()
        {
            this.HookMouse();
            lock (TradeSetupHub.Sync)
            {
                TradeSetup setup = TradeSetupHub.Current;
                if (!setup.ShowEntry && !setup.ShowTakeProfit && !setup.ShowStop)
                    return;
                this.PlaceDirectionLines(setup, keepExistingStop: this.FindOpenPosition() != null && setup.ShowStop);
            }
            TradeSetupHub.NotifySetupChanged();
            this.CurrentChart?.RedrawBuffer();
        }

        private void ApplyDrawBe()
        {
            this.HookMouse();
            lock (TradeSetupHub.Sync)
            {
                TradeSetup setup = TradeSetupHub.Current;
                if (setup.ShowBreakEven && setup.BreakEven.HasValue)
                {
                    setup.ShowBreakEven = false;
                    setup.BreakEven = null;
                    TradeSetupHub.NotifySetupChanged();
                    this.CurrentChart?.RedrawBuffer();
                    return;
                }

                Position position = this.FindOpenPosition();
                int direction = position != null
                    ? (position.Side == Side.Buy ? 1 : -1)
                    : (setup.DrawDirection >= 0 ? 1 : -1);
                double market = this.GetMarketPrice();
                if (market <= 0 && position != null)
                    market = position.OpenPrice;
                if (market <= 0)
                    return;
                double far = Math.Max(this.GetAtrOffset() * 1.5, (this.Symbol?.TickSize > 0 ? this.Symbol.TickSize : 0.25) * 40);
                double be = market + direction * far;
                setup.BreakEven = PositionSizing.RoundToTick(this.Symbol, be);
                setup.ShowBreakEven = true;
            }

            TradeSetupHub.NotifySetupChanged();
            this.CurrentChart?.RedrawBuffer();
        }

        private void ApplyClearLines()
        {
            lock (TradeSetupHub.Sync)
                TradeSetupHub.Current.ClearLines();
            TradeSetupHub.NotifySetupChanged();
            this.CurrentChart?.RedrawBuffer();
        }

        private void OnMouseDown(object sender, ChartMouseNativeEventArgs e)
        {
            if (this.CurrentChart?.MainWindow == null)
                return;
            if (!this.CurrentChart.MainWindow.ClientRectangle.Contains(e.Location))
                return;

            if (e.Button == NativeMouseButtons.Right)
            {
                if (this.HitTest(e.Location.X, e.Location.Y) == DragTarget.TakeProfit)
                {
                    e.Handled = true;
                    lock (TradeSetupHub.Sync)
                    {
                        TradeSetupHub.Current.ShowTakeProfit = false;
                        TradeSetupHub.Current.TakeProfit = null;
                    }
                    TradeSetupHub.NotifySetupChanged();
                    this.CurrentChart?.RedrawBuffer();
                }
                return;
            }

            if (e.Button != NativeMouseButtons.Left)
                return;

            this.dragging = this.HitTest(e.Location.X, e.Location.Y);
            if (this.dragging == DragTarget.None)
                return;

            e.Handled = true;
            lock (TradeSetupHub.Sync)
                TradeSetupHub.Current.Dragging = true;
            this.ApplyDrag(e.Location.Y);
        }

        private void OnMouseMove(object sender, ChartMouseNativeEventArgs e)
        {
            if (this.dragging == DragTarget.None)
                return;
            e.Handled = true;
            this.ApplyDrag(e.Location.Y);
        }

        private void OnMouseUp(object sender, ChartMouseNativeEventArgs e)
        {
            if (this.dragging == DragTarget.None)
                return;
            this.ApplyDrag(e.Location.Y);
            if (this.dragging == DragTarget.Stop)
                this.SnapStopIfWidened();
            this.dragging = DragTarget.None;
            lock (TradeSetupHub.Sync)
                TradeSetupHub.Current.Dragging = false;
            TradeSetupHub.NotifySetupChanged();
            this.CurrentChart?.RedrawBuffer();
        }

        private void ApplyDrag(int y)
        {
            var converter = this.CurrentChart?.MainWindow?.CoordinatesConverter;
            if (converter == null || this.Symbol == null)
                return;

            double price = PositionSizing.RoundToTick(this.Symbol, converter.GetPrice(y));
            lock (TradeSetupHub.Sync)
            {
                TradeSetup setup = TradeSetupHub.Current;
                switch (this.dragging)
                {
                    case DragTarget.Entry:
                        if (!setup.StopLocked)
                            setup.Entry = price;
                        break;
                    case DragTarget.Stop:
                        this.ApplyStopDrag(setup, price);
                        break;
                    case DragTarget.TakeProfit:
                        setup.TakeProfit = price;
                        break;
                    case DragTarget.BreakEven:
                        setup.BreakEven = price;
                        break;
                }
            }

            TradeSetupHub.NotifySetupChanged();
            this.CurrentChart?.RedrawBuffer();
        }

        private void ApplyStopDrag(TradeSetup setup, double price)
        {
            setup.StopLoss = price;
            setup.ShowStop = true;
        }

        private void SnapStopIfWidened()
        {
            Position position = this.FindOpenPosition();
            if (position == null)
                return;

            lock (TradeSetupHub.Sync)
            {
                TradeSetup setup = TradeSetupHub.Current;
                if (!setup.StopLocked || !setup.StopLoss.HasValue)
                    return;

                int direction = position.Side == Side.Buy ? 1 : -1;
                double? maxStop = PositionSizing.WidestLockedStop(
                    this.Symbol, position.OpenPrice, direction, setup.LockedStopTicks, setup.LockedStopFloor);
                if (!maxStop.HasValue)
                    return;
                bool widened = direction > 0
                    ? setup.StopLoss.Value < maxStop.Value
                    : setup.StopLoss.Value > maxStop.Value;
                if (widened)
                    setup.StopLoss = maxStop.Value;
            }
        }

        private double ResolveEntryPrice(TradeSetup setup)
        {
            Position position = this.FindOpenPosition();
            if (position != null && position.OpenPrice > 0)
                return position.OpenPrice;
            if (setup.Entry.HasValue)
                return setup.Entry.Value;
            return this.GetMarketPrice();
        }

        private int ResolveDirection(TradeSetup setup, double entry)
        {
            Position position = this.FindOpenPosition();
            if (position != null)
                return position.Side == Side.Buy ? 1 : -1;
            if (setup.StopLoss.HasValue && entry > 0)
                return setup.StopLoss.Value < entry ? 1 : -1;
            return PositionSizing.InferDirection(this.Symbol, setup);
        }

        private Position FindOpenPosition()
        {
            if (this.Symbol == null)
                return null;
            return Core.Instance.Positions.FirstOrDefault(p => p != null && SameSymbol(p.Symbol, this.Symbol));
        }

        /// <summary>
        /// Chart may be continuous (MNQ) while the position is a dated contract (MNQZ6.CME) — accept prefix match.
        /// </summary>
        private static bool SameSymbol(Symbol a, Symbol b)
        {
            if (a == null || b == null)
                return false;
            if (ReferenceEquals(a, b))
                return true;
            if (string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase))
                return true;

            string left = StripExchange(a.Name);
            string right = StripExchange(b.Name);
            return left.Length >= 2 && right.Length >= 2
                && (left.StartsWith(right, StringComparison.OrdinalIgnoreCase)
                    || right.StartsWith(left, StringComparison.OrdinalIgnoreCase));
        }

        private static string StripExchange(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;
            int dot = name.IndexOf('.');
            return dot > 0 ? name.Substring(0, dot) : name;
        }

        private DragTarget HitTest(int x, int y)
        {
            var converter = this.CurrentChart?.MainWindow?.CoordinatesConverter;
            if (converter == null)
                return DragTarget.None;

            lock (this.handleRects)
            {
                foreach (DragTarget target in HandleHitOrder)
                {
                    if (this.handleRects.TryGetValue(target, out RectangleF rect)
                        && RectangleF.Inflate(rect, 3, 3).Contains(x, y))
                        return target;
                }
            }

            TradeSetup setup;
            lock (TradeSetupHub.Sync)
                setup = TradeSetupHub.Current;

            if (setup.ShowBreakEven && setup.BreakEven.HasValue && Near(y, converter.GetChartY(setup.BreakEven.Value)))
                return DragTarget.BreakEven;
            if (setup.ShowTakeProfit && setup.TakeProfit.HasValue && Near(y, converter.GetChartY(setup.TakeProfit.Value)))
                return DragTarget.TakeProfit;
            if (setup.ShowEntry && setup.Entry.HasValue && Near(y, converter.GetChartY(setup.Entry.Value)))
                return DragTarget.Entry;
            if (setup.ShowStop && setup.StopLoss.HasValue && Near(y, converter.GetChartY(setup.StopLoss.Value)))
                return DragTarget.Stop;
            return DragTarget.None;
        }

        private const float HandleHeight = 20f;

        private static readonly DragTarget[] HandleHitOrder =
            { DragTarget.BreakEven, DragTarget.TakeProfit, DragTarget.Entry, DragTarget.Stop };

        private readonly Dictionary<DragTarget, RectangleF> handleRects = new Dictionary<DragTarget, RectangleF>();

        private static bool Near(int y, double lineY)
        {
            return Math.Abs(y - lineY) <= HitPx;
        }

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            this.PollHub();
            base.OnPaintChart(args);
            if (args?.Graphics == null || this.CurrentChart?.MainWindow == null)
                return;

            var converter = this.CurrentChart.MainWindow.CoordinatesConverter;
            if (converter == null)
                return;

            TradeSetup setup;
            lock (TradeSetupHub.Sync)
                setup = TradeSetupHub.Current;

            lock (this.handleRects)
                this.handleRects.Clear();

            if (!setup.LinesVisible)
                return;

            Rectangle clip = this.CurrentChart.MainWindow.ClientRectangle;
            Graphics gr = args.Graphics;
            GraphicsState state = gr.Save();
            try
            {
                gr.SetClip(clip);
                using (var font = new Font("Segoe UI", 8.5f, FontStyle.Bold))
                {
                    if (setup.ShowStop && setup.StopLoss.HasValue)
                        this.DrawLevel(gr, converter, clip, font, setup.StopLoss.Value, Color.FromArgb(setup.StopColorArgb), this.BuildStopLabel(setup), setup, DragTarget.Stop);
                    if (setup.ShowEntry && setup.Entry.HasValue)
                        this.DrawLevel(gr, converter, clip, font, setup.Entry.Value, Color.FromArgb(setup.EntryColorArgb), "Entry", setup, DragTarget.Entry);
                    if (setup.ShowTakeProfit && setup.TakeProfit.HasValue)
                        this.DrawLevel(gr, converter, clip, font, setup.TakeProfit.Value, Color.FromArgb(setup.TakeProfitColorArgb), "TP", setup, DragTarget.TakeProfit);
                    if (setup.ShowBreakEven && setup.BreakEven.HasValue)
                        this.DrawLevel(gr, converter, clip, font, setup.BreakEven.Value, Color.FromArgb(setup.BreakEvenColorArgb), "BE", setup, DragTarget.BreakEven);
                }
            }
            finally
            {
                gr.Restore(state);
            }
        }

        private string BuildStopLabel(TradeSetup setup)
        {
            string priceText = this.Symbol != null
                ? this.Symbol.FormatPrice(setup.StopLoss.Value)
                : setup.StopLoss.Value.ToString("0.####");

            double? usd = this.EstimateStopUsd(setup);
            if (!usd.HasValue)
                return "SL " + priceText;

            string sign = usd.Value >= 0 ? "+" : "−";
            string amount = Math.Abs(usd.Value).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            return "SL " + priceText + " (" + sign + amount + " USD)";
        }

        private double? EstimateStopUsd(TradeSetup setup)
        {
            if (this.Symbol == null || !setup.StopLoss.HasValue)
                return null;

            Position position = this.FindOpenPosition();
            double entry;
            double qty;
            int direction;

            if (position != null)
            {
                entry = position.OpenPrice;
                qty = position.Quantity;
                direction = position.Side == Side.Buy ? 1 : -1;
            }
            else
            {
                entry = setup.Entry ?? this.GetMarketPrice();
                direction = this.ResolveDirection(setup, entry);
                PositionSizeResult size = PositionSizing.Calculate(this.Symbol, setup);
                if (!size.Ok)
                    return null;
                qty = size.Quantity;
            }

            if (entry <= 0 || qty <= 0 || direction == 0)
                return null;

            return PositionSizing.EstimateStopUsd(this.Symbol, entry, setup.StopLoss.Value, qty, direction);
        }

        private void DrawLevel(Graphics gr, IChartWindowCoordinatesConverter converter, Rectangle clip, Font font, double price, Color color, string label, TradeSetup setup, DragTarget target)
        {
            float y = (float)converter.GetChartY(price);
            if (y < clip.Top - 20 || y > clip.Bottom + 20)
                return;

            float width = setup != null && setup.LineWidth > 0 ? setup.LineWidth : 1.5f;
            using (var pen = new Pen(color, width))
            {
                pen.DashStyle = ToDashStyle(setup?.LineStyle ?? 0);
                gr.DrawLine(pen, clip.Left, y, clip.Right, y);
            }

            string text = label.StartsWith("SL ", StringComparison.Ordinal)
                ? label
                : label + " " + (this.Symbol != null ? this.Symbol.FormatPrice(price) : price.ToString("0.####"));

            RectangleF tag = this.DrawHandle(gr, clip, font, y, color, text);
            lock (this.handleRects)
                this.handleRects[target] = tag;
        }

        /// <summary>Filled tag with a round grip at the left end of the line — the drag handle.</summary>
        private RectangleF DrawHandle(Graphics gr, Rectangle clip, Font font, float y, Color color, string text)
        {
            SizeF textSize = gr.MeasureString(text, font);
            float h = Math.Max(HandleHeight, textSize.Height + 4);
            float grip = h;
            var tag = new RectangleF(clip.Left + 4, y - h / 2, grip + textSize.Width + 8, h);

            SmoothingMode oldMode = gr.SmoothingMode;
            gr.SmoothingMode = SmoothingMode.AntiAlias;
            try
            {
                Color fill = Color.FromArgb(235, color.R, color.G, color.B);
                using (GraphicsPath path = RoundedRect(tag, h / 2))
                using (var fillBrush = new SolidBrush(fill))
                using (var border = new Pen(Color.FromArgb(200, 255, 255, 255), 1f))
                {
                    gr.FillPath(fillBrush, path);
                    gr.DrawPath(border, path);
                }

                var circle = new RectangleF(tag.Left + 3, tag.Top + 3, grip - 6, h - 6);
                using (var circleBrush = new SolidBrush(Color.FromArgb(230, 255, 255, 255)))
                    gr.FillEllipse(circleBrush, circle);

                using (var gripPen = new Pen(color, 1.5f))
                {
                    float cx = circle.Left + circle.Width / 2;
                    float cy = circle.Top + circle.Height / 2;
                    float half = circle.Width * 0.28f;
                    for (int i = -1; i <= 1; i++)
                        gr.DrawLine(gripPen, cx - half, cy + i * 3, cx + half, cy + i * 3);
                }

                using (var textBrush = new SolidBrush(Color.White))
                    gr.DrawString(text, font, textBrush, tag.Left + grip + 2, y - textSize.Height / 2);
            }
            finally
            {
                gr.SmoothingMode = oldMode;
            }

            return tag;
        }

        private static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var path = new GraphicsPath();
            path.AddArc(r.Left, r.Top, d, d, 90, 180);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 180);
            path.CloseFigure();
            return path;
        }

        private static DashStyle ToDashStyle(int style)
        {
            return style switch
            {
                1 => DashStyle.Dash,
                2 => DashStyle.Dot,
                3 => DashStyle.DashDot,
                4 => DashStyle.DashDotDot,
                _ => DashStyle.Solid
            };
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            this.PollHub();
            if (args == null || args.Reason != UpdateReason.HistoricalBar)
                this.ApplyScalpTrail();
        }

        private const int ScalpLookbackBars = 60;

        /// <summary>
        /// Scalping: in profit, once the current with-trend candle breaks the extreme of the last
        /// counter-candle group (long: reds' highest high), SL moves past the group's other extreme.
        /// </summary>
        private void ApplyScalpTrail()
        {
            if (this.Symbol == null || this.Count < 2)
                return;

            bool on;
            bool dragging;
            double? stop;
            int offsetTicks;
            lock (TradeSetupHub.Sync)
            {
                TradeSetup setup = TradeSetupHub.Current;
                on = setup.ScalpingOn;
                dragging = setup.Dragging;
                stop = setup.ShowStop ? setup.StopLoss : null;
                offsetTicks = Math.Max(0, setup.ScalpOffsetTicks);
            }

            if (!on || dragging || !stop.HasValue || this.dragging != DragTarget.None)
                return;

            Position position = this.FindOpenPosition();
            if (position == null || position.OpenPrice <= 0)
                return;

            int direction = position.Side == Side.Buy ? 1 : -1;
            double price = this.GetExitPrice(direction);
            if (price <= 0 || direction * (price - position.OpenPrice) <= 0)
                return;

            double open0 = this.GetPrice(PriceType.Open, 0);
            double close0 = this.GetPrice(PriceType.Close, 0);
            bool withTrend = direction > 0 ? close0 > open0 : close0 < open0;
            if (!withTrend)
                return;

            if (!this.FindCounterGroup(direction, out double groupHigh, out double groupLow))
                return;

            double tick = this.Symbol.TickSize > 0 ? this.Symbol.TickSize : 0.25;
            double newStop;
            if (direction > 0)
            {
                if (this.GetPrice(PriceType.High, 0) <= groupHigh)
                    return;
                newStop = PositionSizing.RoundToTick(this.Symbol, groupLow - offsetTicks * tick);
                if (newStop <= stop.Value + tick * 0.5 || newStop >= price - tick * 0.5)
                    return;
            }
            else
            {
                if (this.GetPrice(PriceType.Low, 0) >= groupLow)
                    return;
                newStop = PositionSizing.RoundToTick(this.Symbol, groupHigh + offsetTicks * tick);
                if (newStop >= stop.Value - tick * 0.5 || newStop <= price + tick * 0.5)
                    return;
            }

            lock (TradeSetupHub.Sync)
            {
                TradeSetup setup = TradeSetupHub.Current;
                if (!setup.ScalpingOn || setup.Dragging || !setup.ShowStop || !setup.StopLoss.HasValue)
                    return;
                bool tighter = direction > 0
                    ? newStop > setup.StopLoss.Value + tick * 0.5
                    : newStop < setup.StopLoss.Value - tick * 0.5;
                if (!tighter)
                    return;
                setup.StopLoss = newStop;
            }

            TradeSetupHub.NotifyScalpStop();
            this.CurrentChart?.RedrawBuffer();
        }

        /// <summary>
        /// Most recent run of counter-direction candles before the current bar (long: red, short: green).
        /// With-trend candles between the run and the current bar must not have broken it already.
        /// </summary>
        private bool FindCounterGroup(int direction, out double groupHigh, out double groupLow)
        {
            groupHigh = double.MinValue;
            groupLow = double.MaxValue;
            int limit = Math.Min(this.Count, ScalpLookbackBars);

            int i = 1;
            double skippedHigh = double.MinValue;
            double skippedLow = double.MaxValue;
            while (i < limit && !this.IsCounterCandle(i, direction))
            {
                skippedHigh = Math.Max(skippedHigh, this.GetPrice(PriceType.High, i));
                skippedLow = Math.Min(skippedLow, this.GetPrice(PriceType.Low, i));
                i++;
            }

            bool found = false;
            while (i < limit && this.IsCounterCandle(i, direction))
            {
                groupHigh = Math.Max(groupHigh, this.GetPrice(PriceType.High, i));
                groupLow = Math.Min(groupLow, this.GetPrice(PriceType.Low, i));
                found = true;
                i++;
            }

            if (!found)
                return false;

            return direction > 0 ? skippedHigh <= groupHigh : skippedLow >= groupLow;
        }

        private bool IsCounterCandle(int index, int direction)
        {
            double open = this.GetPrice(PriceType.Open, index);
            double close = this.GetPrice(PriceType.Close, index);
            return direction > 0 ? close < open : close > open;
        }

        private double GetExitPrice(int direction)
        {
            try
            {
                if (direction > 0 && this.Symbol.Bid > 0)
                    return this.Symbol.Bid;
                if (direction < 0 && this.Symbol.Ask > 0)
                    return this.Symbol.Ask;
                if (this.Symbol.Last > 0)
                    return this.Symbol.Last;
            }
            catch
            {
            }

            return this.Count > 0 ? this.GetPrice(PriceType.Close, 0) : 0;
        }

        private void PollHub()
        {
            if (!TradeSetupHub.TryPull(ref this.hubSeq, out string command))
                return;

            if (command == TradeSetupHub.DrawLinesCommand)
                this.ApplyDrawLines();
            else if (command == TradeSetupHub.DrawBeCommand)
                this.ApplyDrawBe();
            else if (command == TradeSetupHub.ClearLinesCommand)
                this.ApplyClearLines();
            else if (command == TradeSetupHub.OrientCommand)
                this.ApplyOrient();
            else
                this.CurrentChart?.RedrawBuffer();
        }

        private double GetMarketPrice()
        {
            if (this.Symbol == null)
                return 0;
            try
            {
                if (this.Symbol.Ask > 0 && this.Symbol.Bid > 0)
                    return (this.Symbol.Ask + this.Symbol.Bid) * 0.5;
                if (this.Symbol.Last > 0)
                    return this.Symbol.Last;
            }
            catch
            {
            }

            if (this.Count > 0)
                return this.GetPrice(PriceType.Close, 0);
            return 0;
        }

        private double GetAtrOffset()
        {
            double tick = this.Symbol?.TickSize > 0 ? this.Symbol.TickSize : 0.25;
            if (this.Count < 16)
                return tick * 20;

            double sum = 0;
            int n = 14;
            for (int i = 1; i <= n; i++)
            {
                double high = this.GetPrice(PriceType.High, i);
                double low = this.GetPrice(PriceType.Low, i);
                double prevClose = this.GetPrice(PriceType.Close, i + 1);
                double tr = Math.Max(high - low, Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));
                sum += tr;
            }

            return (sum / n) * 2.0;
        }
    }
}
