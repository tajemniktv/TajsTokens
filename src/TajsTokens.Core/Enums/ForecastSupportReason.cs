namespace TajsTokens.Core.Enums;

/// <summary>Independent limitations on facts, point estimates, bands and model selection.</summary>
[Flags]
public enum ForecastSupportReason
{
    None = 0,
    MissingMeter = 1,
    StaleMeter = 2,
    UnknownAccount = 4,
    MissingOrExpiredReset = 8,
    InsufficientHistory = 16,
    FlatMeter = 32,
    InsufficientIntervalEvidence = 64,
    WorkloadFeaturesUnavailable = 128,
    ChallengerNotBetter = 256,
    WindowNotReported = 512,
    OutsideObservedSupport = 1024,
    IncompatibleCohort = 2048,
}
