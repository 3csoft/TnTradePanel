using System.Collections;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Text;
using TradingPlatform.BusinessLayer;
using TradingPlatform.PresentationLayer.Plugins;
using TradingPlatform.PresentationLayer.Plugins.Services.Linking;
using TradingPlatform.PresentationLayer.Plugins.Services.Linking.EventArgs;
using TradingPlatform.PresentationLayer.Plugins.Services.Linking.Models.Scopes;
using TradingPlatform.PresentationLayer.Plugins.ViewControllers;
using TNTradePanel.Shared;

namespace TNTradePanel.Plugin
{
    /// <summary>
    /// Dockable trade panel: risk sizing, entry, panic, virtual SL line.
    /// </summary>
    public class TNTradePanelPlugin : global::TradingPlatform.PresentationLayer.Plugins.Plugin, ILinkable
    {
        private Symbol currentSymbol;
        private Account currentAccount;
        private bool beArmed;
        private bool accountPickedByUser;
        private bool enterBusy;
        private bool panicBusy;
        private bool slCloseBusy;
        private bool lossLimitCloseBusy;
        private bool entryCheckOn = true;
        private bool? lastGhostWarnVisible;
        private int hubSeq;
        private int nextDrawDirection = 1;
        private double? lastAppliedBreakEven;
        private int beOffsetTicks = 4;
        private System.Threading.Timer pollTimer;

        private double lossLimitUsd = 100;
        private Color entryLineColor = Color.FromArgb(220, 40, 160, 70);
        private Color stopLineColor = Color.FromArgb(220, 200, 40, 40);
        private Color takeProfitLineColor = Color.FromArgb(220, 220, 80, 160);
        private Color breakEvenLineColor = Color.FromArgb(220, 140, 60, 200);
        private int lineWidth = 2;
        private int lineStyle;

        public override Symbol CurrentSymbol
        {
            get => this.currentSymbol;
            set
            {
                this.UnsubscribeSymbol();
                this.currentSymbol = value;
                this.SubscribeSymbol();
                this.RefreshLinkedLabels();
                this.RefreshQtyUi();
            }
        }

        public override Account CurrentAccount
        {
            get => this.currentAccount;
            set
            {
                if (this.accountPickedByUser && this.currentAccount != null && value != null
                    && !SameAccount(this.currentAccount, value))
                {
                    this.RefreshLinkedLabels();
                    return;
                }

                this.currentAccount = value;
                this.RefreshLinkedLabels();
            }
        }

        public static PluginInfo GetInfo()
        {
            return new PluginInfo
            {
                Name = "TNTradePanel",
                Title = "TN Trade Panel",
                Group = PluginGroup.Trading,
                ShortName = "TNTP",
                TemplateName = "layout.html",
                AllowSettings = true,
                WindowParameters = new NativeWindowParameters(NativeWindowParameters.Panel)
                {
                    AllowsTransparency = true,
                    ResizeMode = NativeResizeMode.NoResize,
                    HeaderVisible = true,
                    BindingBehaviour = BindingBehaviour.Bindable,
                    StickingEnabled = StickyWindowBehavior.AllowSticking,
                    AllowMaximizeButton = false,
                    AllowFullScreenButton = false,
                    AllowActionsButton = true,
                    AllowCloseButton = true
                },
                CustomProperties = new Dictionary<string, object>
                {
                    { PluginInfo.Const.ALLOW_MANUAL_CREATION, true }
                }
            };
        }

        public override Size DefaultSize => new Size(170, 580);

        public override IList<SettingItem> Settings
        {
            get
            {
                IList<SettingItem> result = base.Settings;
                result.Add(new SettingItemDouble("LossLimitUsd", this.lossLimitUsd)
                {
                    Text = "Loss limit (USD)",
                    SortIndex = 10,
                    Minimum = 1,
                    Maximum = 1000000,
                    Increment = 10,
                    DecimalPlaces = 0
                });
                result.Add(new SettingItemInteger("BeOffsetTicks", this.beOffsetTicks)
                {
                    Text = "BE hit: SL = avg entry + X ticks",
                    SortIndex = 11,
                    Minimum = 0,
                    Maximum = 200
                });
                result.Add(new SettingItemColor("EntryLineColor", this.entryLineColor)
                {
                    Text = "Entry line color",
                    SortIndex = 20
                });
                result.Add(new SettingItemColor("StopLineColor", this.stopLineColor)
                {
                    Text = "SL line color",
                    SortIndex = 21
                });
                result.Add(new SettingItemColor("TakeProfitLineColor", this.takeProfitLineColor)
                {
                    Text = "TP line color",
                    SortIndex = 22
                });
                result.Add(new SettingItemColor("BreakEvenLineColor", this.breakEvenLineColor)
                {
                    Text = "BE line color",
                    SortIndex = 23
                });
                result.Add(new SettingItemInteger("LineWidth", this.lineWidth)
                {
                    Text = "Line width",
                    SortIndex = 30,
                    Minimum = 1,
                    Maximum = 6
                });
                result.Add(new SettingItemSelectorLocalized("LineStyle",
                    new SelectItem("", this.lineStyle),
                    new List<SelectItem>
                    {
                        new SelectItem("Solid", 0),
                        new SelectItem("Dash", 1),
                        new SelectItem("Dot", 2),
                        new SelectItem("Dash-dot", 3),
                        new SelectItem("Dash-dot-dot", 4)
                    })
                {
                    Text = "Line style",
                    SortIndex = 31
                });
                return result;
            }
            set
            {
                base.Settings = value;
                if (value.TryGetValue("LossLimitUsd", out double limit) && limit > 0)
                    this.lossLimitUsd = limit;
                if (value.TryGetValue("BeOffsetTicks", out int beTicks) && beTicks >= 0)
                    this.beOffsetTicks = beTicks;
                if (value.GetItemByPath("EntryLineColor") is SettingItemColor entryColor)
                    this.entryLineColor = (Color)entryColor.Value;
                if (value.GetItemByPath("StopLineColor") is SettingItemColor stopColor)
                    this.stopLineColor = (Color)stopColor.Value;
                if (value.GetItemByPath("TakeProfitLineColor") is SettingItemColor tpColor)
                    this.takeProfitLineColor = (Color)tpColor.Value;
                if (value.GetItemByPath("BreakEvenLineColor") is SettingItemColor beColor)
                    this.breakEvenLineColor = (Color)beColor.Value;
                if (value.TryGetValue("LineWidth", out int width) && width > 0)
                    this.lineWidth = width;
                if (value.TryGetValue("LineStyle", out int style))
                    this.lineStyle = style;

                this.PublishVisualSettings();
                this.RefreshQtyUi();
            }
        }

        public override void Initialize()
        {
            base.Initialize();
            this.Window.Browser.AddEventHandler("accountbutton", "onclick", this.OnAccountClick);
            this.Window.Browser.AddEventHandler("accountselect", "onchange", this.OnAccountSelectChanged);
            this.Window.Browser.AddEventHandler("drawlinesbutton", "onclick", this.OnDrawLinesClick);
            this.Window.Browser.AddEventHandler("longbutton", "onclick", this.OnLongClick);
            this.Window.Browser.AddEventHandler("shortbutton", "onclick", this.OnShortClick);
            this.Window.Browser.AddEventHandler("entrytoggle", "onclick", this.OnEntryToggleClick);
            this.Window.Browser.AddEventHandler("bebutton", "onclick", this.OnBeClick);
            this.Window.Browser.AddEventHandler("enterbutton", "onclick", this.OnEnterClick);
            this.Window.Browser.AddEventHandler("panicbutton", "onclick", this.OnPanicClick);

            this.RegisterService<LinkingPluginService>();
            this.pollTimer = new System.Threading.Timer(_ => this.PollHub(), null, 250, 250);

            this.PublishVisualSettings();
            this.RefreshLinkedLabels();
            this.RefreshEntryToggleUi();
            this.RefreshDirectionUi();
            this.RefreshDrawButtonUi();
            this.RefreshQtyUi();
            this.SetStatus("Select an account and add TN Trade Panel Lines to the chart.");
        }

        public override void Dispose()
        {
            this.pollTimer?.Dispose();
            this.pollTimer = null;
            this.UnsubscribeSymbol();
            base.Dispose();
        }

        public LinkingState LinkingState => this.GetService<LinkingPluginService>().LinkingState;

        public event EventHandler<LinkingEntityEventArgs> LinkingStateChanged
        {
            add { this.GetService<LinkingPluginService>().LinkingStateChanged += value; }
            remove { this.GetService<LinkingPluginService>().LinkingStateChanged -= value; }
        }

        private void OnAccountClick(string elementId, object args)
        {
            var parameters = new PluginParameters()
                .Add(PluginParameters.CALLBACK, new Action<IList<Account>>(this.OnAccountSelected))
                .Add("SelectionMode", SelectionMode.SingleSelect)
                .Add(PluginParameters.WINDOW_POSITION_TYPE, NativeWindowDefaultPositionType.CenterScreenByCurrentMousePosition);

            if (this.currentAccount != null)
                parameters.Add(PluginParameters.ACCOUNT, this.currentAccount);

            Application.Instance.SendCommand(new ApplicationCommandOpenPlugin
            {
                Name = global::TradingPlatform.PresentationLayer.Plugins.Plugin.ACCOUNTS_LOOKUP,
                Parameters = parameters
            });
        }

        private void OnAccountSelected(IList<Account> accounts)
        {
            this.ApplyUserAccount(ExtractAccount(accounts));
        }

        private void OnAccountSelectChanged(string elementId, object args)
        {
            try
            {
                NativeJavascriptResponse response = this.Window.Browser.GetHtmlValue("accountselect", HtmlGetValueAction.GetProperty, "value");
                this.ApplyUserAccount(this.FindAccountByIdOrName(response?.Result?.ToString()));
            }
            catch
            {
            }
        }

        private void ApplyUserAccount(Account selected)
        {
            if (selected == null)
                return;

            this.accountPickedByUser = true;
            this.currentAccount = selected;
            try
            {
                this.PublishLinking(LinkingScope.Account, selected);
            }
            catch
            {
            }

            this.RefreshLinkedLabels();
            this.SetStatus("Account: " + selected.Name);
        }

        private void OnDrawLinesClick(string elementId, object args)
        {
            this.PublishVisualSettings();

            Position open = this.FindPosition();
            if (open != null)
            {
                int openDir = open.Side == Side.Buy ? 1 : -1;
                if (this.nextDrawDirection != openDir)
                    this.nextDrawDirection = openDir;
            }

            lock (TradeSetupHub.Sync)
            {
                TradeSetupHub.Current.UseEntryLine = this.entryCheckOn;
                TradeSetupHub.Current.DrawDirection = this.nextDrawDirection >= 0 ? 1 : -1;
            }

            TradeSetupHub.RequestDrawLines();

            if (TradeSetupHub.LineListeners <= 0)
            {
                this.SetStatus("No indicator on the chart. Add: TN Trade Panel Lines.");
                return;
            }

            bool extras;
            lock (TradeSetupHub.Sync)
                extras = TradeSetupHub.Current.ShowEntry || TradeSetupHub.Current.ShowTakeProfit;

            if (extras)
                this.SetStatus(open != null
                    ? "Entry/TP removed. SL line kept (open position)."
                    : "Lines removed.");
            else
                this.SetStatus(this.nextDrawDirection >= 0
                    ? "Long lines placed."
                    : "Short lines placed.");

            this.RefreshDrawButtonUi();
            this.RefreshQtyUi();
        }

        private void OnLongClick(string elementId, object args)
        {
            this.SetDirection(1);
        }

        private void OnShortClick(string elementId, object args)
        {
            this.SetDirection(-1);
        }

        private void SetDirection(int direction)
        {
            Position open = this.FindPosition();
            if (open != null)
            {
                int openDir = open.Side == Side.Buy ? 1 : -1;
                if (openDir != direction)
                {
                    this.SetStatus(openDir > 0
                        ? "Long position open — SHORT disabled."
                        : "Short position open — LONG disabled.");
                    this.RefreshDirectionUi();
                    return;
                }
            }

            this.nextDrawDirection = direction;
            bool extras;
            lock (TradeSetupHub.Sync)
            {
                TradeSetupHub.Current.DrawDirection = direction;
                extras = TradeSetupHub.Current.ShowEntry || TradeSetupHub.Current.ShowTakeProfit;
            }

            this.RefreshDirectionUi();
            if (extras)
            {
                TradeSetupHub.RequestOrient();
                this.SetStatus(direction > 0 ? "Direction: LONG (lines flipped)." : "Direction: SHORT (lines flipped).");
            }
            else
            {
                this.SetStatus(direction > 0 ? "Direction: LONG." : "Direction: SHORT.");
            }

            this.RefreshQtyUi();
        }

        private void RefreshDrawButtonUi()
        {
            bool extras;
            bool beVisible;
            lock (TradeSetupHub.Sync)
            {
                extras = TradeSetupHub.Current.ShowEntry || TradeSetupHub.Current.ShowTakeProfit;
                beVisible = TradeSetupHub.Current.ShowBreakEven && TradeSetupHub.Current.BreakEven.HasValue;
            }

            this.Window.Browser.UpdateHtml("drawlinesbutton", HtmlAction.SetInnerHtml, extras ? "Lines: off" : "Lines");
            this.Window.Browser.UpdateHtml("bebutton", HtmlAction.SetInnerHtml, beVisible ? "BE line: off" : "BE line");
        }

        private void RefreshDirectionUi()
        {
            try
            {
                this.Window.Browser.UpdateHtml(string.Empty, HtmlAction.InvokeJs,
                    this.nextDrawDirection >= 0 ? "setDir('long')" : "setDir('short')");
            }
            catch
            {
            }
        }

        private void OnEntryToggleClick(string elementId, object args)
        {
            this.entryCheckOn = !this.entryCheckOn;

            Position open = this.FindPosition();
            int direction = open != null
                ? (open.Side == Side.Buy ? 1 : -1)
                : (this.nextDrawDirection >= 0 ? 1 : -1);
            double market = PositionSizing.RoundToTick(
                this.CurrentSymbol, PositionSizing.MarketEntry(this.CurrentSymbol, direction));

            bool linesOut;
            bool placed = false;
            lock (TradeSetupHub.Sync)
            {
                TradeSetup setup = TradeSetupHub.Current;
                setup.UseEntryLine = this.entryCheckOn;
                linesOut = setup.ShowStop || setup.ShowTakeProfit;

                // With SL/TP already out, Entry can be toggled on the fly.
                if (linesOut)
                {
                    if (this.entryCheckOn && market > 0)
                    {
                        setup.Entry = market;
                        setup.ShowEntry = true;
                        placed = true;
                    }
                    else
                    {
                        setup.Entry = null;
                        setup.ShowEntry = false;
                    }
                }
            }

            if (linesOut)
                TradeSetupHub.NotifySetupChanged();

            this.RefreshEntryToggleUi();
            this.RefreshDrawButtonUi();
            this.RefreshQtyUi();

            if (!linesOut)
                this.SetStatus(this.entryCheckOn
                    ? "Entry line: on (applied on next Lines press)."
                    : "Entry line: off — market entry.");
            else if (placed)
                this.SetStatus("Entry line at current price: "
                    + (this.CurrentSymbol?.FormatPrice(market) ?? market.ToString("0.####")));
            else if (this.entryCheckOn)
                this.SetStatus("No valid market price for Entry line.");
            else
                this.SetStatus("Entry line removed — market entry.");
        }

        private void RefreshEntryToggleUi()
        {
            this.Window.Browser.UpdateHtml("entrytoggle", HtmlAction.SetInnerHtml, this.entryCheckOn ? "Entry: on" : "Entry: off");
        }

        private void OnBeClick(string elementId, object args)
        {
            if (this.FindPosition() == null)
            {
                this.SetStatus("BE requires an open position.");
                return;
            }

            if (TradeSetupHub.LineListeners <= 0)
            {
                this.SetStatus("No indicator on the chart. Add: TN Trade Panel Lines.");
                return;
            }

            bool wasVisible;
            lock (TradeSetupHub.Sync)
                wasVisible = TradeSetupHub.Current.ShowBreakEven && TradeSetupHub.Current.BreakEven.HasValue;

            TradeSetupHub.RequestDrawBe();
            this.lastAppliedBreakEven = null;

            if (wasVisible)
            {
                this.beArmed = false;
                this.SetStatus("BE line removed.");
                return;
            }

            this.beArmed = true;
            this.SetStatus("BE line placed (above/below market). Drag into place — on touch SL moves to entry + "
                + this.beOffsetTicks + " ticks.");
        }

        private void OnEnterClick(string elementId, object args)
        {
            if (this.enterBusy)
                return;
            this.enterBusy = true;
            try
            {
                string error = this.TryEnter();
                this.SetStatus(error ?? "Entry sent (TP yes, SL line virtual).");
                this.RefreshQtyUi();
            }
            finally
            {
                this.enterBusy = false;
            }
        }

        private void OnPanicClick(string elementId, object args)
        {
            string error = this.TryPanic();
            this.SetStatus(error ?? "Panic: positions and pendings closed.");
        }

        private void PollHub()
        {
            try
            {
                this.EnforceStopRiskCap();
                this.CheckLossLimitBreach();
                this.CheckVirtualStopTouch();
                this.RefreshGhostOrderWarn();
                if (!TradeSetupHub.TryPull(ref this.hubSeq, out _))
                    return;
                lock (TradeSetupHub.Sync)
                    this.beArmed = TradeSetupHub.Current.ShowBreakEven && TradeSetupHub.Current.BreakEven.HasValue;
                this.RefreshDrawButtonUi();
                this.RefreshQtyUi();
            }
            catch
            {
            }
        }

        private void PublishVisualSettings()
        {
            lock (TradeSetupHub.Sync)
            {
                TradeSetupHub.Current.LossLimitUsd = this.lossLimitUsd;
                TradeSetupHub.Current.EntryColorArgb = this.entryLineColor.ToArgb();
                TradeSetupHub.Current.StopColorArgb = this.stopLineColor.ToArgb();
                TradeSetupHub.Current.TakeProfitColorArgb = this.takeProfitLineColor.ToArgb();
                TradeSetupHub.Current.BreakEvenColorArgb = this.breakEvenLineColor.ToArgb();
                TradeSetupHub.Current.LineWidth = this.lineWidth;
                TradeSetupHub.Current.LineStyle = this.lineStyle;
            }
            TradeSetupHub.NotifySetupChanged();
        }

        private void RefreshQtyUi()
        {
            lock (TradeSetupHub.Sync)
                TradeSetupHub.Current.LossLimitUsd = this.lossLimitUsd;

            TradeSetup setup;
            lock (TradeSetupHub.Sync)
                setup = CloneSetup(TradeSetupHub.Current);

            PositionSizeResult size = PositionSizing.Calculate(this.CurrentSymbol, setup);
            string qtyText = size.Ok
                ? "Quantity: " + FormatQty(this.CurrentSymbol, size.Quantity)
                : "Quantity: —";
            string rrText = size.RewardRisk > 0
                ? "RR: " + size.RewardRisk.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                : "RR: —";
            string limitText = "Limit: " + this.lossLimitUsd.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " USD";

            this.Window.Browser.UpdateHtml("limittext", HtmlAction.SetInnerHtml, limitText);
            this.Window.Browser.UpdateHtml("qtytext", HtmlAction.SetInnerHtml, qtyText);
            this.Window.Browser.UpdateHtml("rrtext", HtmlAction.SetInnerHtml, rrText);
        }

        private void RefreshLinkedLabels()
        {
            this.Window.Browser.UpdateHtml("symboltext", HtmlAction.SetInnerHtml, "Symbol: " + (this.CurrentSymbol?.Name ?? "—"));
            this.RefreshAccountSelect();
        }

        private void RefreshAccountSelect()
        {
            var html = new StringBuilder();
            Account[] accounts = Core.Instance.Accounts?.ToArray() ?? Array.Empty<Account>();
            if (accounts.Length == 0)
                html.Append("<option value=\"\">— no account —</option>");

            foreach (Account account in accounts)
            {
                if (account == null)
                    continue;
                string id = WebUtility.HtmlEncode(account.Id ?? string.Empty);
                string name = WebUtility.HtmlEncode(account.Name ?? account.Id ?? string.Empty);
                html.Append("<option value=\"").Append(id).Append('"');
                if (SameAccount(account, this.currentAccount))
                    html.Append(" selected");
                html.Append('>').Append(name).Append("</option>");
            }

            this.Window.Browser.UpdateHtml("accountselect", HtmlAction.SetInnerHtml, html.ToString());
        }

        private void SetStatus(string text)
        {
            this.Window.Browser.UpdateHtml("statustext", HtmlAction.SetInnerHtml, text ?? string.Empty);
        }

        private void SubscribeSymbol()
        {
            if (this.currentSymbol != null)
                this.currentSymbol.NewLast += this.OnNewLast;
        }

        private void UnsubscribeSymbol()
        {
            if (this.currentSymbol != null)
                this.currentSymbol.NewLast -= this.OnNewLast;
        }

        private void OnNewLast(Symbol symbol, Last last)
        {
            this.CheckLossLimitBreach(last?.Price);
            this.CheckVirtualStopTouch(last?.Price);
            if (!this.beArmed || last == null)
                return;

            double price = last.Price;
            TradeSetup setup;
            lock (TradeSetupHub.Sync)
                setup = CloneSetup(TradeSetupHub.Current);

            if (!setup.ShowBreakEven || !setup.BreakEven.HasValue)
                return;

            Position position = this.FindPosition();
            if (position == null)
                return;

            double be = setup.BreakEven.Value;
            bool hit = position.Side == Side.Buy
                ? price >= be
                : price <= be;
            if (!hit)
                return;

            if (this.lastAppliedBreakEven.HasValue
                && Math.Abs(this.lastAppliedBreakEven.Value - be) < (position.Symbol?.TickSize ?? 0.01) * 0.4)
                return;

            double tick = position.Symbol?.TickSize > 0 ? position.Symbol.TickSize : 0.25;
            int dir = position.Side == Side.Buy ? 1 : -1;
            // BE + X ticks in profit direction from average entry (not trigger price — that would close immediately).
            double beStop = PositionSizing.RoundToTick(position.Symbol, position.OpenPrice + dir * this.beOffsetTicks * tick);
            this.lastAppliedBreakEven = be;
            lock (TradeSetupHub.Sync)
            {
                TradeSetupHub.Current.ShowStop = true;
                TradeSetupHub.Current.StopLoss = beStop;
                TradeSetupHub.Current.StopLocked = true;
                TradeSetupHub.Current.LockedStopFloor = beStop;
            }

            TradeSetupHub.NotifySetupChanged();
            this.SetStatus("BE hit: SL → entry + " + this.beOffsetTicks + " ticks. BE line stays.");
        }

        /// <summary>
        /// Indicator snaps SL on DROP; panel also enforces initial risk if that misses
        /// (e.g. continuous contract chart). Skips while dragging.
        /// </summary>
        private void EnforceStopRiskCap()
        {
            Position position = this.FindPosition();
            if (position == null || position.OpenPrice <= 0)
                return;

            double tick = this.CurrentSymbol?.TickSize > 0 ? this.CurrentSymbol.TickSize : 0.25;
            double capped;
            lock (TradeSetupHub.Sync)
            {
                TradeSetup setup = TradeSetupHub.Current;
                if (setup.Dragging || !setup.StopLocked || !setup.ShowStop || !setup.StopLoss.HasValue)
                    return;

                double ticks = setup.LockedStopTicks;
                if (ticks <= 0)
                    return;

                int direction = position.Side == Side.Buy ? 1 : -1;
                double max = direction > 0
                    ? position.OpenPrice - ticks * tick
                    : position.OpenPrice + ticks * tick;
                capped = PositionSizing.RoundToTick(this.CurrentSymbol, max);

                bool widened = direction > 0
                    ? setup.StopLoss.Value < capped - tick * 0.5
                    : setup.StopLoss.Value > capped + tick * 0.5;
                if (!widened)
                    return;

                setup.StopLoss = capped;
            }

            TradeSetupHub.NotifySetupChanged();
            this.SetStatus("SL restored to initial risk: "
                + (this.CurrentSymbol?.FormatPrice(capped) ?? capped.ToString("0.####")));
        }

        private void RefreshGhostOrderWarn()
        {
            bool show = false;
            if (this.currentAccount != null)
            {
                bool hasPosition = Core.Instance.Positions.Any(p =>
                    p != null && SameAccount(p.Account, this.currentAccount));
                if (!hasPosition)
                {
                    show = Core.Instance.Orders.Any(o =>
                        o != null && SameAccount(o.Account, this.currentAccount));
                }
            }

            if (this.lastGhostWarnVisible.HasValue && this.lastGhostWarnVisible.Value == show)
                return;

            this.lastGhostWarnVisible = show;
            try
            {
                this.Window.Browser.UpdateHtml(string.Empty, HtmlAction.InvokeJs,
                    show ? "setGhostWarn(true)" : "setGhostWarn(false)");
            }
            catch
            {
            }
        }

        /// <summary>
        /// If open PnL loss exceeds the configured USD limit, flatten even while the SL line is
        /// being dragged (so a held drag cannot bypass the risk limit).
        /// </summary>
        private void CheckLossLimitBreach(double? lastPrice = null)
        {
            if (this.panicBusy || this.slCloseBusy || this.lossLimitCloseBusy || this.currentAccount == null)
                return;
            if (this.lossLimitUsd <= 0)
                return;

            Position[] positions = Core.Instance.Positions
                .Where(p => p != null && SameAccount(p.Account, this.currentAccount))
                .ToArray();
            if (positions.Length == 0)
                return;

            double totalPnl = 0;
            int counted = 0;
            foreach (Position position in positions)
            {
                double? pnl = this.EstimateOpenPnlUsd(position, lastPrice);
                if (!pnl.HasValue)
                    continue;
                totalPnl += pnl.Value;
                counted++;
            }

            if (counted == 0)
                return;

            // Breach when floating loss is at least the configured limit.
            if (totalPnl > -this.lossLimitUsd)
                return;

            this.CloseAllOnLossLimit(totalPnl);
        }

        private double? EstimateOpenPnlUsd(Position position, double? lastPrice)
        {
            if (position?.Symbol == null || position.OpenPrice <= 0 || position.Quantity <= 0)
                return null;

            int direction = position.Side == Side.Buy ? 1 : -1;
            double market = lastPrice ?? 0;
            if (market <= 0)
            {
                try
                {
                    Symbol symbol = position.Symbol;
                    if (direction > 0 && symbol.Bid > 0)
                        market = symbol.Bid;
                    else if (direction < 0 && symbol.Ask > 0)
                        market = symbol.Ask;
                    else if (symbol.Last > 0)
                        market = symbol.Last;
                }
                catch
                {
                }
            }

            if (market <= 0)
                return null;

            return PositionSizing.EstimateStopUsd(
                position.Symbol, position.OpenPrice, market, position.Quantity, direction);
        }

        private void CloseAllOnLossLimit(double totalPnl)
        {
            if (this.lossLimitCloseBusy)
                return;
            this.lossLimitCloseBusy = true;
            this.panicBusy = true;
            try
            {
                FlattenResult flat = this.FlattenAccount();
                this.ResetSetupState();
                string pnlText = totalPnl.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                this.SetStatus("Loss limit hit (" + pnlText + " USD) — account flat: "
                    + flat.Closed + " positions, " + flat.Cancelled + " orders cancelled.");
            }
            finally
            {
                this.panicBusy = false;
                this.lossLimitCloseBusy = false;
            }
        }

        private void CheckVirtualStopTouch(double? lastPrice = null)
        {
            if (this.panicBusy || this.slCloseBusy || this.currentAccount == null)
                return;

            TradeSetup setup;
            lock (TradeSetupHub.Sync)
                setup = CloneSetup(TradeSetupHub.Current);

            if (!setup.ShowStop || !setup.StopLoss.HasValue)
                return;

            Position[] positions = Core.Instance.Positions
                .Where(p => p != null && SameAccount(p.Account, this.currentAccount) && SameSymbol(p.Symbol, this.CurrentSymbol))
                .ToArray();
            if (positions.Length == 0)
                return;

            double price = lastPrice ?? 0;
            if (price <= 0 && this.CurrentSymbol != null)
            {
                try
                {
                    if (this.CurrentSymbol.Last > 0)
                        price = this.CurrentSymbol.Last;
                    else if (this.CurrentSymbol.Bid > 0)
                        price = this.CurrentSymbol.Bid;
                }
                catch
                {
                }
            }

            if (price <= 0)
                return;

            double stop = setup.StopLoss.Value;
            double tick = this.CurrentSymbol?.TickSize > 0 ? this.CurrentSymbol.TickSize : 0.25;
            bool hit = positions.Any(p =>
                p.Side == Side.Buy
                    ? price <= stop + tick * 0.5
                    : price >= stop - tick * 0.5);
            if (!hit)
                return;

            this.CloseAllOnVirtualStop();
        }

        private void CloseAllOnVirtualStop()
        {
            if (this.slCloseBusy)
                return;
            this.slCloseBusy = true;
            this.panicBusy = true;
            try
            {
                FlattenResult flat = this.FlattenAccount();
                this.ResetSetupState();
                this.SetStatus("SL line: account flat — "
                    + flat.Closed + " positions, " + flat.Cancelled + " orders cancelled.");
            }
            finally
            {
                this.panicBusy = false;
                this.slCloseBusy = false;
            }
        }

        private string TryEnter()
        {
            if (this.CurrentSymbol == null || this.currentAccount == null)
                return "Select an account and link the panel to the chart.";

            lock (TradeSetupHub.Sync)
            {
                TradeSetupHub.Current.UseEntryLine = this.entryCheckOn;
                TradeSetupHub.Current.LossLimitUsd = this.lossLimitUsd;
            }

            TradeSetup setup;
            lock (TradeSetupHub.Sync)
                setup = CloneSetup(TradeSetupHub.Current);

            if (!setup.ShowStop || !setup.StopLoss.HasValue)
                return "SL line required.";

            // After entry the Entry line is cleared: scale-in uses market orders.
            bool useEntry = setup.HasEntryLine;
            if (setup.UseEntryLine && setup.ShowEntry && !setup.HasEntryAndStop)
                return "Entry and SL required.";

            int direction = PositionSizing.InferDirection(this.CurrentSymbol, setup);
            if (direction == 0)
                return "SL/TP direction unclear.";

            double entry = useEntry && setup.Entry.HasValue
                ? PositionSizing.RoundToTick(this.CurrentSymbol, setup.Entry.Value)
                : PositionSizing.RoundToTick(this.CurrentSymbol, PositionSizing.MarketEntry(this.CurrentSymbol, direction));
            double stop = PositionSizing.RoundToTick(this.CurrentSymbol, setup.StopLoss.Value);
            double? tp = setup.ShowTakeProfit && setup.TakeProfit.HasValue
                ? PositionSizing.RoundToTick(this.CurrentSymbol, setup.TakeProfit.Value)
                : (double?)null;

            if (direction > 0 && stop >= entry)
                return "SL error: for long, SL must be below Entry.";
            if (direction < 0 && stop <= entry)
                return "SL error: for short, SL must be above Entry.";

            if (tp.HasValue)
            {
                if (direction > 0 && tp.Value <= entry)
                    return "TP error: for long, TP must be above Entry.";
                if (direction < 0 && tp.Value >= entry)
                    return "TP error: for short, TP must be below Entry.";
            }

            string scaleError = this.CheckScaleInAllowed(direction, stop);
            if (scaleError != null)
                return scaleError;

            PositionSizeResult size = PositionSizing.Calculate(this.CurrentSymbol, setup);
            if (!size.Ok)
                return size.Error;

            Side side = direction > 0 ? Side.Buy : Side.Sell;
            bool useMarket = !useEntry || this.IsEntryAtMarket(entry, side);
            string orderTypeId;

            if (useMarket)
            {
                orderTypeId = this.FindOrderTypeId(this.CurrentSymbol, OrderTypeUsage.Order, OrderTypeBehavior.Market) ?? OrderType.Market;
            }
            else if (this.IsStopPending(entry, side))
            {
                orderTypeId = this.FindOrderTypeId(this.CurrentSymbol, OrderTypeUsage.Order, OrderTypeBehavior.Stop) ?? OrderType.Stop;
            }
            else
            {
                orderTypeId = this.FindOrderTypeId(this.CurrentSymbol, OrderTypeUsage.Order, OrderTypeBehavior.Limit) ?? OrderType.Limit;
            }

            var request = new PlaceOrderRequestParameters
            {
                Symbol = this.CurrentSymbol,
                Account = this.currentAccount,
                Side = side,
                Quantity = size.Quantity,
                OrderTypeId = orderTypeId,
                TimeInForce = TimeInForce.GTC,
                Comment = "TNTradePanel"
            };

            if (orderTypeId == OrderType.Limit || (!useMarket && !this.IsStopPending(entry, side)))
                request.Price = entry;
            if (!useMarket && this.IsStopPending(entry, side))
                request.TriggerPrice = entry;

            int tpTicks = this.CountTicks(entry, tp);
            if (tpTicks > 0)
                request.TakeProfit = SlTpHolder.CreateTP(tpTicks, PriceMeasurement.Offset);

            TradingOperationResult result = Core.Instance.PlaceOrder(request);
            if (result == null || result.Status != TradingOperationResultStatus.Success)
                return result?.Message ?? "Order rejected.";

            // SL line stays and locks — no SL order, virtual management.
            // Entry and TP became real orders, so their lines are cleared.
            lock (TradeSetupHub.Sync)
            {
                TradeSetupHub.Current.LockStopAtCurrent(entry, this.CurrentSymbol?.TickSize ?? 0);
                TradeSetupHub.Current.ClearUnlockedLines();
            }
            TradeSetupHub.NotifySetupChanged();
            this.beArmed = false;
            return null;
        }

        private string CheckScaleInAllowed(int direction, double stop)
        {
            Position open = this.FindPosition();
            if (open == null)
                return null;

            int openDir = open.Side == Side.Buy ? 1 : -1;
            if (openDir != direction)
                return "Opposite position open — close / Panic first.";

            double tick = open.Symbol?.TickSize > 0 ? open.Symbol.TickSize : (this.CurrentSymbol?.TickSize ?? 0.25);
            double openPrice = open.OpenPrice;
            bool riskFree = openDir > 0
                ? stop >= openPrice - tick * 0.4
                : stop <= openPrice + tick * 0.4;

            if (!riskFree)
                return openDir > 0
                    ? "Scale-in only if SL is at or above prior entry (no risk)."
                    : "Scale-in only if SL is at or below prior entry (no risk).";

            return null;
        }

        private string TryPanic()
        {
            if (this.currentAccount == null)
                return "Select an account from the list.";

            this.panicBusy = true;
            try
            {
                FlattenResult flat = this.FlattenAccount();
                this.ResetSetupState();
                if (flat.Closed == 0 && flat.Cancelled == 0)
                    return flat.Errors.Count > 0
                        ? flat.Errors[0]
                        : "Nothing to close. Lines and state reset.";
                if (flat.Errors.Count > 0)
                    return "Panic: " + flat.Closed + " positions, " + flat.Cancelled + " orders. " + flat.Errors[0];
                return null;
            }
            finally
            {
                this.panicBusy = false;
            }
        }

        private sealed class FlattenResult
        {
            public int Closed;
            public int Cancelled;
            public List<string> Errors = new List<string>();
        }

        /// <summary>Full account flat: all positions + all orders on the selected account.</summary>
        private FlattenResult FlattenAccount()
        {
            var flat = new FlattenResult();
            if (this.currentAccount == null)
                return flat;

            foreach (Position position in Core.Instance.Positions.Where(p => SameAccount(p.Account, this.currentAccount)).ToArray())
            {
                TradingOperationResult result = position.Close();
                if (result == null || result.Status != TradingOperationResultStatus.Success)
                    result = Core.Instance.ClosePosition(position);

                if (result == null || result.Status != TradingOperationResultStatus.Success)
                {
                    result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
                    {
                        Account = position.Account,
                        Symbol = position.Symbol,
                        Side = position.Side == Side.Buy ? Side.Sell : Side.Buy,
                        Quantity = position.Quantity,
                        OrderTypeId = OrderType.Market,
                        PositionId = position.Id,
                        Comment = "TNTradePanel flat"
                    });
                }

                if (result != null && result.Status == TradingOperationResultStatus.Success)
                    flat.Closed++;
                else if (!string.IsNullOrEmpty(result?.Message))
                    flat.Errors.Add(result.Message);
            }

            foreach (Order order in Core.Instance.Orders.Where(o => SameAccount(o.Account, this.currentAccount)).ToArray())
            {
                TradingOperationResult result = order.Cancel();
                if (result != null && result.Status == TradingOperationResultStatus.Success)
                    flat.Cancelled++;
                else if (!string.IsNullOrEmpty(result?.Message))
                    flat.Errors.Add(result.Message);
            }

            return flat;
        }

        /// <summary>Full reset after panic / SL close: clear lines, unlock, direction back to long.</summary>
        private void ResetSetupState()
        {
            lock (TradeSetupHub.Sync)
                TradeSetupHub.Current.ClearLines();
            TradeSetupHub.RequestClearLines();
            this.beArmed = false;
            this.lastAppliedBreakEven = null;
            this.nextDrawDirection = 1;
            this.lastGhostWarnVisible = null;
            this.RefreshDirectionUi();
            this.RefreshDrawButtonUi();
            this.RefreshGhostOrderWarn();
        }

        private int CountTicks(double fromPrice, double? toPrice)
        {
            if (!toPrice.HasValue || this.CurrentSymbol == null)
                return 0;
            double tick = this.CurrentSymbol.TickSize;
            if (tick <= 0)
                return 0;
            return Math.Max(0, (int)Math.Round(Math.Abs(toPrice.Value - fromPrice) / tick));
        }

        private Position FindPosition()
        {
            return Core.Instance.Positions.FirstOrDefault(this.IsMine);
        }

        private bool IsMine(Position position)
        {
            return position != null
                && SameSymbol(position.Symbol, this.CurrentSymbol)
                && SameAccount(position.Account, this.CurrentAccount);
        }

        private bool IsEntryAtMarket(double entry, Side side)
        {
            double market = this.MarketPrice(side);
            double tick = this.CurrentSymbol?.TickSize ?? 0;
            if (tick <= 0)
                return true;
            return Math.Abs(entry - market) <= tick * 2.5;
        }

        private bool IsStopPending(double entry, Side side)
        {
            double market = this.MarketPrice(side);
            return side == Side.Buy ? entry > market : entry < market;
        }

        private double MarketPrice(Side side)
        {
            if (this.CurrentSymbol == null)
                return 0;
            try
            {
                if (side == Side.Buy && this.CurrentSymbol.Ask > 0)
                    return this.CurrentSymbol.Ask;
                if (side == Side.Sell && this.CurrentSymbol.Bid > 0)
                    return this.CurrentSymbol.Bid;
                if (this.CurrentSymbol.Last > 0)
                    return this.CurrentSymbol.Last;
            }
            catch
            {
            }

            return this.CurrentSymbol.Last;
        }

        private string FindOrderTypeId(Symbol symbol, OrderTypeUsage usage, OrderTypeBehavior behavior)
        {
            try
            {
                IList<OrderType> types = symbol?.GetAlowedOrderTypes(usage);
                return types?.FirstOrDefault(ot => ot != null && ot.Behavior == behavior)?.Id;
            }
            catch
            {
                return null;
            }
        }

        private Account FindAccountByIdOrName(string idOrName)
        {
            if (string.IsNullOrWhiteSpace(idOrName))
                return null;
            return Core.Instance.Accounts?.FirstOrDefault(a =>
                a != null
                && (string.Equals(a.Id, idOrName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(a.Name, idOrName, StringComparison.OrdinalIgnoreCase)));
        }

        private static Account ExtractAccount(object raw)
        {
            if (raw == null)
                return null;
            if (raw is Account account)
                return account;
            if (raw is BusinessObjectInfo info)
                return Core.Instance.GetAccount(info);

            if (raw is IEnumerable items)
            {
                foreach (object item in items)
                {
                    Account found = ExtractAccount(item);
                    if (found != null)
                        return found;
                }
            }

            return null;
        }

        private static bool SameSymbol(Symbol a, Symbol b)
        {
            if (a == null || b == null)
                return false;
            if (ReferenceEquals(a, b))
                return true;
            if (string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.IsNullOrEmpty(a.ConnectionId) || !string.Equals(a.ConnectionId, b.ConnectionId, StringComparison.OrdinalIgnoreCase))
                return false;

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

        private static bool SameAccount(Account a, Account b)
        {
            if (a == null || b == null)
                return false;
            if (ReferenceEquals(a, b))
                return true;
            return string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatQty(Symbol symbol, double quantity)
        {
            double step = symbol?.LotStep > 0 ? symbol.LotStep : 1.0;
            if (step >= 1)
                return ((long)Math.Round(quantity)).ToString();
            return quantity.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static TradeSetup CloneSetup(TradeSetup source)
        {
            if (source == null)
                return new TradeSetup();
            return new TradeSetup
            {
                LossLimitUsd = source.LossLimitUsd,
                Entry = source.Entry,
                StopLoss = source.StopLoss,
                TakeProfit = source.TakeProfit,
                BreakEven = source.BreakEven,
                ShowEntry = source.ShowEntry,
                ShowStop = source.ShowStop,
                ShowTakeProfit = source.ShowTakeProfit,
                ShowBreakEven = source.ShowBreakEven,
                UseEntryLine = source.UseEntryLine,
                EntryColorArgb = source.EntryColorArgb,
                StopColorArgb = source.StopColorArgb,
                TakeProfitColorArgb = source.TakeProfitColorArgb,
                BreakEvenColorArgb = source.BreakEvenColorArgb,
                LineWidth = source.LineWidth,
                LineStyle = source.LineStyle,
                StopLocked = source.StopLocked,
                LockedStopFloor = source.LockedStopFloor,
                LockedStopTicks = source.LockedStopTicks,
                DrawDirection = source.DrawDirection
            };
        }
    }
}
