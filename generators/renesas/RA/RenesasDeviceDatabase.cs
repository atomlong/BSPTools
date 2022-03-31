﻿using BSPEngine;
using BSPGenerationTools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace renesas_ra_bsp_generator
{
    class RenesasDeviceDatabase
    {
        public class ParsedDevice
        {
            public string Name;
            public CortexCore Core;
            public List<string> Variants = new List<string>();
            public string FamilyName;
            public MemoryArea[] MemoryMap;

            public override string ToString() => Name;
        }

        public static CortexCore ParseCortexCore(string core)
        {
            if (!core.StartsWith("cortex-", StringComparison.InvariantCultureIgnoreCase))
                throw new Exception("Unexpected core: " + core);

            return (CortexCore)Enum.Parse(typeof(CortexCore), core.Substring(7), true);
        }

        public struct MemoryArea
        {
            public string Type;
            public ulong Start, End;
            public ulong Size => End - Start;
        }

        public static ParsedDevice[] DiscoverDevices(string packDirectory)
        {
            Dictionary<string, ParsedDevice> result = new Dictionary<string, ParsedDevice>();
            foreach (var fn in Directory.GetFiles(packDirectory, "Renesas.RA_mcu_*.pack"))
            {
                using (var zf = ZipFile.Open(fn))
                {
                    Dictionary<string, MemoryArea[]> deviceMemories = LoadDeviceMemories(zf);

                    var pgrFiles = zf.Entries.Where(e => Path.GetFileName(e.FileName).StartsWith("pgr") && e.FileName.EndsWith(".xmi")).ToArray();
                    if (pgrFiles.Length != 1)
                        throw new Exception("Unexpected number of device list files");

                    var xml = new XmlDocument();
                    xml.LoadXml(Encoding.UTF8.GetString(zf.ExtractEntry(pgrFiles[0])));

                    foreach (var target in xml.DocumentElement.SelectNodes("Target").OfType<XmlElement>())
                    {
                        var targetName = target.GetAttribute("shortName");
                        foreach (var cpu in target.SelectNodes("CpuType/Cpu").OfType<XmlElement>())
                        {
                            var familyName = cpu.GetAttribute("shortName");
                            var parsedCore = ParseCortexCore(cpu.GetAttribute("compilerCpuType"));

                            foreach (var dev in cpu.SelectNodes("PinCountRZTC/Device").OfType<XmlElement>())
                            {
                                var fullName = dev.GetAttribute("shortName");
                                var shortName = dev.GetAttribute("DeviceCommand");

                                if (!result.TryGetValue(shortName, out var devObj))
                                    result[shortName] = devObj = new ParsedDevice { Name = shortName, Core = parsedCore, FamilyName = familyName, MemoryMap = deviceMemories[fullName] };

                                devObj.Variants.Add(fullName);

                                if (deviceMemories.TryGetValue(fullName, out var thisMap) && !Enumerable.SequenceEqual(thisMap, devObj.MemoryMap))
                                    throw new Exception($"Inconistent memory maps for {devObj.Variants[0]}/{fullName}");
                            }
                        }
                    }

                }
            }

            return result.Values.ToArray();
        }

        private static Dictionary<string, MemoryArea[]> LoadDeviceMemories(DisposableZipFile zf)
        {
            Dictionary<string, MemoryArea[]> result = new Dictionary<string, MemoryArea[]>();
            foreach (var e in zf.Entries)
            {
                var fn = Path.GetFileName(e.FileName).ToLower();
                if (fn.StartsWith("memory") && fn.EndsWith(".xmi"))
                {
                    var xml = new XmlDocument();
                    xml.LoadXml(Encoding.UTF8.GetString(zf.ExtractEntry(e)));

                    var map = xml.DocumentElement.SelectElements("memoryMap").Single();
                    result[map.GetAttribute("device")] = map.SelectElements("memoryArea")
                        .Select(a => new MemoryArea
                        {
                            Type = a.GetAttribute("type"),
                            Start = a.GetUlongAttribute("startAddress"),
                            End = a.GetUlongAttribute("endAddress"),
                        }).ToArray();
                }
            }
            return result;
        }
    }
}
