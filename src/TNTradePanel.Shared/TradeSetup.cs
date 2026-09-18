namespace TNTradePanel.Shared
{
    /// <summary>
    /// Setup prices, line visibility, loss limit and line style.
    /// </summary>
    public sealed class TradeSetup
    {
        public double LossLimitUsd { get; set; } = 100;

        public double? Entry { get; set; }
        public double? StopLoss { get; set; }
        public double? TakeProfit { get; set; }
        public double? BreakEven { get; set; }

        public bool ShowEntry { get; set; }
        public bool ShowStop { get; set; }
        public bool ShowTakeProfit { get; set; }
        public bool ShowBreakEven { get; set; }
        public bool UseEntryLine { get; set; } = true;

        public int EntryColorArgb { get; set; } = unchecked((int)0xDC28A046);
        public int StopColorArgb { get; set; } = unchecked((int)0xDCC82828);
        public int TakeProfitColorArgb { get; set; } = unchecked((int)0xDCDC50A0);
        public int BreakEvenColorArgb { get; set; } = unchecked((int)0xDC8C3CC8);
        public float LineWidth { get; set; } = 1.5f;
        /// <summary>0 Solid, 1 Dash, 2 Dot, 3 DashDot, 4 DashDotDot</summary>
        public int LineStyle { get; set; }

        /// <summary>Direction for next line placement: 1 long, -1 short.</summary>
        public int DrawDirection { get; set; } = 1;

        /// <summary>After entry, SL cannot widen beyond initial risk.</summary>
        public bool StopLocked { get; set; }
        /// <summary>Initial SL price (reference).</summary>
        public double? LockedStopFloor { get; set; }
        /// <summary>Initial risk in ticks from average entry.</summary>
        public double LockedStopTicks { get; set; }

        /// <summary>True while a chart line is being dragged (panel must not correct).</summary>
        public bool Dragging { get; set; }

        /// <summary>Use Entry line price only when the line is actually visible (cleared after entry).</summary>
        public bool HasEntryLine =>
            this.UseEntryLine && this.ShowEntry && this.Entry.HasValue;

        public bool HasEntryAndStop =>
            Entry.HasValue && StopLoss.HasValue && Entry.Value != StopLoss.Value;

        public bool LinesVisible =>
            this.ShowEntry || this.ShowStop || this.ShowTakeProfit || this.ShowBreakEven;

        public int Direction
        {
            get
            {
                if (!this.HasEntryAndStop)
                    return 0;
                return this.StopLoss.Value < this.Entry.Value ? 1 : -1;
            }
        }

        public void ClearStopLine()
        {
            this.ShowStop = false;
            this.StopLoss = null;
            this.StopLocked = false;
            this.LockedStopFloor = null;
            this.LockedStopTicks = 0;
        }

        public void ClearLines()
        {
            this.ShowEntry = false;
            this.ShowStop = false;
            this.ShowTakeProfit = false;
            this.ShowBreakEven = false;
            this.Entry = null;
            this.StopLoss = null;
            this.TakeProfit = null;
            this.BreakEven = null;
            this.StopLocked = false;
            this.LockedStopFloor = null;
            this.LockedStopTicks = 0;
        }

        /// <summary>
        /// Clears Entry and TP. Also used after entry: both became real orders;
        /// management lines (SL, BE) stay.
        /// </summary>
        public void ClearUnlockedLines()
        {
            this.ShowEntry = false;
            this.Entry = null;
            this.ShowTakeProfit = false;
            this.TakeProfit = null;
        }

        public void LockStopAtCurrent(double? entry = null, double tickSize = 0)
        {
            if (!this.ShowStop || !this.StopLoss.HasValue)
                return;
            bool wasLocked = this.StopLocked;
            this.StopLocked = true;
            if (!wasLocked)
                this.LockedStopFloor = this.StopLoss.Value;
            if (entry.HasValue && entry.Value > 0 && tickSize > 0)
            {
                double ticks = Math.Abs(this.StopLoss.Value - entry.Value) / tickSize;
                // On scale-in risk must never grow: keep the tighter value.
                this.LockedStopTicks = this.LockedStopTicks > 0 ? Math.Min(this.LockedStopTicks, ticks) : ticks;
            }
        }
    }
}
