/* Copyright (c) 2021 Rick (rick 'at' gibbed 'dot' us)
 *
 * This software is provided 'as-is', without any express or implied
 * warranty. In no event will the authors be held liable for any damages
 * arising from the use of this software.
 *
 * Permission is granted to anyone to use this software for any purpose,
 * including commercial applications, and to alter it and redistribute it
 * freely, subject to the following restrictions:
 *
 * 1. The origin of this software must not be misrepresented; you must not
 *    claim that you wrote the original software. If you use this software
 *    in a product, an acknowledgment in the product documentation would
 *    be appreciated but is not required.
 *
 * 2. Altered source versions must be plainly marked as such, and must not
 *    be misrepresented as being the original software.
 *
 * 3. This notice may not be removed or altered from any source
 *    distribution.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Gibbed.IO;
using NDesk.Options;
using Yaml = YamlDotNet.Core;
using YamlEvents = YamlDotNet.Core.Events;

namespace ExtractDarkConfigBinary
{
    internal static partial class Program
    {
        private static string GetExecutableName()
        {
            return Path.GetFileName(System.Reflection.Assembly.GetExecutingAssembly().CodeBase);
        }

        public static void Main(string[] args)
        {
            bool verbose = false;
            bool showHelp = false;

            var options = new OptionSet()
            {
                { "v|verbose", "be verbose", v => verbose = v != null },
                { "h|help", "show this message and exit", v => showHelp = v != null },
            };

            List<string> extra;
            try
            {
                extra = options.Parse(args);
            }
            catch (OptionException e)
            {
                Console.Write("{0}: ", GetExecutableName());
                Console.WriteLine(e.Message);
                Console.WriteLine("Try `{0} --help' for more information.", GetExecutableName());
                return;
            }

            if (extra.Count < 1 || showHelp == true)
            {
                Console.WriteLine("Usage: {0} [OPTIONS]+ <input_path> [output_path]", GetExecutableName());
                Console.WriteLine();
                Console.WriteLine("Options:");
                options.WriteOptionDescriptions(Console.Out);
                return;
            }

            var inputPath = Path.GetFullPath(extra[0]);
            var outputBasePath = extra.Count > 1 ? extra[1] : Path.ChangeExtension(inputPath, null) + "_unpack";

            var inputBytes = File.ReadAllBytes(inputPath);
            using (var input = new MemoryStream(inputBytes, false))
            {
                const uint signature = 0x334D4DE3; // '3MMp'
                var magic = input.ReadValueU32(Endian.Little);
                if (magic != signature && magic.Swap() != signature)
                {
                    throw new FormatException();
                }
                var endian = magic == signature ? Endian.Little : Endian.Big;

                var version = input.ReadValueU8();
                if (version != 1)
                {
                    throw new FormatException();
                }

                var compressionMethod = input.ReadValueU8();
                if (compressionMethod > 3)
                {
                    throw new FormatException();
                }

                var encryptionMethod = input.ReadValueU8();
                if (encryptionMethod > 1)
                {
                    throw new FormatException();
                }

                if (compressionMethod != 0)
                {
                    throw new NotImplementedException("compression not implemented");
                }

                if (encryptionMethod != 0)
                {
                    throw new NotImplementedException("encryption not implemented");
                }

                var stringTable = new Dictionary<int, string>();
                var stringCount = input.ReadValueS32(endian);
                for (int i = 0; i < stringCount; i++)
                {
                    int id = ReadPackedInt(input);
                    string value = ReadString(input);
                    stringTable.Add(id, value);
                }

                var fileCount = input.ReadValueU16(endian);
                for (int i = 0; i < fileCount; i++)
                {
                    var path = ReadString(input);
                    var checksum = input.ReadValueU32(endian);
                    var size = input.ReadValueS32(endian);
                    var modifiedValue = input.ReadValueS64(endian);
                    var modifiedDateTime = DateTime.FromBinary(modifiedValue);

                    path = path
                        .Replace('/', Path.DirectorySeparatorChar)
                        .Replace('\\', Path.DirectorySeparatorChar);

                    var root = Path.GetPathRoot(path);
                    if (string.IsNullOrEmpty(root) == false)
                    {
                        path = path.Substring(root.Length);
                    }

                    Console.WriteLine($"Emitting '{path}'...");

                    using (var stringWriter = new StringWriter())
                    {
                        var emitter = new Yaml.Emitter(stringWriter);

                        emitter.Emit(new YamlEvents.StreamStart());
                        emitter.Emit(new YamlEvents.DocumentStart(null, null, false));

                        Emit(input, emitter, stringTable);

                        emitter.Emit(new YamlEvents.DocumentEnd(false));
                        emitter.Emit(new YamlEvents.StreamEnd());

                        stringWriter.Flush();

                        var outputPath = Path.Combine(outputBasePath, $"{path}.yaml");

                        var outputParentPath = Path.GetDirectoryName(outputPath);
                        if (string.IsNullOrEmpty(outputParentPath) == false)
                        {
                            Directory.CreateDirectory(outputParentPath);
                        }

                        File.WriteAllText(outputPath, stringWriter.ToString());
                        File.SetLastWriteTimeUtc(outputPath, modifiedDateTime);
                    }
                }
            }
        }

        private enum ItemType : byte
        {
            Mapping = 1,
            Sequence = 2,
            Scalar = 3,
        }

        private enum StackType
        {
            Invalid = -1,
            Item,
            KeyValue,
            MappingEnd,
            SequenceEnd,
        }

        private static void Emit(Stream input, Yaml.Emitter emitter, Dictionary<int, string> stringTable)
        {
            var stack = new Stack<StackType>();
            stack.Push(StackType.Item);

            while (stack.Count > 0)
            {
                var stackType = stack.Pop();
                switch (stackType)
                {
                    case StackType.Item:
                    {
                        var itemType = (ItemType)input.ReadValueU8();
                        switch (itemType)
                        {
                            case ItemType.Mapping:
                            {
                                emitter.Emit(new YamlEvents.MappingStart(default, default, false, YamlEvents.MappingStyle.Any));
                                stack.Push(StackType.MappingEnd);
                                var count = ReadPackedInt(input);
                                while (count-- > 0)
                                {
                                    stack.Push(StackType.KeyValue);
                                }
                                break;
                            }

                            case ItemType.Sequence:
                            {
                                emitter.Emit(new YamlEvents.SequenceStart(default, default, false, YamlEvents.SequenceStyle.Any));
                                stack.Push(StackType.SequenceEnd);
                                var count = ReadPackedInt(input);
                                while (count-- > 0)
                                {
                                    stack.Push(StackType.Item);
                                }
                                break;
                            }

                            case ItemType.Scalar:
                            {
                                var value = ReadScalar(input, stringTable);
                                emitter.Emit(new YamlEvents.Scalar(value));
                                break;
                            }
                        }
                        break;
                    }

                    case StackType.KeyValue:
                    {
                        var key = ReadScalar(input, stringTable);
                        emitter.Emit(new YamlEvents.Scalar(key));
                        stack.Push(StackType.Item);
                        break;
                    }

                    case StackType.MappingEnd:
                    {
                        emitter.Emit(new YamlEvents.MappingEnd());
                        break;
                    }

                    case StackType.SequenceEnd:
                    {
                        emitter.Emit(new YamlEvents.SequenceEnd());
                        break;
                    }
                }
            }
        }

        private enum ScalarType : byte
        {
            Value = 0xF0,
            Id = 0xFF,
        }

        private static string ReadScalar(Stream input, Dictionary<int, string> stringTable)
        {
            var type = (ScalarType)input.ReadValueU8();
            switch (type)
            {
                case ScalarType.Value:
                {
                    return ReadString(input);
                }
                case ScalarType.Id:
                {
                    var id = ReadPackedInt(input);
                    return stringTable[id];
                }
            }
            throw new NotSupportedException();
        }

        private static string ReadString(Stream input)
        {
            var length = ReadPackedInt(input);
            return input.ReadString(length, Encoding.ASCII);
        }

        private static int ReadPackedInt(Stream input)
        {
            int value = 0;
            int shift = 0;
            byte b;
            do
            {
                if (shift > 28)
                {
                    throw new FormatException();
                }
                b = input.ReadValueU8();
                value |= (b & 0x7F) << shift;
                shift += 7;
            }
            while ((b & 0x80) != 0);
            return value;
        }
    }
}
