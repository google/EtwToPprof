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

using System;
using System.Collections.Generic;
using System.Linq;

using CommandLine;
using CommandLine.Text;

using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Symbols;
using Microsoft.Windows.EventTracing.Cpu;

namespace EtwToPprof
{
  class Program
  {
    class Options
    {
      [Usage(ApplicationAlias = "EtwToPprof")]
      public static IEnumerable<Example> Examples
      {
        get
        {
          return new List<Example>() {
            new Example("Export to specified pprof profile using default options", new UnParserSettings { PreferShortName = true },
              new Options { etlFileName = "trace.etl", outputFileName = "profile.pb.gz" }),
            new Example("Export samples from specified process names", new UnParserSettings { PreferShortName = true },
              new Options { etlFileName = "trace.etl", processFilter = "viz_perftests.exe,dwm.exe" }),
            new Example("Export samples from all processes from 10s to 30s", new UnParserSettings { PreferShortName = true },
              new Options { etlFileName = "trace.etl", processFilter = "*", timeStart = 10, timeEnd = 30 }),
            new Example("Export inlined functions and thread/process ids", new UnParserSettings { PreferShortName = true },
              new Options { etlFileName = "trace.etl", includeInlinedFunctions = true, includeProcessAndThreadIds = true})
          };
        }
      }

      [Value(0, MetaName = "etlFileName", Required = true, HelpText = "ETL trace file name.")]
      public string etlFileName { get; set; }

      [Option('o', "outputFileName", Required = false, Default = "profile.pb.gz",
              HelpText = "Output file name for gzipped pprof profile.")]
      public string outputFileName { get; set; }

      [Option('p', "processFilter", Required = false, Default = "chrome.exe,dwm.exe,audiodg.exe",
              HelpText = "Filter for process names (comma-separated) to be included in the exported profile. "
                         + "All processes will be exported if set to *.")]
      public string processFilter { get; set; }

      [Option("includeInlinedFunctions", Required = false, Default = false,
              HelpText = "Whether inlined functions should be included in the exported profile (slow).")]
      public bool includeInlinedFunctions { get; set; }

      [Option("stripSourceFileNamePrefix", Required = false, Default = @"^c:/b/s/w/ir/cache/builder/",
              HelpText = "Prefix regex to strip out of source file names in the exported profile.")]
      public string stripSourceFileNamePrefix { get; set; }

      [Option("timeStart", Required = false, Default = null,
              HelpText = "Start of time range to export in seconds")]
      public decimal? timeStart { get; set; }

      [Option("timeEnd", Required = false, Default = null,
              HelpText = "End of time range to export in seconds")]
      public decimal? timeEnd { get; set; }

      [Option("includeProcessAndThreadIds", Required = false, Default = false,
              HelpText = "Whether process and thread ids are included in the exported profile.")]
      public bool includeProcessAndThreadIds { get; set; }
    }

    static void Main(string[] args)
    {
      CommandLine.Parser.Default.ParseArguments<Options>(args).WithParsed(RunWithOptions);
    }

    static void RunWithOptions(Options opts)
    {
      using (ITraceProcessor trace = TraceProcessor.Create(opts.etlFileName))
      {
        IPendingResult<ICpuSampleDataSource> pendingCpuSampleData = trace.UseCpuSamplingData();
        IPendingResult<ISymbolDataSource> pendingSymbolData = trace.UseSymbols();

        trace.Process();

        ISymbolDataSource symbolData = pendingSymbolData.Result;
        ICpuSampleDataSource cpuSampleData = pendingCpuSampleData.Result;

        var symbolProgress = new Progress<SymbolLoadingProgress>(progress =>
        {
          Console.Write("\r{0:P} {1} of {2} symbols processed ({3} loaded)",
                        (double)progress.ImagesProcessed / progress.ImagesTotal,
                        progress.ImagesProcessed,
                        progress.ImagesTotal,
                        progress.ImagesLoaded);
        });
        symbolData.LoadSymbolsAsync(
            SymCachePath.Automatic, SymbolPath.Automatic, symbolProgress)
            .GetAwaiter().GetResult();
        Console.WriteLine();

        var profileWriter = new ProfileWriter(opts.etlFileName,
                                              opts.includeInlinedFunctions,
                                              opts.includeProcessAndThreadIds,
                                              opts.stripSourceFileNamePrefix);

        var timeStart = opts.timeStart ?? 0;
        var timeEnd = opts.timeEnd ?? decimal.MaxValue;

        var exportAllProcesses = opts.processFilter == "*";
        var processFilterSet = new HashSet<string>(
            opts.processFilter.Trim().Split(",", StringSplitOptions.RemoveEmptyEntries));

        for (int i = 0; i < cpuSampleData.Samples.Count; i++)
        {
          if (i % 100 == 0)
          {
            Console.Write("\r{0:P} {1} of {2} samples processed",
                (double)i / cpuSampleData.Samples.Count, i, cpuSampleData.Samples.Count);
          }

          var cpuSample = cpuSampleData.Samples[i];

          if ((cpuSample.IsExecutingDeferredProcedureCall ?? false) ||
              (cpuSample.IsExecutingInterruptServicingRoutine ?? false))
            continue;

          if (!exportAllProcesses)
          {
            var processImage = cpuSample.Process.Images
                .FirstOrDefault(image => image.FileName == cpuSample.Process.ImageName);

            string imagePath = processImage?.Path ?? cpuSample.Process.ImageName;

            if (!processFilterSet.Any(filter => imagePath.Contains(filter.Replace("/", "\\"))))
              continue;
          }

          var timestamp = cpuSample.Timestamp.RelativeTimestamp.TotalSeconds;
          if (timestamp < timeStart || timestamp > timeEnd)
            continue;

          profileWriter.AddSample(cpuSample);
        }
        Console.WriteLine();

        long outputSize = profileWriter.Write(opts.outputFileName);
        Console.WriteLine("Wrote {0:N0} bytes to {1}", outputSize, opts.outputFileName);
      }
    }
  }
}
