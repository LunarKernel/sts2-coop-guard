using System.Security.Cryptography;
using System.Text;

namespace BetterCoop;

internal sealed class FingerprintLimitException(string message)
    : IOException(message);

internal sealed record PackageFileStamp(
    string RelativePath,
    long Length,
    long LastWriteTimeUtcTicks);

internal sealed record PackageCapture(
    string Root,
    string ModId,
    string ModVersion,
    string CanonicalText,
    string Digest,
    IReadOnlyList<PackageFileStamp> Files,
    int ScannedEntryCount,
    long TotalBytes)
{
  public int FileCount => Files.Count;
}

internal sealed record MountedPckCapture(
    string Path,
    long Length,
    long LastWriteTimeUtcTicks,
    string Digest);

internal static class PackageHasher
{
  public const int MaxFilesPerPackage = 10_000;
  public const long MaxBytesPerPackage = 2L * 1024 * 1024 * 1024;
  public const int MaxCanonicalCharactersPerPackage = 16 * 1024 * 1024;
  public const int MaxEntriesPerPackage = 20_000;
  private const int MaxRelativePathCharacters = 2048;

  public static PackageCapture Capture(
      string root,
      int _,
      string modId,
      string modVersion = "",
      int maxFiles = MaxFilesPerPackage,
      long maxBytes = MaxBytesPerPackage,
      int maxCanonicalCharacters = MaxCanonicalCharactersPerPackage,
      int maxEntries = MaxEntriesPerPackage)
  {
    int fileLimit = Math.Min(MaxFilesPerPackage, maxFiles);
    long byteLimit = Math.Min(MaxBytesPerPackage, maxBytes);
    int characterLimit = Math.Min(
        MaxCanonicalCharactersPerPackage,
        maxCanonicalCharacters);
    int entryLimit = Math.Min(MaxEntriesPerPackage, maxEntries);
    if (fileLimit <= 0
        || byteLimit <= 0
        || characterLimit <= 0
        || entryLimit <= 0)
    {
      throw new FingerprintLimitException(
          "The aggregate Mod package safety limit was reached.");
    }

    string fullRoot = Path.GetFullPath(root);
    List<string> paths = EnumerateRegularFiles(
        fullRoot,
        modId,
        modVersion,
        fileLimit,
        entryLimit,
        out int entryCount);
    List<string> lines = new(paths.Count);
    List<PackageFileStamp> stamps = new(paths.Count);
    HashSet<string> canonicalPaths = new(StringComparer.Ordinal);
    long totalBytes = 0;
    int canonicalCharacters = 0;

    foreach (string path in paths)
    {
      string relativePath = RawRelativePath(fullRoot, path);
      if (!canonicalPaths.Add(NormalizedRelativePath(fullRoot, path)))
      {
        throw new InvalidDataException(
            "Mod package contains paths that normalize to the same value.");
      }

      FileInfo before = new(path);
      before.Refresh();

      if (before.Length > byteLimit - totalBytes)
      {
        throw new FingerprintLimitException(
            $"Mod package exceeds the {byteLimit} byte safety limit.");
      }

      byte[] hash;
      long streamLength;
      if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
      {
        throw new InvalidDataException(
            "Mod package file became a reparse point while hashing.");
      }

      using (FileStream stream = new(
                 path,
                 FileMode.Open,
                 FileAccess.Read,
                 FileShare.Read,
                 bufferSize: 128 * 1024,
                 FileOptions.SequentialScan))
      {
        streamLength = stream.Length;
        if (streamLength > byteLimit - totalBytes)
        {
          throw new FingerprintLimitException(
              $"Mod package exceeds the {byteLimit} byte safety limit.");
        }

        hash = SHA256.HashData(stream);
      }

      FileInfo after = new(path);
      after.Refresh();
      if ((after.Attributes & FileAttributes.ReparsePoint) != 0
          || before.Length != streamLength
          || after.Length != streamLength
          || before.LastWriteTimeUtc.Ticks != after.LastWriteTimeUtc.Ticks)
      {
        throw new IOException("A Mod package file changed while it was being hashed.");
      }

      totalBytes += streamLength;
      stamps.Add(new PackageFileStamp(
          relativePath,
          streamLength,
          after.LastWriteTimeUtc.Ticks));
      string digest = Convert.ToHexString(hash).ToLowerInvariant();
      string line = FingerprintCodec.Line(
          "file",
          modId,
          relativePath,
          streamLength,
          digest);
      int separatorLength = lines.Count == 0 ? 0 : 1;
      if (line.Length > characterLimit - canonicalCharacters - separatorLength)
      {
        throw new FingerprintLimitException(
            $"Mod package exceeds the {characterLimit} character safety limit.");
      }

      lines.Add(line);
      canonicalCharacters += separatorLength + line.Length;
    }

    string canonicalText = string.Join('\n', lines);
    PackageCapture capture = new(
        fullRoot,
        modId,
        modVersion,
        canonicalText,
        FingerprintCodec.Hash(canonicalText),
        stamps,
        entryCount,
        totalBytes);
    int validationEntries = entryLimit;
    if (!IsCurrent(capture, ref validationEntries))
    {
      throw new IOException(
          "The Mod package changed while it was being hashed.");
    }

    return capture;
  }

  public static bool IsCurrent(PackageCapture capture)
  {
    int remainingEntries = MaxEntriesPerPackage;
    return IsCurrent(capture, ref remainingEntries);
  }

  public static bool IsCurrent(
      PackageCapture capture,
      ref int remainingEntries)
  {
    int entryLimit = Math.Min(MaxEntriesPerPackage, remainingEntries);
    if (entryLimit <= 0)
    {
      throw new FingerprintLimitException(
          "The aggregate Mod package entry limit was reached.");
    }

    List<string> paths = EnumerateRegularFiles(
        capture.Root,
        capture.ModId,
        capture.ModVersion,
        MaxFilesPerPackage,
        entryLimit,
        out int entryCount);
    remainingEntries -= entryCount;
    if (paths.Count != capture.Files.Count)
    {
      return false;
    }

    for (int index = 0; index < paths.Count; index++)
    {
      string path = paths[index];
      PackageFileStamp expected = capture.Files[index];
      FileInfo current = new(path);
      current.Refresh();
      if ((current.Attributes & FileAttributes.ReparsePoint) != 0
          || !string.Equals(
              RawRelativePath(capture.Root, path),
              expected.RelativePath,
              StringComparison.Ordinal)
          || current.Length != expected.Length
          || current.LastWriteTimeUtc.Ticks != expected.LastWriteTimeUtcTicks)
      {
        return false;
      }
    }

    return true;
  }

  public static MountedPckCapture CaptureMountedPck(
      string path,
      long maxBytes)
  {
    if (maxBytes <= 0)
    {
      throw new FingerprintLimitException(
          "The aggregate Mod package byte limit was reached.");
    }

    string fullPath = Path.GetFullPath(path);
    if (!File.Exists(fullPath))
    {
      throw new FileNotFoundException(
          "A mounted PCK file does not exist.",
          fullPath);
    }

    FileInfo before = new(fullPath);
    before.Refresh();
    if ((before.Attributes & FileAttributes.ReparsePoint) != 0)
    {
      throw new InvalidDataException(
          "A mounted PCK cannot be a reparse point.");
    }

    if (before.Length > maxBytes)
    {
      throw new FingerprintLimitException(
          $"Mounted PCK files exceed the {maxBytes} remaining byte safety limit.");
    }

    byte[] hash;
    long streamLength;
    using (FileStream stream = new(
               fullPath,
               FileMode.Open,
               FileAccess.Read,
               FileShare.Read,
               bufferSize: 128 * 1024,
               FileOptions.SequentialScan))
    {
      streamLength = stream.Length;
      if (streamLength > maxBytes)
      {
        throw new FingerprintLimitException(
            $"Mounted PCK files exceed the {maxBytes} remaining byte safety limit.");
      }

      hash = SHA256.HashData(stream);
    }

    FileInfo after = new(fullPath);
    after.Refresh();
    if ((after.Attributes & FileAttributes.ReparsePoint) != 0
        || before.Length != streamLength
        || after.Length != streamLength
        || before.LastWriteTimeUtc.Ticks != after.LastWriteTimeUtc.Ticks)
    {
      throw new IOException(
          "A mounted PCK changed while it was being hashed.");
    }

    return new MountedPckCapture(
        fullPath,
        streamLength,
        after.LastWriteTimeUtc.Ticks,
        Convert.ToHexString(hash).ToLowerInvariant());
  }

  public static bool IsCurrent(MountedPckCapture capture)
  {
    if (!File.Exists(capture.Path))
    {
      return false;
    }

    FileInfo current = new(capture.Path);
    current.Refresh();
    return (current.Attributes & FileAttributes.ReparsePoint) == 0
        && current.Length == capture.Length
        && current.LastWriteTimeUtc.Ticks == capture.LastWriteTimeUtcTicks;
  }

  internal static string PackageRelativePath(string root, string path) =>
      RawRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));

  private static List<string> EnumerateRegularFiles(
      string fullRoot,
      string modId,
      string modVersion,
      int maxFiles,
      int maxEntries,
      out int entryCount)
  {
    if (!Directory.Exists(fullRoot))
    {
      throw new DirectoryNotFoundException("Mod package directory does not exist.");
    }

    if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
    {
      throw new InvalidDataException("Mod package root cannot be a reparse point.");
    }

    List<string> files = [];
    Stack<string> pending = new();
    pending.Push(fullRoot);
    entryCount = 0;

    while (pending.Count > 0)
    {
      string directory = pending.Pop();
      if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
      {
        throw new InvalidDataException(
            "Mod package directory became a reparse point while hashing.");
      }

      foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
      {
        entryCount++;
        if (entryCount > maxEntries)
        {
          throw new FingerprintLimitException(
              $"Mod package exceeds the {maxEntries} entry safety limit.");
        }

        FileAttributes attributes = File.GetAttributes(entry);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
          throw new InvalidDataException(
              "Mod package contains an unsupported reparse point.");
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
          if (!ShouldIgnoreDirectory(
              modId,
              modVersion,
              RawRelativePath(fullRoot, entry)))
          {
            pending.Push(entry);
          }
          continue;
        }

        string relative = RawRelativePath(fullRoot, entry);
        if (ShouldIgnoreFile(modId, modVersion, relative))
        {
          continue;
        }

        files.Add(Path.GetFullPath(entry));
        if (files.Count > maxFiles)
        {
          throw new FingerprintLimitException(
              $"Mod package exceeds the {maxFiles} file safety limit.");
        }
      }
    }

    files.Sort((left, right) =>
    {
      int canonical = StringComparer.Ordinal.Compare(
              NormalizedRelativePath(fullRoot, left),
              NormalizedRelativePath(fullRoot, right));
      return canonical != 0
              ? canonical
              : StringComparer.Ordinal.Compare(
                  RawRelativePath(fullRoot, left),
                  RawRelativePath(fullRoot, right));
    });
    return files;
  }

  private static string NormalizedRelativePath(string fullRoot, string path) =>
      RawRelativePath(fullRoot, path).Normalize(NormalizationForm.FormC);

  private static string RawRelativePath(string fullRoot, string path)
  {
    string relative = Path.GetRelativePath(fullRoot, path);
    if (Path.IsPathRooted(relative)
        || relative.Equals("..", StringComparison.Ordinal)
        || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
    {
      throw new InvalidDataException("Mod package path escaped its package root.");
    }

    string portable = relative.Replace(Path.DirectorySeparatorChar, '/');
    if (portable.Length > MaxRelativePathCharacters)
    {
      throw new FingerprintLimitException(
          $"Mod package path exceeds {MaxRelativePathCharacters} characters.");
    }

    return portable;
  }

  private static bool ShouldIgnoreDirectory(
      string modId,
      string modVersion,
      string relativePath) =>
      modId.Equals("OnlineExchange", StringComparison.Ordinal)
      && modVersion.Equals("1.2.0", StringComparison.Ordinal)
      && relativePath.Equals("user_data", StringComparison.OrdinalIgnoreCase);

  private static bool ShouldIgnoreFile(
      string modId,
      string modVersion,
      string relativePath) =>
      modId.Equals("OnlineExchange", StringComparison.Ordinal)
      && modVersion.Equals("1.2.0", StringComparison.Ordinal)
      && (relativePath.Equals("log.oejson", StringComparison.OrdinalIgnoreCase)
          || relativePath.Equals(
              "log.oejson.tmp",
              StringComparison.OrdinalIgnoreCase)
          || relativePath.Equals(
              "log.oejson.backup",
              StringComparison.OrdinalIgnoreCase));
}
