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
    }

    public void AddSample(ICpuSample sample)
    {
      if ((sample.IsExecutingDeferredProcedureCall ?? false) ||
          (sample.IsExecutingInterruptServicingRoutine ?? false))
        return;

      var timestamp = sample.Timestamp.RelativeTimestamp.TotalSeconds;
      if (timestamp < options.timeStart || timestamp > options.timeEnd)
        return;

      if (options.processFilterSet?.Count != 0)
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
            sampleProto.LocationId.Add(GetLocationId(stackFrame.Symbol));
          }
          else
          {
            string imageName = stackFrame.Image?.FileName ?? "<unknown>";
            string functionLabel = "<unknown>";
            sampleProto.LocationId.Add(
              GetPseudoLocationId(processId, imageName, null, functionLabel));
          }
        }
        string processName = sample.Process.ImageName;
        string threadLabel = sample.Thread?.Name;
        if (threadLabel == null || threadLabel == "")
          threadLabel = "anonymous thread";
        if (options.includeProcessAndThreadIds)
        {
          threadLabel = String.Format("{0} ({1})", threadLabel, sample.Thread?.Id ?? 0);
        }
        sampleProto.LocationId.Add(
          GetPseudoLocationId(processId, processName, sample.Thread?.StartAddress, threadLabel));

        string processLabel = processName;
        if (options.includeProcessIds || options.includeProcessAndThreadIds)
        {
          processLabel = String.Format("{0} ({1})", processName, processId);
        }
        sampleProto.LocationId.Add(
          GetPseudoLocationId(processId, processName, sample.Process.ObjectAddress, processLabel));

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

        var line = new pb.Line();
        line.FunctionId = GetFunctionId(imageName, label);
        locationProto.Line.Add(line);

        profile.Location.Add(locationProto);
      }
      return locationId;
    }

    ulong GetLocationId(IStackSymbol stackSymbol)
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
    ulong nextLocationId;

    Dictionary<Function, ulong> functions;
    ulong nextFunctionId;

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
