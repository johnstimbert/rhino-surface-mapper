# Rhino Surface Mapper — Beta 1

Rhino Surface Mapper is an independent companion tool for *Elite Dangerous* Commanders, designed to help map Surface Mining activity, keep track of explored areas and recorded deposits, and navigate back to points of interest.

![Rhino Surface Mapper main map](docs/images/main-map.jpg)

## Why Rhino Surface Mapper?

When Surface Mining and the Rhino SRV arrived in *Elite Dangerous*, I found it difficult to search for deposits systematically: avoiding repeated coverage, knowing which areas I had already investigated, and remembering where I had found deposits of interest. What started as a simple application to solve that problem gradually gained additional functionality that I hope will be useful to other Commanders.

## Download Beta 1

Download the complete Beta 1 ZIP from [GitHub Releases](https://github.com/pbgaspar/rhino-surface-mapper/releases).

Beta 1 is for 64-bit Windows 10 and Windows 11. Extract the complete ZIP to a writable folder, keep the extracted folder together, and run `RhinoSurfaceMapper.exe`. Do not run the executable from inside the ZIP, and avoid protected locations such as `Program Files`.

This is a portable Beta. It stores `options.json` and the `MAPAS/` directory beside the executable.

## Features

- Live SRV state, position, heading, and fuel telemetry from *Elite Dangerous* `Status.json`, with authoritative system identity from Elite Dangerous Journal data when required.
- Surface mapping with route tracks and search routes.
- Planetary Mining Deposits, rigs, and named markers.
- Radar coverage and radar pulse visualisation.
- Saving and reopening maps as JSON files.
- Map Library for browsing saved maps.
- Favourite and protection functionality, including mining-only behaviour for protected maps.
- Navigation to map targets and a transparent navigation overlay.
- Optional steering assistance using game telemetry and configured input conditions.
- Configurable map, overlay, radar, and navigation settings.
- Windows, Dark, and Light interface themes.

![Map Library](docs/images/map-library.jpg)

The Map Library provides a view of saved maps and their recorded Planetary Mining Deposits, routes, rigs, and markers.

### Navigation overlay

The navigation overlay can display navigation information while *Elite Dangerous* is running. Optional steering assistance can use valid game telemetry and configured input conditions.

![Navigation overlay and steering assistance](docs/images/navigation-overlay.gif)

The application does not detect obstacles. Radar input observation does not prove that the game successfully fired a pulse.

## Language

The interface supports English and Portuguese (Portugal). Language changes apply after restarting the application.

## Planned features

**Powerplay Surface Mining assistance** — planned integration of the already developed market-data core to help Commanders earn Powerplay merits by identifying the three most relevant Surface Mining commodities in the current system, together with stations showing demand and market prices.

## Development

The original application lives in [`python/`](python) and is being ported to .NET
(Clean Architecture, Blazor Hybrid UI); see the design document in [`docs/design/`](docs/design).

Rhino Surface Mapper is written in Python. Its desktop interface uses PySide6 and Qt, with selected static layouts defined in Qt Designer `.ui` files to make visual UI work easier while application behaviour and state remain in Python. The codebase separates presentation, mapping and domain logic, Elite Dangerous telemetry, and persistence and I/O. Automated tests use pytest.

Useful contribution areas include UI/UX, mapping and navigation, Elite Dangerous telemetry, translations and documentation, testing, and future Surface Mining and Powerplay functionality. See [`CONTRIBUTING.md`](CONTRIBUTING.md) for development guidance and contribution information.

## Feedback and contributions

Please report reproducible bugs and suggestions through [GitHub Issues](https://github.com/pbgaspar/rhino-surface-mapper/issues).

Contributions, documentation improvements, and translation updates are welcome. See [`CONTRIBUTING.md`](CONTRIBUTING.md) for contribution guidance.

## Author

Rhino Surface Mapper was created and is maintained by **CMDR Paulo Gaspar**, with development assistance from ChatGPT and AI coding tools.

Developed as an independent companion tool for the *Elite Dangerous* community.

## Licence and third-party components

Rhino Surface Mapper is licensed under the [GNU GPL-3.0](LICENSE).

Runtime component notices are listed in [`THIRD_PARTY_NOTICES.txt`](THIRD_PARTY_NOTICES.txt), with corresponding source information in [`SOURCE_AVAILABILITY.txt`](SOURCE_AVAILABILITY.txt).

Rhino Surface Mapper is an independent project and is not affiliated with or endorsed by Frontier Developments. *Elite Dangerous* and related marks belong to Frontier Developments plc.
