# Design notes

Everything needed to pick this project up cold: what it is for, how the game works underneath it,
what was found, what was decided and why, how it is verified, and what is still open.

| Document | Read it for |
| --- | --- |
| [GOALS.md](GOALS.md) | What the mod is for, the requirements it is held to, what is out of scope |
| [GAME-MODEL.md](GAME-MODEL.md) | How the game's dormant planetary atmosphere works, with file and line evidence |
| [DEFECTS.md](DEFECTS.md) | The defects switching it on exposes, each with evidence, fix and test status |
| [INTERACTIONS.md](INTERACTIONS.md) | The census: every game method that touches the planet or outdoor air, what each does, and which bypasses are fixed or accepted |
| [ARCHITECTURE.md](ARCHITECTURE.md) | How the mod is built, every patch, the rules that are easy to break |
| [VERIFICATION.md](VERIFICATION.md) | The tools, what each proves and cannot, and the hard-won facts about running the game headless |
| [BALANCE.md](BALANCE.md) | Pacing research: machine rates, habitability thresholds, cost per world, the planet-size presets |
| [ASSUMPTIONS.md](ASSUMPTIONS.md) | Every assumption and simplification in the models and the design, with its justification, what breaks if it is wrong, and how to settle it. Nothing is left out of a model without an entry here |
| [TEMPERATURE.md](TEMPERATURE.md) | The temperature rule: the gap in the game's formula, the options weighed, the rule as built, the reviews that changed it, and what the game says when it runs |
| [SETTINGS.md](SETTINGS.md) | Every config entry the mod binds, generated from `src/Plugin.cs`: section, key, label, full description, default |
| [STORMS.md](STORMS.md) | How storms respond to terraforming: the two rules, every threshold and where its number comes from, and why the measure is what it is |
| [ROADMAP.md](ROADMAP.md) | Open work in priority order, open questions, things deliberately not done |

Conventions used throughout:

- `D/` is a decompile of `Assembly-CSharp.dll` for game build 0.2.6428.27798 (made with `ilspycmd -p`).
  It is not in this repository and must not be. Line numbers are from that build and move.
- `S/` is `<game>\rocketstation_Data\StreamingAssets`.
- Claims are tagged where it matters: **MEASURED** (the game produced the number, live or by dump),
  **CODE** (read from the decompile or shipped XML), **ASSUMED** (a modelling choice), **UNVERIFIED**.
- Player-facing docs live in the repository root and ship in the mod folder: README.md, CURVES.md
  (tuning the response), WORLDS.md (custom worlds).
- A planet's air is quoted in moles per 8000 L outdoor cell, because that number does not change with
  planet size and is what a player sees.
