# Scoring (svai)

This document explains how the scoring pipeline in `StockScreener.Core` is intended to work and what each input parameter means.

> Note:
> - `Scoring.Compute(...)` produces a final `Score`.
> - `Scoring.Explain(...)` mirrors the same calculation but returns intermediate values so the CLI can show *why* a ticker scored the way it did.
> - The current implementation is intentionally a **toy heuristic** (not sector-normalized, not z-scored).

---

## Overview: what scoring does

For each ticker, the screener gathers:

- **Fundamentals** (`Fundamentals`)  valuation / quality metrics
- **Prices** (`IReadOnlyList<PriceBar>`)  to compute momentum
- **Options (optional)** (`OptionsSnapshot?`)  may be missing/unavailable
- **Macro snapshot** (`MacroSnapshot`)  fetched once per run (falls back to neutral values on errors)

Then scoring:

1. Computes **raw** scores for key factors:
   - Value
   - Quality
   - Momentum
   - Options
   - Macro
2. Multiplies each factor by a **weight** (`ScoringWeights`).
3. Sums them into a single **Total** score.

Conceptually:

`Total = Value*wV + Quality*wQ + Momentum*wM + Options*wO + Macro*wMacro`

---

## Factor 1: Value  "is this cheap for what it produces?"

The Value factor tries to reward companies that look inexpensive relative to earnings or cash generation.

### Inputs (typical)

From `Fundamentals`:

- **P/E (`Pe`)**
  - Meaning: price relative to earnings.
  - Why it matters: lower P/E is often interpreted as "cheaper".
  - Caveat: low P/E can also mean low growth or elevated risk.

- **EV/EBITDA (`EvToEbitda`)**
  - Meaning: enterprise value (equity + debt  cash) relative to EBITDA.
  - Why it matters: more capital-structure-neutral than P/E.
  - Interpretation: lower EV/EBITDA is usually "cheaper".

- **FCF Yield (`FcfYield`)**
  - Meaning: free cash flow divided by market cap (or equivalent).
  - Why it matters: higher yield means you pay less per unit of cash generation.
  - Interpretation: higher FCF yield is better.

- **P/B (`Pb`)**
  - Meaning: price relative to book value.
  - Why it matters: sometimes useful for asset-heavy businesses.
  - Caveat: not meaningful for many modern/asset-light businesses.

### Intuition

Value scoring is a coarse way of representing: *"If I buy this company today, how expensive is it relative to what it earns/produces?"*

---

## Factor 2: Quality  "is the business actually good?"

The Quality factor tries to reward profitable/efficient companies and penalize fragile balance sheets.

### Inputs

From `Fundamentals`:

- **ROIC (`Roic`)**
  - Meaning: Return on invested capital.
  - Why it matters: high ROIC often indicates superior capital allocation and business quality.
  - Interpretation: higher ROIC is better.

- **Gross Margin (`GrossMargin`)**
  - Meaning: (revenue  cost of goods) / revenue.
  - Why it matters: higher margins can imply pricing power and/or better unit economics.
  - Interpretation: higher gross margin is better.

- **Net Debt / EBITDA (`NetDebtToEbitda`)**
  - Meaning: leverage proxy; roughly the number of years of EBITDA to pay down net debt.
  - Why it matters: higher leverage increases downside risk and can be punished in higher-rate environments.
  - Interpretation: lower net debt/EBITDA is better.

### Intuition

Quality scoring is a coarse way of preferring:

- businesses that can *compound*, and
- avoiding "cheap for a reason" companies with leverage/profitability issues.

---

## Factor 3: Momentum  "is price action supportive?"

Momentum is computed from the historical daily closes (`PriceBar.Close`).

### Typical approach

A simple momentum proxy is a short-horizon return, e.g. ~20 trading days:

- `mom = (lastClose - close20DaysAgo) / close20DaysAgo`

### Meaning

- Positive momentum suggests the market has been bidding the stock up recently.
- Empirically, momentum can persist over intermediate horizons.

### Caveats

- Momentum is not a measure of business quality; its purely market behavior.
- In sudden regime changes or event-driven moves, momentum can flip quickly.

---

## Factor 4: Options (optional)  "what does the options market imply?"

Options scoring uses `OptionsSnapshot?` which can be `null`.

### Why its optional

- Options data can be expensive, entitlement-gated, or missing.
- The engine treats missing options as acceptable and is designed so the options factor becomes neutral-ish when unavailable.

### What options metrics typically represent

Depending on provider/snapshot shape, options data can reflect:

- **Implied volatility (IV):** priced uncertainty.
- **Put/call skew or ratios:** demand for downside protection vs upside speculation.
- **Front-month activity:** event/earnings risk.

### Caveats

- Options are noisy and often event-driven.
- Missing data is expected; this factor is usually weighted modestly.

---

## Factor 5: Macro  "is the broad environment favorable for this sector?"

Macro scoring uses:

- `MacroSnapshot` (fetched once per run)
- `Fundamentals.Sector`

The macro factor is intended to be a small heuristic called `MacroSectorTilt`.

### MacroSnapshot fields and their meaning

- **TenYearYield**  long-term rate level; affects discount rates / cost of capital
- **TwoTenSpread**  yield curve slope; proxy for growth expectations / recession risk
- **CpiYoY**  inflation pressure
- **Pmi**  business cycle indicator (50 ~= neutral)
- **Dxy**  dollar strength; can pressure exporters/commodities
- **Wti**  oil price; tailwind for energy, cost headwind for some sectors

### Fail-open behavior

If macro fetch fails, the screener falls back to neutral-ish defaults so macro contribution is near 0.

### Caveats

- Macro is the least stable factor here: its heuristic and can be regime dependent.

---

## Weights  what they mean and why they exist

`ScoringWeights` controls how important each factor is:

- `Value`
- `Quality`
- `Momentum`
- `Options`
- `Macro`

Weights allow changing the "personality" of the screener:

- More value-oriented: increase `Value`, decrease `Momentum`
- More trend-following: increase `Momentum`
- More macro-aware: increase `Macro` (use cautiously)

In the CLI, weights are read from configuration:

- `Scoring:Weights:Value`
- `Scoring:Weights:Quality`
- `Scoring:Weights:Momentum`
- `Scoring:Weights:Options`
- `Scoring:Weights:Macro`

---

## Why these parameters are used (and when they dont work)

These inputs map to common investing concepts:

- **Valuation** (P/E, EV/EBITDA, FCF yield)  how much you pay per unit of earnings/cash
- **Business quality** (ROIC, margins)  ability to sustainably generate returns
- **Balance sheet risk** (net debt / EBITDA)  fragility under stress
- **Momentum**  market trend persistence
- **Macro**  regime effects on different sectors
- **Options**  market-implied expectations (if available)

They can behave poorly when:

- Fundamentals are stale/wrong, sector classification is off, or data is missing.
- You compare across sectors without normalization.
- The market is in a regime shift (macro/momentum can flip quickly).

---

## Next: tying this document to the exact code

If you want this document to match *exact* equations and clamp ranges, update it directly from:

- `src/StockScreener.Core/Scoring.cs`

(Example: describe each raw sub-score mapping and its output range.)
