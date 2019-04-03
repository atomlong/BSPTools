﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ESP8266DebugPackage
{
    public struct ProgrammableRegion
    {
        public int Offset;
        public int Size;
        public string FileName;
    }

    public struct CustomStartStep
    {
        public int ProgressWeight;
        public bool CheckResult;
        public string[] Commands;

        public string ResultVariable;

        public string ErrorMessage;
        public bool CanRetry;

        public CustomStartStep(params string[] commands)
        {
            Commands = commands;
            ProgressWeight = 0;
            ResultVariable = null;
            ErrorMessage = null;
            CheckResult = false;
            CanRetry = false;
        }
    }

    public class CustomStartupSequence
    {
        public List<CustomStartStep> Steps;
        public string InitialHardBreakpointExpression;
    }

    class ESP8266StartupSequence
    {
        struct ParsedFLASHLoader
        {
            public readonly byte[] Data;
            public readonly uint LoadAddress;
            public readonly uint EntryPoint;
            public readonly uint ParameterArea;
            public readonly uint DataBuffer;
            public readonly int DataBufferSize;
            public readonly string FullPath;

            public readonly uint pCommand;
            public readonly uint pArg1;
            public readonly uint pArg2;
            public readonly uint pResult;

            public ParsedFLASHLoader(string fn)
            {
                Data = File.ReadAllBytes(fn);
                if (BitConverter.ToUInt32(Data, 0) != 0x48534C46)
                    throw new Exception("Signature mismatch");
                LoadAddress = BitConverter.ToUInt32(Data, 4);
                FullPath = fn;
                EntryPoint = BitConverter.ToUInt32(Data, 8);
                ParameterArea = BitConverter.ToUInt32(Data, 12);
                DataBuffer = BitConverter.ToUInt32(Data, 16);
                DataBufferSize = BitConverter.ToInt32(Data, 20);

                if (EntryPoint <= LoadAddress || EntryPoint >= LoadAddress + Data.Length)
                    throw new Exception("Invalid entry point for FLASH loader");

                pCommand = ParameterArea;
                pArg1 = ParameterArea + 4;
                pArg2 = ParameterArea + 8;
                pResult = ParameterArea + 12;
            }

            public CustomStartStep QueueInvocation(int cmd, string arg1, string arg2, string dataFile, int dataOffset, int dataSize, bool loadSelf = false, string error = null)
            {
                List<string> cmds = new List<string>();

                if (loadSelf)
                {
                    if (FullPath.Contains(" "))
                        throw new Exception("FLASH loader stub path cannot contain spaces: " + FullPath);
                    cmds.Add(string.Format("restore {0} binary 0x{1:x} 0 0x{2:x}", FullPath.Replace('\\', '/'), LoadAddress, Data.Length));
                }

                if (dataFile != null)
                {
                    if (dataFile.Contains(" "))
                        throw new Exception("Programmed file path cannot contain spaces: " + FullPath);

                    cmds.Add(string.Format("restore {0} binary 0x{1:x} 0x{2:x} 0x{3:x}", dataFile.Replace('\\', '/'), DataBuffer - dataOffset, dataOffset, dataOffset + dataSize));
                }

                cmds.Add(string.Format("flushregs", EntryPoint));
                cmds.Add(string.Format("set $epc2=0x{0:x}", EntryPoint));
                cmds.Add(string.Format("set $ps=0x20", EntryPoint));
                cmds.Add(string.Format("set $sp=$$DEBUG:INITIAL_STACK_POINTER$$"));
                cmds.Add(string.Format("set *((unsigned *)0x{0:x})={1}", pCommand, cmd));
                cmds.Add(string.Format("set *((unsigned *)0x{0:x})={1}", pArg1, arg1));
                cmds.Add(string.Format("set *((unsigned *)0x{0:x})={1}", pArg2, arg2));
                cmds.Add(string.Format("set *((unsigned *)0x{0:x})={1}", pResult, uint.MaxValue));
                cmds.Add(string.Format("set $intclear=-1"));
                cmds.Add(string.Format("set $intenable=0"));
                cmds.Add("set $eps2=0x20");
                cmds.Add("set $icountlevel=0");
                cmds.Add(string.Format("-exec-continue"));

                return new CustomStartStep(cmds.ToArray())
                {
                    ResultVariable = string.Format("*((unsigned *)0x{0:x})", pResult),
                    ErrorMessage = error,
                    ProgressWeight = dataSize,
                    CanRetry = true,
                };
            }

            public void QueueRegionProgramming(List<CustomStartStep> cmds, ProgrammableRegion region)
            {
                cmds.Add(QueueInvocation(1, region.Offset.ToString(), region.Size.ToString(), null, 0, 0, false, string.Format("Failed to erase the FLASH region starting at 0x{0:x}", region.Offset)));

                for (int off = 0; off < region.Size;)
                {
                    int todo = Math.Min(region.Size - off, DataBufferSize);
                    int alignment = 4;
                    int alignedTodo = ((todo + alignment - 1) / alignment) * alignment;

                    cmds.Add(QueueInvocation(2, (region.Offset + off).ToString(), alignedTodo.ToString(), region.FileName, off, todo, false, string.Format("Failed to program the FLASH region starting at 0x{0:x}, offset 0x{1:x}, size 0x{2:x}", region.Offset, off, todo)));
                    off += todo;
                }
            }
        }
        public static CustomStartupSequence BuildSequence(BSPEngine.IDebugStartService service, ESP8266OpenOCDSettings settings, BSPEngine.LiveMemoryLineHandler lineHandler, bool programFLASH, string debugMethodDirectory = null)
        {
            List<CustomStartStep> cmds = new List<CustomStartStep>();
            cmds.Add(new CustomStartStep("mon reset halt",
                "-exec-next-instruction",
                "set $com_sysprogs_esp8266_wdcfg=0",
                "set $vecbase=0x40000000",
                "$$com.sysprogs.esp8266.interrupt_disable_command$$",
                "set $ccompare=0",
                "set $intclear=-1",
                "set $intenable=0",
                "set $eps2=0x20",
                "set $icountlevel=0"));

            var result = new CustomStartupSequence { Steps = cmds };
            var bspDict = service.SystemDictionary;
            var targetPath = service.TargetPath;

            string val;
            if (bspDict.TryGetValue("com.sysprogs.esp8266.load_flash", out val) && val == "1")  //Not a FLASHless project
            {
                if (programFLASH)
                {
                    string bspPath = bspDict["SYS:BSP_ROOT"];
                    List<ProgrammableRegion> regions;

                    if (!ESP32DebugController.BuildProgrammableBlocksFromSynthesizedESPIDFVariables(service, out regions))  //Same variable names are reused between ESP32 and ESP8266 for backward compatibility.
                        regions = BuildFLASHImages(service, settings, lineHandler);

                    string loader = bspPath + @"\sysprogs\flashprog\ESP8266FlashProg.bin";
                    if (!File.Exists(loader) && debugMethodDirectory != null)
                        loader = debugMethodDirectory + @"\sysprogs\flashprog\ESP8266FlashProg.bin";

                    if (!File.Exists(loader))
                        throw new Exception("FLASH loader not found: " + loader);

                    var parsedLoader = new ParsedFLASHLoader(loader);

                    cmds.Add(new CustomStartStep("print *((int *)0x60000900)", "set *((int *)0x60000900)=0"));
                    cmds.Add(parsedLoader.QueueInvocation(0, settings.ProgramSectorSize.ToString(), settings.EraseSectorSize.ToString(), null, 0, 0, true));
                    foreach (var region in regions)
                        parsedLoader.QueueRegionProgramming(cmds, region);
                }

                var resetMode = settings.ResetMode;
                if (resetMode == ResetMode.Soft)
                {
                    try
                    {
                        using (var elfFile = new ELFFile(targetPath))
                        {
                            string pathBase = Path.Combine(Path.GetDirectoryName(targetPath), Path.GetFileName(targetPath));
                            string status;
                            int appMode = ESP8266BinaryImage.DetectAppMode(elfFile, out status);
                            if (appMode != 0)
                            {
                                if (service.GUIService.Prompt("The soft reset mechanism is not compatible with the OTA images. Use the jump-to-entry reset instead?"))
                                    resetMode = ResetMode.Hard;
                            }
                        }
                    }
                    catch
                    {
                    }
                }

                if (resetMode == ResetMode.Soft || resetMode == ResetMode.JumpToEntry)
                {
                    string entry = "0x40000080";

                    if (resetMode == ResetMode.JumpToEntry)
                    {
                        using (ELFFile elf = new ELFFile(targetPath))
                        {
                            foreach (var sec in elf.AllSections)
                            {
                                if (!sec.PresentInMemory || !sec.HasData || sec.Type != ELFFile.SectionType.SHT_PROGBITS)
                                    continue;

                                bool isInRAM = false;
                                if (sec.VirtualAddress >= 0x3FFE8000 && sec.VirtualAddress < (0x3FFE8000 + 81920))
                                    isInRAM = true;
                                else if (sec.VirtualAddress >= 0x40100000 && sec.VirtualAddress <= (0x40100000 + 32768))
                                    isInRAM = true;

                                if (isInRAM)
                                {
                                    cmds.Add(new CustomStartStep(string.Format("restore {0} binary 0x{1:x} 0x{2:x} 0x{3:x}", targetPath.Replace('\\', '/'),
                                        sec.VirtualAddress - sec.OffsetInFile, sec.OffsetInFile, sec.OffsetInFile + sec.Size))
                                    { CheckResult = true, ErrorMessage = "Failed to program the " + sec.SectionName + " section" });
                                }
                            }
                        }

                        entry = "$$DEBUG:ENTRY_POINT$$";
                    }

                    cmds.Add(new CustomStartStep("set $ps=0x20",
                        "set $epc2=" + entry,
                        "set $sp=$$DEBUG:INITIAL_STACK_POINTER$$",
                        "set $vecbase=0x40000000",
                        "$$com.sysprogs.esp8266.interrupt_disable_command$$",
                        "set $intclear=-1",
                        "set $intenable=0",
                        "set $eps2=0x20",
                        "set $icountlevel=0"));
                    result.InitialHardBreakpointExpression = "*$$DEBUG:ENTRY_POINT$$";
                }
                else
                    cmds.Add(new CustomStartStep("mon reset halt"));
            }
            else
            {
                cmds.Add(new CustomStartStep("load",
                    "set $ps=0x20",
                    "set $epc2=$$DEBUG:ENTRY_POINT$$",
                    "set $sp=$$DEBUG:INITIAL_STACK_POINTER$$",
                    "set $vecbase=0x40000000",
                    "$$com.sysprogs.esp8266.interrupt_disable_command$$",
                    "set $ccompare=0",
                    "set $intclear=-1",
                    "set $intenable=0",
                    "set $eps2=0x20",
                    "set $icountlevel=0"));
            }

            return result;
        }

        public static List<ProgrammableRegion> BuildFLASHImages(BSPEngine.IDebugStartService service, IESP8266Settings settings, BSPEngine.LiveMemoryLineHandler lineHandler)
        {
            var bspDict = service.SystemDictionary;
            var targetPath = service.TargetPath;
            string bspPath = bspDict["SYS:BSP_ROOT"];
            string toolchainPath = bspDict["SYS:TOOLCHAIN_ROOT"];

            Regex rgBinFile = new Regex("^" + Path.GetFileName(targetPath) + "-0x([0-9a-fA-F]+)\\.bin$", RegexOptions.IgnoreCase);
            foreach (var fn in Directory.GetFiles(Path.GetDirectoryName(targetPath)))
                if (rgBinFile.IsMatch(Path.GetFileName(fn)))
                    File.Delete(fn);

            int initDataAddress = 0;
            if (!string.IsNullOrEmpty(settings.InitDataAddress))
            {
                initDataAddress = (int)(ESP32StartupSequence.TryParseNumber(settings.InitDataAddress) ?? 0);
            }
            else
            {
                switch (settings.FLASHSettings.Size)
                {
                    case ESP8266BinaryImage.FLASHSize.size4M:
                        initDataAddress = 0x7c000;
                        break;
                    case ESP8266BinaryImage.FLASHSize.size8M:
                        initDataAddress = 0xfc000;
                        break;
                    case ESP8266BinaryImage.FLASHSize.size16M:
                    case ESP8266BinaryImage.FLASHSize.size16M_c1:
                        initDataAddress = 0x1fc000;
                        break;
                    case ESP8266BinaryImage.FLASHSize.size32M:
                    case ESP8266BinaryImage.FLASHSize.size32M_c1:
                    case ESP8266BinaryImage.FLASHSize.size32M_c2:
                        initDataAddress = 0x3fc000;
                        break;
                }
            }

            List<ProgrammableRegion> regions = new List<ProgrammableRegion>();

            if (initDataAddress != 0)
            {
                string initFile = settings.InitDataFile;
                if (initFile != "")
                {
                    if (initFile == null)
                        initFile = ESPxxOpenOCDSettingsEditor.DefaultInitDataFile;

                    if (initFile.StartsWith("$$SYS:BSP_ROOT$$"))
                        initFile = bspDict["SYS:BSP_ROOT"] + initFile.Substring("$$SYS:BSP_ROOT$$".Length);
                    if (!Path.IsPathRooted(initFile))
                        initFile = Path.Combine(bspDict["SYS:PROJECT_DIR"], initFile);

                    if (!File.Exists(initFile))
                        throw new Exception("Missing initialization data file: " + initFile);
                    regions.Add(new ProgrammableRegion { FileName = initFile, Offset = initDataAddress, Size = File.ReadAllBytes(initFile).Length });
                }
            }

            if (settings.FLASHResources != null)
                foreach (var r in settings.FLASHResources)
                    if (r.Valid)
                        regions.Add(r.ToProgrammableRegion(service));

            using (var elfFile = new ELFFile(targetPath))
            {
                string pathBase = Path.Combine(Path.GetDirectoryName(targetPath), Path.GetFileName(targetPath));
                string status;
                int appMode = ESP8266BinaryImage.DetectAppMode(elfFile, out status);
                if (status != null && lineHandler != null)
                    lineHandler(status, true);

                if (appMode == 0)
                {
                    var img = ESP8266BinaryImage.MakeNonBootloaderImageFromELFFile(elfFile, settings.FLASHSettings);

                    string fn = pathBase + "-0x00000.bin";
                    using (var fs = new FileStream(fn, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
                    {
                        img.Save(fs);
                        regions.Add(new ProgrammableRegion { FileName = fn, Offset = 0, Size = (int)fs.Length });
                    }

                    foreach (var sec in ESP8266BinaryImage.GetFLASHSections(elfFile))
                    {
                        fn = string.Format("{0}-0x{1:x5}.bin", pathBase, sec.OffsetInFLASH);
                        using (var fs = new FileStream(fn, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
                        {
                            fs.Write(sec.Data, 0, sec.Data.Length);
                            regions.Add(new ProgrammableRegion { FileName = fn, Offset = (int)sec.OffsetInFLASH, Size = sec.Data.Length });
                        }
                    }
                }
                else
                {
                    var img = ESP8266BinaryImage.MakeBootloaderBasedImageFromELFFile(elfFile, settings.FLASHSettings, appMode);
                    var header = settings.FLASHSettings;

                    string bspRoot, bootloader;
                    if (!bspDict.TryGetValue("SYS:BSP_ROOT", out bspRoot) || !bspDict.TryGetValue("com.sysprogs.esp8266.bootloader", out bootloader))
                        throw new Exception("Cannot determine bootloader image path. Please check your BSP consistency.");

                    string fn = Path.Combine(bspRoot, bootloader);
                    if (!File.Exists(fn))
                        throw new Exception(fn + " not found. Cannot program OTA images.");

                    byte[] data = File.ReadAllBytes(fn);
                    data[2] = (byte)header.Mode;
                    data[3] = (byte)(((byte)header.Size << 4) | (byte)header.Frequency);
                    fn = string.Format("{0}-0x00000.bin", pathBase);
                    File.WriteAllBytes(fn, data);

                    regions.Add(new ProgrammableRegion { FileName = fn, Offset = 0, Size = File.ReadAllBytes(fn).Length });


                    fn = string.Format("{0}-0x{1:x5}.bin", pathBase, img.BootloaderImageOffset);
                    using (var fs = new FileStream(fn, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
                    {
                        img.Save(fs);
                        regions.Add(new ProgrammableRegion { FileName = fn, Offset = (int)img.BootloaderImageOffset, Size = (int)fs.Length });
                    }
                }
            }

            return regions;
        }
    }
}
