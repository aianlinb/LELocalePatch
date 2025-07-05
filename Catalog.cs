using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace LELocalePatch;
public static class Catalog {
	public static void RemoveCRC(string catalogPath, AssetsManager? manager) {
		var stream = File.Open(catalogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
		bool CheckIsBundle() {
			try {
				var magic = "UnityFS"u8;
				if (stream.Length < magic.Length)
					return false;
				Span<byte> head = stackalloc byte[magic.Length];
				stream.ReadExactly(head);
				return head.SequenceEqual(magic);
			} catch {
				stream.Close();
				throw;
			}
		}
		if (CheckIsBundle()) { // Compressed catalog.bundle
			manager ??= new();
			try {
				var bundle = manager.LoadBundleFile(stream, catalogPath);
				var file = manager.LoadAssetsFileFromBundle(bundle, 0).file;
				var reader = file.Reader;
				var info = file.Metadata.AssetInfos.First(a => a.TypeId == (int)AssetClassID.TextAsset);
				reader.Position = info.GetAbsoluteByteOffset(file);
				reader.BaseStream.Seek(reader.ReadInt32() + 3 & ~3 /*align 4*/, SeekOrigin.Current);
				var json = RemoveCRCFromJson(reader.BaseStream);
				if (json is not null) {
					// Create TextAsset
					var str = json.ToJsonString(new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
					var len = Encoding.UTF8.GetByteCount(str);
					var bytes = GC.AllocateUninitializedArray<byte>(16 + len + 3 & ~3 /*align 4*/);
					((ReadOnlySpan<byte>)[7, 0, 0, 0, .. "catalog"u8, 0]).CopyTo(bytes);
					BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), len);
					Encoding.UTF8.GetBytes(str, bytes.AsSpan(16));

					// Save bundle
					info.SetNewData(bytes);
					bundle.file.BlockAndDirInfo.DirectoryInfos[0].SetNewData(file);
					File.Move(catalogPath, catalogPath + ".bak", true);
					using var writer = new AssetsFileWriter(catalogPath);
					bundle.file.Write(writer);
				}
			} finally {
				manager.UnloadAll();
			}
		} else
			using (stream) {
				var reverseEndian = false;
				var magic = ReadInt(0);
				if (magic == 0x4289E30D)
					reverseEndian = true;
				else if (magic != 0x0DE38942) { // Not binary
					var json = RemoveCRCFromJson(stream);
					if (json is not null) {
						stream.Close();
						File.Move(catalogPath, catalogPath + ".bak", true);
						using var fs = new FileStream(catalogPath, FileMode.Create, FileAccess.Write, FileShare.None);
						using var writer = new Utf8JsonWriter(fs, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
						json.WriteTo(writer, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
					}
					return;
				}

#pragma warning disable CA2014 // Do not use stackalloc in loops
				// RemoveCRCFromBinary
				var modified = false;
				HashSet<int> offsets = [-1]; // Processed offsets (-1 which mean null is always skipped)
				int? abpOffset = null; // AssetBundleProvider offset

				var version = ReadInt();
				if (version is not (1 or 2))
					Console.WriteLine("Warning: Unknown catalog version: " + version);
				var offset = ReadInt(); // KeyOffsetsOffset
				if (offset != -1) {
					Span<int> span = stackalloc int[ReadInt(offset - sizeof(int)) / sizeof(int)];
					if (reverseEndian)
						BinaryPrimitives.ReverseEndianness(span, span);
					stream.ReadExactly(MemoryMarshal.AsBytes(span)); // KeyLocationOffsets
					for (var i = 1; i < span.Length; i += 2) { // Key, Locations, Key, Locations, ...
						if (!offsets.Add(span[i]))
							continue;
						Span<int> span2 = stackalloc int[ReadInt(span[i] - sizeof(int)) / sizeof(int)];
						stream.ReadExactly(MemoryMarshal.AsBytes(span2)); // LocationOffsets
						if (reverseEndian)
							BinaryPrimitives.ReverseEndianness(span2, span2);
						foreach (var locationOffset in span2) {
							if (!offsets.Add(locationOffset))
								continue;
							offset = ReadInt(locationOffset + sizeof(int) * 2); // ProviderIdOffset
							if (abpOffset.HasValue) {
								if (offset != abpOffset.Value) // Not AssetBundleProvider
									continue;
							} else {
								if (!offsets.Add(offset))
									continue;
								// New provider found
								if (ReadString(offset, '.') == "UnityEngine.ResourceManagement.ResourceProviders.AssetBundleProvider")
									abpOffset = offset;
								else
									continue; // Not AssetBundleProvider
							}
							offset = ReadInt(locationOffset + sizeof(int) * 5); // DataOffset
							if (!offsets.Add(offset))
								continue;
							offset = ReadInt(offset + sizeof(int)) + sizeof(int) * 2; // ObjectOffset
							if (!modified) {
								if (ReadInt(offset) == 0) // Crc
									continue;
								File.Copy(catalogPath, catalogPath + ".bak", true);
								modified = true;
							}
							stream.Position = offset;
							stream.Write([0, 0, 0, 0]);
						}
					}
				}

				int ReadInt(int? offset = null) {
					if (offset.HasValue)
						stream.Position = offset.Value;
					Unsafe.SkipInit(out int v);
					stream.ReadExactly(MemoryMarshal.AsBytes(new Span<int>(ref v)));
					return reverseEndian ? BinaryPrimitives.ReverseEndianness(v) : v;
				}

				string? ReadString(int offset, char dynSep = default) {
					if (offset == -1)
						return null;
					bool unicode = (offset & 0x80000000) != 0;
					bool dynamicString = dynSep != default && (offset & 0x40000000) != 0;
					int pos = offset & 0x3fffffff;
					if (dynamicString) {
						stream.Position = pos;
						var partStrs = new List<string>();
						while (true) {
							var partStringOffset = ReadInt();
							var nextPartOffset = ReadInt();
							var str = ReadString(partStringOffset);
							if (str is not null)
								partStrs.Add(str);
							if (nextPartOffset == -1)
								break;
							stream.Position = nextPartOffset;
						}
						return partStrs.Count == 1 ? partStrs[0]
							: version > 1 ? string.Join('.', Enumerable.Range(1, partStrs.Count).Select(i => partStrs[^i]))
								: string.Join(dynSep, partStrs);
					} else {
						Span<byte> span = stackalloc byte[ReadInt(pos - sizeof(int))];
						stream.ReadExactly(span);
						if (unicode) {
							var charSpan = MemoryMarshal.Cast<byte, char>(span);
							if (reverseEndian) {
								var tmp = MemoryMarshal.Cast<char, ushort>(charSpan);
								BinaryPrimitives.ReverseEndianness(tmp, tmp);
							}
							return new(charSpan);
						}
						return Encoding.UTF8.GetString(span);
					}
				}
			}
	}

	private static JsonObject? RemoveCRCFromJson(Stream stream) {
		var json = JsonNode.Parse(stream, null, new() {
			AllowTrailingCommas = true,
			CommentHandling = JsonCommentHandling.Skip
		})!.AsObject();
		var providerIndex = 0;
		foreach (var v in json["m_ProviderIds"]!.AsArray()) {
			if (!((string?)v)?.EndsWith("AssetBundleProvider") ?? true)
				break;
			++providerIndex;
		}
		var entryData = Convert.FromBase64String((string)json["m_EntryDataString"]!);
		var extraData = Convert.FromBase64String((string)json["m_ExtraDataString"]!);
		if (!BitConverter.IsLittleEndian) {
			var span = MemoryMarshal.Cast<byte, int>(entryData);
			BinaryPrimitives.ReverseEndianness(span, span);
		}
		var entryCount = MemoryMarshal.Read<int>(entryData);
		var entryDatas = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<byte, EntryData>(
			ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(entryData), sizeof(int))), entryCount);

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
				var len = BinaryPrimitives.ReadInt32LittleEndian(new(extraData, ++offset, sizeof(int)));
				var str = MemoryMarshal.Cast<byte, char>(new ReadOnlySpan<byte>(extraData, offset + sizeof(int), len));
				if (!BitConverter.IsLittleEndian) {
					var tmp = MemoryMarshal.CreateSpan(ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(str)), str.Length);
					BinaryPrimitives.ReverseEndianness(tmp, tmp);
				}
				if (JsonNode.Parse(str.ToString()) is JsonObject jsonObj) {
					if (!jsonObj.TryGetPropertyValue("m_Crc", out var node) || node is not JsonValue v
						|| !v.TryGetValue<long>(out var l) || l == 0L)
						continue;
					jsonObj["m_Crc"] = 0;
					var result = jsonObj.ToJsonString();
					Debug.Assert(result.Length * 2 <= len);
					BinaryPrimitives.WriteInt32LittleEndian(extraData.AsSpan(offset, sizeof(int)), result.Length * 2);
					var span = result.AsSpan();
					if (!BitConverter.IsLittleEndian) {
						var tmp = MemoryMarshal.CreateSpan(ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(span)), span.Length);
						BinaryPrimitives.ReverseEndianness(tmp, tmp);
					}
					MemoryMarshal.AsBytes(span).CopyTo(extraData.AsSpan(offset + sizeof(int)));
					modified = true;
				} else
					Debug.Assert(false);
			}
		}
		if (modified) {
			json["m_ExtraDataString"] = Convert.ToBase64String(extraData);
			return json;
		}
		return null;
	}

	private readonly struct EntryData {
#pragma warning disable CS0649
		public readonly int InternalIdIndex;
		public readonly int ProviderIndex;
		public readonly int DependencyKey;
		public readonly int DepHash;
		public readonly int DataIndex;
		public readonly int PrimaryKeyIndex;
		public readonly int ResourceTypeIndex;
#pragma warning restore CS0649
	}
}