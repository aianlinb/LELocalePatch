# LastEpoch Localization Patcher

A simple command line program to dump/patch the localizations of game [Last Epoch](https://store.steampowered.com/app/899770).

## Note

For disabling the CRC check, the program will patch the `"Last Epoch_Data\StreamingAssets\aa\catalog.bin"` file.
And we will find it by `"../catalog.bin"` relative to the bundle path.
So if your bundle file is not in the game folder, you need to copy the `catalog.bin` to the relative path too.

## Usage

```sh
LELocalePatch <bundlePath> {export|import} <folderPath|zipPath>
```
or
```sh
LELocalePatch <bundlePath> translate <dictionaryFilePath>
```

- `bundlePath`: The path of the bundle file.
	(e.g. @"Last Epoch_Data\StreamingAssets\aa\StandaloneWindows64\localization-string-tables-chinese(simplified)(zh)_assets_all.bundle")

- `actions`:
	- `export`: Export the localization tables in bundle to json files and save them to `folderPath` or a zip file at `zipPath`.
	- `import`: Write the localization tables from json files in `folderPath` or a zip file at `zipPath` to the bundle.
	- `translate`: Similar to `import` but use a json dictionary file at `dictionaryFilePath` to translate the strings in localization tables instead of overwriting them with some values.

## Platforms

Tested on Windows.
Haven't been tested on other platforms, but should work fine.

## Example

[Traditional Chinese](https://github.com/aianlinb/LETraditionalChinese/tree/main/LETraditionalChinese)

## Libraries

- [AssetsTools.NET](https://github.com/nesrak1/AssetsTools.NET) (MIT license)