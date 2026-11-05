# Changelog

All notable changes to Actually Working Employees are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/), and this
project adheres to Semantic Versioning.

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
