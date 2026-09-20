# Temperature design

Requirement 3: any world can be made habitable, harder worlds harder. This is the record of the gap
that blocked it, the options weighed, the rule now in `src/Patching/Climate.cs`, the adversarial
review it went through, and what the game itself says about it.

## The game's formula and its gap

`GlobalGasMix.GetGlobalGasMixTemperature(data, angle, percent)`:

    T = base(angle) + weather + sun distance(angle, percent) + greenhouse(index) + density(mmol/L)
        + latent / capacity + external / capacity

Only Mars ships the greenhouse and density curves (GAME-MODEL.md), so elsewhere air changes and
temperature cannot follow. v0.1.0 filled the two curves with Mars's, measured from the starting air.
That left two worlds out of reach (**MEASURED** in `tools/Balance`):

- **Venus** ships one flat 737 K and no greenhouse curve. Mars's curve credits Venus's whole starting
  load (index 33) with 118 K. Strip every greenhouse gas: still about 600 K. Bare-rock equilibrium at
  Venus's sunlight is about 300 K, so the shipped 737 K holds about 440 K the player could never remove.
- **Vulcan** ships 975 K at noon, 400 K at night, plus a day-only sun-distance term of up to +750 K.
  Additive density damping is worth 55 K against a swing of 575 to 1,300 K.

The old mod could do both because it replaced the game's temperature outright; its recipes leaned on
steam and liquid water, which worked only because it had no phase change.

## Options considered

| # | Option | Verdict |
| --- | --- | --- |
| 1 | **Anchored greenhouse**: read a hot world's shipped mean as equilibrium plus the warming of its starting air, and scale the response to match | **Built** |
| 2 | **Swing damping as a fraction of the world's own swing** | **Built** |
| 3 | Albedo from clouds and ice caps | Later. Gives water a job; feedback and runaway risk |
| 4 | Replace the game's temperature outright, as the old mod did | Rejected. Throws away the game's Mars tuning and weather coupling |
| 5 | Hand-made curves per world | Rejected as the mechanism: numbers to defend per world, stale on game updates, nothing for custom worlds. The curves file already lets a player do it |
| 6 | Leave Venus and Vulcan unreachable | Rejected by requirement 3 |

## The rule

For a world lacking its own greenhouse curve, density curve, or both (each part is independent; a
part the world ships is never touched). `g`, `s`, `m` come from the default curves (Mars's, editable):
`g(index)` the mean of the day and night greenhouse curves, `s(d)` = max(0, day - night) of the
density curve, `m(d)` their mean.

    shipped(angle, percent) = base(angle) + sun distance(angle, percent)
                              (airless with no base: equilibrium(mid-orbit sun, AirlessAlbedo) + density curve at 0)
    mean0   = ( shipped(0, 50) + shipped(180, 50) ) / 2
    warming = mean0 - equilibrium(mid-orbit sun, albedo 0.3)
    weight  = clamp( (index0 - 5) / 5, 0, 1 )
    gain    = 1 + weight * ( min(warming / g(index0), 10) / GhgResponseScale - 1 )     when warming > 0 and g(index0) > 0, else 1

    greenhouse = [ gain * ( max(g,0) - max(g0,0) ) + ( min(g,0) - min(g0,0) ) ] * GhgResponseScale
    damp       = clamp( s(d) / s(d0), 0, 1 ) ^ DensityResponseScale      (1 when s(d0) = 0)
    density    = ( shipped(angle, percent) - mean0 ) * (damp - 1) + ( m(d) - m(d0) ) * DensityResponseScale

    T = game's own answer + greenhouse + density        (+ the airless base where the game has none)

Mid-orbit sun is the middle of `OrbitalSimulation.System.GetSolarEnergy()`, the game's own range for
the world's orbit, so there is no per-world table and custom worlds work (WORLDS.md). Every term is
zero on untouched air; `Climate.Lookup` evaluates that at world start and switches the response off
for the world, with an error, if it is not.

`GhgResponseScale` is divided out of the gain on purpose. On a fully anchored world it therefore
cancels out of the warming side entirely: the end point stays at equilibrium once the greenhouse air
is gone, and so does every point on the way. Without that, lowering the strength below 0.95 made
Venus impossible and raising it sent a stripped Venus below 0 K: a setting that silently breaks a
world is a trap. `DensityResponseScale` is an exponent for the same reason: as a plain multiplier, anything under
0.96 left Vulcan a swing it could never lose. As an exponent, 0 is off, more bites sooner, and air
thick enough to end the swing ends it at any strength. On a hot world the greenhouse setting still scales the cooling side, and the curves file
reshapes the route. On worlds that are not anchored the setting scales the response directly.

On an airless world this reduces exactly to v0.1.0's rule. On Europa (index0 -8, no swing) it is
v0.1.0's rule. Venus gets gain 3.70, Vulcan 8.01.

### Hook

One postfix on the three-argument `GetGlobalGasMixTemperature` adds the terms to the game's result, so
weather, latent and external heat stay as the game computed them. The one-argument overload calls the
three-argument one (checked at load: `SelfTest.StillCalls`), so patching both would apply it twice.
`__instance` is the mix asked about, never assumed to be the planet: the new-game menu passes a fresh
starting mix and reads as shipped. A second postfix on `CacheTemperatureCurveOffsets` corrects
`AggregateTemperature`, which the game's debug window, `terraform` and LiveCheck read and which the
game adds up by itself. v0.1.0's three postfixes on one-line getters are gone.

## Adversarial review (2026-09-20) and what changed

An independent pass tried to break the first draft of this rule. Findings that changed it:

| Finding | Evidence | Change |
| --- | --- | --- |
| Unclamped damping multiplies the swing when air is thinned. Stripping Vulcan's fuel first (forced by 573 K auto-ignition) took nights to 0 K and vacuum days to 1,773 K; a world starting near 34 mmol/L got damp = 165 | model runs | `damp` clamped to 0..1 |
| The gain amplified coolants: +300 N2 on untouched Vulcan read 241 K, a fix with no removal | model runs | Gain applies to warming only |
| Cliff at the anchoring threshold (index 4.9 vs 5.1: 782 K vs 2,557 K) and unbounded gain as g(index0) nears 0 | model runs | Blend over index 5 to 10; gain capped at 10 |
| `AirlessAlbedo` moved a touched Venus or Vulcan by about 30 K mid-save | model runs | Anchor albedo fixed at 0.3 |
| A total-only postfix leaves the readout at stock | `PAS:390`, `TerraformCommand`, `LiveCheck` | Readout postfix |
| Per-world irradiance table in the design experiment | the experiment script, since removed | Game's orbit range |
| `_curves` and the cached entry could mix old and new for a tick after a reload | `Climate.Load` | Entry holds its curves; one immutable swap |
| **The simulator ignored phase change.** The planet tick freezes a whole gas in one tick below freezing + 2 K. CO2 needs 219.8 K, so "+285 CO2" on a 124 K Europa cannot work as written, and Mimas looked like a dead end | `PAS:436`, `Mole.cs:809` | Simulator now carries the game's freeze and boil rules (dumped from the game), and `path.py` checks a recipe can be reached. See below |

Checked and sound: untouched reads stock exactly; table ranges cover what Venus needs; the day-night
easing matches the game; eclipse does not touch temperature; the long method is not an inlining
candidate.

## What the game says (live, headless)

| Run | Result |
| --- | --- |
| Unmodded Mars through a fast day | Night 226.24 K, day 281.29 K at the site's sun angles (38 to 166 degrees). Model: 226.31 and 281.5. Mars nights sit 6 K above CO2's freezing threshold; at the far end of its orbit the model gives 221.3 K, still above |
| Europa with the mod, 20 mol per cell of CO2 added at 134 K | All of it out of the air within 5 ticks, snow scheduled, moving into the ice caps at 3,000 mol per tick planet-wide, latent heat +1.9 K. Base temperatures 133.05 and 124.0 K vs model 133.07 and 124.0 |
| Unmodded Vulcan through a fast day | Night 399.98 K, day peak 945 K at 19 degrees |
| **Venus with the mod, air set to the solver's recipe** (CO2 23, HCl 1, N2 22, O2 48 per cell) | **322.17 K day and night** (stock 737 K; model 322.3), readout equal to the real value, the acid stayed a gas |
| **Vulcan with the mod, air set to the solver's recipe** (CO2 27, H2 3, N2 99, O2 130, pollutant 1), a full fast day near the far end of its orbit | **273.97 K at night to 292.99 K at noon** (stock 400 to 945 K). Model at the same six sun angles: 273.94, 273.94, 281.62, 287.61, 291.55, 292.99. Largest gap 0.19 K. The anchored greenhouse, the proportional damping and the sun-distance term are all in that number |

The rows below were judged by `run.ps1 -Model`: the simulator is rebuilt from the air, sun angle and
orbit position the game reports at each sample and has to give the game's temperature within 0.5 K.

| Judged run | Largest gap | What it showed |
| --- | --- | --- |
| Europa, oxygen out, 80 volatiles per cell (the safe route's first waypoint) | 0.11 K | 224 to 234 K, and the volatiles stayed a gas all day |
| The Moon on its finished air (57 O2, 62 CO2, 2 pollutant) | 0.02 K | First live run of an airless world: the mod's bare-rock base and the damping of its swing behave as modelled (defect D4's path, exercised without incident) |
| Mimas with 20 helium and 5 volatiles per cell, held 25 K warm | 0.06 K | Nights 86.5 K, above the volatiles' 83.5 K threshold: **they stayed a gas**. The heat kick works as sized |
| Vulcan on the fire-safe recipe (19 CO2, 46 N2, 193 O2, 1 pollutant) | 0.16 K | 272.9 K at night to 293.4 K at noon at the far end of its orbit |
| Europa: 20 CO2 frozen out at 134 K, then warmed with 120 volatiles at tick 60 | 0.13 K | All 20 mol per cell came back into the air within 30 ticks (it was still liquid in the tank: 19.9 to 0) |
| The same, but warmed at tick 1,900, after 19.9 of the 20 mol per cell had moved into the ice caps | 0.07 K over 516 samples | At 274 K the caps melted back at exactly the game's rate, 1,000 mol per tick planet-wide (19.95 to 17.18 mol per cell in 700 ticks, the air gaining the same 2.78). This is the run that proves the ice caps read the planet's air (GAME-MODEL.md): read as the game leaves them they would have sat near 136 K and never melted |

## Phase change and reachability

`tools/Balance/planet.py` applies the game's phase rules to a settled planet, from constants the game
dumped (freezing point, minimum liquid pressure, boiling point by pressure, per gas). Freeze
thresholds (+2 K included): hydrogen 17, nitrogen 42, ozone 53, oxygen 58, volatiles 84, silanol 145,
pollutant 175, CO2 220, HCl 249, N2O 253, steam 275 K. Helium never freezes.

`tools/Balance/path.py` walks from the starting air to the recipe in steps where every addition has
to hold from its first puff, uses rain-out and freeze-out of unwanted gas as free removal (and checks
whether it would come back on the finished planet), finds temporary gases to get unstuck, and as a
last resort a heat kick: the game banks the heat of gas vented outdoors, the mod lets it fade and caps
it at 50 K, so venting gas warmer than the planet holds the planet warm while it lasts.

Results are in BALANCE.md. Open questions are in ASSUMPTIONS.md (S4, S5, S6, S8, S9) and ROADMAP.md.
