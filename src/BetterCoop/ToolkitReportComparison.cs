using System.Globalization;
using System.Text;

namespace BetterCoop;

public enum ReportComparisonConfidence
{
  Low,
  Medium,
  High
}

public sealed record ParsedToolkitReport(
    int Format,
    string BetterCoopVersion,
    DateTimeOffset CapturedUtc,
    string GameVersion,
    string SessionId,
    string Role,
    string RuntimeState,
    string PackageHealth,
    string ErrorCode,
    IReadOnlyList<string> Network,
    IReadOnlyList<string> Environment,
    IReadOnlyList<string> Timeline);

public sealed record ToolkitReportComparisonResult(
    bool Structured,
    ReportComparisonConfidence Confidence,
    int TotalDifferences,
    string Text);

public static class ToolkitReportComparison
{
  public const int SupportedFormat = 2;
  public const int MaxInputBytes = 1024 * 1024;
  public const int MaxDisplayedDifferences = 200;
  public const string ClipboardSeparator =
      "\n--- BETTERCOOP REPORT B ---\n";

  private const int MaxLines = 4096;
  private const int MaxLineChars = 8192;

  public static bool TrySplitClipboard(
      string input,
      out string first,
      out string second,
      out string error)
  {
    first = string.Empty;
    second = string.Empty;
    error = string.Empty;
    string normalized = NormalizeNewlines(input);
    int separator = normalized.IndexOf(
        ClipboardSeparator,
        StringComparison.Ordinal);
    if (separator < 0
        || normalized.IndexOf(
            ClipboardSeparator,
            separator + ClipboardSeparator.Length,
            StringComparison.Ordinal) >= 0)
    {
      error =
          "Clipboard must contain exactly two reports separated by "
          + "--- BETTERCOOP REPORT B ---.";
      return false;
    }

    first = normalized[..separator];
    second = normalized[(separator + ClipboardSeparator.Length)..];
    return WithinInputBound(first) && WithinInputBound(second)
        ? true
        : Fail(
            "Each report must be at most 1 MiB.",
            out error);
  }

  public static ToolkitReportComparisonResult Compare(
      string firstText,
      string secondText)
  {
    if (!TryParse(firstText, out ParsedToolkitReport first, out string error))
    {
      return Invalid("Report A: " + error);
    }

    if (!TryParse(secondText, out ParsedToolkitReport second, out error))
    {
      return Invalid("Report B: " + error);
    }

    List<string> limitations = [];
    bool compatible = first.Format == second.Format
        && first.Format == SupportedFormat;
    if (!compatible)
    {
      limitations.Add(
          $"Report formats are incompatible ({first.Format} vs {second.Format}); "
          + $"only format {SupportedFormat} is supported.");
    }

    bool sameSession = IsSessionId(first.SessionId)
        && first.SessionId == second.SessionId;
    if (!sameSession)
    {
      limitations.Add(
          first.SessionId == "unavailable"
              || second.SessionId == "unavailable"
              ? "A shared session ID is unavailable; reports were not auto-paired."
              : "Session IDs differ; reports were not merged.");
    }

    double captureDelta = Math.Abs(
        (first.CapturedUtc - second.CapturedUtc).TotalSeconds);
    bool aligned = captureDelta <= 30;
    if (!aligned)
    {
      limitations.Add(
          $"Capture times differ by {captureDelta:F1}s; timeline alignment is unavailable.");
    }

    bool structured = compatible && sameSession && aligned;
    ReportComparisonConfidence confidence = !structured
        ? ReportComparisonConfidence.Low
        : captureDelta <= 2
            ? ReportComparisonConfidence.High
            : ReportComparisonConfidence.Medium;

    List<string> common = [];
    List<string> differences = [];
    int totalDifferences = 0;
    CompareValue(
        "BetterCoop version",
        first.BetterCoopVersion,
        second.BetterCoopVersion,
        common,
        differences,
        ref totalDifferences);
    CompareValue(
        "Game version",
        first.GameVersion,
        second.GameVersion,
        common,
        differences,
        ref totalDifferences);
    CompareValue(
        "Role",
        first.Role,
        second.Role,
        common,
        differences,
        ref totalDifferences);
    CompareValue(
        "Error",
        first.ErrorCode,
        second.ErrorCode,
        common,
        differences,
        ref totalDifferences);
    CompareValue(
        "Package health",
        first.PackageHealth,
        second.PackageHealth,
        common,
        differences,
        ref totalDifferences);
    CompareSequence(
        "Environment",
        first.Environment,
        second.Environment,
        common,
        differences,
        ref totalDifferences);

    string firstDivergence =
        "Unavailable because the reports could not be safely aligned.";
    if (structured)
    {
      firstDivergence = FirstDivergence(first, second);
      CompareSequence(
          "Timeline",
          first.Timeline.Select(CanonicalTimeline).ToArray(),
          second.Timeline.Select(CanonicalTimeline).ToArray(),
          common,
          differences,
          ref totalDifferences);
      CompareSequence(
          "Network",
          first.Network,
          second.Network,
          common,
          differences,
          ref totalDifferences);
    }

    List<string> lines =
    [
        "BetterCoop offline report comparison",
            $"Structured comparison: {(structured ? "available" : "unavailable")}",
            $"Reports: A={first.Role} {first.CapturedUtc:O}; B={second.Role} {second.CapturedUtc:O}",
            $"Session: {(sameSession ? first.SessionId : "not aligned")}",
            $"Capture delta: {captureDelta:F1}s",
            $"Observable-divergence confidence: {confidence}",
            "Root cause: not established by report comparison [Low confidence].",
            string.Empty,
            "First observable divergence:",
            "  " + firstDivergence,
            string.Empty,
            "Commonalities:"
    ];
    lines.AddRange(common.Count == 0
        ? ["  <none confirmed>"]
        : common.Select(value => "  " + value));
    lines.Add(string.Empty);
    lines.Add(
        $"Differences: showing {differences.Count} of {totalDifferences}");
    lines.AddRange(differences.Count == 0
        ? ["  <none observed>"]
        : differences.Select(value => "  " + value));
    lines.Add(string.Empty);
    lines.Add("Limitations:");
    lines.AddRange(limitations.Count == 0
        ? ["  None for structural alignment. Root cause still requires independent evidence."]
        : limitations.Select(value => "  " + value));
    lines.Add(
        "Offline only: report text was parsed as bounded data; no paths, network, or embedded content were accessed.");
    return new ToolkitReportComparisonResult(
        structured,
        confidence,
        totalDifferences,
        string.Join('\n', lines));
  }

  public static bool TryParse(
      string input,
      out ParsedToolkitReport report,
      out string error)
  {
    report = null!;
    error = string.Empty;
    if (!WithinInputBound(input))
    {
      error = "Report exceeds the 1 MiB input limit.";
      return false;
    }

    foreach (char character in input)
    {
      if (char.IsControl(character)
          && character is not '\r' and not '\n' and not '\t')
      {
        error = "Report contains a disallowed control character.";
        return false;
      }
    }

    string[] lines = NormalizeNewlines(input)
        .Split('\n', StringSplitOptions.None);
    if (lines.Length > MaxLines
        || lines.Any(line => line.Length > MaxLineChars))
    {
      error = "Report has too many lines or an oversized line.";
      return false;
    }

    if (lines.Length == 0
        || lines[0] != "BetterCoop diagnostic report")
    {
      error = "Not a BetterCoop diagnostic report.";
      return false;
    }

    if (!UniqueField(lines, "Report format: ", out string formatText)
        || !int.TryParse(
            formatText,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out int format)
        || !UniqueField(lines, "BetterCoop version: ", out string version)
        || !UniqueField(lines, "Captured UTC: ", out string capturedText)
        || !DateTimeOffset.TryParseExact(
            capturedText,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out DateTimeOffset captured)
        || !UniqueField(lines, "Game version: ", out string gameVersion)
        || !UniqueField(lines, "Session ID: ", out string sessionId)
        || !(sessionId == "unavailable" || IsSessionId(sessionId))
        || !UniqueField(lines, "Role: ", out string role)
        || role is not ("Host" or "Client" or "Offline" or "Unknown")
        || !UniqueField(lines, "Runtime state: ", out string runtimeState)
        || !UniqueField(lines, "Package health: ", out string packageHealth)
        || !UniqueField(lines, "Error code: ", out string errorCode)
        || !IsToken(errorCode))
    {
      error = "Report is missing a required field or contains an invalid value.";
      return false;
    }

    string[] network = Repeated(lines, "Network: ", 64);
    string[] environment = Repeated(lines, "Environment: ", 64);
    string[] timeline = Repeated(lines, "Timeline: ", 1024);
    if (network.Length == 0
        || environment.Length == 0
        || timeline.Length == 0)
    {
      error = "Report is missing network, environment, or timeline data.";
      return false;
    }

    report = new ParsedToolkitReport(
        format,
        version,
        captured.ToUniversalTime(),
        gameVersion,
        sessionId,
        role,
        runtimeState,
        packageHealth,
        errorCode,
        network,
        environment,
        timeline);
    return true;
  }

  private static string FirstDivergence(
      ParsedToolkitReport first,
      ParsedToolkitReport second)
  {
    int count = Math.Min(first.Timeline.Count, second.Timeline.Count);
    for (int index = 0; index < count; index++)
    {
      string a = CanonicalTimeline(first.Timeline[index]);
      string b = CanonicalTimeline(second.Timeline[index]);
      if (a != b)
      {
        return $"Timeline item {index + 1}: A={a}; B={b}.";
      }
    }

    if (first.Timeline.Count != second.Timeline.Count)
    {
      return $"Timeline length: A={first.Timeline.Count}; B={second.Timeline.Count}.";
    }

    count = Math.Min(first.Network.Count, second.Network.Count);
    for (int index = 0; index < count; index++)
    {
      if (first.Network[index] != second.Network[index])
      {
        return $"Capture-time network item {index + 1}: "
            + $"A={first.Network[index]}; B={second.Network[index]}.";
      }
    }

    return first.Network.Count == second.Network.Count
        ? "No timeline or capture-time network divergence was observed."
        : $"Network item count: A={first.Network.Count}; B={second.Network.Count}.";
  }

  private static string CanonicalTimeline(string value)
  {
    int separator = value.IndexOf('|');
    return separator < 0 ? value : value[(separator + 1)..];
  }

  private static void CompareValue(
      string label,
      string first,
      string second,
      List<string> common,
      List<string> differences,
      ref int totalDifferences)
  {
    if (first == second)
    {
      common.Add($"{label}: {first}");
      return;
    }

    AddDifference(
        $"{label}: A={first}; B={second}",
        differences,
        ref totalDifferences);
  }

  private static void CompareSequence(
      string label,
      IReadOnlyList<string> first,
      IReadOnlyList<string> second,
      List<string> common,
      List<string> differences,
      ref int totalDifferences)
  {
    int commonCount = first.Intersect(second, StringComparer.Ordinal).Count();
    common.Add($"{label}: {commonCount} common item(s)");
    int count = Math.Max(first.Count, second.Count);
    for (int index = 0; index < count; index++)
    {
      string a = index < first.Count ? first[index] : "<missing>";
      string b = index < second.Count ? second[index] : "<missing>";
      if (a != b)
      {
        AddDifference(
            $"{label}[{index + 1}]: A={a}; B={b}",
            differences,
            ref totalDifferences);
      }
    }
  }

  private static void AddDifference(
      string difference,
      List<string> displayed,
      ref int total)
  {
    total++;
    if (displayed.Count < MaxDisplayedDifferences)
    {
      displayed.Add(difference);
    }
  }

  private static string[] Repeated(
      IEnumerable<string> lines,
      string prefix,
      int max)
  {
    string[] values = lines
        .Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
        .Select(line => line[prefix.Length..].Replace('\t', ' '))
        .Take(max + 1)
        .ToArray();
    return values.Length <= max ? values : [];
  }

  private static bool UniqueField(
      IEnumerable<string> lines,
      string prefix,
      out string value)
  {
    string[] matches = lines
        .Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
        .Take(2)
        .ToArray();
    value = matches.Length == 1
        ? matches[0][prefix.Length..].Replace('\t', ' ')
        : string.Empty;
    return matches.Length == 1 && value.Length <= MaxLineChars;
  }

  private static bool IsSessionId(string value) =>
      value.Length == 32
      && value.All(character =>
          character is >= '0' and <= '9'
              or >= 'a' and <= 'f');

  private static bool IsToken(string value) =>
      value.Length is > 0 and <= 64
      && value.All(character =>
          character is >= 'A' and <= 'Z'
              or >= '0' and <= '9'
              or '-');

  private static bool WithinInputBound(string value)
  {
    try
    {
      return new UTF8Encoding(false, true).GetByteCount(value)
          <= MaxInputBytes;
    }
    catch (EncoderFallbackException)
    {
      return false;
    }
  }

  private static string NormalizeNewlines(string value) =>
      value.Replace("\r\n", "\n", StringComparison.Ordinal)
          .Replace('\r', '\n');

  private static ToolkitReportComparisonResult Invalid(string error) =>
      new(
          false,
          ReportComparisonConfidence.Low,
          0,
          "BetterCoop offline report comparison\n"
          + "Structured comparison: unavailable\n"
          + "Input rejected: "
          + error
          + "\nNo paths, network, or embedded content were accessed.");

  private static bool Fail(string message, out string error)
  {
    error = message;
    return false;
  }
}
