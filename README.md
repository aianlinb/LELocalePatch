# LastEpoch Localization Patcher

A simple command line program to dump/patch the localizations of game [Last Epoch](https://store.steampowered.com/app/899770).
> [繁中翻譯請點這](https://github.com/aianlinb/LELocalePatch/tree/main/Traditional%20Chinese)

## Note

The bundles of this game are protected by the CRC check, so you have to disable it by [AddressablesTools](https://github.com/nesrak1/AddressablesTools/releases) with the following command in your game installation folder to make any changes work.
```cmd
Example.exe patchcrc "Last Epoch_Data\StreamingAssets\aa\catalog.json"
```
This may need to be done every time the game is updated. (If it updates the catalog.json)

## Usage

```sh
LELocalePatch <bundlePath> {dump|patch|patchFull} <folderPath>
```

- `bundlePath`: The path of the bundle file.
	(e.g. @"Last Epoch_Data\StreamingAssets\aa\StandaloneWindows64\localization-string-tables-chinese(simplified)(zh)_assets_all.bundle")

- `actions`:
	- `dump`: Dump the localization in bundle to json files and save them to `folderPath`.
	- `patch`: Patch the localization from json files in `folderPath` to the bundle.
	- `patchFull`: Same as `patch` but throw an exception when any entry in bundle is not found in the json file.

- `folderPath`: The folder path to dump or apply the json files. Missing files are ignored in `patch` mode.

## Platforms

Tested on Windows.
For other platforms, you may need to build the `AddressablesTools` yourself.

## Libraries

- [AssetsTools.NET](https://github.com/nesrak1/AssetsTools.NET) (MIT license)