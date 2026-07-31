using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using BetterCoop;

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

Check(
    FingerprintCodec.ProtocolVersion == 6
    && FingerprintCodec.CompatibilityPrefix.Contains(
        "-v6-",
        StringComparison.Ordinal)
    && ToolkitEnvelopeCodec.Major == 2,
    "BetterCoop v0.6 protocol versions are inconsistent.");

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
    [
        FingerprintCodec.CompatibilityPrefix + new string('a', 64),
        FingerprintCodec.ComponentEntry(
            "Merchant2CuteII",
            new string('c', 64))
    ],
    [
        FingerprintCodec.CompatibilityPrefix + new string('b', 64),
        FingerprintCodec.ComponentEntry(
            "Merchant2CuteII",
            new string('d', 64))
    ],
    "ModMismatch",
    [],
    chinese: true)
    ?? throw new InvalidOperationException("Package mismatch was not explained.");
Check(
    byteMismatch.Code == "CG-MOD-MISMATCH"
        && byteMismatch.Body.Contains("Merchant2CuteII", StringComparison.Ordinal)
        && byteMismatch.Body.Contains("内容或版本不同", StringComparison.Ordinal)
        && byteMismatch.Body.Contains(
            "Mod | 差异方向 | 置信度 | 只读处理建议",
            StringComparison.Ordinal)
        && byteMismatch.Body.Contains("双方：", StringComparison.Ordinal)
        && !byteMismatch.Body.Contains(new string('c', 64), StringComparison.Ordinal),
    "The differing Mod was not identified or its fingerprint leaked.");

IncidentText oneSidedMod = IncidentExplainer.ExplainNetwork(
    "ModMismatch",
    [
        FingerprintCodec.CompatibilityPrefix + new string('a', 64),
        FingerprintCodec.ComponentEntry(
            "LocalOnlyCosmetic",
            new string('c', 64))
    ],
    [FingerprintCodec.CompatibilityPrefix + new string('b', 64)],
    "ModMismatch",
    [],
    chinese: false)
    ?? throw new InvalidOperationException("A one-sided Mod was not explained.");
Check(
    oneSidedMod.Body.Contains(
        "Present only locally: LocalOnlyCosmetic",
        StringComparison.Ordinal)
        && oneSidedMod.Body.Contains("Local:", StringComparison.Ordinal)
        && oneSidedMod.Body.Contains("Both peers:", StringComparison.Ordinal),
    "A one-sided non-gameplay Mod was not named.");

IncidentText unsafeComponentName = IncidentExplainer.ExplainNetwork(
    "ModMismatch",
    [
        FingerprintCodec.CompatibilityPrefix + new string('a', 64),
        FingerprintCodec.ComponentEntry(
            "Bad\nMod\u0001",
            new string('c', 64))
    ],
    [FingerprintCodec.CompatibilityPrefix + new string('b', 64)],
    "ModMismatch",
    [],
    chinese: false)
    ?? throw new InvalidOperationException("An unsafe Mod ID was not explained.");
Check(
    !unsafeComponentName.Body.Contains("Bad\nMod", StringComparison.Ordinal)
        && !unsafeComponentName.Body.Any(character =>
            char.IsControl(character) && character != '\n'),
    "A peer-controlled Mod ID injected control characters into the popup.");

IncidentText missingGuard = IncidentExplainer.ExplainNetwork(
    "ModMismatch",
    [FingerprintCodec.CompatibilityPrefix + "aaaaaaaa"],
    [],
    "ModMismatch",
    [],
    chinese: true)
    ?? throw new InvalidOperationException("A missing BetterCoop peer was not explained.");
Check(
    missingGuard.Code == "CG-BETTERCOOP-MISSING"
        && missingGuard.Body.Contains("所有玩家安装同一个", StringComparison.Ordinal),
    "A missing BetterCoop peer was confused with a package-byte mismatch.");

IncidentText incompatibleGuard = IncidentExplainer.ExplainNetwork(
    "ModMismatch",
    [FingerprintCodec.CompatibilityPrefix + "aaaaaaaa"],
    [FingerprintCodec.CompatibilityFamilyPrefix + "5-bbbbbbbb"],
    "ModMismatch",
    [],
    chinese: false)
    ?? throw new InvalidOperationException("An incompatible BetterCoop protocol was not explained.");
Check(
    incompatibleGuard.Code == "CG-BETTERCOOP-MISSING"
        && incompatibleGuard.Body.Contains("different BetterCoop protocol", StringComparison.Ordinal),
    "An incompatible BetterCoop protocol was confused with package bytes.");

IncidentText localVerificationFailure = IncidentExplainer.ExplainNetwork(
    "ModMismatch",
    [FingerprintCodec.CompatibilityPrefix + "error-local"],
    [FingerprintCodec.CompatibilityPrefix + "healthy-host"],
    "ModMismatch",
    [],
    chinese: true)
    ?? throw new InvalidOperationException("A peer verification failure was not explained.");
Check(
    localVerificationFailure.Code == "CG-PEER-VERIFY-FAILED"
        && localVerificationFailure.Body.Contains("本机返回", StringComparison.Ordinal),
    "A local verification failure was confused with a package-byte mismatch.");

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
      "MegaAnimationState.SetAnimation changed",
      chinese: false);
  Check(
      incident.Code == code
          && incident.Body.Contains("Root cause", StringComparison.Ordinal),
      $"{exceptionType} was not mapped to its expected root cause.");
}

IncidentText unattributedApi = IncidentExplainer.ExplainException(
    "MissingMethodException",
    null,
    null,
    "Missing MegaAnimationState.SetAnimation",
    chinese: false);
Check(
    unattributedApi.Code == "CG-MOD-API-INCOMPATIBLE"
        && unattributedApi.Title.Contains("Assembly or API", StringComparison.Ordinal)
        && unattributedApi.Body.Contains(
            "source unknown",
            StringComparison.OrdinalIgnoreCase)
        && unattributedApi.Body.Contains(
            "MegaAnimationState.SetAnimation",
            StringComparison.Ordinal),
    "An unattributed API failure blamed a Mod or hid the missing member.");

IncidentText changedFiles = IncidentExplainer.ExplainLocalVerification(
    "A Mod package changed after startup. Restart the game before multiplayer.",
    chinese: true);
Check(
    changedFiles.Code == "CG-LOCAL-FILES-CHANGED",
    "A changed local package was not given its specific root cause.");

Check(
    IncidentExplainer.ExplainLocalVerification(
        "Package fingerprint failed: The current STS2 build is not supported by this BetterCoop version.",
        chinese: false).Code == "CG-UNSUPPORTED-GAME-BUILD",
    "An unsupported game build did not receive its specific diagnosis.");
Check(
    IncidentExplainer.ExplainLocalVerification(
        "BetterCoop could not install its required multiplayer patches (MissingMethodException).",
        chinese: false).Code == "CG-GUARD-INITIALIZATION-FAILED",
    "A guard initialization failure did not receive its specific diagnosis.");

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
    "76561198824432109 127.0.0.1:1234 [2001:db8::1]:443 "
        + "token=secret \"password\":\"json-secret\" "
        + "Authorization: Bearer bearer-secret "
        + "C:\\Users\\name\\save.dat \\\\server\\share\\save.dat "
        + FingerprintCodec.CompatibilityPrefix
        + "aaaaaaaa "
        + FingerprintCodec.ComponentEntry(
            "PrivateFixture",
            new string('c', 64))
        + " "
        + new string('b', 64));
Check(
    !redacted.Contains("76561198824432109", StringComparison.Ordinal)
        && !redacted.Contains("127.0.0.1", StringComparison.Ordinal)
        && !redacted.Contains("2001:db8", StringComparison.Ordinal)
        && !redacted.Contains("secret", StringComparison.Ordinal)
        && !redacted.Contains("C:\\Users", StringComparison.Ordinal)
        && !redacted.Contains("\\\\server", StringComparison.Ordinal)
        && !redacted.Contains(
            FingerprintCodec.CompatibilityPrefix,
            StringComparison.Ordinal)
        && !redacted.Contains(
            FingerprintCodec.ComponentPrefix,
            StringComparison.Ordinal)
        && !redacted.Contains(new string('b', 64), StringComparison.Ordinal),
    "A diagnostic summary leaked identity, network, credential, or path data.");

IncidentText healthy = IncidentExplainer.ExplainHealthy(
    3,
    42,
    1024,
    chinese: true);
string report = IncidentExplainer.BuildReport(
    healthy,
    "0.109.1",
    "network=Host; connected=True; runInProgress=True",
    "quick freshness passed; mods=3; files=42; bytes=1024; full bytes not reread",
    [
        "warning for 76561198824432109",
        "token=secret C:\\Users\\name\\save.dat"
    ],
    DateTimeOffset.Parse(
        "2026-07-28T00:00:00Z",
        System.Globalization.CultureInfo.InvariantCulture));
Check(
    report.Contains("[CG-HEALTHY]", StringComparison.Ordinal)
        && report.Contains("Report format: 2", StringComparison.Ordinal)
        && report.Contains("Ctrl+F8", StringComparison.Ordinal)
        && healthy.Body.Contains("没有重新读取全部文件", StringComparison.Ordinal)
        && !report.Contains("76561198824432109", StringComparison.Ordinal)
        && !report.Contains("secret", StringComparison.Ordinal)
        && !report.Contains("C:\\Users", StringComparison.Ordinal),
    "The copyable health report was incomplete or leaked sensitive data.");

string sessionId = new('a', 32);
string hostReport = IncidentExplainer.BuildReport(
    healthy,
    "0.109.1",
    "network=Host; connected=True; runInProgress=False",
    "quick freshness passed; mods=3; files=42",
    [],
    DateTimeOffset.Parse(
        "2026-07-28T00:00:00Z",
        System.Globalization.CultureInfo.InvariantCulture),
    [
        "Session ID: " + sessionId,
        "Role: Host",
        "Network: local|connected=1|type=Host",
        "Timeline: 1000|Lifecycle|CG-SESSION-START|local|1"
    ]);
string clientReport = IncidentExplainer.BuildReport(
    healthy,
    "0.109.1",
    "network=Client; connected=False; runInProgress=False",
    "quick freshness passed; mods=3; files=42",
    [],
    DateTimeOffset.Parse(
        "2026-07-28T00:00:01Z",
        System.Globalization.CultureInfo.InvariantCulture),
    [
        "Session ID: " + sessionId,
        "Role: Client",
        "Network: local|connected=0|type=Client",
        "Timeline: 900|Lifecycle|CG-SESSION-START|local|1"
    ]);
ToolkitReportComparisonResult comparison =
    ToolkitReportComparison.Compare(hostReport, clientReport);
Check(
    comparison.Structured
        && comparison.Confidence == ReportComparisonConfidence.High
        && comparison.Text.Contains(
            "First observable divergence:\n  Capture-time network",
            StringComparison.Ordinal)
        && comparison.Text.Contains(
            "Root cause: not established",
            StringComparison.Ordinal),
    "Compatible same-session reports were not aligned conservatively.");
Check(
    !ToolkitReportComparison.Compare(
            hostReport,
            clientReport.Replace(
                "Report format: 2",
                "Report format: 999",
                StringComparison.Ordinal))
        .Structured
        && !ToolkitReportComparison.Compare(
                hostReport + "\0",
                clientReport)
            .Structured
        && !ToolkitReportComparison.TryParse(
            new string('x', ToolkitReportComparison.MaxInputBytes + 1),
            out _,
            out _),
    "Untrusted report schema, controls, or size were not safely rejected.");
string longRedactedReport = IncidentExplainer.RedactReport(
    string.Join(
        '\n',
        Enumerable.Range(0, 100)
            .Select(index =>
                $"line {index} token=secret C:\\Users\\fixture\\{index}.dat")));
Check(
    longRedactedReport.Length > 900
        && longRedactedReport.Contains("line 99", StringComparison.Ordinal)
        && !longRedactedReport.Contains("secret", StringComparison.Ordinal)
        && !longRedactedReport.Contains("C:\\Users", StringComparison.Ordinal),
    "Whole-report redaction truncated history or leaked sensitive data.");

string escaped = FingerprintCodec.Line("a|b", "line\r\nbreak", "100%");
Check(
    escaped == "a%7Cb|line%0D%0Abreak|100%25",
    "Canonical field escaping changed.");

long capturedAt = Stopwatch.GetTimestamp();
Observation<int> freshObservation = Observation<int>.Capture(
    42,
    ObservationSource.NativeStatistic,
    ObservationConfidence.High,
    TimeSpan.FromSeconds(1),
    sequence: 7,
    capturedAtTicks: capturedAt);
Check(
    freshObservation.IsFresh(capturedAt + Stopwatch.Frequency)
        && !freshObservation.IsFresh(capturedAt + Stopwatch.Frequency + 1)
        && !freshObservation.IsFresh(capturedAt - 1),
    "Observation freshness did not use bounded monotonic time.");

FlightRecorder recorder = new();
for (int index = 0; index < FlightRecorder.Capacity + 5; index++)
{
  recorder.Record(
      TimelineEventKind.Progress,
      "CG-TEST",
      "peer\n" + index,
      new string('界', 600) + "\u0001");
}

IReadOnlyList<TimelineEntry> timeline = recorder.Snapshot(100);
Check(
    recorder.Count == FlightRecorder.Capacity
        && timeline.Count == 100
        && timeline[0].Sequence
            == FlightRecorder.Capacity + 5 - 100 + 1
        && timeline[^1].Sequence == FlightRecorder.Capacity + 5,
    "The flight recorder did not remain bounded or ordered.");
Check(
    recorder.DroppedCount == 5,
    "The flight recorder did not count overwritten events.");
Check(
    timeline.All(entry =>
        Encoding.UTF8.GetByteCount(entry.Code)
            + Encoding.UTF8.GetByteCount(entry.Subject)
            + Encoding.UTF8.GetByteCount(entry.Message)
            <= FlightRecorder.MaxEntryUtf8Bytes
        && !entry.Subject.Any(char.IsControl)
        && !entry.Message.Any(char.IsControl)),
    "The flight recorder retained oversized or control-character text.");

int fuseCalls = 0;
OptionalModuleFuse fuse = new();
Check(
    !fuse.TryRun(() => throw new InvalidOperationException("fixture"))
        && fuse.IsDisabled
        && !fuse.TryRun(() => fuseCalls++)
        && fuseCalls == 0,
    "An optional module restarted after its first unhandled failure.");
fuse.Reset();
Check(
    fuse.TryRun(() => fuseCalls++)
        && fuseCalls == 1
        && !fuse.IsDisabled,
    "An optional module fuse did not reset at a new session boundary.");

ToolkitSession toolkitSession = new();
toolkitSession.Begin();
Guid firstSessionId = toolkitSession.Id;
toolkitSession.MembershipChanged();
Check(
    toolkitSession.IsActive
        && toolkitSession.MembershipEpoch == 2,
    "Toolkit session membership did not advance its epoch.");
toolkitSession.End();
toolkitSession.Begin();
Check(
    toolkitSession.Id != firstSessionId
        && toolkitSession.MembershipEpoch == 1,
    "A new Toolkit session reused the prior identity or epoch.");

NetworkHistory history = new();
for (int index = 0; index < NetworkHistory.Capacity + 3; index++)
{
  history.Add(new NetworkSample(
      index,
      index,
      0.01f,
      0.5,
      RemoteIsLoading: false,
      Connected: true));
}
Check(
    history.Count == NetworkHistory.Capacity
        && history.Snapshot()[0].CapturedAtTicks == 3
        && history.Snapshot()[^1].CapturedAtTicks
            == NetworkHistory.Capacity + 2,
    "The 60-second network history was not a bounded ordered ring.");

ProgressLease lease = new();
lease.Reset(0);
WaitAssessment shortWait = lease.Observe(
    "phase-a",
    WaitReasonCode.Unknown,
    ObservationConfidence.Low,
    "fixture",
    canFlagStall: true,
    Stopwatch.Frequency / 4);
WaitAssessment longWait = lease.Observe(
    "phase-a",
    WaitReasonCode.Unknown,
    ObservationConfidence.Low,
    "fixture",
    canFlagStall: true,
    91L * Stopwatch.Frequency);
Check(
    shortWait.DurationTicks == 0
        && longWait.Stall == StallLevel.HighlySuspected,
    "The progress lease ignored stabilization or soft-lock thresholds.");

AlertCenter alerts = new();
long alertNow = Stopwatch.GetTimestamp();
for (int index = 0; index < 20; index++)
{
  alerts.Add(
      AlertSeverity.Warning,
      "CG-NETWORK",
      "P1",
      "packet loss",
      alertNow + index);
}
Check(
    alerts.Snapshot().Count == 1
        && alerts.Snapshot()[0].Count == 20,
    "The alert center did not deduplicate a five-second burst.");
alerts.Clear();
for (int index = 0; index < AlertCenter.Capacity; index++)
{
  alerts.Add(
      AlertSeverity.Fatal,
      "CG-FATAL-" + index,
      "local",
      "fatal",
      alertNow + index);
}
alerts.Add(
    AlertSeverity.Fatal,
    "CG-CURRENT-FATAL",
    "local",
    "current fatal",
    alertNow + AlertCenter.Capacity);
Check(
    alerts.Snapshot().Count == AlertCenter.Capacity
        && alerts.Snapshot().Any(alert =>
            alert.Code == "CG-CURRENT-FATAL"),
    "A full alert center dropped the current fatal alert.");

CueRateLimiter cues = new();
Check(
    cues.TryTake(1, alertNow)
        && !cues.TryTake(1, alertNow + Stopwatch.Frequency)
        && cues.TryTake(2, alertNow + Stopwatch.Frequency)
        && cues.TryTake(3, alertNow + Stopwatch.Frequency)
        && !cues.TryTake(4, alertNow + Stopwatch.Frequency)
        && cues.TryTake(1, alertNow + 10L * Stopwatch.Frequency),
    "Sound cue rate limits did not enforce 2s per type and 3/10s global.");

PeerIdentityMap identities = new();
PeerIdentity firstIdentity = identities.GetOrAdd(42);
for (ulong index = 43; index < 55; index++)
{
  identities.GetOrAdd(index);
}
Check(
    identities.GetOrAdd(42) == firstIdentity
        && identities.GetOrAdd(54).Ordinal == 13
        && identities.GetOrAdd(54).Label == "P13",
    "Session peer identities were not stable beyond the base palette.");

Guid protocolSession = Guid.ParseExact(
    "00112233445566778899aabbccddeeff",
    "N");
ToolkitEnvelope protocolEnvelope = new(
    ToolkitEnvelopeCodec.Major,
    ToolkitEnvelopeCodec.Minor,
    ToolkitMessageType.Hello,
    0,
    protocolSession,
    1,
    ToolkitHelloCodec.Encode(
        ToolkitFeature.HealthMatrix | ToolkitFeature.Heartbeat,
        locale: 1));
Check(
    ToolkitEnvelopeCodec.TryEncode(protocolEnvelope, out byte[] encodedEnvelope)
        && encodedEnvelope.Length
            == ToolkitEnvelopeCodec.FixedBytes
                + ToolkitHelloCodec.PayloadBytes
        && ToolkitEnvelopeCodec.TryDecode(
            encodedEnvelope,
            out ToolkitEnvelope decodedEnvelope,
            out _)
        && decodedEnvelope.Major == protocolEnvelope.Major
        && decodedEnvelope.Minor == protocolEnvelope.Minor
        && decodedEnvelope.Type == protocolEnvelope.Type
        && decodedEnvelope.Flags == protocolEnvelope.Flags
        && decodedEnvelope.SessionId == protocolEnvelope.SessionId
        && decodedEnvelope.Sequence == protocolEnvelope.Sequence
        && decodedEnvelope.Payload.SequenceEqual(protocolEnvelope.Payload)
        && ToolkitHelloCodec.TryDecode(
            decodedEnvelope.Payload,
            out ToolkitFeature decodedFeatures,
            out byte decodedLocale)
        && decodedFeatures
            == (ToolkitFeature.HealthMatrix | ToolkitFeature.Heartbeat)
        && decodedLocale == 1,
    "Diagnostics envelope/Hello did not round-trip canonically.");
byte[] truncatedEnvelope = encodedEnvelope[..^1];
byte[] oversizedEnvelope =
    new byte[ToolkitEnvelopeCodec.MaxEncodedBytes + 1];
Check(
    !ToolkitEnvelopeCodec.TryDecode(
        truncatedEnvelope,
        out _,
        out _)
        && !ToolkitEnvelopeCodec.TryDecode(
            oversizedEnvelope,
            out _,
            out _)
        && !ToolkitEnvelopeCodec.TryDecode(
            encodedEnvelope.AsSpan(0, ToolkitEnvelopeCodec.FixedBytes - 1),
            out _,
            out _)
        && ToolkitSequence.IsNewer(1, uint.MaxValue)
        && !ToolkitSequence.IsNewer(uint.MaxValue, 1),
    "Diagnostics bounds or sequence wrap handling regressed.");
ToolkitStateRow[] stateRows =
[
    new(
        1,
        ToolkitStateFlags.ToolkitAvailable
            | ToolkitStateFlags.GuardHealthy
            | ToolkitStateFlags.Connected
            | ToolkitStateFlags.ReadyKnown,
        (byte)WaitReasonCode.PlayerChoice,
        (byte)ObservationConfidence.High,
        7,
        42,
        5),
    new(
        2,
        ToolkitStateFlags.ToolkitAvailable
            | ToolkitStateFlags.PeerDeclared,
        (byte)WaitReasonCode.None,
        (byte)ObservationConfidence.Low,
        0,
        ushort.MaxValue,
        ushort.MaxValue)
];
Check(
    ToolkitStateCodec.TryEncode(stateRows, out byte[] statePayload)
        && ToolkitStateCodec.TryDecode(
            statePayload,
            out ToolkitStateRow[] decodedRows)
        && decodedRows.SequenceEqual(stateRows)
        && !ToolkitStateCodec.TryDecode(
            [byte.MaxValue],
            out _)
        && ToolkitEnvironmentCode.TryCreate(
            protocolSession,
            "v0.109.1",
            FingerprintCodec.ProtocolVersion,
            new string('a', 64),
            out string shortCode)
        && shortCode.Length == 10
        && shortCode.All(character =>
            character is >= 'A' and <= 'Z'
                or >= '2' and <= '7'),
    "Bounded state rows or session-salted environment code regressed.");
ToolkitSendLimiter sendLimiter = new();
long limiterNow = Stopwatch.GetTimestamp();
Check(
    Enumerable.Range(0, ToolkitSendLimiter.DefaultBurst)
        .All(_ => sendLimiter.TryConsume(1, limiterNow))
        && !sendLimiter.TryConsume(1, limiterNow)
        && sendLimiter.TryConsume(
            1,
            limiterNow + Stopwatch.Frequency)
        && !sendLimiter.TryConsume(0, limiterNow)
        && !sendLimiter.TryConsume(
            ToolkitSendLimiter.MaxPeers + 1,
            limiterNow),
    "Shared diagnostics token bucket did not enforce 1/s burst-4 bounds.");
ToolkitQuickStatusLimiter quickLimiter = new();
Check(
    Enumerable.Range(0, 3).All(_ =>
        quickLimiter.TryTake(limiterNow))
        && !quickLimiter.TryTake(limiterNow)
        && !quickLimiter.TryTake(
            limiterNow + 2 * Stopwatch.Frequency)
        && quickLimiter.TryTake(
            limiterNow + 3 * Stopwatch.Frequency)
        && ToolkitQuickStatusCodec.TryEncode(
            2,
            ToolkitQuickStatus.PleaseWait,
            out byte[] quickPayload)
        && ToolkitQuickStatusCodec.TryDecode(
            quickPayload,
            out byte quickOrigin,
            out ToolkitQuickStatus quickStatus)
        && quickOrigin == 2
        && quickStatus == ToolkitQuickStatus.PleaseWait
        && !ToolkitQuickStatusCodec.TryDecode(
            [0, byte.MaxValue],
            out _,
            out _),
    "C1 fixed-enum codec or burst/cooldown limiter regressed.");

string componentEntry = FingerprintCodec.ComponentEntry(
    "中文-Mod_Id",
    new string('a', 64));
Check(
    FingerprintCodec.TryParseComponentEntry(
        componentEntry,
        out string parsedComponentId)
        && parsedComponentId == "中文-Mod_Id"
        && !FingerprintCodec.TryParseComponentEntry(
            componentEntry[..^1] + "x",
            out _),
    "Per-Mod compatibility entry encoding became ambiguous.");

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

DirectoryInfo persistenceTemporary =
    Directory.CreateTempSubdirectory("bettercoop-persistence-selfcheck-");
try
{
  string reportsRoot = Path.Combine(
      persistenceTemporary.FullName,
      "reports");
  ToolkitReportHistory.Configure(reportsRoot);
  ToolkitReportHistory.Remember(
      "token=secret C:\\Users\\fixture\\save.dat");
  Check(
      !Directory.Exists(reportsRoot),
      "Report history created files before explicit user save.");
  for (int index = 0;
       index < ToolkitReportHistory.MaxReports + 1;
       index++)
  {
    ToolkitReportHistory.Remember(
        $"report {index} token=secret C:\\Users\\fixture\\save.dat");
    Check(
        ToolkitReportHistory.TrySaveLatest(out _),
        "An explicit report save failed in a writable directory.");
  }

  string[] reportFiles = Directory.GetFiles(reportsRoot, "report-*.txt");
  Check(
      reportFiles.Length == ToolkitReportHistory.MaxReports
          && reportFiles.All(path =>
          {
            string text = File.ReadAllText(path);
            return !text.Contains("secret", StringComparison.Ordinal)
                  && !text.Contains("C:\\Users", StringComparison.Ordinal);
          }),
      "Manual report retention or pre-write redaction failed.");
  IReadOnlyList<string> reportNames =
      ToolkitReportHistory.ReportNames();
  Check(
      reportNames.Count == ToolkitReportHistory.MaxReports
          && ToolkitReportHistory.TryRead(
              reportNames[0],
              out string savedReport,
              out _)
          && savedReport.StartsWith("report ", StringComparison.Ordinal)
          && !ToolkitReportHistory.TryRead(
              "..\\outside.txt",
              out _,
              out _)
          && !ToolkitReportHistory.TryDelete(
              "..\\outside.txt",
              out _),
      "Report history listing/read path validation failed.");
  Check(
      ToolkitReportHistory.TryDelete(reportNames[0], out _)
          && ToolkitReportHistory.ReportCount()
              == ToolkitReportHistory.MaxReports - 1
          && ToolkitReportHistory.Clear()
              == ToolkitReportHistory.MaxReports - 1
          && ToolkitReportHistory.ReportCount() == 0,
      "Report history single-delete or clear failed.");

  string preferencesRoot = Path.Combine(
      persistenceTemporary.FullName,
      "preferences");
  Check(
      ToolkitPreferenceStore.ConfigureAndLoad(preferencesRoot)
          == ToolkitPreferences.Default,
      "Missing preferences did not use safe defaults.");
  ToolkitPreferences preferences = ToolkitPreferences.Default with
  {
    SoundEnabled = false,
    SoundVolume = 0.25f,
    UiScale = 2f,
    ReducedMotion = true,
    HighContrast = true,
    RollbackEnabled = true
  };
  Check(
      ToolkitPreferenceStore.TrySave(preferences, out _)
          && ToolkitPreferenceStore.ConfigureAndLoad(preferencesRoot)
              == preferences
          && !File.Exists(Path.Combine(
              preferencesRoot,
              "preferences.json.tmp")),
      "Preferences did not round-trip through an atomic bounded save.");
  File.WriteAllText(
      Path.Combine(preferencesRoot, "preferences.json"),
      """{"Schema":1,"SoundVolume":99,"UiScale":0}""");
  Check(
      ToolkitPreferenceStore.ConfigureAndLoad(preferencesRoot)
          == ToolkitPreferences.Default,
      "Invalid preferences did not fail open to safe defaults.");

  string sidecarRoot = Path.Combine(
      persistenceTemporary.FullName,
      "sidecars");
  string nativeSave = Path.Combine(
      persistenceTemporary.FullName,
      "current_multiplayer_run.save");
  byte[] originalSave = "secret-save-bytes"u8.ToArray();
  File.WriteAllBytes(nativeSave, originalSave);
  SaveEnvironmentStamp saveEnvironment = new(
      "v0.109.1",
      4,
      FingerprintCodec.Hash("environment"),
      FingerprintCodec.Hash("mods"),
      string.Empty,
      FingerprintCodec.Hash("manifest"),
      FingerprintCodec.Hash("assembly"),
      FingerprintCodec.Hash("content"),
      FingerprintCodec.Hash("other"),
      string.Empty);
  SaveEnvironmentSidecars.Configure(sidecarRoot);
  Check(
      SaveEnvironmentSidecars.TrySeal(
          nativeSave,
          saveEnvironment,
          out _)
      && File.ReadAllBytes(nativeSave).SequenceEqual(originalSave)
      && SaveEnvironmentSidecars.Review(
          nativeSave,
          saveEnvironment).State
          == SaveSealState.MatchingLocalRecord,
      "A native save was changed or its matching local sidecar was rejected.");
  SaveEnvironmentStamp changedEnvironment = saveEnvironment with
  {
    AssemblyDigest = FingerprintCodec.Hash("changed assembly")
  };
  SaveSealReview environmentReview = SaveEnvironmentSidecars.Review(
      nativeSave,
      changedEnvironment);
  Check(
      environmentReview.State == SaveSealState.EnvironmentMismatch
          && environmentReview.Differences.Contains("assemblies"),
      "A sealed save did not classify an assembly-category environment difference.");
  string sidecarFile = Directory.EnumerateFiles(
          Path.Combine(sidecarRoot, "save-environments"),
          "*.sidecar",
          SearchOption.TopDirectoryOnly)
      .Single();
  string sidecarJson = File.ReadAllText(sidecarFile);
  Check(
      new FileInfo(sidecarFile).Length
          <= SaveEnvironmentSidecars.MaxSidecarBytes
      && !sidecarJson.Contains(
          nativeSave,
          StringComparison.OrdinalIgnoreCase)
      && !sidecarJson.Contains(
          "secret-save-bytes",
          StringComparison.Ordinal),
      "A sidecar exceeded bounds or stored a native path/content.");
  File.WriteAllBytes(
      nativeSave,
      "changed-save-byte"u8.ToArray());
  Check(
      SaveEnvironmentSidecars.Review(
          nativeSave,
          saveEnvironment).State
          == SaveSealState.SaveBindingMismatch,
      "Changed native save bytes were accepted by an existing sidecar.");
  File.WriteAllText(sidecarFile + ".tmp", "partial");
  SaveEnvironmentSidecars.Configure(sidecarRoot);
  Check(
      !File.Exists(sidecarFile + ".tmp")
          && File.Exists(sidecarFile),
      "Stale sidecar temp cleanup removed a complete record or kept a partial one.");

  string rollbackRoot = Path.Combine(
      persistenceTemporary.FullName,
      "rollback");
  string rollbackSave = Path.Combine(
      persistenceTemporary.FullName,
      "rollback-current.save");
  byte[] checkpointBytes = "checkpoint-node-b"u8.ToArray();
  byte[] preRollbackBytes = "current-node-c"u8.ToArray();
  File.WriteAllBytes(rollbackSave, checkpointBytes);
  RollbackCheckpointJournal.Configure(rollbackRoot);
  RollbackCheckpointBinding rollbackBinding = new(
      FingerprintCodec.Hash("run"),
      Guid.NewGuid().ToString("N"),
      string.Empty,
      0,
      1,
      1,
      9,
      8,
      2,
      "Act 1, node 8:2",
      "v0.109.1",
      6,
      FingerprintCodec.Hash("environment"),
      new string('a', 32),
      FingerprintCodec.Hash("seed-tag"),
      FingerprintCodec.Hash("rng"));
  Check(
      RollbackCheckpointJournal.TryArchive(
          rollbackSave,
          rollbackBinding,
          out RollbackCheckpointMetadata? archived,
          out _)
      && archived != null
      && RollbackCheckpointJournal.TryArchive(
          rollbackSave,
          rollbackBinding,
          out RollbackCheckpointMetadata? duplicate,
          out _)
      && duplicate?.CheckpointId == archived.CheckpointId
      && RollbackCheckpointJournal.List(
          rollbackBinding.RunId) is [{ } listed]
      && listed.CheckpointId == archived.CheckpointId
      && RollbackCheckpointIdentity.Create(listed).Length
          == ToolkitRunControlCodec.DigestBytes,
      "R1 checkpoint archive, de-duplication or verified listing failed.");
  RollbackCheckpointMetadata archivedCheckpoint = archived
      ?? throw new InvalidOperationException(
          "R1 archive unexpectedly returned null metadata.");
  File.WriteAllBytes(rollbackSave, preRollbackBytes);
  Guid recoveryTransaction = Guid.NewGuid();
  Check(
      RollbackCheckpointJournal.TryActivate(
          archivedCheckpoint.CheckpointId,
          recoveryTransaction,
          rollbackSave,
          out _,
          out _)
      && File.ReadAllBytes(rollbackSave)
          .SequenceEqual(checkpointBytes)
      && RollbackCheckpointJournal.TryGetRecoveryTransaction(
          out Guid pendingRecovery)
      && pendingRecovery == recoveryTransaction
      && RollbackCheckpointJournal.TryRecover(
          recoveryTransaction,
          out _)
      && File.ReadAllBytes(rollbackSave)
          .SequenceEqual(preRollbackBytes),
      "R1 atomic activation or explicit emergency recovery failed.");
  Guid commitTransaction = Guid.NewGuid();
  Check(
      RollbackCheckpointJournal.TryActivate(
          archivedCheckpoint.CheckpointId,
          commitTransaction,
          rollbackSave,
          out _,
          out _)
      && RollbackCheckpointJournal.TryCommit(
          commitTransaction,
          out _)
      && File.ReadAllBytes(rollbackSave)
          .SequenceEqual(checkpointBytes)
      && RollbackCheckpointJournal.TryResolveActiveBranch(
          rollbackSave,
          out string resolvedRun,
          out string resolvedBranch,
          out string resolvedParent,
          out ulong resolvedFork)
      && resolvedRun == rollbackBinding.RunId
      && resolvedBranch == commitTransaction.ToString("N")
      && resolvedParent == rollbackBinding.BranchId
      && resolvedFork == archivedCheckpoint.VisitIndex,
      "R1 committed activation or branch-lineage recovery failed.");
  string compressedCheckpoint = Path.Combine(
      rollbackRoot,
      "rollback-journal",
      rollbackBinding.RunId,
      archivedCheckpoint.CheckpointId + ".save.br");
  byte[] corrupt = File.ReadAllBytes(compressedCheckpoint);
  corrupt[0] ^= 0xFF;
  File.WriteAllBytes(compressedCheckpoint, corrupt);
  Check(
      RollbackCheckpointJournal.List(
          rollbackBinding.RunId).Count == 0,
      "R1 corrupted checkpoint payload remained selectable.");

  string markerRoot = Path.Combine(
      persistenceTemporary.FullName,
      "marker");
  string crashLog = Path.Combine(
      persistenceTemporary.FullName,
      "godot.log");
  File.WriteAllText(
      crashLog,
      "previous lines\nFatal error. 0xC0000005\n");
  Check(
      ToolkitCrashMarker.Start(markerRoot, crashLog) == null,
      "A clean first launch incorrectly reported an old crash.");
  ToolkitCrashMarker.UpdateStage("waiting-initial-info");
  CrashReview review = ToolkitCrashMarker.Start(markerRoot, crashLog)
      ?? throw new InvalidOperationException(
          "A stale crash marker was not reviewed.");
  Check(
      review.Confidence == CrashReviewConfidence.High
          && review.Signature == "native-access-violation"
          && review.LastStage == "waiting-initial-info",
      "Crash review did not separate marker evidence and native signature.");
  ToolkitCrashMarker.Finish();
  Check(
      !File.Exists(Path.Combine(markerRoot, "session.marker")),
      "A normal finish did not remove the crash marker.");
}
finally
{
  ToolkitCrashMarker.Finish();
  Directory.Delete(persistenceTemporary.FullName, recursive: true);
}

IReadOnlyList<DoctorFinding> doctorFindings = ModDependencyDoctor.Analyze(
[
    new("LibraryMod", "1.5.0", true, []),
    new(
        "FeatureMod",
        "1.0.0",
        true,
        [new("LibraryMod", "2.0")]),
    new("CycleA", "1.0.0", true, [new("CycleB", "1")]),
    new("CycleB", "1.0.0", true, [new("CycleA", "1")]),
    new("BrokenConstraint", "1.0.0", true, [new("LibraryMod", ">=2")]),
    new("MissingConsumer", "1.0.0", true, [new("AbsentMod", "1.0")])
]);
Check(
    doctorFindings.Any(finding =>
        finding.Confidence == "Confirmed"
        && finding.Evidence.Contains(
            "FeatureMod requires LibraryMod >= 2.0.0; installed 1.5.0",
            StringComparison.Ordinal))
    && doctorFindings.Any(finding =>
        finding.Confidence == "Confirmed"
        && finding.Evidence.Contains(
            "Dependency cycle: CycleA -> CycleB",
            StringComparison.Ordinal))
    && doctorFindings.Any(finding =>
        finding.Confidence == "Inconclusive"
        && finding.Evidence.Contains(
            "invalid minimum version",
            StringComparison.Ordinal))
    && doctorFindings.Any(finding =>
        finding.Confidence == "Confirmed"
        && finding.Evidence.Contains(
            "requires missing dependency AbsentMod",
            StringComparison.Ordinal)),
    "Local Mod Doctor did not classify version, cycle, invalid and missing dependencies safely.");

List<DoctorModRecord> bisectMods =
[
    new("CommonA", "1.0.0", true, []),
    new("CommonB", "1.0.0", true, [])
];
bisectMods.AddRange(Enumerable.Range(1, 8).Select(index =>
    new DoctorModRecord(
        $"Candidate{index}",
        "1.0.0",
        true,
        [new("CommonA", "1.0")])));
ModBisectPlan bisectPlan = ModBisectPlanner.Create(
    bisectMods,
    Enumerable.Range(1, 8)
        .Select(index => $"Candidate{index}")
        .ToArray());
Check(
    bisectPlan is
    {
      Available: true,
      CandidateGroups: 8,
      MaximumRounds: <= 5
    }
    && bisectPlan.Text.Contains(
        "Baseline: CommonA, CommonB",
        StringComparison.Ordinal)
    && bisectPlan.Text.Contains(
        "reproduced -> R1Y, not reproduced -> R1N",
        StringComparison.Ordinal)
    && bisectPlan.Text.Length <= ModBisectPlanner.MaxOutputCharacters,
    "Dependency-aware A/B plan did not preserve baseline dependencies or round bounds.");
ModBisectPlan unsafeBisect = ModBisectPlanner.Create(
    [
        new("Candidate", "1.0.0", true, []),
        new(
            "BaselineConsumer",
            "1.0.0",
            true,
            [new("Candidate", "1.0")])
    ],
    ["Candidate"]);
Check(
    !unsafeBisect.Available
    && unsafeBisect.Text.Contains(
        "depends on a candidate",
        StringComparison.Ordinal),
    "A/B planner proposed disabling a dependency required by its baseline.");

SettingsDeclarationRead validSettingsDeclaration =
    SettingsDeclarationCodec.Parse(
        """
        {
          "Schema": 1,
          "Providers": [
            { "Id": "Fixture", "Schema": 1 },
            { "Id": "Fixture:combat", "Schema": 2 }
          ]
        }
        """u8.ToArray(),
        "Fixture");
Check(
    validSettingsDeclaration is
    {
      Present: true,
      Error.Length: 0,
      Providers.Count: 2
    }
    && validSettingsDeclaration.Providers[1].ProviderId
        == "Fixture:combat",
    "A valid bounded deterministic-settings declaration was rejected.");
Check(
    SettingsDeclarationCodec.Parse(
        """{"Schema":1,"Providers":[{"Id":"../Fixture","Schema":1}]}"""u8
            .ToArray(),
        "Fixture").Error.Length > 0
    && SettingsDeclarationCodec.Parse(
        """{"Schema":1,"Providers":[{"Id":"Other","Schema":1}]}"""u8
            .ToArray(),
        "Fixture").Error.Length > 0
    && SettingsDeclarationCodec.Parse(
        """{"Schema":1,"Providers":[],"Unknown":true}"""u8.ToArray(),
        "Fixture").Error.Length > 0
    && SettingsDeclarationCodec.Parse(
        """{"Schema":1,"Providers":[{"Id":"Fixture","Schema":1},{"Id":"Fixture","Schema":1}]}"""u8
            .ToArray(),
        "Fixture").Error.Length > 0,
    "Traversal, cross-Mod ownership, unknown fields or duplicate providers were accepted.");
string tooManyProviders =
    """{"Schema":1,"Providers":["""
    + string.Join(
        ',',
        Enumerable.Range(0, 65).Select(index =>
            $$"""{"Id":"Fixture:{{index}}","Schema":1}"""))
    + "]}";
Check(
    SettingsDeclarationCodec.Parse(
        Encoding.UTF8.GetBytes(tooManyProviders),
        "Fixture").Error.Length > 0
    && SettingsDeclarationCodec.Parse(
        """{"Schema":1,"Providers":["""u8.ToArray(),
        "Fixture").Error.Length > 0
    && SettingsDeclarationCodec.Parse(
        new byte[SettingsDeclarationCodec.MaxFileBytes + 1],
        "Fixture").Error.Length > 0,
    "Provider-count, malformed-JSON or file-size bounds were not enforced.");

DirectoryInfo temporary = Directory.CreateTempSubdirectory("bettercoop-selfcheck-");
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
  Check(
      first.Digest
          == PackageHasher.Capture(firstRoot, 99, "Fixture").Digest,
      "Mod load order leaked into the per-Mod package digest.");
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

F1CategoryDigest[] golden =
[
    F1SchemaV1.Run(new(7, 1, 2, 5)),
    F1SchemaV1.Combat(new(7, 3, 2, true)),
    F1SchemaV1.Players(
        [new(127, 1, 50, 80, 10, 3, true, 5)]),
    F1SchemaV1.Monsters(
        [new(31, 7, "SLIME", 20, 30, 2)]),
    F1SchemaV1.CardCounts(
        [new(31, 1, 5, 10, 3, 1)]),
    F1SchemaV1.Effects(
        [new(63, 1, 1, 1, "POWER", 2, 1)]),
    F1SchemaV1.RngCounts(
        [new(3, "Rng.NextBool", 9)]),
    F1SchemaV1.Contributions(
        [new(15, "Fixture", 1, 2, Enumerable.Range(0, 32)
            .Select(value => (byte)value).ToArray())])
];
string[] expectedGolden =
[
    "46CF92E8B1329B96205EB8307C49630DF8D11849A606BDDEB4F2750E34FA00F9",
    "1C79DF0F78D89D37DAE9D5305D216D599C89AFCF50337E495B381B2453A1505C",
    "FCAFCC16C3D58F37C6CC66643D8A82706AF5FCD9A7883D48571180BF18BBDE12",
    "214C3C8BDAC21227556FEA522C7BBC76DD42EE1937C8B388188B3DC0143CBFB3",
    "88EA0AE80CD9A1F86DEF292FFB37A072E475355D4255B43470B7ACD6D7C0CBD3",
    "ED596472C81F48D507D1236170DD305BF621D7518E330DCCFB64125B9A6B445F",
    "21D4B2E7FBCF838B5D9F0F56156821DCE8973AAF6F039BCE74C40A5946E35C89",
    "7056C27B7EB27F31A5F231C26C1EE3AE66DF6C72B6455C2F5FA5BAD7FACF9D64"
];
for (int index = 0; index < golden.Length; index++)
{
  Check(
      golden[index].Available
          && Convert.ToHexString(golden[index].Digest)
              == expectedGolden[index],
      $"F1SchemaV1 category {index + 1} differs from its independent golden vector.");
}

F1CategoryDigest orderedPlayers = F1SchemaV1.Players(
[
    new(127, 2, 40, 70, 0, 2, false, 3),
    new(127, 1, 50, 80, 10, 3, true, 5)
]);
F1CategoryDigest reversedPlayers = F1SchemaV1.Players(
[
    new(127, 1, 50, 80, 10, 3, true, 5),
    new(127, 2, 40, 70, 0, 2, false, 3)
]);
Check(
    orderedPlayers.Digest.SequenceEqual(reversedPlayers.Digest),
    "F1 player ordering changed a canonical digest.");
Check(
    !F1SchemaV1.Players(
        [
            new(1, 1, 0, 0, 0, 0, false, 0),
            new(1, 1, 0, 0, 0, 0, false, 0)
        ]).Available,
    "F1 duplicate player sort keys were accepted.");
Check(
    !F1SchemaV1.Monsters(
        [new(31, 1, "\uD800", 1, 1, 0)]).Available,
    "F1 invalid UTF-16 was accepted.");
Check(
    !F1SchemaV1.Effects(
        Enumerable.Range(0, 257)
            .Select(index => new F1Effect(
                63,
                1,
                (ulong)index + 1,
                1,
                "P",
                1,
                1))
            .ToArray()).Available,
    "F1 over-limit effect collection was accepted.");

Guid f1Session = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
Check(
    F1SchemaV1.TryCreateCheckpoint(
        f1Session,
        42,
        "After action",
        golden,
        out F1Checkpoint checkpointA)
        && F1CheckpointCodec.TryEncode(
            checkpointA,
            out byte[] checkpointPayload)
        && checkpointPayload.Length == F1CheckpointCodec.PayloadBytes
        && F1CheckpointCodec.TryDecode(
            checkpointPayload,
            out F1Checkpoint checkpointRoundTrip)
        && checkpointRoundTrip.Tags[0].SequenceEqual(
            checkpointA.Tags[0]),
    "F1 checkpoint tag/codec round trip failed.");
Check(
    F1SchemaV1.TryCreateCheckpoint(
        Guid.Parse("10112233-4455-6677-8899-aabbccddeeff"),
        42,
        "After action",
        golden,
        out F1Checkpoint checkpointOtherSession)
        && !checkpointOtherSession.Tags[0].SequenceEqual(
            checkpointA.Tags[0]),
    "F1 session rollover did not change the wire tag.");

F1CategoryDigest[] divergent = golden.ToArray();
divergent[2] = F1SchemaV1.Players(
    [new(127, 1, 49, 80, 10, 3, true, 5)]);
Check(
    F1SchemaV1.TryCreateCheckpoint(
        f1Session,
        42,
        "After action",
        divergent,
        out F1Checkpoint checkpointB),
    "F1 divergent checkpoint fixture could not be created.");
F2History f2 = new();
Check(
    F1SchemaV1.TryCreateCheckpoint(
        f1Session,
        41,
        "Before action",
        golden,
        out F1Checkpoint checkpointCommon),
    "F2 common checkpoint fixture could not be created.");
Check(
    F1SchemaV1.TryCreateCheckpoint(
            f1Session,
            43,
            "After recovery",
            golden,
            out F1Checkpoint checkpointRecovered),
    "F2 recovered checkpoint fixture could not be created.");
f2.Add(1, checkpointCommon);
f2.Add(2, checkpointCommon);
f2.Add(1, checkpointA);
f2.Add(2, checkpointB);
f2.Add(1, checkpointRecovered);
f2.Add(2, checkpointRecovered);
F2Result f2Result = f2.Compare(1, 2);
Check(
    f2Result.LastCommon == 41
        && f2Result.FirstObservedDivergent == 42
        && f2Result.LowestDifferingCategory
            == F1Category.PlayersPublic
        && f2Result.Status.Contains(
        "not established",
            StringComparison.Ordinal),
    "F2 failed to report the first observed category divergence with attribution limits.");
F1CheckpointResult checkpointResult =
    new(2, 42, 41, 0xFF, 0x04);
byte[] checkpointResultPayload =
    F1CheckpointResultCodec.Encode(checkpointResult);
Check(
    checkpointResultPayload.Length
        == F1CheckpointResultCodec.PayloadBytes
        && F1CheckpointResultCodec.TryDecode(
            checkpointResultPayload,
            out F1CheckpointResult checkpointResultRoundTrip)
        && checkpointResultRoundTrip == checkpointResult,
    "F2 bounded subject/result codec failed.");
checkpointResultPayload[9] = 2;
Check(
    !F1CheckpointResultCodec.TryDecode(
        checkpointResultPayload,
        out _)
        && F1CheckpointResultCodec.Encode(
            checkpointResult with { SubjectOrdinal = 0 }).Length == 0,
    "F2 malformed result flag or subject was accepted.");

ulong[] consentRoster = [30, 10, 20];
byte[] rosterDigest = ToolkitConsentCodec.RosterDigest(
    f1Session,
    1,
    consentRoster);
byte[] commitToken = ToolkitConsentCodec.CommitToken(
    f1Session,
    ToolkitConsentFeature.HandSharing,
    1,
    rosterDigest);
ToolkitConsent consent = new(
    ToolkitConsentFeature.HandSharing,
    ToolkitConsentAction.Commit,
    1,
    rosterDigest,
    commitToken);
Check(
    rosterDigest.SequenceEqual(
        ToolkitConsentCodec.RosterDigest(
            f1Session,
            1,
            consentRoster.Reverse()))
        && ToolkitConsentCodec.TryEncode(
            consent,
            out byte[] consentPayload)
        && ToolkitConsentCodec.TryDecode(
            consentPayload,
            out ToolkitConsent consentRoundTrip)
        && consentRoundTrip.CommitToken.SequenceEqual(commitToken),
    "C9 consent roster binding/codec failed.");

ToolkitHandSnapshot hand = new(
    1,
    uint.MaxValue,
    2,
    [new("CARD_ID", 1, -1)]);
Check(
    ToolkitHandSnapshotCodec.TryEncode(hand, out byte[] handPayload)
        && ToolkitHandSnapshotCodec.TryDecode(
            handPayload,
            out ToolkitHandSnapshot handRoundTrip)
        && handRoundTrip.Cards.SequenceEqual(hand.Cards),
    "C9 hand snapshot round trip failed.");
byte[] maliciousHand = new byte[10];
BinaryPrimitives.WriteUInt32LittleEndian(maliciousHand, 1);
BinaryPrimitives.WriteUInt32LittleEndian(maliciousHand.AsSpan(4), 1);
maliciousHand[8] = 1;
maliciousHand[9] = byte.MaxValue;
Check(
    !ToolkitHandSnapshotCodec.TryDecode(
        maliciousHand,
        out _),
    "C9 uint/count-style allocation attack was accepted.");

Check(
    ToolkitTextCodec.TryEncode(
        0,
        "中文 e\u0301\nemoji 👩‍💻",
        out byte[] textPayload)
        && ToolkitTextCodec.TryDecode(
            textPayload,
            out ToolkitTextMessage textRoundTrip)
        && textRoundTrip.OriginOrdinal == 0
        && textRoundTrip.Text == "中文 é\nemoji 👩‍💻",
    "C11 Unicode normalization/round trip failed.");
Check(
    !ToolkitTextCodec.TryEncode(0, "bad\0text", out _)
        && !ToolkitTextCodec.TryEncode(0, "spoof\u202Ename", out _)
        && !ToolkitTextCodec.TryEncode(0, "1\n2\n3\n4\n5", out _)
        && !ToolkitTextCodec.TryEncode(
            0,
            "a" + string.Concat(
                Enumerable.Repeat("\u0301", 9)),
            out _)
        && !ToolkitTextCodec.TryEncode(
            0,
            new string('x', ToolkitTextCodec.MaxUtf8Bytes + 1),
            out _),
    "C11 unsafe or oversized text was accepted.");

ToolkitHandWatch watch = new(7, ToolkitLimits.MaxPlayers);
byte[] watchPayload = ToolkitHandWatchCodec.Encode(watch);
Check(
    ToolkitHandWatchCodec.TryDecode(
        watchPayload,
        out ToolkitHandWatch watchRoundTrip)
        && watchRoundTrip == watch
        && ToolkitHandWatchCodec.Encode(
            watch with
            {
              OwnerOrdinal = (byte)(ToolkitLimits.MaxPlayers + 1)
            }).Length == 0,
    "H13 hand-watch codec failed its player boundary.");

Guid rngSession =
    Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
byte[] rngSeedTag = ToolkitSeedTag.Create(
    rngSession,
    0x0102030405060708UL);
Check(
    Convert.ToHexString(rngSeedTag)
        == "77E313FBF6BD1873D5C35F74AA5412D33AB509BE3A308E4DE3E59DDE266E9733"
        && ToolkitSeedTag.Create(Guid.Empty, 1).Length == 0,
    "F9 seed-tag byte order or HMAC golden vector regressed.");

ToolkitRngCounter.Enable();
ToolkitRngCounter.Clear();
ToolkitRngAnalyzer.Observe(0, 1, 10, 0, 4);
ToolkitRngAnalyzer.Observe(0, 0, 0, 0, 1);
ToolkitRngAnalyzer.Observe(-1, 0, 0, 0, 0);
ToolkitRngAnalyzer.Observe(0, 0, 0, 0, 0, chaotic: true);
ToolkitRngStreamSummary[] rngStreams =
    ToolkitRngAnalyzer.Snapshot();
ToolkitRngSummary rngSummary = new(
    0,
    7,
    3,
    11,
    rngSeedTag,
    rngStreams);
Check(
    rngStreams[0].Calls == 2
        && rngStreams[0].RollingHash != 0
        && ToolkitRngAnalyzer.UnknownCalls == 1
        && ToolkitRngAnalyzer.ChaoticCalls == 1
        && ToolkitRngAnalyzer.Recent(8) is
        [
        {
          Stream: 0,
          Method: 1,
          CallIndex: 1,
          ArgumentA: 10,
          Result: 4
        },
        {
          Stream: 0,
          Method: 0,
          CallIndex: 2,
          Result: 1
        }
        ]
        && ToolkitRngSummaryCodec.TryEncode(
            rngSummary,
            out byte[] rngPayload)
        && rngPayload.Length == ToolkitRngSummaryCodec.PayloadBytes
        && ToolkitRngSummaryCodec.TryDecode(
            rngPayload,
            out ToolkitRngSummary rngRoundTrip)
        && rngRoundTrip.OriginOrdinal == 0
        && rngRoundTrip.MembershipEpoch == 7
        && rngRoundTrip.RollbackEpoch == 3
        && rngRoundTrip.CheckpointId == 11
        && rngRoundTrip.SeedTag.SequenceEqual(rngSeedTag)
        && rngRoundTrip.Streams.SequenceEqual(rngStreams)
        && !ToolkitRngSummaryCodec.TryDecode(
            rngPayload.AsSpan(0, rngPayload.Length - 1),
            out _),
    "F9 bounded observer or summary codec regressed.");

byte[] limitSettings =
    ToolkitLimitBreakContract.DigestSettings(
        enabled: true,
        multiplier: 1.25);
byte[] limitIdentity =
    ToolkitLimitBreakContract.DigestIdentity(
        "0.109.1",
        "0.1.3",
        "b2785afd3dc31fd6b32cb073af495ab343fcc31fa9e479049959ff43eb09356f",
        "0.4.66");
ToolkitLimitBreakCapability limitCapability = new(
    0,
    7,
    ToolkitLimitBreakContract.RequiredFlags,
    ToolkitLimitBreakContract.Capacity,
    ToolkitLimitBreakContract.SlotBits,
    ToolkitLimitBreakContract.ListBits,
    1,
    limitIdentity,
    limitSettings);
Check(
    Convert.ToHexString(limitSettings)
        == "341FC6E308723ACBB2AF258D79A3FF94"
        && Convert.ToHexString(limitIdentity)
            == "A7005D425C31BE596AE3D944F5E19D48"
        && ToolkitLimitBreakContract.IsCompatible(
            limitCapability,
            out _)
        && ToolkitLimitBreakContract.IsReadyCompatible(
            limitCapability with
            {
              Flags = limitCapability.Flags
                  & ~ToolkitLimitBreakFlags.SettingsSynchronized,
              HostSettingsEpoch = 0
            },
            out _)
        && !ToolkitLimitBreakContract.IsCompatible(
            limitCapability with
            {
              Flags = limitCapability.Flags
                  & ~ToolkitLimitBreakFlags.SettingsSynchronized,
              HostSettingsEpoch = 0
            },
            out string pendingSettingsReason)
        && pendingSettingsReason.Contains(
            nameof(ToolkitLimitBreakFlags.SettingsSynchronized),
            StringComparison.Ordinal)
        && ToolkitLimitBreakCapabilityCodec.TryEncode(
            limitCapability,
            out byte[] limitPayload)
        && limitPayload.Length
            == ToolkitLimitBreakCapabilityCodec.PayloadBytes
        && ToolkitLimitBreakCapabilityCodec.TryDecode(
            limitPayload,
            out ToolkitLimitBreakCapability limitRoundTrip)
        && limitRoundTrip.OriginOrdinal == 0
        && limitRoundTrip.MembershipEpoch == 7
        && limitRoundTrip.Flags
            == ToolkitLimitBreakContract.RequiredFlags
        && limitRoundTrip.ContractDigest.SequenceEqual(
            limitIdentity)
        && limitRoundTrip.SettingsDigest.SequenceEqual(
            limitSettings)
        && !ToolkitLimitBreakContract.IsCompatible(
            limitCapability with
            {
              Flags = limitCapability.Flags
                  & ~ToolkitLimitBreakFlags.Enabled
            },
            out string limitReason)
        && limitReason.Contains(
            nameof(ToolkitLimitBreakFlags.Enabled),
            StringComparison.Ordinal)
        && !ToolkitLimitBreakCapabilityCodec.TryEncode(
            limitCapability with
            {
              OriginOrdinal =
                  (byte)(ToolkitLimits.MaxPlayers + 1)
            },
            out _),
    "G13 capability contract, golden digest or codec regressed.");

ToolkitRunControl runControl = new(
    ToolkitRunControlAction.Prepare,
    1,
    ToolkitRunControlResult.None,
    7,
    3,
    Guid.ParseExact(
        "00112233445566778899aabbccddeeff",
        "N"),
    Guid.ParseExact(
        "ffeeddccbbaa99887766554433221100",
        "N"),
    42,
    Convert.FromHexString(
        "000102030405060708090A0B0C0D0E0F"
        + "101112131415161718191A1B1C1D1E1F"));
Check(
    ToolkitRunControlCodec.TryEncode(
        runControl,
        out byte[] runControlPayload)
        && runControlPayload.Length
            == ToolkitRunControlCodec.PayloadBytes
        && ToolkitRunControlCodec.TryDecode(
            runControlPayload,
            out ToolkitRunControl runControlRoundTrip)
        && runControlRoundTrip.Action == runControl.Action
        && runControlRoundTrip.OriginOrdinal
            == runControl.OriginOrdinal
        && runControlRoundTrip.Result == runControl.Result
        && runControlRoundTrip.MembershipEpoch
            == runControl.MembershipEpoch
        && runControlRoundTrip.RollbackEpoch
            == runControl.RollbackEpoch
        && runControlRoundTrip.TransactionId
            == runControl.TransactionId
        && runControlRoundTrip.CheckpointId
            == runControl.CheckpointId
        && runControlRoundTrip.VisitIndex
            == runControl.VisitIndex
        && runControlRoundTrip.CheckpointDigest.SequenceEqual(
            runControl.CheckpointDigest)
        && !ToolkitRunControlCodec.TryEncode(
            runControl with
            {
              OriginOrdinal =
                  (byte)(ToolkitLimits.MaxPlayers + 1)
            },
            out _)
        && !ToolkitRunControlCodec.TryDecode(
            runControlPayload.AsSpan(
                0,
                runControlPayload.Length - 1),
            out _),
    "R1 fixed Run Control codec or trust-boundary validation regressed.");
ulong[] rollbackRoster = [11, 22, 33, 44, 55];
Check(
    !ToolkitRunControlRoster.AllResponded(
        rollbackRoster,
        [11],
        new HashSet<ulong> { 11 })
        && !ToolkitRunControlRoster.AllResponded(
            rollbackRoster,
            rollbackRoster,
            new HashSet<ulong> { 11, 22, 33, 44 })
        && ToolkitRunControlRoster.AllResponded(
            rollbackRoster,
            rollbackRoster,
            rollbackRoster.ToHashSet())
        && !ToolkitRunControlRoster.IsExact(
            rollbackRoster,
            [11, 22, 33, 44, 55, 66]),
    "R1 committed before the complete original roster verified.");

ToolkitSendLimiter customLimiter = new(
    maxPeers: 1,
    tokensPerSecond: 2,
    burst: 2);
Check(
    customLimiter.TryConsume(1, 0)
        && customLimiter.TryConsume(1, 0)
        && !customLimiter.TryConsume(1, 0)
        && customLimiter.TryConsume(1, Stopwatch.Frequency / 2),
    "Configurable fair-send budget failed.");

ToolkitContributionLedger ledger = new();
Check(
    ledger.Add(1, 1, damage: 7, kills: 1)
        && !ledger.Add(1, 1, damage: 7)
        && ledger.Snapshot() is
        [
          {
            Damage: 7,
            Kills: 1
          }
        ],
    "C10 event de-duplication failed.");
ToolkitContributionSnapshot remoteContribution =
    new(2, 9, 8, 7, 6, 5);
Check(
    ToolkitContributionCodec.TryEncode(
        remoteContribution,
        out byte[] contributionPayload)
        && contributionPayload.Length
            == ToolkitContributionCodec.PayloadBytes
        && ToolkitContributionCodec.TryDecode(
            contributionPayload,
            out ToolkitContributionSnapshot contributionRoundTrip)
        && contributionRoundTrip == remoteContribution
        && ledger.TryApplySnapshot(remoteContribution)
        && !ledger.TryApplySnapshot(
            remoteContribution with { Damage = 8 })
        && ledger.TryGet(2, out ToolkitContributionSnapshot remoteStored)
        && remoteStored == remoteContribution,
    "C10 bounded snapshot codec or monotonic merge failed.");
ledger.KeepOnly(1);
Check(
    ledger.Snapshot() is [{ Ordinal: 1, Damage: 7 }],
    "C10 disabling sharing did not clear remote rows.");

int fuzzIterations = int.TryParse(
        Environment.GetEnvironmentVariable(
            "BETTERCOOP_FUZZ_ITERATIONS"),
        out int requestedFuzz)
    ? Math.Clamp(requestedFuzz, 1, 1_000_000)
    : 10_000;
Random fuzz = new(0x4347);
byte[] fuzzBytes = new byte[ToolkitEnvelopeCodec.MaxEncodedBytes];
long fuzzAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
for (int iteration = 0; iteration < fuzzIterations; iteration++)
{
  int length = fuzz.Next(fuzzBytes.Length + 1);
  fuzz.NextBytes(fuzzBytes.AsSpan(0, length));
  ReadOnlySpan<byte> input = fuzzBytes.AsSpan(0, length);
  ToolkitEnvelopeCodec.TryDecode(input, out _, out _);
  ToolkitConsentCodec.TryDecode(input, out _);
  ToolkitTextCodec.TryDecode(input, out _);
  ToolkitHandWatchCodec.TryDecode(input, out _);
  ToolkitRngSummaryCodec.TryDecode(input, out _);
  ToolkitLimitBreakCapabilityCodec.TryDecode(input, out _);
  ToolkitRunControlCodec.TryDecode(input, out _);
  ToolkitHandSnapshotCodec.TryDecode(input, out _);
  F1CheckpointCodec.TryDecode(input, out _);
  F1CheckpointResultCodec.TryDecode(input, out _);
  ToolkitContributionCodec.TryDecode(input, out _);
  ToolkitStateCodec.TryDecode(input, out _);
  if ((iteration & 1023) == 0)
  {
    ToolkitEnvelopeCodec.TryDecode(
        encodedEnvelope,
        out _,
        out _);
    ToolkitHandSnapshotCodec.TryDecode(
        handPayload,
        out _);
  }
}

long fuzzAllocated = GC.GetAllocatedBytesForCurrentThread()
    - fuzzAllocatedBefore;
Check(
    fuzzAllocated <= (long)fuzzIterations * 1024,
    $"Diagnostics parser fuzz allocated {fuzzAllocated} bytes for {fuzzIterations} iterations.");

Console.WriteLine(
    $"Fingerprint and incident-explanation self-check passed; parser fuzz={fuzzIterations}.");
