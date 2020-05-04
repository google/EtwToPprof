using System;
using System.Collections.Generic;

using CommandLine;

using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Symbols;
using Microsoft.Windows.EventTracing.Cpu;

namespace EtwToPprof
{
  class Program
  {
    class Options
    {
      [Option('o', "outputFileName", Required = false, Default = "profile.pb.gz",
              HelpText = "Output file name for gzipped pprof profile.")]
      public string outputFileName { get; set; }

      [Option('p', "processFilter", Required = false, Default = "chrome.exe",
              HelpText = "Filter for process name to be included in the exported profile.")]
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

      [Value(0, MetaName = "etlFileName", Required = true,  HelpText = "ETL trace file name")]
      public string etlFileName { get; set; }
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

        var symbolProgress = new Progress<SymbolLoadingProgress>(progress => {
            Console.Write("\r{0:P} {1} of {2} symbols processed ({3} loaded)",
                          (double)progress.ImagesProcessed / progress.ImagesTotal,
                          progress.ImagesProcessed,
                          progress.ImagesTotal,
                          progress.ImagesLoaded);
        });
        var includedProcesses = new string[] { opts.processFilter };
        symbolData.LoadSymbolsAsync(
            SymCachePath.Automatic, SymbolPath.Automatic, symbolProgress, includedProcesses)
            .GetAwaiter().GetResult();
        Console.WriteLine();

        var profileWriter = new ProfileWriter(opts.etlFileName,
                                              opts.includeInlinedFunctions,
                                              opts.stripSourceFileNamePrefix);

        var timeStart = opts.timeStart ?? 0;
        var timeEnd = opts.timeEnd ?? decimal.MaxValue;

        for (int i = 0; i < cpuSampleData.Samples.Count; i++)
        {
            if (i % 100 == 0) {
                Console.Write("\r{0:P} {1} of {2} samples processed",
                    (double)i / cpuSampleData.Samples.Count, i, cpuSampleData.Samples.Count);
            }

            var cpuSample = cpuSampleData.Samples[i];

            if ((cpuSample.IsExecutingDeferredProcedureCall ?? false) ||
                (cpuSample.IsExecutingInterruptServicingRoutine ?? false))
                continue;

            if (cpuSample.Process.ImageName != opts.processFilter)
                continue;

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
