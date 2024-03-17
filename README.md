# LastEpoch Localization Patcher

A simple command line program to dump/patch the localizations of game [Last Epoch](https://store.steampowered.com/app/899770).
> [繁體中文翻譯請點這](https://forum.gamer.com.tw/Co.php?bsn=35693&sn=2598)

## Note

For disabling the CRC check, the program will patch the `"Last Epoch_Data\StreamingAssets\aa\catalog.json"` file.
And we will find it by `"../catalog.json"` relative to the bundle path.
So if your bundle file is not in the game folder, you need to copy the `catalog.json` to the relative path too.

## Usage

```sh
LELocalePatch <bundlePath> {dump|patch|patchFull} <folderPath|zipPath>
```

- `bundlePath`: The path of the bundle file.
	(e.g. @"Last Epoch_Data\StreamingAssets\aa\StandaloneWindows64\localization-string-tables-chinese(simplified)(zh)_assets_all.bundle")

- `actions`:
	- `dump`: Dump the localization in bundle to json files and save them to `folderPath`.
	- `patch`: Patch the localization from json files in `folderPath` to the bundle.
	- `patchFull`: Same as `patch` but throw an exception when any entry in bundle is not found in the json file (whenever exists or not).

- `folderPath`: Path of the folder/zipFile to dump or apply the json files. Missing files are ignored in `patch` mode (but not `pathFull`).

## Platforms

Tested on Windows.
Not tested on other platforms, but should work fine.

## Libraries

- [AssetsTools.NET](https://github.com/nesrak1/AssetsTools.NET) (MIT license)