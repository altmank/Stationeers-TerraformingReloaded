# Custom worlds

Terraforming Reloaded has no list of worlds. It reads whatever world is loaded, so a custom world
from the Workshop works without anything being added to the mod. This page is for world authors who
want to control how their world responds, and for players wondering why a custom world behaves the
way it does.

## What works on any world

- The finite planet: gas vented outdoors stays, air drawn in is taken from the planet.
- The planet size setting. It multiplies the `Volume` the world declares (the game's default is
  40,000,000,000 L, five million outdoor cells).
- `terraform reset confirm`, saving and loading, multiplayer sync, the sky.
- Sunlight for the temperature rule comes from the world's own orbit and its star's `SolarConstant`.

The mod stays off in a world marked `<IsTutorial Value="true"/>`, and in a world whose
`GlobalAtmosphere` declares a `Volume` of zero.

## What the mod fills in

The game adds a planet's temperature up from parts a world file may or may not contain, all inside
`<World><GlobalAtmosphere>`:

| Element | X axis | If the world leaves it out |
| --- | --- | --- |
| `Temperature` | sun angle, 0 overhead to 180 | Airless world: the mod supplies the temperature bare rock settles at for the world's sunlight, swinging +50 K by day and -60 K by night. World with air: **nothing**, it reads 0 K with or without the mod. Give a world with air a `Temperature` curve |
| `SolarRadiationTemperature` | 0-100 % of the orbit's sunlight range | No seasons from orbit distance. The mod adds none |
| `GHGTemperatureOffset` | greenhouse index of the air | The mod supplies a response measured from the world's starting air |
| `DensityOffset` | gas density, mmol per litre | The mod evens out the world's own day and night swing as the air thickens |

Whatever the mod fills in is **zero while the air is as the world ships it**: an untouched custom
world reads exactly what its author set. `terraform` in the console shows which parts are being
filled, the starting greenhouse index and density, and how many kelvin the mod is adding right now.

A world that starts hot under heavy greenhouse air (a starting greenhouse index above 5, fully from
10) is treated as bare rock plus the warming its starting air causes, so stripping that air cools it
toward what its sunlight alone supports. That is what makes a Venus-like custom world recoverable.

## Taking control of your world

Ship the curve yourself and the mod leaves that part alone, exactly as it does on Mars. Any `Day` or
`Night` block with at least one key counts. A flat curve at zero means "this world does not respond".

```xml
<GlobalAtmosphere>
  <Temperature>
    <Key Time="0" Value="261" InTangent="0" OutTangent="0" InWeight="0" OutWeight="0" WeightedMode="None" />
    <Key Time="180" Value="240" InTangent="0" OutTangent="0" InWeight="0" OutWeight="0" WeightedMode="None" />
  </Temperature>
  <GHGTemperatureOffset>
    <Day>
      <Key Time="-100" Value="-50" ... />
      <Key Time="0" Value="0" ... />
      <Key Time="100" Value="250" ... />
    </Day>
    <Night> ... </Night>
  </GHGTemperatureOffset>
  <DensityOffset>
    <Day> ... </Day>
    <Night> ... </Night>
  </DensityOffset>
  <Gas>
    <Gas Type="CarbonDioxide" Quantity="8.66" />
  </Gas>
  <Volume Value="40000000000" />
</GlobalAtmosphere>
```

Gas `Quantity` is moles per 8000 L outdoor cell. The shipped Mars world file
(`rocketstation_Data\StreamingAssets\Worlds\Mars2\Mars2.xml`) is a complete example of all four
curves, and `terraform curves export` writes the mod's default greenhouse and density curves in the
same shape, ready to paste into a world and adjust.

Note that a world which ships its own `GHGTemperatureOffset` gets exactly that curve and nothing
more: the hot-world treatment above applies only to the mod's filled-in response.

## Things to get right

- `<PlayableBody>` must name a celestial body that has an `<Orbit>`. The mod takes the sunlight at
  the middle of that orbit's range.
- A star that is not the Sun: set `<PrimaryBody SolarConstant="...">`.
- A world with air needs a `Temperature` curve (see the table).
- The game freezes a gas out of the air, all of it, the moment the planet is colder than that gas's
  freezing point plus 2 K, and rains it out below its boiling point. Carbon dioxide needs 220 K. A
  world whose nights are colder than that cannot hold CO2 in its starting air either.

## Players: tuning without a world edit

The default curves are one file for every world that lacks its own; see CURVES.md. The config has a
strength setting for each response.
