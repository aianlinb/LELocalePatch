using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
		/// Entry point of the program.=
		/// </summary>
		/// <remarks>
		/// Parses the command line <paramref name="args"/> and calls <see cref="Run"/>.
		/// Then outputs the success message or exception to the <see cref="Console"/>.
		/// </remarks>
		public static void Main(string[] args) {
			if (args.Length != 3) {
				Console.WriteLine("Usage: LELocalePatch <bundlePath> {dump|patch|patchFull} <folderPath>");
				if (args.Length == 0) {
					Console.WriteLine();
					Console.Write("Enter to exit . . .");
					Console.ReadLine();
				}
			}

			bool dump, @throw = false;
			switch (args[1].ToLowerInvariant()) {
				case "dump":
					dump = true;
					break;
				case "patchfull":
					@throw = true; goto case "patch";
				case "patch":
					dump = false;
					break;
				default:
					Console.WriteLine("Invalid action: " + args[1]);
					return;
			}

			try {
				Run(args[0], args[2], dump, @throw);
				Console.WriteLine("Done!");
			} catch (Exception ex) {
				var tmp = Console.ForegroundColor;
				Console.ForegroundColor = ConsoleColor.Red;
				Console.Error.WriteLine(ex);
				Console.ForegroundColor = tmp;
				Console.WriteLine();
				Console.Write("Enter to exit . . .");
				Console.ReadLine();
#if DEBUG
				ExceptionDispatchInfo.Capture(ex).Throw();
#endif
			}
		}

		/// <param name="bundlePath">
		/// The path of the bundle file.
		/// (e.g. @"Last Epoch_Data\StreamingAssets\aa\StandaloneWindows64\localization-string-tables-chinese(simplified)(zh)_assets_all.bundle")
		/// </param>
		/// <param name="folderPath">
		/// The folder path to dump or apply the json files (in UTF-8).
		/// </param>
		/// <param name="dump">
		/// <see langword="true"/> to dump the localization to json files; <see langword="false"/> to apply them back.
		/// </param>
		/// <param name="throwNotMatch">
		/// <see langword="true"/> to throw an exception when any entry in bundle is not found in the json file,
		/// or the json file doesn't exist.<br />
		/// Ignored when <paramref name="dump"/> is <see langword="true"/>.
		/// </param>
		/// <exception cref="FileNotFoundException"/>
		/// <exception cref="DirectoryNotFoundException"/>
		/// <exception cref="DummyFieldAccessException"/>
		/// <exception cref="JsonException"/>
		/// <exception cref="KeyNotFoundException"/>
		public static void Run(string bundlePath, string folderPath, bool dump, bool throwNotMatch = false) {
			if (dump)
				folderPath = Directory.CreateDirectory(folderPath).FullName;
			else if (!Directory.Exists(folderPath)) // patch
				ThrowDirectoryNotFound(folderPath);
			bundlePath = Path.GetFullPath(bundlePath);

			var manager = new AssetsManager();
			try {
				var bundle = manager.LoadBundleFile(new MemoryStream(File.ReadAllBytes(bundlePath)), bundlePath);
				const int ASSETS_INDEX_IN_BUNDLE = 0;
				var assets = manager.LoadAssetsFileFromBundle(bundle, ASSETS_INDEX_IN_BUNDLE);

				var modified = false;
				foreach (var info in assets.file.AssetInfos) {
					if (info.TypeId != (int)AssetClassID.MonoBehaviour)
						continue; // MonoScript or AssetBundle
					var stringTable = manager.GetBaseField(assets, info);
					var tableEnties = stringTable["m_TableData"]["Array"];
					var name = stringTable["m_Name"].AsString;

					var path = $"{folderPath}{Path.DirectorySeparatorChar}{name}.json";
					Console.WriteLine(path);
					if (dump) {
						using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
						Dump(tableEnties.Children, fs);
					} else { // patch
						if (File.Exists(path)) {
							if (Patch(tableEnties.Children, File.ReadAllBytes(path), throwNotMatch)) {
								//tableEnties.AsArray = new(tableEnties.Children.Count); // Uncomment this if someday the Patch method will add/remove entries
								info.SetNewData(stringTable);
								modified = true;
							}
						} else if (throwNotMatch)
							ThrowJsonFileNotFound(path);
					}
				}

				if (!dump && modified) {
					bundle.file.BlockAndDirInfo.DirectoryInfos[ASSETS_INDEX_IN_BUNDLE].SetNewData(assets.file);
					var uncompressed = new MemoryStream();
					bundle.file.Write(new(uncompressed)); // The `Pack` method doesn't consider the replacer (The modified data), so write and read again here.
					bundle.file.Close();
					bundle.file.Read(new(uncompressed));
					using var writer = new AssetsFileWriter(bundlePath);
					bundle.file.Pack(writer, AssetBundleCompressionType.LZMA);
				}
			} finally {
				manager.UnloadAll(true);
			}
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
		public static void Dump(IReadOnlyList<AssetTypeValueField> tableEnties, Stream utf8Json) {
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

		/// <param name="tableEnties">
		/// <code>UnityEngine.Localization.Tables.StringTable.m_TableData</code>
		/// Get by <c>BaseField["m_TableData"]["Array"].Children</c> of an asset in bundle
		/// </param>
		/// <param name="utf8JsonData">Json file to read the content to patch</param>
		/// <param name="throwNotMatch">
		/// <see langword="true"/> to throw an exception when any entry in bundle is not found in the json file.<br />
		/// Ignored when <paramref name="dump"/> is <see langword="true"/>.
		/// </param>
		/// <returns>Whether any entry is modified</returns>
		/// <exception cref="JsonException"/>
		/// <exception cref="DummyFieldAccessException"/>
		/// <exception cref="KeyNotFoundException"/>
		public static bool Patch(IReadOnlyList<AssetTypeValueField> tableEnties, ReadOnlySpan<byte> utf8JsonData, bool throwNotMatch = false) {
			if (tableEnties.Count == 0)
				return false;
			if (utf8JsonData.StartsWith<byte>([0xEF, 0xBB, 0xBF])) // UTF-8 BOM
				utf8JsonData = utf8JsonData[3..];
			var reader = new Utf8JsonReader(utf8JsonData, new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
			var node = JsonNode.Parse(ref reader)!.AsObject();
			if (node.Count == 0)
				return false;

			var modified = false;
			for (var i = 0; i < tableEnties.Count; ++i) {
				if (node.TryGetPropertyValue(tableEnties[i]["m_Id"].Value.ToString(), out var n)) {
					tableEnties[i]["m_Localized"].AsString = (string?)n;
					modified = true;
				} else if (throwNotMatch)
					ThrowKeyNotFound(tableEnties[i]["m_Id"].Value.ToString());
			}
			return modified;
		}

		/// <exception cref="DirectoryNotFoundException"/>
		[DoesNotReturn, DebuggerNonUserCode]
		private static void ThrowDirectoryNotFound(string folderPath)
			=> throw new DirectoryNotFoundException("The input folder does not exist: " + folderPath);

		/// <exception cref="FileNotFoundException"/>
		[DoesNotReturn, DebuggerNonUserCode]
		private static void ThrowJsonFileNotFound(string jsonPath)
			=> throw new FileNotFoundException("The json file does not exist: " + jsonPath);

		/// <exception cref="DirectoryNotFoundException"/>
		[DoesNotReturn, DebuggerNonUserCode]
		private static void ThrowKeyNotFound(string key)
			=> throw new KeyNotFoundException($"The key {key} is not found in the json file");
	}
}