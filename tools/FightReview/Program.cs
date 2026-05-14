using FightReview;

return await ProgramMain.RunAsync(args);

internal static class ProgramMain
{
    public static Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            if (options.Help)
            {
                Options.PrintUsage();
                return Task.FromResult(0);
            }

            var xcai = XcaiLogReader.Read(options.XcaiPath ?? throw new InvalidOperationException("--xcai is required."));
            BmrReplayData bmr;
            MatchResult match;
            if (options.AutoMatch)
            {
                bmr = BmrReplayReader.AutoMatch(options.XcaiPath!, xcai, options.BmrDirectory ?? throw new InvalidOperationException("--bmr-dir is required with --auto-match."), out match);
            }
            else
            {
                bmr = BmrReplayReader.Read(options.BmrPath ?? throw new InvalidOperationException("--bmr is required unless --auto-match is used."));
                match = BmrReplayReader.ScoreExplicitMatch(xcai, bmr);
            }

            var incidents = IncidentDetector.Detect(xcai);
            var output = options.OutputDirectory ?? DefaultOutputDirectory(options.XcaiPath!);
            ArtifactWriter.Write(new ReviewBundle(xcai, bmr.Summary, match, incidents), output);
            Console.WriteLine($"Wrote fight review artifacts to: {Path.GetFullPath(output)}");
            Console.WriteLine($"Detected incidents: {incidents.Count}");
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return Task.FromResult(1);
        }
    }

    private static string DefaultOutputDirectory(string xcaiPath)
    {
        var fullPath = Path.GetFullPath(xcaiPath);
        var parent = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        var name = Path.GetFileNameWithoutExtension(fullPath);
        return Path.Combine(parent, $"{name}-review");
    }

    private sealed record Options(
        string? XcaiPath,
        string? BmrPath,
        string? BmrDirectory,
        string? OutputDirectory,
        bool AutoMatch,
        bool Help)
    {
        public static Options Parse(string[] args)
        {
            string? xcai = null;
            string? bmr = null;
            string? bmrDirectory = null;
            string? output = null;
            var autoMatch = false;
            var help = false;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--xcai":
                        xcai = ReadValue(args, ref i, "--xcai");
                        break;
                    case "--bmr":
                        bmr = ReadValue(args, ref i, "--bmr");
                        break;
                    case "--bmr-dir":
                        bmrDirectory = ReadValue(args, ref i, "--bmr-dir");
                        break;
                    case "--out":
                        output = ReadValue(args, ref i, "--out");
                        break;
                    case "--auto-match":
                        autoMatch = true;
                        break;
                    case "-h":
                    case "--help":
                        help = true;
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown argument '{args[i]}'.");
                }
            }

            if (!help)
            {
                if (xcai == null)
                {
                    throw new InvalidOperationException("--xcai is required.");
                }

                if (autoMatch && bmrDirectory == null)
                {
                    throw new InvalidOperationException("--bmr-dir is required with --auto-match.");
                }

                if (!autoMatch && bmr == null)
                {
                    throw new InvalidOperationException("--bmr is required unless --auto-match is used.");
                }
            }

            return new Options(xcai, bmr, bmrDirectory, output, autoMatch, help);
        }

        public static void PrintUsage()
        {
            Console.WriteLine("""
XCAI Fight Review

Usage:
  dotnet run --project tools/FightReview -- --xcai <xcai.jsonl> --bmr <bmr.log> [--out <dir>]
  dotnet run --project tools/FightReview -- --xcai <xcai.jsonl> --bmr-dir <dir> --auto-match [--out <dir>]

Outputs:
  fight.normalized.jsonl
  fight.report.md
  fight.html
  incidents/*.json
  agent.improvement.json
""");
        }

        private static string ReadValue(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length)
            {
                throw new InvalidOperationException($"{option} requires a value.");
            }

            return args[++index];
        }
    }
}
