using CoopGuard;

string first = string.Join('\n',
    FingerprintCodec.Line("mod", 0, "BaseLib", "3.3.8", true),
    FingerprintCodec.Line("file", 0, "BaseLib", "dll/BaseLib.dll", 12, "aaaa"));
string same = string.Join('\n',
    FingerprintCodec.Line("mod", 0, "BaseLib", "3.3.8", true),
    FingerprintCodec.Line("file", 0, "BaseLib", "dll/BaseLib.dll", 12, "aaaa"));
string changed = string.Join('\n',
    FingerprintCodec.Line("mod", 0, "BaseLib", "3.3.8", true),
    FingerprintCodec.Line("file", 0, "BaseLib", "dll/BaseLib.dll", 12, "bbbb"));

if (FingerprintCodec.Hash(first) != FingerprintCodec.Hash(same))
{
    throw new InvalidOperationException("Equal canonical inputs produced different hashes.");
}

if (FingerprintCodec.Hash(first) == FingerprintCodec.Hash(changed))
{
    throw new InvalidOperationException("Changed package bytes were not detected.");
}

IReadOnlyList<string> diff = FingerprintCodec.Diff(first, changed);
if (diff.Count != 2 || !diff.Any(line => line.Contains("aaaa", StringComparison.Ordinal))
    || !diff.Any(line => line.Contains("bbbb", StringComparison.Ordinal)))
{
    throw new InvalidOperationException("Difference report did not identify both package hashes.");
}

Console.WriteLine("Fingerprint self-check passed.");
