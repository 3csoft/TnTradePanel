using System.Collections;
using System.Drawing;
using System.IO;
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
        private bool accountInfoOpen;
        private bool scalpingOn;
        private bool maExitOn;
        private int maPeriod = 20;
        private int maType;
        private int lastMaExitPulse;
        private bool? lastGhostWarnVisible;
        private int hubSeq;
        private int nextDrawDirection = 1;
        private double? lastAppliedBreakEven;
        private int beOffsetTicks = 4;
        private System.Threading.Timer pollTimer;

        private double lossLimitUsd = 100;
        private bool fixedContractsMode;
        private double fixedContracts = 1;
        private bool allowMinOneContract;
        private double dailyMaxLossUsd;
        private int maxOpenContracts = 10;
        private bool tradeManagementOn;
        private double beAfterPct = 100;
        private double partial1LevelPct = 100;
        private double partial1ClosePct = 50;
        private double partial2LevelPct = 200;
        private double partial2ClosePct = 50;
        private bool mgmtActive;
        /// <summary>True once an open position was seen for this armed management cycle.</summary>
        private bool mgmtSawPosition;
        private double mgmtRiskDistance;
        private bool mgmtBeDone;
        private bool mgmtPartial1Done;
        private bool mgmtPartial2Done;
        private bool mgmtBusy;
        /// <summary>True after a partial order is sent, until the open quantity actually drops.</summary>
        private bool mgmtAwaitingFill;
        private double mgmtQtyBeforePartial;
        private bool dailyCloseBusy;
        /// <summary>Raised loss limit while a trade risks more than the configured amount; null = use lossLimitUsd.</summary>
        private double? effectiveLossLimitUsd;
        /// <summary>Shown next to a raised loss limit: "1-lot" or "fixed".</summary>
        private string effectiveLimitNote;
        private DateTime effectiveLossLimitSetUtc;
        private string lastLimitText;
        private DateTime? lossBreachSinceUtc;
        private DateTime? dailyBreachSinceUtc;
        private DateTime? lockOutConfirmUntilUtc;
        private DateTime? lock1hConfirmUntilUtc;
        private string lastLockOutUiState;
        private string lastLock1hUiState;
        private const double BreachConfirmMs = 300;
        private const int LockOutConfirmSeconds = 8;
        private static readonly TimeSpan Lock1hDuration = TimeSpan.FromHours(1);
        /// <summary>Prefix for a successful TryEnter result that carries a status note.</summary>
        private const string OkWithNote = "\u0001OK:";
        private static readonly object LockOutFileSync = new object();
        private static readonly string LockOutStorePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TNTradePanel",
            "daily-lockouts.txt");
        private static readonly string TimedLockStorePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TNTradePanel",
            "timed-lockouts.txt");
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

        public override Size DefaultSize => new Size(180, 1140);

        public override IList<SettingItem> Settings
        {
            get
            {
                IList<SettingItem> result = base.Settings;
                result.Add(new SettingItemSelectorLocalized("SizeMode",
                    new SelectItem("", this.fixedContractsMode ? 1 : 0),
                    new List<SelectItem>
                    {
                        new SelectItem("Risk amount", 0),
                        new SelectItem("Fix contracts", 1)
                    })
                {
                    Text = "Default size mode",
                    SortIndex = 9
                });
                result.Add(new SettingItemDouble("LossLimitUsd", this.lossLimitUsd)
                {
                    Text = "Default risk amount (USD)",
                    SortIndex = 10,
                    Minimum = 1,
                    Maximum = 1000000,
                    Increment = 10,
                    DecimalPlaces = 0
                });
                result.Add(new SettingItemInteger("FixedContracts", (int)Math.Max(1, Math.Round(this.fixedContracts)))
                {
                    Text = "Default fixed contracts",
                    SortIndex = 11,
                    Minimum = 1,
                    Maximum = 10000
                });
                result.Add(new SettingItemInteger("BeOffsetTicks", this.beOffsetTicks)
                {
                    Text = "BE hit: SL = avg entry + X ticks (also scalping SL offset)",
                    SortIndex = 12,
                    Minimum = 0,
                    Maximum = 200
                });
                result.Add(new SettingItemBoolean("AllowMinOneContract", this.allowMinOneContract)
                {
                    Text = "Allow 1 contract when quantity = 0 (raises the risk amount for that trade)",
                    SortIndex = 13
                });
                result.Add(new SettingItemDouble("DailyMaxLossUsd", this.dailyMaxLossUsd)
                {
                    Text = "Daily max loss (USD, 0 = off)",
                    SortIndex = 14,
                    Minimum = 0,
                    Maximum = 1000000,
                    Increment = 50,
                    DecimalPlaces = 0
                });
                result.Add(new SettingItemInteger("MaxOpenContracts", this.maxOpenContracts)
                {
                    Text = "Max open contracts (scale-in cap)",
                    SortIndex = 15,
                    Minimum = 1,
                    Maximum = 10000
                });
                result.Add(new SettingItemSelectorLocalized("MaType",
                    new SelectItem("", this.maType),
                    new List<SelectItem>
                    {
                        new SelectItem("SMA", 0),
                        new SelectItem("EMA", 1)
                    })
                {
                    Text = "MA exit type (chart timeframe)",
                    SortIndex = 16
                });
                result.Add(new SettingItemInteger("MaPeriod", this.maPeriod)
                {
                    Text = "MA exit period",
                    SortIndex = 17,
                    Minimum = 2,
                    Maximum = 500
                });
                result.Add(new SettingItemBoolean("TradeManagementOn", this.tradeManagementOn)
                {
                    Text = "Partial close & trade management",
                    SortIndex = 40
                });
                result.Add(new SettingItemDouble("BeAfterPct", this.beAfterPct)
                {
                    Text = "BE after (% of Entry↔SL, 0 = off)",
                    SortIndex = 41,
                    Minimum = 0,
                    Maximum = 1000,
                    Increment = 1,
                    DecimalPlaces = 0
                });
                result.Add(new SettingItemDouble("Partial1LevelPct", this.partial1LevelPct)
                {
                    Text = "1st exit level (% of Entry↔SL, 0 = off)",
                    SortIndex = 42,
                    Minimum = 0,
                    Maximum = 1000,
                    Increment = 1,
                    DecimalPlaces = 0
                });
                result.Add(new SettingItemDouble("Partial1ClosePct", this.partial1ClosePct)
                {
                    Text = "1st partial close (% of open qty, ceil)",
                    SortIndex = 43,
                    Minimum = 1,
                    Maximum = 100,
                    Increment = 1,
                    DecimalPlaces = 0
                });
                result.Add(new SettingItemDouble("Partial2LevelPct", this.partial2LevelPct)
                {
                    Text = "2nd exit level (% of Entry↔SL, 0 = off)",
                    SortIndex = 44,
                    Minimum = 0,
                    Maximum = 1000,
                    Increment = 1,
                    DecimalPlaces = 0
                });
                result.Add(new SettingItemDouble("Partial2ClosePct", this.partial2ClosePct)
                {
                    Text = "2nd partial close (% of open qty, ceil)",
                    SortIndex = 45,
                    Minimum = 1,
                    Maximum = 100,
                    Increment = 1,
                    DecimalPlaces = 0
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
                if (value.TryGetValue("SizeMode", out int sizeMode))
                    this.fixedContractsMode = sizeMode == 1;
                if (value.TryGetValue("LossLimitUsd", out double limit) && limit > 0)
                    this.lossLimitUsd = limit;
                if (value.TryGetValue("FixedContracts", out int fixedQty) && fixedQty >= 1)
                    this.fixedContracts = fixedQty;
                if (value.TryGetValue("BeOffsetTicks", out int beTicks) && beTicks >= 0)
                    this.beOffsetTicks = beTicks;
                if (value.TryGetValue("AllowMinOneContract", out bool allowMinOne))
                    this.allowMinOneContract = allowMinOne;
                if (value.TryGetValue("DailyMaxLossUsd", out double dailyMax) && dailyMax >= 0)
                    this.dailyMaxLossUsd = dailyMax;
                if (value.TryGetValue("MaxOpenContracts", out int maxOpen) && maxOpen >= 1)
                    this.maxOpenContracts = maxOpen;
                if (value.TryGetValue("MaType", out int maKind))
                    this.maType = maKind == 1 ? 1 : 0;
                if (value.TryGetValue("MaPeriod", out int maLen) && maLen >= 2)
                    this.maPeriod = Math.Min(500, maLen);
                bool wasTmOn = this.tradeManagementOn;
                if (value.GetItemByPath("TradeManagementOn") is SettingItemBoolean tmItem)
                    this.tradeManagementOn = (bool)tmItem.Value;
                else if (value.TryGetValue("TradeManagementOn", out bool tmOn))
                    this.tradeManagementOn = tmOn;
                if (value.TryGetValue("BeAfterPct", out double beAfter) && beAfter >= 0)
                    this.beAfterPct = beAfter;
                if (value.TryGetValue("Partial1LevelPct", out double p1Level) && p1Level >= 0)
                    this.partial1LevelPct = p1Level;
                if (value.TryGetValue("Partial1ClosePct", out double p1Close) && p1Close > 0)
                    this.partial1ClosePct = Math.Min(100, p1Close);
                if (value.TryGetValue("Partial2LevelPct", out double p2Level) && p2Level >= 0)
                    this.partial2LevelPct = p2Level;
                if (value.TryGetValue("Partial2ClosePct", out double p2Close) && p2Close > 0)
                    this.partial2ClosePct = Math.Min(100, p2Close);
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
                this.RefreshSizeUi();
                this.RefreshQtyUi();
                // Turning TM on with an already-open position: arm from current avg entry ↔ SL.
                if (this.tradeManagementOn && !wasTmOn)
                    this.TryArmTradeManagementFromOpenPosition();
                else if (!this.tradeManagementOn && wasTmOn)
                    this.ClearTradeManagement();
            }
        }

        public override void Initialize()
        {
            base.Initialize();
            this.Window.Browser.AddEventHandler("accountbutton", "onclick", this.OnAccountClick);
            this.Window.Browser.AddEventHandler("accountselect", "onchange", this.OnAccountSelectChanged);
            this.Window.Browser.AddEventHandler("riskmodebutton", "onclick", this.OnRiskModeClick);
            this.Window.Browser.AddEventHandler("fixmodebutton", "onclick", this.OnFixModeClick);
            this.Window.Browser.AddEventHandler("sizeinput", "oninput", this.OnSizeInputChanged);
            this.Window.Browser.AddEventHandler("sizeinput", "onchange", this.OnSizeInputChanged);
            this.Window.Browser.AddEventHandler("drawlinesbutton", "onclick", this.OnDrawLinesClick);
            this.Window.Browser.AddEventHandler("longbutton", "onclick", this.OnLongClick);
            this.Window.Browser.AddEventHandler("shortbutton", "onclick", this.OnShortClick);
            this.Window.Browser.AddEventHandler("entrytoggle", "onclick", this.OnEntryToggleClick);
            this.Window.Browser.AddEventHandler("bebutton", "onclick", this.OnBeClick);
            this.Window.Browser.AddEventHandler("scalpbutton", "onclick", this.OnScalpClick);
            this.Window.Browser.AddEventHandler("maexitbutton", "onclick", this.OnMaExitClick);
            this.Window.Browser.AddEventHandler("enterbutton", "onclick", this.OnEnterClick);
            this.Window.Browser.AddEventHandler("panicbutton", "onclick", this.OnPanicClick);
            this.Window.Browser.AddEventHandler("accountinfobutton", "onclick", this.OnAccountInfoToggle);
            this.Window.Browser.AddEventHandler("lock1hbutton", "onclick", this.OnLock1hClick);
            this.Window.Browser.AddEventHandler("lockoutbutton", "onclick", this.OnLockOutClick);

            this.RegisterService<LinkingPluginService>();
            this.pollTimer = new System.Threading.Timer(_ => this.PollHub(), null, 250, 250);

            this.PublishVisualSettings();
            this.RefreshSizeUi();
            this.RefreshLinkedLabels();
            this.RefreshEntryToggleUi();
            this.RefreshDirectionUi();
            this.RefreshDrawButtonUi();
            this.RefreshScalpUi();
            this.RefreshMaExitUi();
            this.RefreshQtyUi();
            this.RefreshAccountInfoUi(force: true);
            this.RefreshLockOutUi(force: true);
            this.RefreshLock1hUi(force: true);
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

        private void OnRiskModeClick(string elementId, object args)
        {
            this.CommitSizeInput();
            bool changed = this.fixedContractsMode;
            this.fixedContractsMode = false;
            this.PublishVisualSettings();
            this.RefreshSizeUi();
            this.RefreshQtyUi();
            if (changed)
                this.SetStatus("Size mode: risk amount.");
        }

        private void OnFixModeClick(string elementId, object args)
        {
            this.CommitSizeInput();
            bool changed = !this.fixedContractsMode;
            this.fixedContractsMode = true;
            this.PublishVisualSettings();
            this.RefreshSizeUi();
            this.RefreshQtyUi();
            if (changed)
                this.SetStatus("Size mode: fix contracts.");
        }

        private void OnSizeInputChanged(string elementId, object args)
        {
            double before = this.fixedContractsMode ? this.fixedContracts : this.lossLimitUsd;
            if (!this.CommitSizeInput())
            {
                this.RefreshSizeUi();
                return;
            }

            double after = this.fixedContractsMode ? this.fixedContracts : this.lossLimitUsd;
            if (Math.Abs(before - after) < 1e-6)
            {
                this.RefreshSizeUi();
                return;
            }

            this.PublishVisualSettings();
            this.RefreshSizeUi();
            this.RefreshQtyUi();
        }

        private bool CommitSizeInput()
        {
            try
            {
                NativeJavascriptResponse response = this.Window.Browser.GetHtmlValue("sizeinput", HtmlGetValueAction.GetProperty, "value");
                if (!TryParsePositive(response?.Result?.ToString(), out double value))
                    return false;
                return this.ApplySizeValue(value);
            }
            catch
            {
                return false;
            }
        }

        private bool ApplySizeValue(double value)
        {
            if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value))
                return false;

            if (this.fixedContractsMode)
            {
                double step = this.CurrentSymbol?.LotStep > 0 ? this.CurrentSymbol.LotStep : 1.0;
                double qty = Math.Floor((value + step * 1e-9) / step) * step;
                double maxLot = this.CurrentSymbol != null && this.CurrentSymbol.MaxLot > 0
                    ? this.CurrentSymbol.MaxLot
                    : 10000;
                if (qty > maxLot)
                    qty = Math.Floor(maxLot / step) * step;
                if (qty <= 0)
                    return false;
                this.fixedContracts = qty;
                return true;
            }

            if (value < 1)
                value = 1;
            if (value > 1000000)
                value = 1000000;
            this.lossLimitUsd = Math.Round(value, 2);
            return true;
        }

        private static bool TryParsePositive(string text, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            text = text.Trim().Replace(',', '.');
            return double.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out value)
                && value > 0
                && !double.IsNaN(value)
                && !double.IsInfinity(value);
        }

        private void RefreshSizeUi()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string mode = this.fixedContractsMode ? "fix" : "risk";
            string value = this.fixedContractsMode
                ? this.fixedContracts.ToString("0.##", inv)
                : this.lossLimitUsd.ToString("0.##", inv);
            try
            {
                this.Window.Browser.UpdateHtml(string.Empty, HtmlAction.InvokeJs,
                    "setSizeUi('" + mode + "','" + value + "')");
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
            this.RefreshAccountInfoUi(force: true);
            this.lockOutConfirmUntilUtc = null;
            this.lock1hConfirmUntilUtc = null;
            this.RefreshLockOutUi(force: true);
            this.RefreshLock1hUi(force: true);
            if (this.IsDayLockedOut())
                this.SetStatus("Account locked out for today — trading blocked.");
            else if (this.GetTimedLockUntilUtc(this.currentAccount).HasValue)
                this.SetStatus("Account locked for 1h — trading blocked.");
            else
                this.SetStatus("Account: " + selected.Name);
        }

        private void OnAccountInfoToggle(string elementId, object args)
        {
            this.accountInfoOpen = !this.accountInfoOpen;
            this.RefreshAccountInfoUi(force: true);
        }

        private void RefreshAccountInfoUi(bool force)
        {
            try
            {
                this.Window.Browser.UpdateHtml(string.Empty, HtmlAction.InvokeJs,
                    this.accountInfoOpen ? "setAccountInfo(true)" : "setAccountInfo(false)");
            }
            catch
            {
            }

            if (!this.accountInfoOpen && !force)
                return;

            string balance = "—";
            string cash = "—";
            string dayPnl = "—";

            Account account = this.currentAccount;
            if (account != null)
            {
                try
                {
                    balance = FormatMoney(account.Balance, account);
                }
                catch
                {
                }

                cash = this.TryGetAccountAdditionalValue(account, exactOnly: true,
                    "Cash on hand", "CashOnHand", "Cash on Hand")
                    ?? "—";

                // Quantower Account Info "Net PnL" (not Open PnL / random Daily fields).
                dayPnl = this.TryGetAccountAdditionalValue(account, exactOnly: true,
                    "Net PnL", "NetPnL", "Net Profit/Loss", "Net P&L", "Net Profit Loss")
                    ?? this.TryGetAccountAdditionalValue(account, exactOnly: true,
                    "Total Profit/Loss", "Total PnL", "Daily PnL", "Day PnL")
                    ?? "—";
            }

            this.Window.Browser.UpdateHtml("accbalance", HtmlAction.SetInnerHtml, balance);
            this.Window.Browser.UpdateHtml("acccash", HtmlAction.SetInnerHtml, cash);
            this.Window.Browser.UpdateHtml("accdaypnl", HtmlAction.SetInnerHtml, dayPnl);
        }

        private string TryGetAccountAdditionalValue(Account account, bool exactOnly, params string[] keys)
        {
            if (account?.AdditionalInfo == null || keys == null || keys.Length == 0)
                return null;

            try
            {
                foreach (string key in keys)
                {
                    if (string.IsNullOrWhiteSpace(key))
                        continue;
                    if (account.AdditionalInfo.TryGetItem(key, out AdditionalInfoItem item)
                        && item?.Value != null)
                        return FormatAccountFieldValue(item.Value, account);
                }

                foreach (AdditionalInfoItem item in account.AdditionalInfo)
                {
                    if (item?.Value == null)
                        continue;
                    string id = item.Id ?? string.Empty;
                    string name = item.NameKey ?? string.Empty;
                    foreach (string key in keys)
                    {
                        if (string.IsNullOrWhiteSpace(key))
                            continue;
                        bool exact = id.Equals(key, StringComparison.OrdinalIgnoreCase)
                            || name.Equals(key, StringComparison.OrdinalIgnoreCase);
                        if (exact)
                            return FormatAccountFieldValue(item.Value, account);

                        if (!exactOnly
                            && (id.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0
                                || name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0))
                            return FormatAccountFieldValue(item.Value, account);
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private string TryGetAccountAdditionalValue(Account account, params string[] keys)
        {
            return this.TryGetAccountAdditionalValue(account, exactOnly: false, keys);
        }

        private double? TryGetAccountAdditionalNumber(Account account, params string[] keys)
        {
            if (account?.AdditionalInfo == null)
                return null;

            try
            {
                foreach (string key in keys)
                {
                    if (account.AdditionalInfo.TryGetItem(key, out AdditionalInfoItem item) && item?.Value != null)
                        return ToDouble(item.Value);
                }

                foreach (AdditionalInfoItem item in account.AdditionalInfo)
                {
                    if (item?.Value == null)
                        continue;
                    foreach (string key in keys)
                    {
                        if (key.Equals(item.Id ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                            || key.Equals(item.NameKey ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                            return ToDouble(item.Value);
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static double? ToDouble(object value)
        {
            switch (value)
            {
                case double d: return double.IsNaN(d) || double.IsInfinity(d) ? (double?)null : d;
                case float f: return f;
                case decimal m: return (double)m;
                case int i: return i;
                case long l: return l;
                case string s:
                    string cleaned = new string(s.Where(c => char.IsDigit(c) || c == '-' || c == '.' || c == ',').ToArray());
                    if (double.TryParse(cleaned, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double inv))
                        return inv;
                    if (double.TryParse(cleaned, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out double cur))
                        return cur;
                    return null;
                default:
                    try { return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture); }
                    catch { return null; }
            }
        }

        private string TryFormatOpenPositionsPnl(Account account)
        {
            try
            {
                double sum = 0;
                bool any = false;
                foreach (Position position in Core.Instance.Positions.Where(p =>
                    p != null && SameAccount(p.Account, account)))
                {
                    double? pnl = null;
                    try
                    {
                        // NetPnL / GrossPnL are PnLItem on many Quantower builds.
                        object net = position.GetType().GetProperty("NetPnL")?.GetValue(position)
                            ?? position.GetType().GetProperty("GrossPnL")?.GetValue(position);
                        if (net != null)
                        {
                            object value = net.GetType().GetProperty("Value")?.GetValue(net) ?? net;
                            if (value is double d)
                                pnl = d;
                            else if (value is float f)
                                pnl = f;
                            else if (value is decimal m)
                                pnl = (double)m;
                            else if (value != null && double.TryParse(value.ToString(),
                                System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out double parsed))
                                pnl = parsed;
                        }
                    }
                    catch
                    {
                    }

                    if (!pnl.HasValue)
                        pnl = this.EstimateOpenPnlUsd(position, null);

                    if (!pnl.HasValue)
                        continue;
                    sum += pnl.Value;
                    any = true;
                }

                return any ? FormatMoney(sum, account) : null;
            }
            catch
            {
                return null;
            }
        }

        private static string FormatAccountFieldValue(object value, Account account)
        {
            if (value == null)
                return "—";
            if (value is double d)
                return FormatMoney(d, account);
            if (value is float f)
                return FormatMoney(f, account);
            if (value is decimal m)
                return FormatMoney((double)m, account);
            if (value is int i)
                return FormatMoney(i, account);
            if (double.TryParse(value.ToString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double parsed))
                return FormatMoney(parsed, account);
            return value.ToString();
        }

        private static string FormatMoney(double value, Account account)
        {
            string ccy = null;
            try
            {
                ccy = account?.AccountCurrency?.Name;
            }
            catch
            {
            }

            string amount = value.ToString("N2", System.Globalization.CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(ccy) ? amount : amount + " " + ccy;
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

        private void OnScalpClick(string elementId, object args)
        {
            this.scalpingOn = !this.scalpingOn;
            this.PublishVisualSettings();
            this.RefreshScalpUi();

            if (!this.scalpingOn)
                this.SetStatus("Scalping: off — SL trailing stopped.");
            else if (TradeSetupHub.LineListeners <= 0)
                this.SetStatus("Scalping: on — but no indicator on the chart. Add: TN Trade Panel Lines.");
            else
                this.SetStatus("Scalping: on — in profit, SL trails below broken red candles (above green for short) − "
                    + this.beOffsetTicks + " ticks.");
        }

        private void OnMaExitClick(string elementId, object args)
        {
            this.maExitOn = !this.maExitOn;
            this.PublishVisualSettings();
            this.RefreshMaExitUi();
            this.lastLimitText = null;
            this.RefreshLimitUi();

            string kind = this.maType == 1 ? "EMA" : "SMA";
            if (!this.maExitOn)
                this.SetStatus("MA exit: off.");
            else if (TradeSetupHub.LineListeners <= 0)
                this.SetStatus("MA exit: on — but no indicator on the chart. Add: TN Trade Panel Lines.");
            else
                this.SetStatus("MA exit: on — flattens the account when price touches the chart "
                    + kind + " " + this.maPeriod + " (profit or loss).");
        }

        private void RefreshMaExitUi()
        {
            try
            {
                this.Window.Browser.UpdateHtml(string.Empty, HtmlAction.InvokeJs,
                    this.maExitOn ? "setMaExit(true)" : "setMaExit(false)");
            }
            catch
            {
            }
        }

        private void RefreshScalpUi()
        {
            try
            {
                this.Window.Browser.UpdateHtml(string.Empty, HtmlAction.InvokeJs,
                    this.scalpingOn ? "setScalp(true)" : "setScalp(false)");
            }
            catch
            {
            }
        }

        private void OnEnterClick(string elementId, object args)
        {
            if (this.enterBusy)
                return;
            this.enterBusy = true;
            try
            {
                string error = this.TryEnter();
                if (error != null && error.StartsWith(OkWithNote, StringComparison.Ordinal))
                    this.SetStatus(error.Substring(OkWithNote.Length));
                else
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

        private void OnLock1hClick(string elementId, object args)
        {
            if (this.currentAccount == null)
            {
                this.SetStatus("Select an account first.");
                return;
            }

            if (this.IsDayLockedOut())
            {
                this.RefreshLock1hUi(force: true);
                this.SetStatus("Already locked out for this account today.");
                return;
            }

            if (this.GetTimedLockUntilUtc(this.currentAccount).HasValue)
            {
                this.RefreshLock1hUi(force: true);
                this.SetStatus("Already locked for 1 hour on this account.");
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (!this.lock1hConfirmUntilUtc.HasValue || now > this.lock1hConfirmUntilUtc.Value)
            {
                this.lock1hConfirmUntilUtc = now.AddSeconds(LockOutConfirmSeconds);
                this.lockOutConfirmUntilUtc = null;
                this.RefreshLock1hUi(force: true);
                this.RefreshLockOutUi(force: true);
                this.SetStatus("Really lock trading for 1 hour on this account? Press again to confirm.");
                return;
            }

            this.lock1hConfirmUntilUtc = null;
            this.PersistTimedLock(this.currentAccount, DateTime.UtcNow.Add(Lock1hDuration));
            this.lastLimitText = null;
            this.RefreshLock1hUi(force: true);
            this.RefreshLimitUi();
            this.SetStatus("LOCKED 1H — no panel trading for this account for one hour.");
        }

        private void OnLockOutClick(string elementId, object args)
        {
            if (this.currentAccount == null)
            {
                this.SetStatus("Select an account first.");
                return;
            }

            if (this.IsDayLockedOut())
            {
                this.RefreshLockOutUi(force: true);
                this.SetStatus("Already locked out for this account today.");
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (!this.lockOutConfirmUntilUtc.HasValue || now > this.lockOutConfirmUntilUtc.Value)
            {
                this.lockOutConfirmUntilUtc = now.AddSeconds(LockOutConfirmSeconds);
                this.lock1hConfirmUntilUtc = null;
                this.RefreshLockOutUi(force: true);
                this.RefreshLock1hUi(force: true);
                this.SetStatus("Really lock out trading for this account for the rest of today? Press again to confirm.");
                return;
            }

            this.lockOutConfirmUntilUtc = null;
            this.PersistLockOut(this.currentAccount, DateTime.Today);
            this.lastLimitText = null;
            this.RefreshLockOutUi(force: true);
            this.RefreshLock1hUi(force: true);
            this.RefreshLimitUi();
            this.SetStatus("LOCKED OUT — no panel trading for this account until tomorrow.");
        }

        private void PollHub()
        {
            try
            {
                this.EnforceStopRiskCap();
                this.ReleaseEffectiveLossLimitIfFlat();
                this.CheckTradeManagement();
                this.CheckDailyLossLimit();
                this.CheckLossLimitBreach();
                this.CheckVirtualStopTouch();
                this.RefreshGhostOrderWarn();
                this.RefreshLimitUi();
                this.RefreshLockOutUi(force: false);
                this.RefreshLock1hUi(force: false);
                if (!TradeSetupHub.TryPull(ref this.hubSeq, out string command))
                    return;
                bool scalpStale;
                bool maExitNow = false;
                double? stop;
                lock (TradeSetupHub.Sync)
                {
                    this.beArmed = TradeSetupHub.Current.ShowBreakEven && TradeSetupHub.Current.BreakEven.HasValue;
                    // A payload posted by the indicator before it saw a toggle carries the old value.
                    scalpStale = TradeSetupHub.Current.ScalpingOn != this.scalpingOn
                        || TradeSetupHub.Current.ScalpOffsetTicks != this.beOffsetTicks
                        || TradeSetupHub.Current.MaExitOn != this.maExitOn
                        || TradeSetupHub.Current.MaType != this.maType
                        || TradeSetupHub.Current.MaPeriod != this.maPeriod;
                    stop = TradeSetupHub.Current.StopLoss;
                    int pulse = TradeSetupHub.Current.MaExitPulse;
                    if (pulse != this.lastMaExitPulse)
                    {
                        this.lastMaExitPulse = pulse;
                        maExitNow = this.maExitOn && this.AccountHasWork();
                    }
                }
                if (scalpStale)
                    this.PublishVisualSettings();
                if (maExitNow)
                    this.TryMaExit();
                if (command == TradeSetupHub.ScalpStopCommand && this.scalpingOn && stop.HasValue)
                    this.SetStatus("Scalp: SL → " + (this.CurrentSymbol?.FormatPrice(stop.Value)
                        ?? stop.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)));
                this.RefreshDrawButtonUi();
                this.RefreshQtyUi();
                if (this.accountInfoOpen)
                    this.RefreshAccountInfoUi(force: false);
            }
            catch
            {
            }
        }

        private void PublishVisualSettings()
        {
            this.ApplyLiveSizing();
            lock (TradeSetupHub.Sync)
            {
                TradeSetupHub.Current.EntryColorArgb = this.entryLineColor.ToArgb();
                TradeSetupHub.Current.StopColorArgb = this.stopLineColor.ToArgb();
                TradeSetupHub.Current.TakeProfitColorArgb = this.takeProfitLineColor.ToArgb();
                TradeSetupHub.Current.BreakEvenColorArgb = this.breakEvenLineColor.ToArgb();
                TradeSetupHub.Current.LineWidth = this.lineWidth;
                TradeSetupHub.Current.LineStyle = this.lineStyle;
                TradeSetupHub.Current.ScalpingOn = this.scalpingOn;
                TradeSetupHub.Current.ScalpOffsetTicks = this.beOffsetTicks;
                TradeSetupHub.Current.MaExitOn = this.maExitOn;
                TradeSetupHub.Current.MaType = this.maType;
                TradeSetupHub.Current.MaPeriod = this.maPeriod;
            }
            TradeSetupHub.NotifySetupChanged();
        }

        /// <summary>
        /// Writes the live size mode onto the shared setup. Returns true when the hub values changed.
        /// </summary>
        private bool ApplyLiveSizing()
        {
            lock (TradeSetupHub.Sync)
            {
                TradeSetup current = TradeSetupHub.Current;
                bool changed = current.LossLimitUsd != this.lossLimitUsd
                    || current.UseFixedContracts != this.fixedContractsMode
                    || Math.Abs(current.FixedContracts - this.fixedContracts) > 1e-6;
                current.LossLimitUsd = this.lossLimitUsd;
                current.UseFixedContracts = this.fixedContractsMode;
                current.FixedContracts = this.fixedContracts;
                return changed;
            }
        }

        private void RefreshQtyUi()
        {
            if (this.ApplyLiveSizing())
                TradeSetupHub.NotifySetupChanged();

            TradeSetup setup;
            lock (TradeSetupHub.Sync)
                setup = CloneSetup(TradeSetupHub.Current);

            PositionSizeResult size = this.CalculateSize(setup, out bool minOne, out bool qtyCapped);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string qtyText;
            if (size.Ok)
            {
                qtyText = "Quantity: " + FormatQty(this.CurrentSymbol, size.Quantity)
                    + (minOne ? " (min)" : string.Empty)
                    + (qtyCapped ? " (cap)" : string.Empty);
                if (this.fixedContractsMode && size.RiskPerContract > 0 && size.Quantity > 0)
                    qtyText += "<br>Risk: " + (size.RiskPerContract * size.Quantity).ToString("0.##", inv) + " USD";
            }
            else if (this.fixedContractsMode && IsSizingWaitingForLines(size))
                qtyText = "Quantity: " + FormatQty(this.CurrentSymbol, this.fixedContracts);
            else
                qtyText = "Quantity: —";
            string rrText = size.RewardRisk > 0
                ? "RR: " + size.RewardRisk.ToString("0.00", inv)
                : "RR: —";

            this.lastLimitText = null;
            this.RefreshLimitUi();
            this.Window.Browser.UpdateHtml("qtytext", HtmlAction.SetInnerHtml, qtyText);
            this.Window.Browser.UpdateHtml("rrtext", HtmlAction.SetInnerHtml, rrText);
        }

        private void RefreshLimitUi()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            // In risk-amount mode the input already shows this number. Keep the line when the
            // flatten limit differs (fixed size, or a raised limit for the open trade).
            string text = string.Empty;
            if (this.fixedContractsMode || this.effectiveLossLimitUsd.HasValue)
            {
                text = "Limit: " + this.EffectiveLossLimitUsd.ToString("0.##", inv) + " USD";
                if (this.effectiveLossLimitUsd.HasValue && !string.IsNullOrEmpty(this.effectiveLimitNote))
                    text += " (" + this.effectiveLimitNote + ")";
            }

            double openQty = this.GetOpenContractQuantity();
            if (text.Length > 0)
                text += "<br>";
            text += "Open: " + openQty.ToString("0.##", inv)
                + " / max " + this.maxOpenContracts.ToString(inv);

            if (this.dailyMaxLossUsd > 0)
            {
                double? day = this.GetDailyPnlUsd();
                text += "<br>Day: " + (day.HasValue ? day.Value.ToString("0.##", inv) : "—")
                    + " / -" + this.dailyMaxLossUsd.ToString("0.##", inv);
                if (this.IsDailyLimitReached(day))
                    text += "<br><b style=\"color:#ff4040\">DAILY LIMIT — BLOCKED</b>";
            }

            if (this.IsDayLockedOut())
                text += "<br><b style=\"color:#ff4040\">LOCK OUT — TODAY</b>";
            else
            {
                DateTime? until = this.GetTimedLockUntilUtc(this.currentAccount);
                if (until.HasValue)
                {
                    int mins = Math.Max(1, (int)Math.Ceiling((until.Value - DateTime.UtcNow).TotalMinutes));
                    text += "<br><b style=\"color:#ff9040\">LOCK 1H — " + mins + "m left</b>";
                }
            }

            if (this.maExitOn)
                text += "<br>MA exit: " + (this.maType == 1 ? "EMA " : "SMA ") + this.maPeriod.ToString(inv);

            text += "<br>" + this.BuildTradeManagementStatusHtml(inv);

            if (text == this.lastLimitText)
                return;
            this.lastLimitText = text;
            try
            {
                this.Window.Browser.UpdateHtml("limittext", HtmlAction.SetInnerHtml, text);
            }
            catch
            {
            }
        }

        private double EffectiveLossLimitUsd => this.effectiveLossLimitUsd ?? this.lossLimitUsd;

        private static bool IsSizingWaitingForLines(PositionSizeResult size)
        {
            if (size == null || string.IsNullOrEmpty(size.Error))
                return false;
            return size.Error.IndexOf("SL", StringComparison.OrdinalIgnoreCase) >= 0
                || size.Error.IndexOf("Entry", StringComparison.OrdinalIgnoreCase) >= 0
                || size.Error.IndexOf("direction", StringComparison.OrdinalIgnoreCase) >= 0
                || size.Error.IndexOf("symbol", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Normal risk sizing; if it yields 0 and the setting allows it, falls back to the
        /// smallest tradable lot (the trade's risk then exceeds the configured loss limit).
        /// Then caps quantity so total open contracts stay within MaxOpenContracts.
        /// </summary>
        private PositionSizeResult CalculateSize(TradeSetup setup, out bool minOne, out bool qtyCapped)
        {
            minOne = false;
            qtyCapped = false;
            PositionSizeResult size = PositionSizing.Calculate(this.CurrentSymbol, setup);
            if (size.Ok || this.fixedContractsMode || !this.allowMinOneContract || size.RiskPerContract <= 0 || this.CurrentSymbol == null)
            {
                if (size.Ok)
                    this.ApplyMaxOpenContractsCap(size, out qtyCapped);
                return size;
            }

            double lot = this.CurrentSymbol.MinLot > 0
                ? this.CurrentSymbol.MinLot
                : (this.CurrentSymbol.LotStep > 0 ? this.CurrentSymbol.LotStep : 1.0);
            size.Quantity = lot;
            size.Ok = true;
            size.Error = null;
            minOne = true;
            this.ApplyMaxOpenContractsCap(size, out qtyCapped);
            if (!size.Ok)
                minOne = false;
            return size;
        }

        private void ApplyMaxOpenContractsCap(PositionSizeResult size, out bool qtyCapped)
        {
            qtyCapped = false;
            if (size == null || !size.Ok || this.CurrentSymbol == null)
                return;

            double open = this.GetOpenContractQuantity();
            double remaining = this.maxOpenContracts - open;
            double lotStep = this.CurrentSymbol.LotStep > 0 ? this.CurrentSymbol.LotStep : 1.0;
            double minLot = this.CurrentSymbol.MinLot > 0 ? this.CurrentSymbol.MinLot : lotStep;
            if (remaining < minLot - 1e-9)
            {
                size.Ok = false;
                size.Quantity = 0;
                size.Error = "Max open contracts reached ("
                    + open.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                    + " / " + this.maxOpenContracts + ").";
                return;
            }

            double free = Math.Floor(remaining / lotStep) * lotStep;
            if (free < minLot - 1e-9)
            {
                size.Ok = false;
                size.Quantity = 0;
                size.Error = "Max open contracts reached ("
                    + open.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                    + " / " + this.maxOpenContracts + ").";
                return;
            }

            if (size.Quantity > free + 1e-9)
            {
                size.Quantity = free;
                qtyCapped = true;
            }
        }

        private double GetOpenContractQuantity()
        {
            if (this.CurrentSymbol == null || this.currentAccount == null)
                return 0;
            return Core.Instance.Positions
                .Where(this.IsMine)
                .Sum(p => p.Quantity);
        }

        private void ReleaseEffectiveLossLimitIfFlat()
        {
            if (!this.effectiveLossLimitUsd.HasValue || this.currentAccount == null)
                return;
            // Grace period: the entry order may not be visible in Core yet right after PlaceOrder.
            if ((DateTime.UtcNow - this.effectiveLossLimitSetUtc).TotalSeconds < 5)
                return;

            bool busy = Core.Instance.Positions.Any(p => p != null && SameAccount(p.Account, this.currentAccount))
                || Core.Instance.Orders.Any(o => o != null && SameAccount(o.Account, this.currentAccount));
            if (!busy)
            {
                this.effectiveLossLimitUsd = null;
                this.effectiveLimitNote = null;
            }
        }

        /// <summary>
        /// Broker Net PnL for the day; with open positions the broker's open part is swapped for
        /// a live tick-based estimate so the check does not lag the account updates.
        /// </summary>
        private double? GetDailyPnlUsd(double? lastPrice = null)
        {
            Account account = this.currentAccount;
            if (account == null)
                return null;

            double? net = this.TryGetAccountAdditionalNumber(account,
                "Net PnL", "NetPnL", "Net Profit/Loss", "Net P&L", "Net Profit Loss");
            if (!net.HasValue)
                return null;

            Position[] positions = Core.Instance.Positions
                .Where(p => p != null && SameAccount(p.Account, account))
                .ToArray();
            if (positions.Length == 0)
                return net;

            double? brokerOpen = this.TryGetAccountAdditionalNumber(account,
                "OpenProfit/Loss", "Open Profit/Loss", "Open PnL", "OpenPnL");
            if (!brokerOpen.HasValue)
                return net;

            double liveOpen = 0;
            foreach (Position position in positions)
                liveOpen += this.EstimateOpenPnlUsd(position, lastPrice) ?? 0;

            return net.Value - brokerOpen.Value + liveOpen;
        }

        private bool IsDailyLimitReached(double? dailyPnl)
        {
            return this.dailyMaxLossUsd > 0 && dailyPnl.HasValue && dailyPnl.Value <= -this.dailyMaxLossUsd;
        }

        private void CheckDailyLossLimit()
        {
            if (this.dailyMaxLossUsd <= 0 || this.currentAccount == null)
                return;
            if (this.panicBusy || this.slCloseBusy || this.lossLimitCloseBusy || this.dailyCloseBusy)
                return;

            double? day = this.GetDailyPnlUsd();
            if (!this.IsDailyLimitReached(day))
            {
                this.dailyBreachSinceUtc = null;
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (!this.dailyBreachSinceUtc.HasValue)
            {
                this.dailyBreachSinceUtc = now;
                return;
            }
            if ((now - this.dailyBreachSinceUtc.Value).TotalMilliseconds < BreachConfirmMs)
                return;

            bool busy = Core.Instance.Positions.Any(p => p != null && SameAccount(p.Account, this.currentAccount))
                || Core.Instance.Orders.Any(o => o != null && SameAccount(o.Account, this.currentAccount));
            if (!busy)
                return;

            this.dailyBreachSinceUtc = null;
            this.dailyCloseBusy = true;
            this.panicBusy = true;
            try
            {
                FlattenResult flat = this.FlattenAccount();
                this.ResetSetupState();
                this.SetStatus("Daily max loss hit (" + day.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                    + " USD) — account flat: " + flat.Closed + " positions, " + flat.Cancelled
                    + " orders cancelled. Trading blocked.");
            }
            finally
            {
                this.panicBusy = false;
                this.dailyCloseBusy = false;
            }
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
            this.CheckTradeManagement(last?.Price);
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
                // Widen limit stays the stop captured at the last entry; BE only moves the line.
                TradeSetupHub.Current.ShowBreakEven = false;
                TradeSetupHub.Current.BreakEven = null;
            }

            this.beArmed = false;
            this.lastAppliedBreakEven = null;
            TradeSetupHub.NotifySetupChanged();
            this.RefreshDrawButtonUi();
            this.SetStatus("BE hit: SL → entry + " + this.beOffsetTicks + " ticks. BE line removed.");
        }

        /// <summary>
        /// Indicator snaps SL on DROP; panel also enforces the last-entry stop if that misses
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

                int direction = position.Side == Side.Buy ? 1 : -1;
                double? max = PositionSizing.WidestLockedStop(
                    this.CurrentSymbol, position.OpenPrice, direction, setup.LockedStopTicks, setup.LockedStopFloor);
                if (!max.HasValue)
                    return;

                capped = max.Value;
                bool widened = direction > 0
                    ? setup.StopLoss.Value < capped - tick * 0.5
                    : setup.StopLoss.Value > capped + tick * 0.5;
                if (!widened)
                    return;

                setup.StopLoss = capped;
            }

            TradeSetupHub.NotifySetupChanged();
            this.SetStatus("SL restored to entry stop: "
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
            double limit = this.EffectiveLossLimitUsd;
            if (limit <= 0)
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
            if (totalPnl > -limit)
            {
                this.lossBreachSinceUtc = null;
                return;
            }

            // Require the breach to persist so a single bad quote cannot flatten the account.
            DateTime now = DateTime.UtcNow;
            if (!this.lossBreachSinceUtc.HasValue)
            {
                this.lossBreachSinceUtc = now;
                return;
            }
            if ((now - this.lossBreachSinceUtc.Value).TotalMilliseconds < BreachConfirmMs)
                return;

            this.lossBreachSinceUtc = null;
            this.CloseAllOnLossLimit(totalPnl);
        }

        private double? EstimateOpenPnlUsd(Position position, double? lastPrice)
        {
            if (position?.Symbol == null || position.OpenPrice <= 0 || position.Quantity <= 0)
                return null;

            int direction = position.Side == Side.Buy ? 1 : -1;
            // The position lives on the dated contract (MNQZ6) while orders go to the chart's
            // continuous symbol (MNQ); only the chart symbol is subscribed, the dated one can hold stale quotes.
            bool onChartSymbol = this.CurrentSymbol != null && SameSymbol(position.Symbol, this.CurrentSymbol);
            double market = onChartSymbol ? (lastPrice ?? 0) : 0;
            if (market <= 0)
            {
                try
                {
                    Symbol symbol = onChartSymbol ? this.CurrentSymbol : position.Symbol;
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

            if (this.IsDayLockedOut())
                return "Lock Out active for this account today — trading blocked.";
            DateTime? timedUntil = this.GetTimedLockUntilUtc(this.currentAccount);
            if (timedUntil.HasValue)
            {
                int mins = Math.Max(1, (int)Math.Ceiling((timedUntil.Value - DateTime.UtcNow).TotalMinutes));
                return "Lock 1h active for this account (" + mins + "m left) — trading blocked.";
            }

            if (this.IsDailyLimitReached(this.GetDailyPnlUsd()))
                return "Daily max loss reached (" + this.dailyMaxLossUsd.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                    + " USD) — trading blocked.";

            lock (TradeSetupHub.Sync)
                TradeSetupHub.Current.UseEntryLine = this.entryCheckOn;
            this.ApplyLiveSizing();

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

            PositionSizeResult size = this.CalculateSize(setup, out bool minOne, out bool qtyCapped);
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
            this.ArmTradeManagement(Math.Abs(entry - stop));

            bool raisedLimit = false;
            if (size.RiskPerContract > 0 && size.Quantity > 0)
            {
                // Headroom of 2 ticks so slippage on the virtual SL does not trigger the limit first.
                double tradeRisk = size.RiskPerContract * size.Quantity + 2 * size.TickCost * size.Quantity;
                bool exceeds = minOne || (this.fixedContractsMode && tradeRisk > this.lossLimitUsd + 0.01);
                if (exceeds)
                {
                    double raisedTo = Math.Max(this.lossLimitUsd, tradeRisk);
                    if (!this.effectiveLossLimitUsd.HasValue || raisedTo > this.effectiveLossLimitUsd.Value + 0.01)
                    {
                        this.effectiveLossLimitUsd = Math.Max(raisedTo, this.effectiveLossLimitUsd ?? 0);
                        this.effectiveLossLimitSetUtc = DateTime.UtcNow;
                        this.effectiveLimitNote = minOne ? "1-lot" : "fixed";
                        raisedLimit = true;
                        this.lastLimitText = null;
                        this.RefreshLimitUi();
                    }
                }
            }

            if (minOne)
            {
                return OkWithNote + "Entry sent with minimum lot (quantity was 0). Loss limit raised to "
                    + this.EffectiveLossLimitUsd.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                    + " USD until flat.";
            }

            if (raisedLimit)
            {
                return OkWithNote + "Entry sent at fixed size. Loss limit raised to "
                    + this.effectiveLossLimitUsd.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                    + " USD until flat.";
            }
            if (qtyCapped)
            {
                return OkWithNote + "Entry sent — quantity capped to max open contracts ("
                    + FormatQty(this.CurrentSymbol, size.Quantity) + ").";
            }
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

        private bool AccountHasWork()
        {
            if (this.currentAccount == null)
                return false;
            return Core.Instance.Positions.Any(p => p != null && SameAccount(p.Account, this.currentAccount))
                || Core.Instance.Orders.Any(o => o != null && SameAccount(o.Account, this.currentAccount));
        }

        /// <summary>Same flatten as Panic. The MA exit button stays on for the next touch.</summary>
        private void TryMaExit()
        {
            if (this.currentAccount == null || this.panicBusy || this.slCloseBusy || this.lossLimitCloseBusy)
                return;

            this.panicBusy = true;
            try
            {
                FlattenResult flat = this.FlattenAccount();
                this.ResetSetupState();
                string kind = this.maType == 1 ? "EMA" : "SMA";
                if (flat.Errors.Count > 0)
                    this.SetStatus("MA exit (" + kind + " " + this.maPeriod + "): "
                        + flat.Closed + " positions, " + flat.Cancelled + " orders. " + flat.Errors[0]);
                else
                    this.SetStatus("MA exit (" + kind + " " + this.maPeriod + "): account flat — "
                        + flat.Closed + " positions, " + flat.Cancelled + " orders.");
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
            this.ClearTradeManagement();
            this.nextDrawDirection = 1;
            this.lastGhostWarnVisible = null;
            this.RefreshDirectionUi();
            this.RefreshDrawButtonUi();
            this.RefreshGhostOrderWarn();
        }

        private void ArmTradeManagement(double riskDistance)
        {
            if (!this.tradeManagementOn || riskDistance <= 0)
                return;

            // Keep the first arm of this trade; scale-ins do not reset progress flags.
            if (this.mgmtActive && this.mgmtRiskDistance > 0)
                return;

            this.mgmtActive = true;
            this.mgmtSawPosition = false;
            this.mgmtRiskDistance = riskDistance;
            this.mgmtBeDone = this.beAfterPct <= 0;
            this.mgmtPartial1Done = this.partial1LevelPct <= 0;
            this.mgmtPartial2Done = this.partial2LevelPct <= 0;
            this.mgmtBusy = false;
            this.mgmtAwaitingFill = false;
            this.mgmtQtyBeforePartial = 0;
            this.lastLimitText = null;
        }

        /// <summary>
        /// Arms management from the live position and Entry↔SL risk (e.g. TM toggled on mid-trade).
        /// Falls back to LockedStopTicks when the SL was already pulled to entry for scale-in.
        /// </summary>
        private void TryArmTradeManagementFromOpenPosition()
        {
            if (!this.tradeManagementOn || (this.mgmtActive && this.mgmtRiskDistance > 0))
                return;

            Position position = this.FindPosition();
            if (position == null || position.OpenPrice <= 0)
                return;

            double? risk = this.ResolveManagementRiskDistance(position);
            if (!risk.HasValue || risk.Value <= 0)
                return;

            this.ArmTradeManagement(risk.Value);
            this.mgmtSawPosition = true;
        }

        private double? ResolveManagementRiskDistance(Position position)
        {
            if (position == null || position.OpenPrice <= 0)
                return null;

            double entry = position.OpenPrice;
            double tick = position.Symbol?.TickSize > 0
                ? position.Symbol.TickSize
                : (this.CurrentSymbol?.TickSize > 0 ? this.CurrentSymbol.TickSize : 0.25);
            double minRisk = tick * 0.5;

            double? stop;
            double lockedTicks;
            double? lockedFloor;
            lock (TradeSetupHub.Sync)
            {
                TradeSetup setup = TradeSetupHub.Current;
                stop = setup.StopLoss;
                lockedTicks = setup.LockedStopTicks;
                lockedFloor = setup.LockedStopFloor;
            }

            if (stop.HasValue)
            {
                double fromStop = Math.Abs(entry - stop.Value);
                if (fromStop >= minRisk)
                    return fromStop;
            }

            if (lockedFloor.HasValue)
            {
                double fromFloor = Math.Abs(entry - lockedFloor.Value);
                if (fromFloor >= minRisk)
                    return fromFloor;
            }

            if (lockedTicks > 0)
                return lockedTicks * tick;

            return null;
        }

        private void ClearTradeManagement()
        {
            this.mgmtActive = false;
            this.mgmtSawPosition = false;
            this.mgmtRiskDistance = 0;
            this.mgmtBeDone = false;
            this.mgmtPartial1Done = false;
            this.mgmtPartial2Done = false;
            this.mgmtBusy = false;
            this.mgmtAwaitingFill = false;
            this.mgmtQtyBeforePartial = 0;
            this.lastLimitText = null;
        }

        private string BuildTradeManagementStatusHtml(System.Globalization.CultureInfo inv)
        {
            if (!this.tradeManagementOn)
                return "TM: off";

            Position position = this.FindPosition();
            if (!this.mgmtActive || this.mgmtRiskDistance <= 0)
            {
                if (position != null)
                    return "<b style=\"color:#ff9040\">TM: on — not armed</b>";
                return "TM: on — idle";
            }

            string flags = (this.mgmtBeDone ? "BE✓" : "BE")
                + " " + (this.mgmtPartial1Done ? "P1✓" : "P1")
                + " " + (this.mgmtPartial2Done ? "P2✓" : "P2");

            if (position == null || position.OpenPrice <= 0)
                return "TM: armed " + flags;

            double? price = this.GetManagementMarkPrice(position, null);
            if (!price.HasValue)
                return "TM: armed " + flags;

            int direction = position.Side == Side.Buy ? 1 : -1;
            double prog = direction * (price.Value - position.OpenPrice) / this.mgmtRiskDistance * 100.0;
            string color = prog >= this.beAfterPct && this.beAfterPct > 0 ? "#40c070" : "#d0d0d0";
            return "<b style=\"color:" + color + "\">TM: " + prog.ToString("0.#", inv) + "%R</b> " + flags;
        }

        private double? GetManagementMarkPrice(Position position, double? lastPrice)
        {
            if (lastPrice.HasValue && lastPrice.Value > 0)
                return lastPrice.Value;

            int direction = position.Side == Side.Buy ? 1 : -1;
            try
            {
                Symbol symbol = position.Symbol ?? this.CurrentSymbol;
                if (symbol == null)
                    return null;
                if (direction > 0 && symbol.Bid > 0)
                    return symbol.Bid;
                if (direction < 0 && symbol.Ask > 0)
                    return symbol.Ask;
                if (symbol.Last > 0)
                    return symbol.Last;
            }
            catch
            {
            }

            return null;
        }

        /// <summary>
        /// Auto BE + two partial exits from Entry↔SL distance (%R). Disabled when Trade Management is off.
        /// Partial qty uses ceil so 1 contract × 50% still closes 1.
        /// </summary>
        private void CheckTradeManagement(double? lastPrice = null)
        {
            if (!this.tradeManagementOn || this.mgmtBusy
                || this.panicBusy || this.slCloseBusy || this.lossLimitCloseBusy)
                return;

            // Mid-trade enable: arm once a position + SL/risk exist.
            if (!this.mgmtActive || this.mgmtRiskDistance <= 0)
            {
                this.TryArmTradeManagementFromOpenPosition();
                if (!this.mgmtActive || this.mgmtRiskDistance <= 0)
                    return;
            }

            Position position = this.FindPosition();
            if (position == null || position.OpenPrice <= 0 || position.Quantity <= 0)
            {
                // Right after Enter the fill may not be in Core yet — do not disarm.
                if (this.mgmtSawPosition)
                    this.ClearTradeManagement();
                return;
            }

            this.mgmtSawPosition = true;

            double? price = this.GetManagementMarkPrice(position, lastPrice);
            if (!price.HasValue)
                return;

            int direction = position.Side == Side.Buy ? 1 : -1;
            double progressPct = direction * (price.Value - position.OpenPrice) / this.mgmtRiskDistance * 100.0;
            if (progressPct < 0)
                return;

            if (this.mgmtAwaitingFill)
            {
                if (Math.Abs(position.Quantity - this.mgmtQtyBeforePartial) > 1e-6)
                    this.mgmtAwaitingFill = false;
            }

            if (!this.mgmtBeDone && this.beAfterPct > 0 && progressPct + 1e-9 >= this.beAfterPct)
                this.ApplyTradeManagementBe(position);

            // One partial per update. The next level waits until this fill shows up in the position,
            // otherwise a fast move through both targets would size the second close off the old quantity.
            if (this.mgmtAwaitingFill)
                return;

            if (!this.mgmtPartial1Done && this.partial1LevelPct > 0 && progressPct + 1e-9 >= this.partial1LevelPct)
            {
                if (this.ApplyTradeManagementPartial(position, 1, this.partial1ClosePct))
                    return;
            }

            position = this.FindPosition();
            if (position == null || position.Quantity <= 0)
            {
                this.ClearTradeManagement();
                return;
            }

            if (!this.mgmtPartial2Done && this.partial2LevelPct > 0 && progressPct + 1e-9 >= this.partial2LevelPct)
                this.ApplyTradeManagementPartial(position, 2, this.partial2ClosePct);
        }

        private void ApplyTradeManagementBe(Position position)
        {
            double tick = position.Symbol?.TickSize > 0
                ? position.Symbol.TickSize
                : (this.CurrentSymbol?.TickSize > 0 ? this.CurrentSymbol.TickSize : 0.25);
            int dir = position.Side == Side.Buy ? 1 : -1;
            double beStop = PositionSizing.RoundToTick(
                position.Symbol ?? this.CurrentSymbol,
                position.OpenPrice + dir * this.beOffsetTicks * tick);

            lock (TradeSetupHub.Sync)
            {
                TradeSetupHub.Current.ShowStop = true;
                TradeSetupHub.Current.StopLoss = beStop;
                TradeSetupHub.Current.StopLocked = true;
                TradeSetupHub.Current.LockedStopFloor = beStop;
                // Keep the original risk ticks for widen-cap; only move the visible SL to BE.
                if (TradeSetupHub.Current.LockedStopTicks <= 0)
                    TradeSetupHub.Current.LockedStopTicks = this.beOffsetTicks;
            }

            this.mgmtBeDone = true;
            this.lastLimitText = null;
            TradeSetupHub.NotifySetupChanged();
            this.SetStatus("TM: BE after " + this.beAfterPct.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                + "% — SL → " + (this.CurrentSymbol?.FormatPrice(beStop) ?? beStop.ToString("0.####")));
        }

        /// <summary>Sends the partial and returns true when the order was accepted.</summary>
        private bool ApplyTradeManagementPartial(Position position, int step, double closePct)
        {
            double closeQty = this.CeilPartialQuantity(position, closePct);
            if (closeQty <= 0)
                return false;

            // ClosePosition / position.Close on Rithmic flatten the whole position and ignore the
            // quantity. A reducing market order for the exact lot count leaves the remainder.
            Symbol symbol = position.Symbol ?? this.CurrentSymbol;
            string orderTypeId = this.FindOrderTypeId(symbol, OrderTypeUsage.Order, OrderTypeBehavior.Market) ?? OrderType.Market;

            this.mgmtBusy = true;
            try
            {
                TradingOperationResult result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
                {
                    Account = position.Account,
                    Symbol = symbol,
                    Side = position.Side == Side.Buy ? Side.Sell : Side.Buy,
                    Quantity = closeQty,
                    OrderTypeId = orderTypeId,
                    TimeInForce = TimeInForce.GTC,
                    Comment = "TNTradePanel TM P" + step
                });

                if (result != null && result.Status == TradingOperationResultStatus.Success)
                {
                    if (step == 1)
                        this.mgmtPartial1Done = true;
                    else
                        this.mgmtPartial2Done = true;
                    this.mgmtQtyBeforePartial = position.Quantity;
                    this.mgmtAwaitingFill = closeQty + 1e-6 < position.Quantity;
                    this.lastLimitText = null;
                    this.SetStatus("TM: partial #" + step + " sent "
                        + FormatQty(symbol, closeQty)
                        + " of " + FormatQty(symbol, position.Quantity)
                        + " at "
                        + (step == 1 ? this.partial1LevelPct : this.partial2LevelPct).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                        + "%R.");
                    return true;
                }

                this.SetStatus("TM: partial #" + step + " failed — " + (result?.Message ?? "unknown error"));
                return false;
            }
            finally
            {
                this.mgmtBusy = false;
            }
        }

        private double CeilPartialQuantity(Position position, double closePct)
        {
            if (position == null || position.Quantity <= 0 || closePct <= 0)
                return 0;

            Symbol symbol = position.Symbol ?? this.CurrentSymbol;
            double lotStep = symbol != null && symbol.LotStep > 0 ? symbol.LotStep : 1.0;
            double raw = position.Quantity * (closePct / 100.0);
            double closeQty = Math.Ceiling(raw / lotStep - 1e-9) * lotStep;
            if (closeQty < lotStep)
                closeQty = lotStep;
            if (closeQty > position.Quantity)
                closeQty = position.Quantity;
            return closeQty;
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
                && SameAccount(position.Account, this.currentAccount);
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

        private void RefreshLockOutUi(bool force)
        {
            if (this.lockOutConfirmUntilUtc.HasValue && DateTime.UtcNow > this.lockOutConfirmUntilUtc.Value)
                this.lockOutConfirmUntilUtc = null;

            string state = this.IsDayLockedOut()
                ? "locked"
                : (this.lockOutConfirmUntilUtc.HasValue ? "confirm" : "idle");
            if (!force && state == this.lastLockOutUiState)
                return;

            this.lastLockOutUiState = state;
            try
            {
                this.Window.Browser.UpdateHtml(string.Empty, HtmlAction.InvokeJs,
                    "setLockOut('" + state + "')");
            }
            catch
            {
            }
        }

        private void RefreshLock1hUi(bool force)
        {
            if (this.lock1hConfirmUntilUtc.HasValue && DateTime.UtcNow > this.lock1hConfirmUntilUtc.Value)
                this.lock1hConfirmUntilUtc = null;

            DateTime? until = this.GetTimedLockUntilUtc(this.currentAccount);
            string state;
            string label = null;
            if (this.IsDayLockedOut())
            {
                state = "idle";
            }
            else if (until.HasValue)
            {
                state = "locked";
                int mins = Math.Max(1, (int)Math.Ceiling((until.Value - DateTime.UtcNow).TotalMinutes));
                label = "LOCKED 1H (" + mins + "m)";
            }
            else if (this.lock1hConfirmUntilUtc.HasValue)
            {
                state = "confirm";
            }
            else
            {
                state = "idle";
            }

            string uiKey = state + "|" + (label ?? string.Empty);
            if (!force && uiKey == this.lastLock1hUiState)
                return;

            this.lastLock1hUiState = uiKey;
            try
            {
                string js = string.IsNullOrEmpty(label)
                    ? "setLock1h('" + state + "')"
                    : "setLock1h('" + state + "','" + label.Replace("'", string.Empty) + "')";
                this.Window.Browser.UpdateHtml(string.Empty, HtmlAction.InvokeJs, js);
            }
            catch
            {
            }
        }

        private bool IsTradingLockedOut()
        {
            return this.IsDayLockedOut() || this.GetTimedLockUntilUtc(this.currentAccount).HasValue;
        }

        private bool IsDayLockedOut()
        {
            return this.IsDayLockedOut(this.currentAccount, DateTime.Today);
        }

        private bool IsDayLockedOut(Account account, DateTime localDay)
        {
            string key = DayLockOutKey(account, localDay);
            if (key == null)
                return false;
            return this.ReadDayLockOutKeys().Contains(key);
        }

        private void PersistLockOut(Account account, DateTime localDay)
        {
            string key = DayLockOutKey(account, localDay);
            if (key == null)
                return;

            lock (LockOutFileSync)
            {
                HashSet<string> keys = this.ReadDayLockOutKeysUnlocked();
                if (!keys.Add(key))
                    return;
                this.WriteLinesUnlocked(LockOutStorePath, keys);
            }
        }

        private void PersistTimedLock(Account account, DateTime untilUtc)
        {
            string accountKey = AccountLockKey(account);
            if (accountKey == null)
                return;

            lock (LockOutFileSync)
            {
                Dictionary<string, DateTime> map = this.ReadTimedLocksUnlocked();
                map[accountKey] = untilUtc.ToUniversalTime();
                this.WriteTimedLocksUnlocked(map);
            }
        }

        private DateTime? GetTimedLockUntilUtc(Account account)
        {
            string accountKey = AccountLockKey(account);
            if (accountKey == null)
                return null;

            lock (LockOutFileSync)
            {
                Dictionary<string, DateTime> map = this.ReadTimedLocksUnlocked();
                if (!map.TryGetValue(accountKey, out DateTime until))
                    return null;
                if (until <= DateTime.UtcNow)
                    return null;
                return until;
            }
        }

        private HashSet<string> ReadDayLockOutKeys()
        {
            lock (LockOutFileSync)
                return this.ReadDayLockOutKeysUnlocked();
        }

        private HashSet<string> ReadDayLockOutKeysUnlocked()
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(LockOutStorePath))
                    return keys;

                string today = FormatLockOutDay(DateTime.Today);
                foreach (string line in File.ReadAllLines(LockOutStorePath))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    // Drop expired days so the file does not grow forever.
                    if (!line.EndsWith("|" + today, StringComparison.OrdinalIgnoreCase))
                        continue;
                    keys.Add(line.Trim());
                }
            }
            catch
            {
            }

            return keys;
        }

        private Dictionary<string, DateTime> ReadTimedLocksUnlocked()
        {
            var map = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(TimedLockStorePath))
                    return map;

                DateTime now = DateTime.UtcNow;
                foreach (string raw in File.ReadAllLines(TimedLockStorePath))
                {
                    if (string.IsNullOrWhiteSpace(raw))
                        continue;
                    string line = raw.Trim();
                    int split = line.LastIndexOf('|');
                    if (split <= 0 || split >= line.Length - 1)
                        continue;
                    string accountKey = line.Substring(0, split);
                    string untilText = line.Substring(split + 1);
                    if (!DateTime.TryParse(
                            untilText,
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                            out DateTime until))
                        continue;
                    until = until.ToUniversalTime();
                    if (until <= now)
                        continue;
                    if (!map.TryGetValue(accountKey, out DateTime existing) || until > existing)
                        map[accountKey] = until;
                }
            }
            catch
            {
            }

            return map;
        }

        private void WriteTimedLocksUnlocked(Dictionary<string, DateTime> map)
        {
            try
            {
                string dir = Path.GetDirectoryName(TimedLockStorePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                IEnumerable<string> lines = map
                    .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kv => kv.Key + "|" + kv.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture));
                File.WriteAllLines(TimedLockStorePath, lines);
            }
            catch
            {
            }
        }

        private void WriteLinesUnlocked(string path, HashSet<string> keys)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllLines(path, keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
            }
            catch
            {
            }
        }

        private static string DayLockOutKey(Account account, DateTime localDay)
        {
            string accountKey = AccountLockKey(account);
            if (accountKey == null)
                return null;
            return accountKey + "|" + FormatLockOutDay(localDay);
        }

        private static string AccountLockKey(Account account)
        {
            if (account == null)
                return null;
            string connection = string.IsNullOrEmpty(account.ConnectionId) ? "_" : account.ConnectionId;
            string id = !string.IsNullOrEmpty(account.Id) ? account.Id : account.Name;
            if (string.IsNullOrEmpty(id))
                return null;
            return connection + "|" + id;
        }

        private static string FormatLockOutDay(DateTime localDay)
        {
            return localDay.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
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
                UseFixedContracts = source.UseFixedContracts,
                FixedContracts = source.FixedContracts,
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
                DrawDirection = source.DrawDirection,
                ScalpingOn = source.ScalpingOn,
                ScalpOffsetTicks = source.ScalpOffsetTicks,
                MaExitOn = source.MaExitOn,
                MaType = source.MaType,
                MaPeriod = source.MaPeriod,
                MaExitPulse = source.MaExitPulse
            };
        }
    }
}
