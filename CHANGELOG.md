# Changelog

All notable changes to Actually Working Employees are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/), and this
project adheres to Semantic Versioning.

## [1.3.1] - 2026-11-16

### Changed

- "Use vanilla balance" now says what it pauses: your speed, wage, maximum and timing changes. They come back when
  you switch it off.

## [1.3.0] - 2026-11-16

### Added

- The Employees app and all its settings are now in German on a German system. `Language` under `[DooDesch]` picks
  the language by hand.
- Other languages can be added as a file: copy the template from
  `UserData/DooDesch/Localization/ActuallyWorkingEmployees/` and translate the values.

## [1.2.1] - 2026-11-16

### Fixed

- The phone app no longer opens empty with "Something went wrong" on older MelonLoader versions such as 0.7.0.

## [1.2.0] - 2026-11-09

### Added

- New host setting "Players pass through employees": players walk through employees, for every player in the lobby.
- "Allow values outside the limits" switch in the settings app: number settings can go beyond their range after a
  warning.

### Fixed

- Handlers show every route up to Max routes. The route list scrolls once it has more than five.
- The employee capacity setting no longer warns about the Sweatshop from Based, and returns to Based's capacity when
  the override is off.

## [1.1.0] - 2026-11-06

### Added
- New setting "Work through 4 AM" (`WorkThroughEndOfDay`, off): employees keep working while the clock stands at 4 AM.
  Plants and station timers still wait until you sleep.

## [1.0.1] - 2026-11-05

### Fixed
- Employees teleported after endless partial walks no longer land on the roof above their target. They land on its
  floor, and one already on a roof comes back down.

## [1.0.0] - 2026-11-04

First public release. Not compatible with Harder Working Employees: remove `HarderWorkingEmployees.dll` from your Mods folder.

### Added
- Fixes for botanists, chemists, handlers and cleaners, running on the host.
- An employee that loops a failed walk or stands still while it should walk is moved to its target.
- A chemist that fills a cauldron counts as working and is not sent to a new task.
- Stations and slots an employee leaves locked are freed when it stops working, is fired or the night is skipped.
- Items an employee still carries go back to their source slot when the task is cut short.
- Idle chemists and cleaners say which station or bin holds them back.
- Path checks to stations are remembered for a few seconds.
- A phone app called Employees with every employee setting: walk speed, wages, signing fees, assignment limits and the time of each action for all four roles. Every value starts at the vanilla default.
- A Fast preset that doubles the work speed and raises the walk speed of all four roles.
- Handler routes: wait for a worthwhile load before a trip and choose the fullest, emptiest or nearest route first.
- A botanist option to leave pots alone that a sprinkler watered recently.
- Speed multipliers for plant and mushroom growth, drying racks, cauldrons, lab ovens and mixing stations.
- More employees than idle spots per property, and an employee limit you can change.
- Options to let employees walk through each other and through furniture.
- Problem badges, a Setup Doctor and idle time tracking on the Diagnostics page, all off by default.
- Co-op: only the host changes the values. Guests who have the mod see the host's values, locked.
- Settings are saved in `UserData/ActuallyWorkingEmployees.cfg`. On the first start the values from `MelonPreferences.cfg` are taken over.
