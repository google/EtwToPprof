// Copyright 2020 Google LLC
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using Google.Protobuf;
using pb = Google.Pprof.Protobuf;

using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Cpu;
using Microsoft.Windows.EventTracing.Processes;
using Microsoft.Windows.EventTracing.Symbols;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;

namespace EtwToPprof
{
  class ProfileWriter
  {
    public struct Options
    {
      public string etlFileName { get; set; }
      public bool includeInlinedFunctions { get; set; }
      public bool includeProcessIds { get; set; }
      public bool includeProcessAndThreadIds { get; set; }
      public bool splitChromeProcesses { get; set; }
      public string stripSourceFileNamePrefix { get; set; }
      public decimal timeStart { get; set; }
      public decimal timeEnd { get; set; }
      public HashSet<string> processFilterSet { get; set; }
    }

    public ProfileWriter(Options options)
    {
      this.options = options;

      stripSourceFileNamePrefixRegex = new Regex(options.stripSourceFileNamePrefix,
                                                 RegexOptions.Compiled | RegexOptions.IgnoreCase);

      profile = new pb.Profile();
      profile.StringTable.Add("");
      strings = new Dictionary<string, long>();
      strings.Add("", 0);
      nextStringId = 1;

      var cpuTimeValueType = new pb.ValueType();
      cpuTimeValueType.Type = GetStringId("cpu");
      cpuTimeValueType.Unit = GetStringId("nanoseconds");
      profile.SampleType.Add(cpuTimeValueType);

      locations = new Dictionary<Location, ulong>();
      nextLocationId = 1;

      functions = new Dictionary<Function, ulong>();
      nextFunctionId = 1;

      mappings = new Dictionary<string, ulong>();
      nextMappingId = 1;

      unsymbolizedLocations = new Dictionary<ulong, ulong>();

      mappingsWithUnsymbolized = new HashSet<ulong>();
      // Maps mapping ID -> absolute base address of the loaded image.
      // Used to convert absolute VAs to RVAs for Location.address.
      mappingBaseAddresses = new Dictionary<ulong, ulong>();
    }

    public void AddSample(ICpuSample sample)
    {
      if ((sample.IsExecutingDeferredProcedureCall ?? false) ||
          (sample.IsExecutingInterruptServicingRoutine ?? false))
        return;

      var timestamp = sample.Timestamp.RelativeTimestamp.TotalSeconds;
      if (timestamp < options.timeStart || timestamp > options.timeEnd)
        return;

      if (options.processFilterSet?.Count > 0)
      {
        var processImage = sample.Process.Images.FirstOrDefault(
            image => image.FileName == sample.Process.ImageName);

        string imagePath = processImage?.Path ?? sample.Process.ImageName;

        if (!options.processFilterSet.Any(filter => imagePath.Contains(filter.Replace("/", "\\"))))
          return;
      }

      wallTimeStart = Math.Min(wallTimeStart, timestamp);
      wallTimeEnd = Math.Max(wallTimeEnd, timestamp);

      var sampleProto = new pb.Sample();
      sampleProto.Value.Add(sample.Weight.Nanoseconds);
      if (sample.Stack != null && sample.Stack.Frames.Count != 0)
      {
        int processId = sample.Process.Id;
        foreach (var stackFrame in sample.Stack.Frames)
        {
          if (stackFrame.HasValue && stackFrame.Symbol != null)
          {
            sampleProto.LocationId.Add(GetLocationId(stackFrame.Symbol, stackFrame.Address));
          }
          else if (stackFrame.HasValue)
          {
            sampleProto.LocationId.Add(
              GetUnsymbolizedLocationId(stackFrame.Address, stackFrame.Image));
          }
        }
        string processName = sample.Process.ImageName;
        string threadLabel = sample.Thread?.Name;
        if (threadLabel == null || threadLabel == "" || threadLabel.StartsWith("0x"))
          threadLabel = "anonymous thread";
        if (options.includeProcessAndThreadIds)
        {
          threadLabel = String.Format("{0} ({1})", threadLabel, sample.Thread?.Id ?? 0);
        }
        // When process IDs are not included, use 0/null so that processes
        // with the same label merge into a single flame graph entry.
        int threadPseudoProcessId = (options.includeProcessIds || options.includeProcessAndThreadIds)
            ? processId : 0;
        sampleProto.LocationId.Add(
          GetPseudoLocationId(threadPseudoProcessId, processName, sample.Thread?.StartAddress, threadLabel));

        string processLabel = processName;
        if (options.splitChromeProcesses && processName == "chrome.exe" &&
            sample.Process.CommandLine != null)
        {
          var commandLineSplit = sample.Process.CommandLine.Split();
          foreach (string commandLineArg in commandLineSplit)
          {
            const string kProcessTypeParam = "--type=";
            if (commandLineArg.StartsWith(kProcessTypeParam))
            {
              string chromeProcessType = commandLineArg.Substring(kProcessTypeParam.Length);

              const string kUtilityProcessType = "utility";
              const string kUtilitySubTypeParam = "--utility-sub-type=";
              if (chromeProcessType == kUtilityProcessType)
              {
                var utilitySubType = commandLineSplit.First(s => s.StartsWith(kUtilitySubTypeParam));
                if (utilitySubType != null)
                {
                  processLabel = processLabel +
                      $" ({utilitySubType.Substring(kUtilitySubTypeParam.Length)})";
                  break;
                }
              }

              processLabel = processLabel + $" ({chromeProcessType})";
            }
          }
        }
        if (options.includeProcessIds || options.includeProcessAndThreadIds)
        {
          processLabel = processLabel + $" ({processId})";
        }
        // When process IDs are not included, use 0/null so that processes
        // with the same label merge into a single flame graph entry.
        int pseudoProcessId = (options.includeProcessIds || options.includeProcessAndThreadIds)
            ? processId : 0;
        sampleProto.LocationId.Add(
          GetPseudoLocationId(pseudoProcessId, processName, null, processLabel));

        if (processThreadCpuTimes.ContainsKey(processLabel))
        {
          Dictionary<string, decimal> threadCpuTimes = processThreadCpuTimes[processLabel];
          if (threadCpuTimes.ContainsKey(threadLabel))
          {
            threadCpuTimes[threadLabel] += sample.Weight.TotalMilliseconds;
          }
          else
          {
            threadCpuTimes[threadLabel] = sample.Weight.TotalMilliseconds;
          }
        }
        else
        {
          processThreadCpuTimes[processLabel] = new Dictionary<string, decimal>();
          processThreadCpuTimes[processLabel][threadLabel] = sample.Weight.TotalMilliseconds;
        }

        if (processCpuTimes.ContainsKey(processLabel))
        {
          processCpuTimes[processLabel] += sample.Weight.TotalMilliseconds;
        }
        else
        {
          processCpuTimes[processLabel] = sample.Weight.TotalMilliseconds;
        }

        totalCpuTime += sample.Weight.TotalMilliseconds;
      }

      profile.Sample.Add(sampleProto);
    }

    public long Write(string outputFileName)
    {
      profile.Comment.Add(GetStringId($"Converted by EtwToPprof from {Path.GetFileName(options.etlFileName)}"));
      if (wallTimeStart < wallTimeEnd)
      {
        decimal wallTimeMs = (wallTimeEnd - wallTimeStart) * 1000;
        profile.Comment.Add(GetStringId($"Wall time {wallTimeMs:F} ms"));
        profile.Comment.Add(GetStringId($"CPU time {totalCpuTime:F} ms ({totalCpuTime / wallTimeMs:P})"));

        var sortedProcesses = processCpuTimes.Keys.ToList();
        sortedProcesses.Sort((a, b) => -processCpuTimes[a].CompareTo(processCpuTimes[b]));

        foreach (var processLabel in sortedProcesses)
        {
          decimal processCpuTime = processCpuTimes[processLabel];
          profile.Comment.Add(GetStringId($"  {processLabel} {processCpuTime:F} ms ({processCpuTime / wallTimeMs:P})"));

          var threadCpuTimes = processThreadCpuTimes[processLabel];

          var sortedThreads = threadCpuTimes.Keys.ToList();
          sortedThreads.Sort((a, b) => -threadCpuTimes[a].CompareTo(threadCpuTimes[b]));

          foreach (var threadLabel in sortedThreads)
          {
            var threadCpuTime = threadCpuTimes[threadLabel];
            profile.Comment.Add(GetStringId($"    {threadLabel} {threadCpuTime:F} ms ({threadCpuTime / wallTimeMs:P})"));
          }
        }
      }
      else
      {
        profile.Comment.Add(GetStringId("No samples exported"));
      }
      // Set Has* flags on mappings: only claim symbolization for mappings
      // where all locations were successfully resolved.
      foreach (var mappingProto in profile.Mapping)
      {
        bool fullySymbolized = !mappingsWithUnsymbolized.Contains(mappingProto.Id);
        mappingProto.HasFunctions = fullySymbolized;
        mappingProto.HasFilenames = fullySymbolized;
        mappingProto.HasLineNumbers = fullySymbolized;
        mappingProto.HasInlineFrames = fullySymbolized && options.includeInlinedFunctions;
      }
      using (FileStream output = File.Create(outputFileName))
      {
        using (GZipStream gzip = new GZipStream(output, CompressionMode.Compress))
        {
          using (CodedOutputStream serialized = new CodedOutputStream(gzip))
          {
            profile.WriteTo(serialized);
            return output.Length;
          }
        }
      }
    }

    readonly struct Location
    {
      public Location(int processId, string imagePath, Address? functionAddress, string functionName)
      {
        ProcessId = processId;
        ImagePath = imagePath;
        FunctionAddress = functionAddress;
        FunctionName = functionName;
      }
      int ProcessId { get; }
      string ImagePath { get; }
      Address? FunctionAddress { get; }
      string FunctionName { get; }

      public override bool Equals(object other)
      {
        return other is Location l
               && l.ProcessId == ProcessId
               && l.ImagePath == ImagePath
               && l.FunctionAddress == FunctionAddress
               && l.FunctionName == FunctionName;
      }

      public override int GetHashCode()
      {
        return (ProcessId, ImagePath, FunctionAddress, FunctionName).GetHashCode();
      }
    }

    ulong GetPseudoLocationId(int processId, string imageName, Address? address, string label)
    {
      var location = new Location(processId, imageName, address, label);
      ulong locationId;
      if (!locations.TryGetValue(location, out locationId))
      {
        locationId = nextLocationId++;
        locations.Add(location, locationId);

        var locationProto = new pb.Location();
        locationProto.Id = locationId;
        if (address.HasValue)
          locationProto.Address = unchecked((ulong)address.Value.Value);

        var line = new pb.Line();
        line.FunctionId = GetFunctionId(imageName, label);
        locationProto.Line.Add(line);

        profile.Location.Add(locationProto);
      }
      return locationId;
    }

    ulong GetUnsymbolizedLocationId(Address address, IImage image)
    {
      // Use the raw address as the dedup key for unsymbolized frames.
      ulong addr = unchecked((ulong)address.Value);
      if (!unsymbolizedLocations.TryGetValue(addr, out ulong locationId))
      {
        locationId = nextLocationId++;
        unsymbolizedLocations.Add(addr, locationId);

        var locationProto = new pb.Location();
        locationProto.Id = locationId;
        locationProto.Address = addr;
        if (image != null)
        {
          ulong mid = GetMappingId(image);
          locationProto.MappingId = mid;
          // Convert absolute VA to RVA (see comment in GetMappingId).
          locationProto.Address = addr - mappingBaseAddresses[mid];
          mappingsWithUnsymbolized.Add(mid);
        }

        // No Line entries — leaves the location bare for offline symbolization.
        profile.Location.Add(locationProto);
      }
      return locationId;
    }

    ulong GetLocationId(IStackSymbol stackSymbol, Address instructionAddress)
    {
      var processId = stackSymbol.Image?.ProcessId ?? 0;
      var imageName = stackSymbol.Image?.FileName;
      var imagePath = stackSymbol.Image?.Path ?? "<unknown>";
      var functionAddress = stackSymbol.AddressRange.BaseAddress;
      var functionName = stackSymbol.FunctionName;

      var location = new Location(processId, imagePath, functionAddress, functionName);

      ulong locationId;
      if (!locations.TryGetValue(location, out locationId))
      {
        locationId = nextLocationId++;
        locations.Add(location, locationId);

        var locationProto = new pb.Location();
        locationProto.Id = locationId;
        // Store the RVA (see comment in GetMappingId).
        ulong absAddr = unchecked((ulong)instructionAddress.Value);
        locationProto.Address = absAddr;
        if (stackSymbol.Image != null)
        {
          ulong mid = GetMappingId(stackSymbol.Image);
          locationProto.MappingId = mid;
          locationProto.Address = absAddr - mappingBaseAddresses[mid];
        }

        pb.Line line;
        if (options.includeInlinedFunctions && stackSymbol.InlinedFunctionNames != null)
        {
          foreach (var inlineFunctionName in stackSymbol.InlinedFunctionNames)
          {
            line = new pb.Line();
            line.FunctionId = GetFunctionId(imageName, inlineFunctionName);
            locationProto.Line.Add(line);
          }
        }
        line = new pb.Line();
        line.FunctionId = GetFunctionId(imageName, functionName, stackSymbol.SourceFileName);
        line.Line_ = stackSymbol.SourceLineNumber;
        locationProto.Line.Add(line);

        profile.Location.Add(locationProto);
      }
      return locationId;
    }

    readonly struct Function
    {
      public Function(string imageName, string functionName)
      {
        ImageName = imageName;
        FunctionName = functionName;
      }
      string ImageName { get; }
      string FunctionName { get; }

      public override bool Equals(object other)
      {
        return other is Function f && f.ImageName == ImageName && f.FunctionName == FunctionName;
      }

      public override int GetHashCode()
      {
        return (ImageName, FunctionName).GetHashCode();
      }

      public override string ToString()
      {
        return String.Format("{0}!{1}", ImageName, FunctionName);
      }
    }

    ulong GetFunctionId(string imageName, string functionName, string sourceFileName = null)
    {
      ulong functionId;
      var function = new Function(imageName, functionName);
      if (!functions.TryGetValue(function, out functionId))
      {
        var functionProto = new pb.Function();
        functionProto.Id = nextFunctionId++;
        functionProto.Name = GetStringId(functionName ?? function.ToString());
        functionProto.SystemName = GetStringId(function.ToString());
        if (sourceFileName == null)
        {
          sourceFileName = imageName;
        }
        else
        {
          sourceFileName = sourceFileName.Replace('\\', '/');
          sourceFileName = stripSourceFileNamePrefixRegex.Replace(sourceFileName, "");
        }
        functionProto.Filename = GetStringId(sourceFileName);

        functionId = functionProto.Id;
        functions.Add(function, functionId);
        profile.Function.Add(functionProto);
      }
      return functionId;
    }

    long GetStringId(string str)
    {
      long stringId;
      if (!strings.TryGetValue(str, out stringId))
      {
        stringId = nextStringId++;
        strings.Add(str, stringId);
        profile.StringTable.Add(str);
      }
      return stringId;
    }

    private readonly Options options;

    Dictionary<Location, ulong> locations;
    Dictionary<ulong, ulong> unsymbolizedLocations;
    ulong nextLocationId;

    Dictionary<Function, ulong> functions;
    ulong nextFunctionId;

    Dictionary<string, ulong> mappings;
    ulong nextMappingId;
    HashSet<ulong> mappingsWithUnsymbolized;
    // Maps mapping ID -> absolute base address of the loaded image.
    // Used to convert absolute VAs to RVAs for Location.address.
    Dictionary<ulong, ulong> mappingBaseAddresses;

    static string FormatBreakpadBuildId(IImage image)
    {
      if (image.Pdb == null)
        return null;
      return image.Pdb.Id.ToString("N").ToLowerInvariant()
           + image.Pdb.Age.ToString("x");
    }

    ulong GetMappingId(IImage image)
    {
      // Key by image path to deduplicate mappings for the same binary.
      string key = image.Path ?? image.FileName ?? "<unknown>";
      ulong mappingId;
      if (!mappings.TryGetValue(key, out mappingId))
      {
        mappingId = nextMappingId++;
        mappings.Add(key, mappingId);

        var mappingProto = new pb.Mapping();
        mappingProto.Id = mappingId;

        // Workaround for pprof symbolization servers that assume ELF binaries:
        // Some servers reject memory_start values that don't match standard
        // Linux load addresses (0, 0x400000, 0x8048000) when ElfHeaders are
        // absent. Windows PE/PDB binaries never have ElfHeaders, so any real
        // Windows load address causes a symbolization failure.
        // By setting memory_start=0 and memory_limit=module_size, and storing
        // RVAs in Location.address, we ensure compatibility with servers that
        // use memory_start==0 as a passthrough for RVA-based symbol lookup.
        ulong baseAddr = unchecked((ulong)image.AddressRange.BaseAddress.Value);
        mappingBaseAddresses.Add(mappingId, baseAddr);
        mappingProto.MemoryStart = 0;
        mappingProto.MemoryLimit = (ulong)image.Size.Bytes;
        mappingProto.FileOffset = 0;
        mappingProto.Filename = GetStringId(image.Path ?? image.FileName ?? "<unknown>");

        string buildId = FormatBreakpadBuildId(image);
        if (buildId != null)
          mappingProto.BuildId = GetStringId(buildId);

        // Has* flags are finalized in Write() after all samples are processed.

        profile.Mapping.Add(mappingProto);
      }
      return mappingId;
    }

    Dictionary<string, long> strings;
    long nextStringId;

    Regex stripSourceFileNamePrefixRegex;

    decimal wallTimeStart = decimal.MaxValue;
    decimal wallTimeEnd = 0;

    decimal totalCpuTime = 0;
    Dictionary<string, decimal> processCpuTimes = new Dictionary<string, decimal>();
    Dictionary<string, Dictionary<string, decimal>> processThreadCpuTimes = new Dictionary<string, Dictionary<string, decimal>>();

    pb.Profile profile;
  }
}
