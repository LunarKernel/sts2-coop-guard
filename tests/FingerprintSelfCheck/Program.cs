using CoopGuard;

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void Throws<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

static void WriteFixture(string root, bool reverse)
{
    (string path, byte[] bytes)[] files =
    [
        ("mod_manifest.json", """{"id":"Fixture","version":"1.0.0"}"""u8.ToArray()),
        ("Fixture.dll", [0x01, 0x02, 0x03]),
        ("Fixture.pck", [0x04, 0x05]),
        ("data/rules.json", """{"damage":7}"""u8.ToArray()),
        ("images/art.png", [0x89, 0x50, 0x4E, 0x47]),
        ("empty.bin", [])
    ];

    IEnumerable<(string path, byte[] bytes)> ordered = reverse ? files.Reverse() : files;
    foreach ((string path, byte[] bytes) in ordered)
    {
        string fullPath = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, bytes);
    }
}

IncidentText chineseDivergence = IncidentExplainer.ExplainNetwork(
    "StateDivergence",
    [],
    [],
    "StateDivergence",
    [],
    chinese: true)
    ?? throw new InvalidOperationException("State divergence was not explained.");
Check(
    chineseDivergence.Code == "CG-STATE-DIVERGENCE"
        && chineseDivergence.Body.Contains("根因", StringComparison.Ordinal)
        && chineseDivergence.Body.Contains("不能单独证明责任 Mod", StringComparison.Ordinal),
    "Chinese state-divergence guidance became incomplete.");

IncidentText englishDivergence = IncidentExplainer.ExplainNetwork(
    "StateDivergence",
    [],
    [],
    "StateDivergence",
    [],
    chinese: false)
    ?? throw new InvalidOperationException("State divergence was not explained.");
Check(
    englishDivergence.Code == chineseDivergence.Code
        && englishDivergence.Body.Contains("Root cause", StringComparison.Ordinal)
        && englishDivergence.Body.Contains("does not by itself identify", StringComparison.Ordinal),
    "English state-divergence guidance became incomplete.");

IncidentText byteMismatch = IncidentExplainer.ExplainNetwork(
    "ModMismatch",
    ["CoopGuard-package-v3-aaaaaaaa"],
    ["CoopGuard-package-v3-bbbbbbbb"],
    "ModMismatch",
    [],
    chinese: true)
    ?? throw new InvalidOperationException("Package mismatch was not explained.");
Check(
    byteMismatch.Code == "CG-MOD-MISMATCH"
        && byteMismatch.Body.Contains("有效包内容指纹不同", StringComparison.Ordinal)
        && !byteMismatch.Body.Contains("aaaaaaaa", StringComparison.Ordinal),
    "Package-byte mismatch details were missing or leaked a fingerprint.");

IncidentText modSetMismatch = IncidentExplainer.ExplainNetwork(
    "ModMismatch",
    ["HostOnly 1.0"],
    ["LocalOnly 2.0"],
    "ModMismatch",
    [],
    chinese: false)
    ?? throw new InvalidOperationException("Mod-set mismatch was not explained.");
Check(
    modSetMismatch.Body.Contains("HostOnly 1.0", StringComparison.Ordinal)
        && modSetMismatch.Body.Contains("LocalOnly 2.0", StringComparison.Ordinal),
    "Safe native Mod-list differences were not preserved.");

IncidentText internalNetwork = IncidentExplainer.ExplainNetwork(
    "InternalError",
    [],
    [],
    "internal failure",
    ["before", "MissingMethodException: method changed"],
    chinese: false)
    ?? throw new InvalidOperationException("Internal network error was not explained.");
Check(
    internalNetwork.Code == "CG-MOD-API-INCOMPATIBLE-NET"
        && internalNetwork.Body.Contains("MissingMethodException", StringComparison.Ordinal),
    "A known API incompatibility in recent fatal context was not classified.");

(string Reason, string Code)[] networkCases =
[
    ("Timeout", "CG-NET-TIMEOUT"),
    ("HandshakeTimeout", "CG-HANDSHAKE-TIMEOUT"),
    ("VersionMismatch", "CG-GAME-VERSION-MISMATCH"),
    ("NoInternet", "CG-NET-OFFLINE"),
    ("SecureConnectionFailed", "CG-NET-SECURE-CONNECTION"),
    ("FailedToHost", "CG-NET-HOST-FAILED"),
    ("RateLimited", "CG-NET-RATE-LIMITED"),
    ("TryAgainLater", "CG-NET-TRY-LATER")
];
foreach ((string reason, string code) in networkCases)
{
    IncidentText chinese = IncidentExplainer.ExplainNetwork(
        reason,
        [],
        [],
        reason,
        [],
        chinese: true)
        ?? throw new InvalidOperationException($"{reason} was not explained.");
    IncidentText english = IncidentExplainer.ExplainNetwork(
        reason,
        [],
        [],
        reason,
        [],
        chinese: false)
        ?? throw new InvalidOperationException($"{reason} was not explained.");
    Check(
        chinese.Code == code
            && english.Code == code
            && chinese.Body.Contains("根因", StringComparison.Ordinal)
            && english.Body.Contains("Root cause", StringComparison.Ordinal),
        $"{reason} did not retain complete Chinese and English explanations.");
}

(string ExceptionType, string Code)[] exceptionCases =
[
    ("MissingMethodException", "CG-MOD-API-INCOMPATIBLE"),
    ("FileNotFoundException", "CG-MOD-DEPENDENCY"),
    ("HarmonyException", "CG-HARMONY-PATCH"),
    ("SoftlockException", "CG-SOFTLOCK")
];
foreach ((string exceptionType, string code) in exceptionCases)
{
    IncidentText incident = IncidentExplainer.ExplainException(
        exceptionType,
        "FixtureMod",
        "FixtureDependency.dll",
        chinese: false);
    Check(
        incident.Code == code
            && incident.Body.Contains("Root cause", StringComparison.Ordinal),
        $"{exceptionType} was not mapped to its expected root cause.");
}

IncidentText changedFiles = IncidentExplainer.ExplainLocalVerification(
    "A Mod package changed after startup. Restart the game before multiplayer.",
    chinese: true);
Check(
    changedFiles.Code == "CG-LOCAL-FILES-CHANGED",
    "A changed local package was not given its specific root cause.");

Check(
    IncidentExplainer.ExplainNetwork(
        "Quit",
        [],
        [],
        "Quit",
        ["MissingMethodException from an older unrelated event"],
        chinese: true) == null,
    "A normal quit was incorrectly converted into a fatal diagnosis.");

string redacted = IncidentExplainer.Redact(
    "76561198824432109 127.0.0.1:1234 token=secret C:\\Users\\name\\save.dat");
Check(
    !redacted.Contains("76561198824432109", StringComparison.Ordinal)
        && !redacted.Contains("127.0.0.1", StringComparison.Ordinal)
        && !redacted.Contains("secret", StringComparison.Ordinal)
        && !redacted.Contains("C:\\Users", StringComparison.Ordinal),
    "A diagnostic summary leaked identity, network, credential, or path data.");

string escaped = FingerprintCodec.Line("a|b", "line\r\nbreak", "100%");
Check(
    escaped == "a%7Cb|line%0D%0Abreak|100%25",
    "Canonical field escaping changed.");

System.Globalization.CultureInfo originalCulture =
    System.Globalization.CultureInfo.CurrentCulture;
try
{
    System.Globalization.CultureInfo.CurrentCulture =
        System.Globalization.CultureInfo.GetCultureInfo("ar-EG");
    Check(
        FingerprintCodec.Line(12345, 1.5) == "12345|1.5",
        "Canonical numeric fields became locale-dependent.");
}
finally
{
    System.Globalization.CultureInfo.CurrentCulture = originalCulture;
}

DirectoryInfo temporary = Directory.CreateTempSubdirectory("coopguard-selfcheck-");
try
{
    string firstRoot = Path.Combine(temporary.FullName, "first");
    string secondRoot = Path.Combine(temporary.FullName, "second");
    Directory.CreateDirectory(firstRoot);
    Directory.CreateDirectory(secondRoot);
    WriteFixture(firstRoot, reverse: false);
    WriteFixture(secondRoot, reverse: true);

    PackageCapture first = PackageHasher.Capture(firstRoot, 0, "Fixture");
    PackageCapture same = PackageHasher.Capture(secondRoot, 0, "Fixture");
    Check(first.Digest == same.Digest, "File creation order changed the package digest.");
    Check(first.FileCount == 6, "Nested or empty package files were not captured.");
    Check(
        first.CanonicalText.Contains("data/rules.json", StringComparison.Ordinal),
        "Nested relative paths were not included.");
    Check(
        !first.CanonicalText.Contains(firstRoot, StringComparison.OrdinalIgnoreCase),
        "An absolute package path entered canonical wire data.");
    Check(PackageHasher.IsCurrent(first), "An unchanged package failed the quick check.");
    Throws<FingerprintLimitException>(
        () => PackageHasher.Capture(firstRoot, 0, "Fixture", maxFiles: 1),
        "A caller-supplied aggregate file limit was ignored.");

    string mountedPckPath = Path.Combine(temporary.FullName, "mounted.pck");
    File.WriteAllBytes(mountedPckPath, [0x10, 0x20, 0x30]);
    MountedPckCapture mountedPck =
        PackageHasher.CaptureMountedPck(mountedPckPath, maxBytes: 3);
    Check(
        PackageHasher.IsCurrent(mountedPck),
        "An unchanged mounted PCK failed the quick check.");
    Throws<FingerprintLimitException>(
        () => PackageHasher.CaptureMountedPck(
            mountedPckPath,
            maxBytes: 2),
        "A mounted PCK ignored the caller-supplied byte limit.");
    DateTime mountedWriteTime = File.GetLastWriteTimeUtc(mountedPckPath);
    File.WriteAllBytes(mountedPckPath, [0x10, 0x20, 0x31]);
    File.SetLastWriteTimeUtc(mountedPckPath, mountedWriteTime);
    MountedPckCapture changedMountedPck =
        PackageHasher.CaptureMountedPck(mountedPckPath, maxBytes: 3);
    Check(
        mountedPck.Digest != changedMountedPck.Digest,
        "Changed mounted-PCK bytes were not detected by a full capture.");

    string rulesPath = Path.Combine(secondRoot, "data", "rules.json");
    DateTime originalWriteTime = File.GetLastWriteTimeUtc(rulesPath);
    File.WriteAllText(rulesPath, """{"damage":8}""");
    File.SetLastWriteTimeUtc(rulesPath, originalWriteTime);
    PackageCapture changedBytes = PackageHasher.Capture(secondRoot, 0, "Fixture");
    Check(
        first.TotalBytes == changedBytes.TotalBytes
            && first.Digest != changedBytes.Digest,
        "Same-length changed bytes with a restored timestamp were not detected.");

    string oldName = Path.Combine(secondRoot, "images", "art.png");
    string newName = Path.Combine(secondRoot, "images", "renamed.png");
    File.Move(oldName, newName);
    PackageCapture renamed = PackageHasher.Capture(secondRoot, 0, "Fixture");
    Check(
        changedBytes.Digest != renamed.Digest,
        "Renaming a package file did not change the digest.");

    File.WriteAllBytes(Path.Combine(firstRoot, "new-empty.bin"), []);
    PackageCapture added = PackageHasher.Capture(firstRoot, 0, "Fixture");
    Check(first.Digest != added.Digest, "An added package file did not change the digest.");
    Check(!PackageHasher.IsCurrent(first), "An added file passed the quick check.");

    string onlineFirstRoot = Path.Combine(temporary.FullName, "online-first");
    string onlineSecondRoot = Path.Combine(temporary.FullName, "online-second");
    Directory.CreateDirectory(onlineFirstRoot);
    Directory.CreateDirectory(onlineSecondRoot);
    WriteFixture(onlineFirstRoot, reverse: false);
    WriteFixture(onlineSecondRoot, reverse: true);
    Directory.CreateDirectory(Path.Combine(onlineFirstRoot, "user_data"));
    Directory.CreateDirectory(Path.Combine(onlineSecondRoot, "user_data"));
    File.WriteAllText(Path.Combine(onlineFirstRoot, "log.oejson"), "first log");
    File.WriteAllText(Path.Combine(onlineSecondRoot, "log.oejson"), "second log");
    File.WriteAllText(Path.Combine(onlineFirstRoot, "log.oejson.tmp"), "first temp log");
    File.WriteAllText(Path.Combine(onlineSecondRoot, "log.oejson.tmp"), "second temp log");
    File.WriteAllText(
        Path.Combine(onlineFirstRoot, "user_data", "card_art_selections.oejson"),
        "first selection");
    File.WriteAllText(
        Path.Combine(onlineSecondRoot, "user_data", "card_art_selections.oejson"),
        "second selection");

    PackageCapture onlineFirst =
        PackageHasher.Capture(
            onlineFirstRoot,
            0,
            "OnlineExchange",
            "1.2.0");
    PackageCapture onlineSecond =
        PackageHasher.Capture(
            onlineSecondRoot,
            0,
            "OnlineExchange",
            "1.2.0");
    Check(
        onlineFirst.Digest == onlineSecond.Digest && onlineFirst.FileCount == 6,
        "OnlineExchange runtime data entered its package digest.");
    File.WriteAllText(Path.Combine(onlineFirstRoot, "log.oejson"), "changed log");
    File.WriteAllText(
        Path.Combine(onlineFirstRoot, "user_data", "card_art_selections.oejson"),
        "changed selection");
    Check(
        PackageHasher.IsCurrent(onlineFirst),
        "Ignored OnlineExchange runtime data invalidated the quick check.");
    Check(
        PackageHasher.Capture(onlineFirstRoot, 0, "Fixture").Digest
            != PackageHasher.Capture(onlineSecondRoot, 0, "Fixture").Digest,
        "Runtime-data exclusions leaked into unrelated Mods.");

    string unicodeFirstRoot = Path.Combine(temporary.FullName, "unicode-first");
    string unicodeSecondRoot = Path.Combine(temporary.FullName, "unicode-second");
    Directory.CreateDirectory(unicodeFirstRoot);
    Directory.CreateDirectory(unicodeSecondRoot);
    File.WriteAllText(Path.Combine(unicodeFirstRoot, "\u00e9.txt"), "same");
    File.WriteAllText(Path.Combine(unicodeSecondRoot, "e\u0301.txt"), "same");
    Check(
        PackageHasher.Capture(unicodeFirstRoot, 0, "Fixture").Digest
            != PackageHasher.Capture(unicodeSecondRoot, 0, "Fixture").Digest,
        "Distinct raw Unicode paths collapsed to the same digest.");

    string unicodeRoot = Path.Combine(temporary.FullName, "unicode-collision");
    Directory.CreateDirectory(unicodeRoot);
    string composed = Path.Combine(unicodeRoot, "\u00e9.txt");
    string decomposed = Path.Combine(unicodeRoot, "e\u0301.txt");
    File.WriteAllText(composed, "one");
    File.WriteAllText(decomposed, "two");
    if (Directory.EnumerateFiles(unicodeRoot).Count() == 2)
    {
        Throws<InvalidDataException>(
            () => PackageHasher.Capture(unicodeRoot, 0, "Fixture"),
            "Unicode-normalized duplicate paths were not rejected.");
    }

    string outside = Path.Combine(temporary.FullName, "outside.bin");
    string link = Path.Combine(secondRoot, "linked.bin");
    File.WriteAllText(outside, "outside");
    try
    {
        File.CreateSymbolicLink(link, outside);
        try
        {
            Throws<InvalidDataException>(
                () => PackageHasher.Capture(secondRoot, 0, "Fixture"),
                "A reparse point was followed instead of rejected.");
            Throws<InvalidDataException>(
                () => PackageHasher.CaptureMountedPck(
                    link,
                    maxBytes: 1024),
                "A mounted-PCK reparse point was followed instead of rejected.");
        }
        finally
        {
            File.Delete(link);
        }
    }
    catch (Exception ex) when (
        ex is UnauthorizedAccessException
        or PlatformNotSupportedException
        or IOException)
    {
        Console.WriteLine("Reparse-point self-check skipped on this filesystem.");
    }
}
finally
{
    Directory.Delete(temporary.FullName, recursive: true);
}

Console.WriteLine("Fingerprint and incident-explanation self-check passed.");
