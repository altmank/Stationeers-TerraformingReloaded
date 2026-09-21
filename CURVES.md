# Tuning the temperature response

How much a world warms or cools as you change its air is set by two curves. You can replace them
without rebuilding anything and without restarting the game.

## Which worlds this affects

| World | Greenhouse and density response comes from |
| --- | --- |
| Mars | The game's own world file. **These curves do nothing on Mars.** |
| Europa, Venus, Vulcan, custom worlds without their own | These curves. On a world that starts hot under greenhouse air (Venus, Vulcan) the warming side of the greenhouse curve is scaled up so that removing all of that air reaches bare-rock temperature |
| Moon, Mimas | These curves, plus a base temperature the mod works out from sunlight |
| Tutorials | Nothing. Tutorials are left as shipped |

A world only uses these curves for a response it ships without. If a later game update gives
Europa its own greenhouse curve, the game's wins and these step aside for Europa.

## Quick start

In the in-game console (F3):

```
terraform curves export
```

That writes the built-in curves to `BepInEx\config\TerraformingReloaded.curves.xml` inside the
game folder. It never overwrites a file that is already there. Edit the file, then:

```
terraform curves reload
```

The change applies at once. `terraform` shows the planet temperature and, on its "temperature
response" line, "adding N K now": the kelvin these curves add, so you can see what your
edit did. (The `greenhouse` and `density` parts it also lists are the game's own and read 0 on these
worlds.) To go back to the
built-in curves, delete the file and reload.

If the file cannot be read, the mod says so in the log and uses the built-in curves.

For a stronger or weaker response you do not need this file at all: the
**Greenhouse strength** and **Air density strength** settings in the mod's config apply at once.
Greenhouse strength multiplies the greenhouse response (on Venus and Vulcan only its cooling side,
see below). Air density strength sets how quickly thickening air evens out day and night: 0 is off,
above 1 it bites sooner, and air thick enough to end the swing ends it at any strength.

## The file

It is the same format as the `GlobalAtmosphere` block of a game world file, so anything you know
from editing worlds carries over.

```xml
<TerraformingCurves>
  <GHGTemperatureOffset>
    <Day>   <Key Time="..." Value="..." ... /> ... </Day>
    <Night> <Key Time="..." Value="..." ... /> ... </Night>
  </GHGTemperatureOffset>
  <DensityOffset>
    <Day>   ... </Day>
    <Night> ... </Night>
  </DensityOffset>
</TerraformingCurves>
```

Both blocks are required, each needs a `Day` and a `Night`, and each of those needs at least one
`Key`. A `Key` is one point on the curve:

| Attribute | Meaning |
| --- | --- |
| `Time` | The X position. What X means is below |
| `Value` | The Y position, in kelvin. How each curve uses it is below |
| `InTangent`, `OutTangent` | Slope arriving at and leaving the point. 0 is flat |
| `InWeight`, `OutWeight`, `WeightedMode` | How far the slope reaches. `WeightedMode` is `None`, `In`, `Out` or `Both` |

If you only want straight-ish lines, set the tangents and weights to 0 and `WeightedMode="None"`.
Past the first and last point the curve holds its end value.

`Day` applies while the sun is within 70 degrees of overhead, `Night` beyond 110 degrees, and the
game blends the two in between. Making `Day` and `Night` differ is how you get a day and night
temperature swing.

### GHGTemperatureOffset: X is the greenhouse index

The game adds up an index from every gas in the planet's air, using per-gas curves in its own
`rocketstation_Data\StreamingAssets\Data\terraforming.xml`. Carbon dioxide, volatiles and
pollutant push it up; oxygen and nitrogen pull it down. The index runs roughly from -100 to 100.

Built in: 0 K at index 0, rising to +250 K at index 100, falling to -50 K at index -100, the same
by day and by night.

### DensityOffset: X is how much gas there is

In millimoles per litre. For scale: Mars starts near 1.1, Vulcan near 7, Venus near 39, Europa
near 43, the Moon at 0.

Built in: thin air swings hard (+50 K by day, -60 K by night at zero) and thick air evens out
(the two cross near 34). That is why a thin atmosphere has brutal nights and a thick
one does not.

What the mod takes from this curve is the **gap between `Day` and `Night`** at the current density,
compared with the gap at the density the world started with. Half the starting gap means the world's
own day and night swing, whatever size it is, is halved (at strength 1); no gap means day and night read the same.
That is what lets thick air tame Vulcan, whose swing is hundreds of kelvin. Thinning the air below
where the world started never makes the swing bigger than the world ships with. The average of `Day`
and `Night` also shifts the mean temperature slightly, as a plain offset.

## Three things that will otherwise surprise you

**Your curves are measured from where the world started.** Every term the mod adds is zero while the
air is as the world ships it, whatever you put in the file, so an untouched world reads exactly as it
does without the mod and only your changes to the air move the temperature. It also means shifting a
whole curve up or down does nothing on a world with air that does not start hot; its shape is what
matters. The exceptions are the next two.

**Hot worlds scale the warming side of the greenhouse curve.** A world that starts under heavy
greenhouse air (a starting index above 5; Venus is 33, Vulcan 18) is read as bare rock plus the
warming that air causes. The mod scales the part of the greenhouse curve above 0 K so that removing
all of that air lands on the bare-rock temperature: by about 3.7 on Venus and 8 on Vulcan with the
built-in curve. `terraform` shows the figure as "greenhouse gain". Make the curve steeper near the
world's starting index and the gain shrinks to match, so the end point stays put and only the route
changes. The cooling side, below 0 K, is never scaled.

**On the Moon and Mimas the density curve also sets the starting day and night swing.** Those
worlds ship with no temperature at all, so the mod starts from the temperature a bare rock reaches
at that distance from the sun, then adds the density curve's value at zero: by default +50 K by
day and -60 K by night. Change the `Time="0"` points of `DensityOffset` and you change how harsh
an airless world's first air is. **Airless world reflectivity** in the config moves the bare-rock
temperature itself.

## Multiplayer

The curves are read by each player's own game. The host decides the planet's air; every client
works out the temperature from it with their own curves. Players who want to see the same outdoor
temperature need the same file.
