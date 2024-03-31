using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Runtime.CompilerServices;
#if DEBUG
using System.Runtime.ExceptionServices;
#endif
using System.Runtime.InteropServices;
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
				Console.WriteLine("Usage: LELocalePatch <bundlePath> {dump|patch|patchFull} <folderPath|zipPath>");
				if (args.Length == 0)
					goto pause;
				return;
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
		/// <param name="folderOrZipPath">
		/// Path of the folder/zip-file to dump or apply the json files (in UTF-8).
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
		public static void Run(string bundlePath, string folderOrZipPath, bool dump, bool throwNotMatch = false) {
			var manager = new AssetsManager();
			ZipArchive? zip = null;
			try {
				if (dump) {
					if (folderOrZipPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
						zip = ZipFile.Open(folderOrZipPath, ZipArchiveMode.Create);
					else
						folderOrZipPath = Directory.CreateDirectory(folderOrZipPath).FullName;
				} else { // patch
					if (!Directory.Exists(folderOrZipPath)) {
						if (File.Exists(folderOrZipPath))
							zip = ZipFile.OpenRead(folderOrZipPath);
						else
							ThrowDirectoryNotFound(folderOrZipPath);
					}
					var catPath = Path.GetDirectoryName(Path.GetDirectoryName(bundlePath)) + "/catalog.json";
					if (!File.Exists(catPath))
						ThrowCatalogNotFound(catPath);
					RemoveCRC(catPath);
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

					Console.WriteLine(filename);
					if (dump) {
						using Stream stream = zip is null
							? new FileStream($"{folderOrZipPath}/{filename}", FileMode.Create, FileAccess.Write, FileShare.Read)
							: zip.CreateEntry(filename, CompressionLevel.Optimal).Open();
						Dump(tableEnties.Children, stream);
					} else { // patch
						using Stream? stream = zip is null
							? File.Exists(filename = Path.GetFullPath($"{folderOrZipPath}/{filename}")) ? File.OpenRead(filename) : null
							: zip.Entries.FirstOrDefault(e => e.FullName == filename) is ZipArchiveEntry e ? e.Open() : null;
						if (stream is null) {
							if (throwNotMatch)
								ThrowJsonFileNotFound(filename);
							continue;
						}
						if (Patch(tableEnties.Children, stream, throwNotMatch)) {
							//tableEnties.AsArray = new(tableEnties.Children.Count); // Uncomment this if someday the Patch method will add/remove entries
							info.SetNewData(stringTable);
							modified = true;
						}
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
				Console.WriteLine("Done!");
			} finally {
				zip?.Dispose();
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
		/// <param name="utf8JsonFile">Json file to read the content to patch</param>
		/// <param name="throwNotMatch">
		/// <see langword="true"/> to throw an exception when any entry in bundle is not found in the json file.<br />
		/// Ignored when <paramref name="dump"/> is <see langword="true"/>.
		/// </param>
		/// <returns>Whether any entry is modified</returns>
		/// <exception cref="JsonException"/>
		/// <exception cref="DummyFieldAccessException"/>
		/// <exception cref="KeyNotFoundException"/>
		public static bool Patch(IReadOnlyList<AssetTypeValueField> tableEnties, Stream utf8JsonFile, bool throwNotMatch = false) {
			if (tableEnties.Count == 0)
				return false;
			var node = JsonNode.Parse(utf8JsonFile, null, new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })!.AsObject();
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

		public static void RemoveCRC(string catalogJsonPath) {
			var utf8 = new Utf8JsonReader(File.ReadAllBytes(catalogJsonPath));
			var json = JsonNode.Parse(ref utf8);
			var providerIndex = 0;
			foreach (var v in json!["m_ProviderIds"]!.AsArray()) {
				if ((string?)v == "UnityEngine.ResourceManagement.ResourceProviders.AssetBundleProvider")
					break;
				++providerIndex;
			}
			var entryData = Convert.FromBase64String((string)json["m_EntryDataString"]!);
			var extraData = Convert.FromBase64String((string)json["m_ExtraDataString"]!);
			var entryCount = MemoryMarshal.Read<int>(entryData);
			var entryDatas = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<byte, EntryData>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(entryData), sizeof(int))), entryCount);

			var modified = false;
			for (var i = 0; i < entryCount; ++i) {
				if (entryDatas[i].ProviderIndex != providerIndex)
					continue;
				var offset = entryDatas[i].DataIndex;
				if (extraData[offset] == 7) { // JsonObject
					++offset;
					offset += extraData[offset]; // ascii string length
					++offset;
					offset += extraData[offset]; // ascii string length
					var len = MemoryMarshal.Read<int>(new(extraData, ++offset, sizeof(int)));
					if (JsonNode.Parse(MemoryMarshal.Cast<byte, char>(new ReadOnlySpan<byte>(extraData, offset + sizeof(int), len)).ToString()) is JsonObject jsonObj) {
						if (!jsonObj.ContainsKey("m_Crc"))
							continue;
						jsonObj["m_Crc"] = 0;
						var result = jsonObj.ToJsonString();
						Debug.Assert(result.Length * 2 <= len);
						MemoryMarshal.Write(extraData.AsSpan(offset, sizeof(int)), result.Length * 2);
						MemoryMarshal.AsBytes(result.AsSpan()).CopyTo(extraData.AsSpan(offset + sizeof(int)));
						modified = true;
					} else
						Debug.Assert(false);
				}
			}
			if (modified) {
				json["m_ExtraDataString"] = Convert.ToBase64String(extraData);
				using var fs = new FileStream(catalogJsonPath, FileMode.Create, FileAccess.Write, FileShare.None);
				using var writer = new Utf8JsonWriter(fs, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
				json.WriteTo(writer, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
			}
		}

		private readonly struct EntryData {
#pragma warning disable CS0649
			public readonly int InternalIdIndex;
			public readonly int ProviderIndex;
			public readonly int DependencyKey;
			public readonly int DepHash;
			public readonly int DataIndex;
			public readonly int PrimaryKeyInde;
			public readonly int ResourceTypeIndex;
#pragma warning restore CS0649
		}

		/// <exception cref="DirectoryNotFoundException"/>
		[DoesNotReturn, DebuggerNonUserCode]
		private static void ThrowDirectoryNotFound(string folderPath)
			=> throw new DirectoryNotFoundException("The input folder does not exist: " + Path.GetFullPath(folderPath));

		/// <exception cref="FileNotFoundException"/>
		[DoesNotReturn, DebuggerNonUserCode]
		private static void ThrowCatalogNotFound(string catalogJsonPath)
			=> throw new FileNotFoundException("The catalog.json file does not exist: " + Path.GetFullPath(catalogJsonPath));

		/// <exception cref="FileNotFoundException"/>
		[DoesNotReturn, DebuggerNonUserCode]
		private static void ThrowJsonFileNotFound(string jsonFileName)
			=> throw new FileNotFoundException("The json file does not exist: " + jsonFileName);

		/// <exception cref="DirectoryNotFoundException"/>
		[DoesNotReturn, DebuggerNonUserCode]
		private static void ThrowKeyNotFound(string key)
			=> throw new KeyNotFoundException($"The key {key} is not found in the json file");
	}
}