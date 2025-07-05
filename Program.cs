using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
#if DEBUG
using System.Runtime.ExceptionServices;
#endif
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace LELocalePatch {
	public static class Program {
		/// <summary>
		/// Entry point of the program.
		/// </summary>
		/// <remarks>
		/// Parses the command line <paramref name="args"/> and calls <see cref="Run"/>.
		/// Then outputs the success message or exception to the <see cref="Console"/>.
		/// </remarks>
		public static void Main(string[] args) {
			static void PrintUsage() {
				Console.WriteLine("Usage: LELocalePatch <bundlePath> {export|import} <folderPath|zipPath>");
				Console.WriteLine("   or: LELocalePatch <bundlePath> translate <dictionaryFilePath>");
			}

			if (args.Length != 3) {
				PrintUsage();
				if (args.Length == 0)
					goto pause;
				return;
			}

			try {
				Mode? mode = args[1].ToLowerInvariant() switch {
					"export" => Mode.Export,
					"import" => Mode.Import,
					"translate" => Mode.Translate,
					_ => null
				};
				if (mode.HasValue)
					Run(args[0], args[2], mode.Value);
				else
					PrintUsage();
				return; // Do not pause if success
			} catch (Exception ex) {
				var tmp = Console.ForegroundColor;
				Console.ForegroundColor = ConsoleColor.Red;
				Console.WriteLine("Error");
				Console.Error.WriteLine(ex);
				Console.ForegroundColor = tmp;
#if DEBUG
				ExceptionDispatchInfo.Capture(ex).Throw(); // Throw to the debugger
#endif
			}
		pause:
			Console.WriteLine();
			Console.Write("Enter to exit . . .");
			Console.ReadLine();
		}

		/// <param name="bundlePath">
		/// The path of the bundle file.
		/// (e.g. @"Last Epoch_Data\StreamingAssets\aa\StandaloneWindows64\localization-string-tables-chinese(simplified)(zh)_assets_all.bundle")
		/// </param>
		/// <param name="targetPath">
		/// Path of the folder/zip-file to ex(im)port the json files (in UTF-8), or the dictionary json file for translation.
		/// </param>
		/// <exception cref="FileNotFoundException"/>
		/// <exception cref="DirectoryNotFoundException"/>
		/// <exception cref="DummyFieldAccessException"/>
		/// <exception cref="JsonException"/>
		/// <exception cref="KeyNotFoundException"/>
		public static void Run(string bundlePath, string targetPath, Mode mode) {
			ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
			ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
			ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)mode, (uint)Mode.Translate);

			var manager = new AssetsManager();
			if (mode != Mode.Export) {
				var basePath = Path.GetFullPath(bundlePath + "/../../catalog");
				var catPath = basePath + ".bin";
				if (!File.Exists(catPath))
					catPath = basePath + ".json";
				if (!File.Exists(catPath))
					catPath = basePath + ".bundle";
				if (!File.Exists(catPath))
					ThrowFileNotFound(basePath + ".bin");
				Catalog.RemoveCRC(catPath, manager);
			}

			targetPath = Path.GetFullPath(targetPath);
			ZipArchive? zip = null;
			IDictionary<string, JsonNode?>? contents = null;
			try {
				switch (mode) {
					case Mode.Export:
						if (targetPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
							zip = ZipFile.Open(targetPath, ZipArchiveMode.Create);
						else
							Directory.CreateDirectory(targetPath);
						break;
					case Mode.Import:
						if (!Directory.Exists(targetPath)) {
							if (File.Exists(targetPath))
								zip = ZipFile.OpenRead(targetPath);
							else
								ThrowDirectoryNotFound(targetPath);
						}
						break;
					case Mode.Translate:
						if (!File.Exists(targetPath))
							ThrowFileNotFound(targetPath);
						Stream stream;
						if (targetPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) {
							zip = ZipFile.Open(targetPath, ZipArchiveMode.Read);
							stream = zip.Entries.First(e => e.FullName.Equals("dictionary.json", StringComparison.OrdinalIgnoreCase)).Open();
						} else {
							stream = File.OpenRead(targetPath);
						}
						using (stream) {
							contents = JsonNode.Parse(stream, null, new() { AllowTrailingCommas = true,
								CommentHandling = JsonCommentHandling.Skip })!.AsObject();
							if (zip is not null) {
								zip.Dispose();
								zip = null;
							}
						}
						break;
				}
				bundlePath = Path.GetFullPath(bundlePath);

				var bundle = manager.LoadBundleFile(new MemoryStream(File.ReadAllBytes(bundlePath)), bundlePath);
				const int ASSETS_INDEX_IN_BUNDLE = 0;
				var assets = manager.LoadAssetsFileFromBundle(bundle, ASSETS_INDEX_IN_BUNDLE);

				var modified = false;
				foreach (var info in assets.file.AssetInfos) {
					if (info.TypeId != (int)AssetClassID.MonoBehaviour)
						continue; // MonoScript or AssetBundle
					var stringTable = manager.GetBaseField(assets, info);
					var tableEnties = stringTable["m_TableData"]["Array"];
					var filename = stringTable["m_Name"].AsString + ".json";

					Console.Write(filename + " ... "); 
					switch (mode) {
						case Mode.Export:
							using(Stream stream = zip is null
								? new FileStream($"{targetPath}/{filename}", FileMode.Create, FileAccess.Write, FileShare.Read)
								: zip.CreateEntry(filename, CompressionLevel.SmallestSize).Open())
								Export(tableEnties.Children, stream);
							break;
						case Mode.Import:
							using (Stream? stream = zip is null
								? File.Exists(filename = Path.GetFullPath($"{targetPath}/{filename}")) ? File.OpenRead(filename) : null
								: zip.Entries.FirstOrDefault(e => e.FullName == filename) is ZipArchiveEntry e ? e.Open() : null) {
								if (stream is null) {
									Console.WriteLine("Not found");
									continue;
								}
								contents = JsonNode.Parse(stream, null, new() { AllowTrailingCommas = true,
									CommentHandling = JsonCommentHandling.Skip })!.AsObject();
							}
							goto case Mode.Translate;
						case Mode.Translate:
							if (Import(tableEnties.Children, contents!, mode is Mode.Translate)) {
								//tableEnties.AsArray = new(tableEnties.Children.Count); // Uncomment this if someday the Import method will add/remove entries
								info.SetNewData(stringTable);
								modified = true;
							}
							break;
						default:
							Console.Error.WriteLine("Unknown mode: " + mode);
							return;
					}
					Console.WriteLine("Done");
				}

				if (modified) {
					bundle.file.BlockAndDirInfo.DirectoryInfos[ASSETS_INDEX_IN_BUNDLE].SetNewData(assets.file);

					// The `Pack` method doesn't consider the replacer (The modified data), so write and read again here.
					var uncompressed = new MemoryStream();
					bundle.file.Write(new(uncompressed)); 
					bundle.file.Close();
					bundle.file.Read(new(uncompressed));

					using var writer = new AssetsFileWriter(bundlePath);
					bundle.file.Pack(writer, AssetBundleCompressionType.LZMA);
				}
				Console.WriteLine("Done!");
			} finally {
				zip?.Dispose();
				manager.UnloadAll();
			}

			[DoesNotReturn, DebuggerNonUserCode]
			static void ThrowDirectoryNotFound(string folderPath)
				=> throw new DirectoryNotFoundException("The input folder does not exist: " + Path.GetFullPath(folderPath));

			[DoesNotReturn, DebuggerNonUserCode]
			static void ThrowFileNotFound(string path)
				=> throw new FileNotFoundException(null, path);
		}

		/// <summary>
		/// Internal implementation of <see cref="Run"/>.
		/// </summary>
		/// <param name="tableEnties">
		/// <code>UnityEngine.Localization.Tables.StringTable.m_TableData</code>
		/// Get by <c>BaseField["m_TableData"]["Array"].Children</c> of an asset in bundle
		/// </param>
		/// <param name="utf8Json">Json file stream to write</param>
		/// <exception cref="DummyFieldAccessException"/>
		public static void Export(IReadOnlyList<AssetTypeValueField> tableEnties, Stream utf8Json) {
			var dic = new SortedList<long, string>(tableEnties.Count);
			foreach (var tableEntryData in tableEnties)
				dic.Add(tableEntryData["m_Id"].AsLong, tableEntryData["m_Localized"].AsString);

			using var json = new Utf8JsonWriter(utf8Json, new() {
				Indented = true,
#if NET9_0_OR_GREATER
				IndentCharacter = '\t',
#endif
				Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
			});
			json.WriteStartObject();
			foreach (var (id, str) in dic)
				json.WriteString(id.ToString(), str);
			json.WriteEndObject();
		}

		/// <param name="tableEntries">
		/// <code>UnityEngine.Localization.Tables.StringTable.m_TableData</code>
		/// Get by <c>BaseField["m_TableData"]["Array"].Children</c> of an asset in bundle
		/// </param>
		/// <param name="utf8JsonFile">Json file to read the content to patch</param>
		/// <param name="throwNotMatch">
		/// <see langword="true"/> to throw an exception when any entry in bundle is not found in the json file.<br />
		/// Ignored when <paramref name="dump"/> is <see langword="true"/>.
		/// </param>
		/// <returns>Whether any entry is modified</returns>
		/// <exception cref="JsonException"/>
		/// <exception cref="DummyFieldAccessException"/>
		/// <exception cref="KeyNotFoundException"/>
		public static bool Import(IReadOnlyList<AssetTypeValueField> tableEntries,
			IDictionary<string, JsonNode?> contents, bool isTranslation, bool throwNotMatch = false) {
			if (tableEntries.Count == 0)
				return false;
			if (contents.Count == 0)
				return false;

			var modified = false;
			for (var i = 0; i < tableEntries.Count; ++i) {
				var localized = tableEntries[i]["m_Localized"].Value;
				var key = isTranslation ? localized : tableEntries[i]["m_Id"].Value;
				if (contents.TryGetValue(key.AsString, out var n)) {
					var str = (string?)n;
					if (!modified && localized.AsString == str)
						continue; // No modification
					localized.AsString = str;
					modified = true;
				} else if (throwNotMatch)
					ThrowKeyNotFound(key.AsString);
			}
			return modified;

			[DoesNotReturn, DebuggerNonUserCode]
			static void ThrowKeyNotFound(string key)
				=> throw new KeyNotFoundException($"The key {key} is not found in the json file");
		}

		public enum Mode {
			Export,
			Import,
			Translate
		}
	}
}