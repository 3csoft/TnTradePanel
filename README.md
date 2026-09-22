# TN Trade Panel

> **⚠ EXPERIMENTAL — USE AT YOUR OWN RISK**
>
> This is a **strictly experimental** system. It has been tested **only** on **Rithmic futures**, specifically **MNQ**. Behavior on other brokers, data feeds, or symbols is unknown and unsupported.
>
> **We accept no liability** for trading losses, missed stops, software bugs, freezes, or any other damage arising from use of this software. The virtual SL is **not** a broker-side stop — if Quantower, the plugin, or the indicator stops updating, protection may fail. **Paper-trade first.** You alone are responsible for every order you send.

A dockable **risk-sized trade panel** for [Quantower](https://www.quantower.com), with draggable chart lines and a **virtual stop-loss** (no SL order — price touch flattens the account).

You place Entry / SL / TP lines on the chart, the panel sizes quantity from a USD loss limit, then **Enter** sends the order with TP attached. The SL stays as a managed line. Optional break-even (BE) line moves the SL to average entry when touched.

### Short demo

[![TN Trade Panel demo](https://img.youtube.com/vi/EEH_Ov1c80U/hqdefault.jpg)](https://youtu.be/EEH_Ov1c80U)

Watch on YouTube: [https://youtu.be/EEH_Ov1c80U](https://youtu.be/EEH_Ov1c80U)

## Features

- **Loss limit (USD)** — sizes quantity; also an **emergency flatten** if floating loss reaches the limit (even while dragging SL).
- **Lines** — place or clear Entry, SL, TP on the color-linked chart (indicator required).
- **LONG / SHORT** — draw direction; opposite side blocked while a position is open.
- **Entry on/off** — toggle Entry while SL/TP are already out; always snaps to current market.
- **Enter** — market / limit / stop entry with TP; SL remains a **virtual line** (no protective SL order).
- **Virtual SL** — when price touches the SL line, **all positions and orders** on the selected account are flattened.
- **SL risk lock** — after entry, SL cannot widen beyond initial risk from average entry (snaps back on drop).
- **BE line** — purple button; with an open position, drag into place; on touch, SL moves to avg entry ± offset ticks and the BE line is removed.
- **Scale-in** — only allowed when SL is already at/beyond prior entry (no open risk).
- **Panic** — close everything on the account and reset line state.
- **Ghost-order warning** — flashing alert under Panic when the account has no position but leftover working orders.
- **Account panel** — bottom toggle shows Balance, Cash on hand, and Daily PnL.
- **Line style** — colors, width, dash style in Settings.

Built-in chart Order Entry is not modified.

## Requirements

- Quantower **v1.146+** (built against v1.146.18)
- Linked chart **symbol** and a selected **account**
- Indicator **TN Trade Panel Lines** on the same (or color-linked) chart
- .NET 10 SDK only if you build from source

## Install

Quantower loads custom scripts from the **user Settings** tree (not Program Files). Typical Windows paths:

| Component | Destination |
| --- | --- |
| Plugin | `C:\Quantower\Settings\Scripts\plug-ins\TNTradePanel\` |
| Indicator | `C:\Quantower\Settings\Scripts\Indicators\TNTradePanelLines\` |

Create the folders if they do not exist. After copying, **restart Quantower**, then:

1. Open the panel: **Panels → TN Trade Panel**
2. Add the indicator on the chart: **Indicators → Custom → TN Trade Panel Lines**
3. Color-link the panel to the chart and pick an account

### Option A: pre-built files from `dist/` (no compilation)

This repository ships a **Release** build under [`dist/`](dist/). Layout mirrors Quantower’s folders:

```
dist/
  plug-ins/TNTradePanel/          → copy into ...\Settings\Scripts\plug-ins\TNTradePanel\
  Indicators/TNTradePanelLines/   → copy into ...\Settings\Scripts\Indicators\TNTradePanelLines\
```

Copy **everything** from each subfolder (`.dll`, `.deps.json`, and for the plugin the `HTML\` folder) **except** `dist/README.txt`.

PowerShell (from the cloned repo root):

```powershell
$scripts = "C:\Quantower\Settings\Scripts"
$plugin  = Join-Path $scripts "plug-ins\TNTradePanel"
$ind     = Join-Path $scripts "Indicators\TNTradePanelLines"

New-Item -ItemType Directory -Force -Path $plugin, $ind | Out-Null
Copy-Item -Path ".\dist\plug-ins\TNTradePanel\*" -Destination $plugin -Recurse -Force
Copy-Item -Path ".\dist\Indicators\TNTradePanelLines\*" -Destination $ind -Recurse -Force
```

If your Quantower profile lives elsewhere, change `$scripts`.

### Option B: build from source

```powershell
git clone https://github.com/3csoft/TnTradePanel.git
cd TnTradePanel
dotnet build TNTradePanel.slnx -c Release
```

Release builds write into your Quantower Scripts folders (see defaults below) **and** refresh `dist/`.

#### Maintainer: refresh `dist/` before pushing

```powershell
dotnet build TNTradePanel.slnx -c Release
git add dist/
git status
git commit -m "chore: refresh dist binaries for release"
```

#### Environment / defaults

| Property | Default | Purpose |
| --- | --- | --- |
| `QuantowerRoot` | `C:\Quantower` | Quantower install root |
| `QuantowerVersion` | `v1.146.18` | Platform folder under `TradingPlatform\` |
| `QuantowerBin` | `...\TradingPlatform\v1.146.18\bin` | Reference assemblies |
| `QuantowerScripts` | `...\Settings\Scripts` | Plugin / indicator output |

Override via MSBuild properties or by editing [`Directory.Build.props`](Directory.Build.props).

## How it works

```
Plugin  (TNTradePanel)
   ↕  TradeSetupHub  (shared state via AppDomain.SetData)
Indicator  (TNTradePanelLines)  ← must be on the chart for draw/drag
         ↓
   Core.Instance.PlaceOrder  (entry + TP; no SL order)
```

1. Press **LONG** or **SHORT**, then **Lines** to place levels; drag SL/TP/Entry on the chart.
2. Panel shows **Quantity** and **RR** from the loss limit and distances.
3. **Enter** sends the order. Entry/TP chart lines clear (they became real orders); SL stays locked.
4. Price touching the SL line → full account flatten + state reset.
5. **BE line** (optional) → on touch, SL jumps to average entry + offset ticks (Settings).

## Panel controls

| Control | Action |
| --- | --- |
| **Account** | Select trading account (or Account lookup) |
| **Entry: on/off** | Show/hide Entry line; with SL/TP out, places at current market |
| **LONG / SHORT** | Draw direction |
| **Lines** | Toggle Entry/SL/TP placement (yellow button) |
| **BE line** | Toggle break-even line (open position only); removed when hit |
| **Enter** | Place order |
| **Panic** | Flatten account + clear setup |
| **Ghost warning** | Flashes under Panic if orders remain with no open position |
| **Account [show]** | Expand/collapse Balance, Cash on hand, Daily PnL |

## Settings

- **Loss limit (USD)**
- **BE hit: SL = avg entry + X ticks**
- Entry / SL / TP / BE **line colors**, **width**, **style** (Solid, Dash, Dot, …)

## Troubleshooting

- **No lines / BE does nothing** — add **TN Trade Panel Lines** to the chart; status bar warns if the indicator is missing.
- **Wrong symbol / continuous vs dated** — panel and indicator match continuous roots (e.g. `MNQ`) to dated contracts (`MNQZ6.CME`).
- **SL widens again** — restart Quantower after updating DLLs so both plugin and indicator load the same build.
- **Quantity: 0** — raise the loss limit or tighten SL.

## Disclaimer

**Strictly experimental.** Tested only on **Rithmic futures (MNQ)**. Not financial advice. Trading involves substantial risk of loss. The authors and publishers **accept no responsibility** for any losses or damages. Virtual SL management depends on the panel/indicator running and receiving ticks — it is **not** a broker-side stop. **Test on a simulator before using on a live account.**

## License

Released under the [MIT License](LICENSE).
