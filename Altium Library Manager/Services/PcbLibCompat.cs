using System.Buffers.Binary;
using System.IO;
using System.Text;
using OpenMcdf;
using OriginalCircuit.AltiumSharp;

namespace Altium_Library_Manager.Services
{
    /// <summary>
    /// Reading PcbLibs that AltiumSharp 1.x can't open although Altium can. Each of these makes the whole
    /// library unreadable:
    ///  - Newer Altium omits 3D component body parameters whose value is zero (MODEL.2D.Y, MODEL.3D.DZ,
    ///    TEXTURECENTERX, …), but AltiumSharp parses them as mandatory coordinates ("Invalid coordinate").
    ///  - A simple (extruded) 3D body has no MODELID, and AltiumSharp dereferences it while matching STEP models.
    ///  - A footprint name with a character outside Windows-1252 (e.g. a Unicode hyphen U+2010 pasted from a
    ///    datasheet): its storage keeps the Unicode name, while the footprint list stores the 1252 fallback ('-'),
    ///    so AltiumSharp can't find the storage ("Footprint resource not found").
    /// When reading fails with one of these, the library is read into memory, patched and the copy is read
    /// instead. The file on disk is never modified, and libraries that read fine take the normal path.
    /// </summary>
    internal static class PcbLibCompat
    {
        private const byte ComponentBodyRecord = 12;

        // Parameters AltiumSharp 1.x requires for a component body, with the value Altium implies when omitted
        private static readonly (string Key, string Value)[] BodyDefaults =
        [
            ("ARCRESOLUTION", "0mil"),
            ("STANDOFFHEIGHT", "0mil"),
            ("OVERALLHEIGHT", "0mil"),
            ("TEXTURECENTERX", "0mil"),
            ("TEXTURECENTERY", "0mil"),
            ("TEXTURESIZEX", "0mil"),
            ("TEXTURESIZEY", "0mil"),
            ("TEXTUREROTATION", "0"),
            ("MODEL.2D.X", "0mil"),
            ("MODEL.2D.Y", "0mil"),
            ("MODEL.3D.DZ", "0mil"),
            // Simple extruded bodies have no model; AltiumSharp still calls ModelId.ToUpperInvariant() on them
            ("MODELID", "")
        ];

        private static readonly Encoding Win1252 = Encoding.GetEncoding(1252);

        public static PcbLib Read(string path)
        {
            try
            {
                using var reader = new PcbLibReader();
                return reader.Read(path);
            }
            catch (Exception ex) when (ex.Message.StartsWith("Invalid coordinate", StringComparison.Ordinal) ||
                                       ex.Message.StartsWith("Footprint resource not found", StringComparison.Ordinal) ||
                                       ex is NullReferenceException && ex.StackTrace?.Contains("ReadLibraryModels") == true)
            {
                using var patched = Patch(path);
                using var reader = new PcbLibReader();
                return reader.Read(patched);
            }
        }

        private static MemoryStream Patch(string path)
        {
            using var source = new MemoryStream(File.ReadAllBytes(path));
            var file = new CompoundFile(source, CFSUpdateMode.Update, CFSConfiguration.Default);
            try
            {
                RenameNon1252Storages(file.RootStorage);

                var streams = new List<CFStream>();
                file.RootStorage.VisitEntries(e =>
                {
                    if (e is CFStream s && s.Name == "Data")
                        streams.Add(s);
                }, true);

                foreach (var stream in streams)
                {
                    var data = stream.GetData();
                    var patched = PatchRecords(data);
                    if (!ReferenceEquals(patched, data))
                        stream.SetData(patched);
                }

                foreach (var parameters in FindStreams(file.RootStorage, "Parameters"))
                {
                    var data = parameters.GetData();
                    var patched = RemoveUtf8Pattern(data);
                    if (!ReferenceEquals(patched, data))
                        parameters.SetData(patched);
                }

                var result = new MemoryStream();
                file.Save(result);
                result.Position = 0;
                return result;
            }
            finally
            {
                file.Close();
            }
        }

        /// <summary>
        /// Renames footprint storages to the name the footprint list uses: the same name passed through
        /// Windows-1252, which turns e.g. a Unicode hyphen into '-'.
        /// </summary>
        private static void RenameNon1252Storages(CFStorage root)
        {
            var names = new List<string>();
            root.VisitEntries(e =>
            {
                if (e is CFStorage)
                    names.Add(e.Name);
            }, false);

            foreach (var name in names)
            {
                var listed = Win1252.GetString(Win1252.GetBytes(name));
                if (listed != name && !names.Contains(listed))
                    root.RenameItem(name, listed);
            }
        }

        private static List<CFStream> FindStreams(CFStorage root, string name)
        {
            var result = new List<CFStream>();
            root.VisitEntries(e =>
            {
                if (e is CFStream s && s.Name == name)
                    result.Add(s);
            }, true);
            return result;
        }

        /// <summary>
        /// A footprint's Parameters hold its name twice: "%UTF8%PATTERN" (exact, e.g. with a Unicode hyphen) and
        /// "PATTERN" (Windows-1252). AltiumSharp prefers the UTF-8 one but checks it against the 1252 name in the Data
        /// stream, so the UTF-8 entry is dropped to keep the 1252 name everywhere.
        /// Layout: [int32 size][C string "|KEY=VALUE|…\0"].
        /// </summary>
        private static byte[] RemoveUtf8Pattern(byte[] data)
        {
            var key = Win1252.GetBytes("|%UTF8%PATTERN=");
            var start = data.AsSpan().IndexOf(key);
            if (start < 4)
                return data;

            var next = data.AsSpan(start + 1).IndexOf((byte)'|');
            var end = next < 0 ? data.Length - 1 : start + 1 + next;   // keep the terminating '\0'
            var result = new byte[data.Length - (end - start)];
            data.AsSpan(0, start).CopyTo(result);
            data.AsSpan(end).CopyTo(result.AsSpan(start));
            AddToInt32(result, 0, -(end - start));
            return result;
        }

        /// <summary>
        /// A body record is: [type 12][int32 record size][common fields][uint32][byte]
        /// [int32 params size][params C string][uint32 outline count][outline: 2 doubles per point].
        /// Each body's parameter string gets the missing keys appended and both size fields are adjusted.
        /// Returns the same array if nothing needed patching.
        /// </summary>
        private static byte[] PatchRecords(byte[] data)
        {
            // Depending on the Altium version the string starts with "|V7_LAYER=" or "V7_LAYER="
            var marker = Win1252.GetBytes("V7_LAYER=");
            var edits = new List<(int RecordSizeAt, int ParamsSizeAt, int InsertAt, byte[] Insert)>();

            for (var found = data.AsSpan().IndexOf(marker); found >= 0;)
            {
                var start = found > 0 && data[found - 1] == (byte)'|' ? found - 1 : found;
                if (TryLocateBody(data, start, out var recordSizeAt, out var paramsEnd))
                {
                    var parameters = Win1252.GetString(data, start, paramsEnd - start);
                    var missing = BodyDefaults
                        .Where(d => !parameters.Contains($"|{d.Key}=", StringComparison.OrdinalIgnoreCase))
                        .Select(d => $"|{d.Key}={d.Value}");
                    var insert = Win1252.GetBytes(string.Concat(missing));
                    if (insert.Length > 0)
                        edits.Add((recordSizeAt, start - 4, paramsEnd - 1, insert));   // insert before the '\0'
                }

                var next = data.AsSpan(found + 1).IndexOf(marker);
                found = next < 0 ? -1 : found + 1 + next;
            }

            if (edits.Count == 0)
                return data;

            // Size fields precede the insertion points, so patch them first, then build the new buffer
            var result = (byte[])data.Clone();
            foreach (var edit in edits)
            {
                AddToInt32(result, edit.ParamsSizeAt, edit.Insert.Length);
                AddToInt32(result, edit.RecordSizeAt, edit.Insert.Length);
            }

            using var output = new MemoryStream(result.Length + edits.Sum(e => e.Insert.Length));
            var position = 0;
            foreach (var edit in edits.OrderBy(e => e.InsertAt))
            {
                output.Write(result, position, edit.InsertAt - position);
                output.Write(edit.Insert);
                position = edit.InsertAt;
            }
            output.Write(result, position, result.Length - position);
            return output.ToArray();
        }

        /// <summary>
        /// Checks that the parameter string at <paramref name="paramsStart"/> belongs to a component body record and
        /// finds the record's size field, by verifying that the sizes add up to the record's real extent.
        /// </summary>
        private static bool TryLocateBody(byte[] data, int paramsStart, out int recordSizeAt, out int paramsEnd)
        {
            recordSizeAt = -1;
            paramsEnd = -1;
            if (paramsStart < 4)
                return false;

            var paramsSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(paramsStart - 4)) & 0x00FFFFFF;
            paramsEnd = paramsStart + paramsSize;
            if (paramsSize <= 0 || paramsEnd + 4 > data.Length || data[paramsEnd - 1] != 0)
                return false;

            var outlineCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(paramsEnd));
            var recordEnd = paramsEnd + 4 + 16L * outlineCount;
            if (recordEnd > data.Length)
                return false;

            // The common fields between the record header and the parameters are a few dozen bytes at most
            for (var at = paramsStart - 9; at >= Math.Max(1, paramsStart - 96); at--)
            {
                var recordSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at)) & 0x00FFFFFF;
                if (data[at - 1] == ComponentBodyRecord && at + 4 + recordSize == recordEnd)
                {
                    recordSizeAt = at;
                    return true;
                }
            }
            return false;
        }

        private static void AddToInt32(byte[] data, int at, int delta)
        {
            // The top byte may carry a flag; only the 24-bit size is changed
            var value = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at));
            var size = (value & 0x00FFFFFF) + delta;
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(at), (value & unchecked((int)0xFF000000)) | size);
        }
    }
}
